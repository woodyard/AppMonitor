using System.IO.Pipes;
using System.Text;
using Arkimentum.AppMonitor.Models;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Ipc;

/// <summary>
/// Named-pipe client used by the tray agent. Maintains a persistent connection to the service, reconnecting with
/// back-off, and raises <see cref="MessageReceived"/> for every server message.
/// </summary>
public sealed class PipeClient : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly string _pipeName;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public PipeClient(ILogger<PipeClient> logger, string pipeName = AgentSettings.PipeName)
    {
        _logger = logger;
        _pipeName = pipeName;
    }

    public bool IsConnected => _pipe?.IsConnected == true;

    public event Action<IpcMessage>? MessageReceived;
    public event Action<bool>? ConnectionChanged;

    /// <summary>Factory for the message sent immediately after each (re)connect.</summary>
    public Func<IpcMessage>? HelloFactory { get; set; }

    public void Start()
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(5000, ct).ConfigureAwait(false);
                pipe.ReadMode = PipeTransmissionMode.Byte;
                _pipe = pipe;
                _writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                delay = TimeSpan.FromSeconds(2);
                _logger.LogInformation("Connected to service pipe {Pipe}", _pipeName);
                ConnectionChanged?.Invoke(true);

                if (HelloFactory is { } hf) await SendAsync(hf(), ct).ConfigureAwait(false);

                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 64 * 1024, leaveOpen: true);
                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    if (line is null) break;
                    IpcMessage? msg;
                    try { msg = IpcJson.Deserialize(line); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Bad message from service: {Line}", Truncate(line)); continue; }
                    if (msg is null) continue;
                    try { MessageReceived?.Invoke(msg); }
                    catch (Exception ex) { _logger.LogError(ex, "Handler failed for {Type}", msg.GetType().Name); }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (TimeoutException)
            {
                _logger.LogDebug("Service pipe {Pipe} not available; retrying in {Delay}s", _pipeName, delay.TotalSeconds);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Pipe connection lost; retrying in {Delay}s", delay.TotalSeconds);
            }
            finally
            {
                var wasConnected = _pipe is not null;
                _writer = null;
                _pipe?.Dispose();
                _pipe = null;
                if (wasConnected) ConnectionChanged?.Invoke(false);
            }

            if (ct.IsCancellationRequested) break;
            try { await Task.Delay(delay, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
        }
    }

    public async Task<bool> SendAsync(IpcMessage message, CancellationToken ct = default)
    {
        var writer = _writer;
        if (writer is null || _pipe?.IsConnected != true) return false;
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(IpcJson.Serialize(message).AsMemory(), ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to send {Type}", message.GetType().Name);
            return false;
        }
        finally { _writeLock.Release(); }
    }

    private static string Truncate(string s) => s.Length > 200 ? s[..200] + "…" : s;

    public async ValueTask DisposeAsync()
    {
        if (_cts is null) return;
        _cts.Cancel();
        _pipe?.Dispose();
        if (_loop is not null) { try { await _loop.ConfigureAwait(false); } catch { } }
        _cts.Dispose();
    }
}
