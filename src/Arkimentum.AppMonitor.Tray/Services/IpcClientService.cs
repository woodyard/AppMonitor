using System.Threading;
using System.Windows.Threading;
using Arkimentum.AppMonitor.Ipc;
using Arkimentum.AppMonitor.Tray.Infrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Tray.Services;

/// <summary>
/// Owns the named-pipe connection to the service. Every incoming message is re-raised on the UI dispatcher so
/// that the rest of the agent never has to think about threads.
/// </summary>
public sealed class IpcClientService : IHostedService
{
    /// <summary>How long to wait for the service's unsolicited StateMessage before asking for one.</summary>
    private static readonly TimeSpan StateProbeDelay = TimeSpan.FromSeconds(4);

    private readonly ILogger<IpcClientService> _log;
    private readonly PipeClient _client;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _cts = new();

    private DateTimeOffset _connectedAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastStateUtc = DateTimeOffset.MinValue;
    private bool _connected;

    public IpcClientService(ILogger<IpcClientService> log, PipeClient client, Dispatcher dispatcher)
    {
        _log = log;
        _client = client;
        _dispatcher = dispatcher;
    }

    /// <summary>Raised on the UI thread for every message received from the service.</summary>
    public event Action<IpcMessage>? MessageReceived;

    /// <summary>Raised on the UI thread whenever the pipe connects or drops.</summary>
    public event Action<bool>? ConnectionChanged;

    public bool IsConnected => _connected;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _client.HelloFactory = CreateHello;
        _client.MessageReceived += OnMessageReceived;
        _client.ConnectionChanged += OnConnectionChanged;
        _log.LogInformation("Starting IPC client for session {SessionId} as {UserName}", AppInfo.SessionId, AppInfo.UserName);
        _client.Start();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        _client.MessageReceived -= OnMessageReceived;
        _client.ConnectionChanged -= OnConnectionChanged;
        await _client.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    private HelloMessage CreateHello() => new()
    {
        SessionId = AppInfo.SessionId,
        UserSid = string.IsNullOrEmpty(AppInfo.UserSid) ? null : AppInfo.UserSid,
        UserName = AppInfo.UserName,
        AgentVersion = AppInfo.Version,
    };

    private void OnMessageReceived(IpcMessage message)
    {
        if (message is StateMessage) _lastStateUtc = DateTimeOffset.UtcNow;
        _log.LogDebug("Received {Type} ({MessageId})", message.GetType().Name, message.MessageId);
        Post(() => MessageReceived?.Invoke(message));
    }

    private void OnConnectionChanged(bool connected)
    {
        _connected = connected;
        if (connected)
        {
            _connectedAtUtc = DateTimeOffset.UtcNow;
            _log.LogInformation("Connected to the Arkimentum AppMonitor service");
            _ = ProbeForStateAsync(_connectedAtUtc);
        }
        else
        {
            _log.LogInformation("Disconnected from the Arkimentum AppMonitor service");
        }
        Post(() => ConnectionChanged?.Invoke(connected));
    }

    /// <summary>The service answers Hello with a StateMessage; if it does not, ask explicitly.</summary>
    private async Task ProbeForStateAsync(DateTimeOffset connectedAtUtc)
    {
        try
        {
            await Task.Delay(StateProbeDelay, _cts.Token).ConfigureAwait(false);
            if (!_connected || _connectedAtUtc != connectedAtUtc) return;
            if (_lastStateUtc >= connectedAtUtc) return;
            _log.LogInformation("No state received within {Seconds}s of connecting; requesting it", StateProbeDelay.TotalSeconds);
            await RequestStateAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogDebug(ex, "State probe failed"); }
    }

    private void Post(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action);
    }

    // ---------------------------------------------------------------- outgoing messages

    public async Task<bool> SendAsync(IpcMessage message)
    {
        var ok = await _client.SendAsync(message, _cts.Token).ConfigureAwait(false);
        if (ok) _log.LogInformation("Sent {Type} {Detail}", message.GetType().Name, Describe(message));
        else _log.LogWarning("Could not send {Type}: not connected", message.GetType().Name);
        return ok;
    }

    public Task<bool> RequestStateAsync() => SendAsync(new GetStateMessage());

    public Task<bool> RequestScanAsync() => SendAsync(new RequestScanMessage());

    /// <summary>
    /// Asks the service to install one update now. <paramref name="closeBlockingProcesses"/> is only set from the
    /// close-apps dialog: it tells the service it may end the processes this agent could not reach itself (elevated,
    /// or in another user's session), which it can because it runs as LocalSystem.
    /// </summary>
    public Task<bool> InstallNowAsync(string updateKey, bool closeBlockingProcesses = false) =>
        SendAsync(new InstallNowMessage { UpdateKey = updateKey, CloseBlockingProcesses = closeBlockingProcesses });

    /// <summary>
    /// Queues several updates at once ("Update all"): one install request per key, in order. The service marks each
    /// as scheduled and runs the installs one after another - it serialises installs itself, so sending them back to
    /// back is safe. Returns the number of requests the pipe accepted.
    /// </summary>
    public async Task<int> InstallAllAsync(IEnumerable<string> updateKeys)
    {
        var sent = 0;
        foreach (var key in updateKeys)
        {
            if (await InstallNowAsync(key).ConfigureAwait(false)) sent++;
        }
        return sent;
    }

    public Task<bool> DeferAsync(string updateKey, int minutes) =>
        SendAsync(new DeferMessage { UpdateKey = updateKey, Minutes = minutes });

    public Task<bool> DismissAsync(string updateKey) => SendAsync(new DismissMessage { UpdateKey = updateKey });

    /// <summary>
    /// Asks the service to check the release feed for a newer agent, and - unless <paramref name="checkOnly"/> - to
    /// install it. The service does the work as SYSTEM and answers with an <see cref="AckMessage"/>; the returned
    /// message id is what matches that answer to this request (null when the pipe is not connected).
    /// </summary>
    public async Task<string?> RequestAgentUpdateAsync(bool checkOnly)
    {
        var message = new UpdateAgentMessage { CheckOnly = checkOnly };
        return await SendAsync(message).ConfigureAwait(false) ? message.MessageId : null;
    }

    private static string Describe(IpcMessage message) => message switch
    {
        InstallNowMessage m => m.CloseBlockingProcesses ? $"{m.UpdateKey} (close blocking apps)" : m.UpdateKey,
        DeferMessage m => $"{m.UpdateKey} +{m.Minutes}min",
        DismissMessage m => m.UpdateKey,
        UserInstallProgressMessage m => $"{m.UpdateKey}: {m.Status}",
        UserInstallResultMessage m => $"{m.UpdateKey}: success={m.Result.Success}",
        UserScanResultMessage m => $"scan {m.ScanId}: {m.Results.Count} result(s)",
        ProcessesClosedMessage m => $"{m.UpdateKey}: stillRunning={m.StillRunning.Count} declined={m.Declined}",
        UpdateAgentMessage m => m.CheckOnly ? "check only" : "check and install",
        _ => string.Empty,
    };
}
