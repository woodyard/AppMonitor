using System.Threading;
using System.Windows.Threading;
using Arkimentum.AppMonitor.Admin.Infrastructure;
using Arkimentum.AppMonitor.Ipc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Admin.Services;

/// <summary>
/// The console's connection to the running service: an <see cref="IpcClientKind.Admin"/> client that reads state
/// and can request a scan. Everything is re-raised on the UI dispatcher, and nothing here is required for the
/// console to work — the service is frequently absent.
/// </summary>
public sealed class AdminIpcService : IHostedService
{
    private readonly ILogger<AdminIpcService> _log;
    private readonly PipeClient _client;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _cts = new();

    public AdminIpcService(ILogger<AdminIpcService> log, PipeClient client, Dispatcher dispatcher)
    {
        _log = log;
        _client = client;
        _dispatcher = dispatcher;
    }

    /// <summary>Raised on the UI thread for every state snapshot the service sends.</summary>
    public event Action<StateMessage>? StateReceived;

    /// <summary>Raised on the UI thread whenever the pipe connects or drops.</summary>
    public event Action<bool>? ConnectionChanged;

    /// <summary>Raised on the UI thread for every acknowledgement the service sends back.</summary>
    public event Action<AckMessage>? AckReceived;

    public bool IsConnected { get; private set; }

    public StateMessage? LastState { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _client.HelloFactory = () => new HelloMessage
        {
            ClientKind = IpcClientKind.Admin,
            SessionId = Environment.ProcessId,
            UserName = AdminAppInfo.UserName,
            AgentVersion = AdminAppInfo.Version,
        };
        _client.MessageReceived += OnMessage;
        _client.ConnectionChanged += OnConnectionChanged;
        _client.Start();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        _client.MessageReceived -= OnMessage;
        _client.ConnectionChanged -= OnConnectionChanged;
        await _client.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    /// <summary>Asks the service to scan now. False when the pipe is not available.</summary>
    public async Task<bool> RequestScanAsync()
    {
        var sent = await _client.SendAsync(new RequestScanMessage(), _cts.Token).ConfigureAwait(false);
        if (sent) _log.LogInformation("Requested a scan from the service.");
        else _log.LogWarning("Could not request a scan: the service pipe is not connected.");
        return sent;
    }

    public Task<bool> RequestStateAsync() => _client.SendAsync(new GetStateMessage(), _cts.Token);

    /// <summary>Asks the service to install or repair the winget prerequisites. False when the pipe is not available.</summary>
    public async Task<bool> RequestPrerequisiteRepairAsync()
    {
        var sent = await _client.SendAsync(new RepairPrerequisitesMessage(), _cts.Token).ConfigureAwait(false);
        if (sent) _log.LogInformation("Requested a prerequisite repair from the service.");
        else _log.LogWarning("Could not request a prerequisite repair: the service pipe is not connected.");
        return sent;
    }

    /// <summary>
    /// Asks the service to update the agent itself: it checks the release feed and, when a newer release applies,
    /// downloads, verifies and installs it as SYSTEM. False when the pipe is not available.
    /// </summary>
    public async Task<bool> RequestAgentUpdateAsync()
    {
        var sent = await _client.SendAsync(new UpdateAgentMessage { CheckOnly = false }, _cts.Token).ConfigureAwait(false);
        if (sent) _log.LogInformation("Requested an agent update from the service.");
        else _log.LogWarning("Could not request an agent update: the service pipe is not connected.");
        return sent;
    }

    private void OnMessage(IpcMessage message)
    {
        switch (message)
        {
            case StateMessage state:
                Post(() =>
                {
                    LastState = state;
                    StateReceived?.Invoke(state);
                });
                break;
            case AckMessage ack:
                Post(() => AckReceived?.Invoke(ack));
                break;
        }
    }

    private void OnConnectionChanged(bool connected)
    {
        Post(() =>
        {
            IsConnected = connected;
            _log.LogInformation(connected ? "Connected to the service pipe." : "Disconnected from the service pipe.");
            ConnectionChanged?.Invoke(connected);
        });
    }

    private void Post(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action);
    }
}
