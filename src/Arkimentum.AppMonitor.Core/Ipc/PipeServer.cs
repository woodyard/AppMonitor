using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Native;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Ipc;

/// <summary>Identity of a connected tray agent, established from the pipe itself (not from the Hello message).</summary>
public sealed class PipeClientConnection
{
    public required string ConnectionId { get; init; }
    public int SessionId { get; internal set; }
    public string? UserSid { get; internal set; }
    public string? UserName { get; internal set; }
    /// <summary>Declared by the client in its Hello; defaults to a tray agent.</summary>
    public IpcClientKind Kind { get; set; } = IpcClientKind.Tray;
    public bool IsTray => Kind == IpcClientKind.Tray;
    public DateTimeOffset ConnectedUtc { get; } = DateTimeOffset.UtcNow;
    internal StreamWriter? Writer { get; set; }
    internal SemaphoreSlim WriteLock { get; } = new(1, 1);
    public override string ToString() => $"{UserName ?? UserSid ?? "?"}@session{SessionId}{(Kind == IpcClientKind.Tray ? "" : $" ({Kind})")}";
}

/// <summary>
/// Multi-client named-pipe server hosted by the service. The pipe ACL allows any authenticated user to connect;
/// the caller's SID/session is determined from the client process's own token so a client cannot spoof another user.
/// </summary>
public sealed class PipeServer : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly string _pipeName;
    private readonly ConcurrentDictionary<string, PipeClientConnection> _clients = new();
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public PipeServer(ILogger<PipeServer> logger, string pipeName = AgentSettings.PipeName)
    {
        _logger = logger;
        _pipeName = pipeName;
    }

    public event Func<PipeClientConnection, IpcMessage, Task>? MessageReceived;
    public event Func<PipeClientConnection, Task>? ClientConnected;
    public event Func<PipeClientConnection, Task>? ClientDisconnected;

    public IReadOnlyCollection<PipeClientConnection> Clients => _clients.Values.ToArray();

    public void Start()
    {
        if (_acceptLoop is not null) return;
        _cts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private static PipeSecurity CreateSecurity()
    {
        var ps = new PipeSecurity();
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        return ps;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.WriteThrough, 64 * 1024, 64 * 1024, CreateSecurity());
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                var p = pipe;
                pipe = null;
                _ = Task.Run(() => HandleClientAsync(p, ct), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { pipe?.Dispose(); }
            catch (Exception ex)
            {
                pipe?.Dispose();
                _logger.LogError(ex, "Pipe accept failed; retrying");
                try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var conn = new PipeClientConnection { ConnectionId = Guid.NewGuid().ToString("N") };
        var registered = false;
        try
        {
            conn.SessionId = NativeMethods.GetNamedPipeClientSessionId(pipe.SafePipeHandle) ?? -1;
            conn.Writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 64 * 1024, leaveOpen: true);
            while (!ct.IsCancellationRequested && pipe.IsConnected)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;

                if (!registered)
                {
                    // Establish who is on the other end from the OS, not from the message payload. Impersonating a
                    // named-pipe client is only possible once data has been read from it, hence after the first line.
                    ResolveIdentity(pipe, conn);
                    // RunAsClient reverts the impersonation itself; this is the safety net for the day it does not,
                    // because the thread carries on to run handlers, saves and installs for the SYSTEM service.
                    Native.ImpersonationGuard.RevertIfImpersonating(_logger, "pipe client identification");
                    registered = true;
                    _clients[conn.ConnectionId] = conn;
                    _logger.LogInformation("Tray agent connected: {Client}", conn);
                    if (ClientConnected is { } onConnected) await onConnected(conn).ConfigureAwait(false);
                }

                IpcMessage? msg;
                try { msg = IpcJson.Deserialize(line); }
                catch (Exception ex) { _logger.LogWarning(ex, "Bad message from {Client}", conn); continue; }
                if (msg is null) continue;
                if (msg is HelloMessage hello && hello.ClientKind != conn.Kind)
                {
                    conn.Kind = hello.ClientKind;
                    _logger.LogInformation("Client {Client} identifies as {Kind}", conn, conn.Kind);
                }
                if (MessageReceived is { } handler)
                {
                    Native.ImpersonationGuard.RevertIfImpersonating(_logger, $"before handling {msg.GetType().Name} from {conn}");
                    try { await handler(conn, msg).ConfigureAwait(false); }
                    catch (Exception ex) { _logger.LogError(ex, "Handler failed for {Type} from {Client}", msg.GetType().Name, conn); }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pipe client {Client} failed", conn);
        }
        finally
        {
            _clients.TryRemove(conn.ConnectionId, out _);
            conn.Writer = null;
            try { pipe.Dispose(); } catch { }
            if (registered)
            {
                _logger.LogInformation("Tray agent disconnected: {Client}", conn);
                if (ClientDisconnected is { } onDisconnected) { try { await onDisconnected(conn).ConfigureAwait(false); } catch { } }
            }
        }
    }

    /// <summary>
    /// Establishes who is on the other end from the OS, never from the message payload, so a client cannot claim to
    /// be another user. The kernel tells us the client's process id; that process's own token carries the account.
    ///
    /// <para>
    /// This deliberately does NOT use <see cref="NamedPipeServerStream.RunAsClient"/>. On .NET 10 the impersonation it
    /// performs leaks into the execution context of the async flow: the calling thread is reverted, but every
    /// continuation after the next await - and every task started from them - runs impersonating the client. In the
    /// SYSTEM service that meant "access denied" on its own state file, an App Installer folder it could not list,
    /// and installers launched into the user's session that then asked for UAC. Reproduced with a 60-line program;
    /// the process-token route below shows no trace of it. The impersonating call is kept only as a fallback and is
    /// then confined to a thread whose execution context does not flow back.
    /// </para>
    /// </summary>
    private void ResolveIdentity(NamedPipeServerStream pipe, PipeClientConnection conn)
    {
        try
        {
            if (NativeMethods.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid))
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                if (NativeMethods.OpenProcessToken(process.Handle, NativeMethods.TOKEN_QUERY, out var token))
                {
                    using (token)
                    using (var id = new WindowsIdentity(token.DangerousGetHandle()))
                    {
                        conn.UserSid = id.User?.Value;
                        conn.UserName = id.Name;
                    }
                }
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Reading the pipe client's process token failed; falling back to impersonation"); }

        if (conn.UserSid is not null) return;
        try
        {
            string? sid = null, name = null;
            Exception? failure = null;
            using (ExecutionContext.SuppressFlow())
            {
                var thread = new Thread(() =>
                {
                    try
                    {
                        pipe.RunAsClient(() =>
                        {
                            using var id = WindowsIdentity.GetCurrent();
                            sid = id.User?.Value;
                            name = id.Name;
                        });
                    }
                    catch (Exception ex) { failure = ex; }
                }) { IsBackground = true, Name = "pipe-client-identity" };
                thread.Start();
                thread.Join();
            }
            if (failure is not null) throw failure;
            conn.UserSid = sid;
            conn.UserName = name;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not determine the identity of the pipe client in session {Session}", conn.SessionId); }
    }

    public async Task<bool> SendAsync(PipeClientConnection conn, IpcMessage message, CancellationToken ct = default)
    {
        var writer = conn.Writer;
        if (writer is null) return false;
        await conn.WriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(IpcJson.Serialize(message).AsMemory(), ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Send to {Client} failed", conn);
            return false;
        }
        finally { conn.WriteLock.Release(); }
    }

    public async Task BroadcastAsync(Func<PipeClientConnection, IpcMessage?> factory, CancellationToken ct = default)
    {
        foreach (var c in _clients.Values)
        {
            var msg = factory(c);
            if (msg is not null) await SendAsync(c, msg, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is null) return;
        _cts.Cancel();
        if (_acceptLoop is not null) { try { await _acceptLoop.ConfigureAwait(false); } catch { } }
        _cts.Dispose();
    }
}
