using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Arkimentum.AppMonitor.Ipc;
using Arkimentum.AppMonitor.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Tray.Services;

/// <summary>
/// The agent's single source of truth: the last <see cref="StateMessage"/> from the service, plus a small local
/// overlay of statuses the agent knows before the service does (a user-context install it is running right now).
/// Everything here is touched on the UI thread only.
/// </summary>
public sealed class AgentStateStore : IHostedService
{
    /// <summary>
    /// "Update all" is greyed out until the service confirms the requests. If it never does - a dropped connection, a
    /// busy service - the button comes back after this long instead of staying dead.
    /// </summary>
    private static readonly TimeSpan UpdateAllPendingTimeout = TimeSpan.FromSeconds(30);

    private readonly ILogger<AgentStateStore> _log;
    private readonly IpcClientService _ipc;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, string> _localStatus = new(StringComparer.Ordinal);
    private readonly HashSet<string> _updateAllKeys = new(StringComparer.Ordinal);
    private DispatcherTimer? _updateAllTimeout;
    private readonly ScanActivity _scan = new();
    private DispatcherTimer? _scanRequestTimeout;

    private List<PendingUpdate> _updates = [];

    public AgentStateStore(ILogger<AgentStateStore> log, IpcClientService ipc, Dispatcher dispatcher)
    {
        _log = log;
        _ipc = ipc;
        _dispatcher = dispatcher;
    }

    /// <summary>Raised on the UI thread whenever anything the UI shows has changed.</summary>
    public event Action? Changed;

    public bool IsConnected { get; private set; }

    public bool HasReceivedState { get; private set; }

    public IReadOnlyList<PendingUpdate> Updates => _updates;

    /// <summary>
    /// The latest finished installs this user may see, newest first (at most 10). Empty before the first state message
    /// and from a service that predates the install history.
    /// </summary>
    public IReadOnlyList<InstallHistoryEntry> RecentInstalls { get; private set; } = [];

    public DateTimeOffset? LastScanUtc { get; private set; }

    public DateTimeOffset? NextScanUtc { get; private set; }

    /// <summary>What the service reported. The UI uses <see cref="IsScanning"/>, which also covers a request in flight.</summary>
    public bool ScanInProgress { get; private set; }

    /// <summary>
    /// True while a scan is running or has just been asked for: "Check now" shows progress at once instead of after
    /// the service has picked the request up. See <see cref="ScanActivity"/>.
    /// </summary>
    public bool IsScanning => _scan.IsActive(ScanInProgress, DateTimeOffset.UtcNow);

    /// <summary>Called by both "Check now" entry points just before the request is sent.</summary>
    public void MarkScanRequested()
    {
        _scan.Requested(DateTimeOffset.UtcNow);
        // One tick after the timeout, so a request the service never took up does not leave the banner showing.
        _scanRequestTimeout ??= new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = ScanActivity.RequestTimeout + TimeSpan.FromSeconds(1) };
        _scanRequestTimeout.Tick -= OnScanRequestTimeout;
        _scanRequestTimeout.Tick += OnScanRequestTimeout;
        _scanRequestTimeout.Stop();
        _scanRequestTimeout.Start();
        Changed?.Invoke();
    }

    /// <summary>The request could not be sent after all (no pipe): stop showing it.</summary>
    public void CancelScanRequest()
    {
        _scan.Reset();
        _scanRequestTimeout?.Stop();
        Changed?.Invoke();
    }

    private void OnScanRequestTimeout(object? sender, EventArgs e)
    {
        _scanRequestTimeout?.Stop();
        Changed?.Invoke();
    }

    public string? ServiceVersion { get; private set; }

    public SettingsSummary Settings { get; private set; } = new();

    /// <summary>
    /// What the service's self-updater last concluded, or null when the service is older than this agent and does not
    /// report it. Null means the UI shows the agent version without a status line or update buttons.
    /// </summary>
    public AgentUpdateStatus? AgentUpdate { get; private set; }

    /// <summary>The service's answer to the last "check for updates"/"update now" request, shown under the version row.</summary>
    public string? AgentUpdateNotice { get; private set; }

    /// <summary>True from the moment the user asked until the service answers, so both buttons grey out at once.</summary>
    public bool AgentUpdateRequested => _agentUpdateRequests.Count > 0;

    private readonly HashSet<string> _agentUpdateRequests = new(StringComparer.Ordinal);
    private DispatcherTimer? _agentUpdateTimeout;

    /// <summary>
    /// Remembers an agent-update request by message id so its acknowledgement can be shown under the version row.
    /// The service answers only when the check (and for an install, the hand-over) is done, which can take a while,
    /// so an unanswered request is dropped after <see cref="UpdateAllPendingTimeout"/> instead of staying pending.
    /// </summary>
    public void TrackAgentUpdateRequest(string messageId)
    {
        _agentUpdateRequests.Add(messageId);
        AgentUpdateNotice = null;
        _agentUpdateTimeout ??= new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = UpdateAllPendingTimeout };
        _agentUpdateTimeout.Tick -= OnAgentUpdateTimeout;
        _agentUpdateTimeout.Tick += OnAgentUpdateTimeout;
        _agentUpdateTimeout.Stop();
        _agentUpdateTimeout.Start();
        Changed?.Invoke();
    }

    private void OnAgentUpdateTimeout(object? sender, EventArgs e)
    {
        _agentUpdateTimeout?.Stop();
        if (_agentUpdateRequests.Count == 0) return;
        _log.LogWarning("The service did not answer {Count} agent update request(s) within {Seconds:F0} s", _agentUpdateRequests.Count, UpdateAllPendingTimeout.TotalSeconds);
        _agentUpdateRequests.Clear();
        Changed?.Invoke();
    }

    /// <summary>Updates that are not finished yet — what the tray badge counts.</summary>
    public int ActiveUpdateCount => _updates.Count(u => u.State != UpdateState.Installed);

    /// <summary>
    /// Updates a user can start right now — the same rule as a card's Install button: not already queued, running or
    /// finished, and not being driven by this agent at the moment. "Update all" sends one install request per entry.
    /// </summary>
    public IReadOnlyList<PendingUpdate> InstallableUpdates =>
        _updates.Where(u => u.State is not (UpdateState.Scheduled or UpdateState.Installing or UpdateState.Installed) &&
                            GetLocalStatus(u.Key) is null)
                .ToList();

    /// <summary>Updates the service is already working on: queued, waiting for applications to close, or installing.</summary>
    public IReadOnlyList<PendingUpdate> UpdatesInProgress =>
        _updates.Where(u => u.State is UpdateState.Scheduled or UpdateState.WaitingForClose or UpdateState.Installing).ToList();

    /// <summary>The update being installed right now (the first by name when several are), or null. Names the progress banner and the tooltip.</summary>
    public PendingUpdate? CurrentInstall =>
        _updates.Where(u => u.State == UpdateState.Installing)
                .OrderBy(u => u.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .FirstOrDefault();

    /// <summary>
    /// True from the moment the user pressed "Update all" until the service's next state message shows that none of the
    /// requested updates can be started any more. Without it the button stays live for the whole round trip and invites
    /// a second press that queues everything twice.
    /// </summary>
    public bool UpdateAllPending => _updateAllKeys.Count > 0;

    /// <summary>Remembers what "Update all" just requested so the button can grey out before the service answers.</summary>
    public void BeginUpdateAll(IEnumerable<string> keys)
    {
        _updateAllKeys.Clear();
        foreach (var key in keys) _updateAllKeys.Add(key);
        if (_updateAllKeys.Count == 0) return;

        _updateAllTimeout ??= new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = UpdateAllPendingTimeout };
        _updateAllTimeout.Tick -= OnUpdateAllTimeout;
        _updateAllTimeout.Tick += OnUpdateAllTimeout;
        _updateAllTimeout.Stop();
        _updateAllTimeout.Start();
        Changed?.Invoke();
    }

    private void OnUpdateAllTimeout(object? sender, EventArgs e)
    {
        _updateAllTimeout?.Stop();
        if (_updateAllKeys.Count == 0) return;
        _log.LogWarning("Update all: the service did not confirm {Count} request(s) within {Seconds:F0} s; re-enabling the button",
            _updateAllKeys.Count, UpdateAllPendingTimeout.TotalSeconds);
        _updateAllKeys.Clear();
        Changed?.Invoke();
    }

    /// <summary>Drops the pending flag once none of the requested updates is installable any more.</summary>
    private void SettleUpdateAll()
    {
        if (_updateAllKeys.Count == 0) return;
        if (InstallableUpdates.Any(u => _updateAllKeys.Contains(u.Key))) return;
        _updateAllKeys.Clear();
        _updateAllTimeout?.Stop();
    }

    /// <summary>True when something needs the user now: a mandatory update past its deadline or a forced close pending.</summary>
    public bool NeedsAttention
    {
        get
        {
            var now = DateTimeOffset.UtcNow;
            return _updates.Any(u => u.State != UpdateState.Installed &&
                                     (u.ForceCloseAtUtc is not null || u.IsPastDeadline(now)));
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ipc.MessageReceived += OnMessage;
        _ipc.ConnectionChanged += OnConnectionChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _ipc.MessageReceived -= OnMessage;
        _ipc.ConnectionChanged -= OnConnectionChanged;
        return Task.CompletedTask;
    }

    public PendingUpdate? Find(string key) => _updates.FirstOrDefault(u => string.Equals(u.Key, key, StringComparison.Ordinal));

    /// <summary>Transient status the agent itself is driving (a user-context install), shown until the service catches up.</summary>
    public string? GetLocalStatus(string key) => _localStatus.TryGetValue(key, out var s) ? s : null;

    public void SetLocalStatus(string key, string? status)
    {
        if (status is null)
        {
            if (!_localStatus.Remove(key)) return;
        }
        else
        {
            if (_localStatus.TryGetValue(key, out var existing) && existing == status) return;
            _localStatus[key] = status;
        }
        Changed?.Invoke();
    }

    /// <summary>Re-raises <see cref="Changed"/> so relative times ("in 2 hours") are recomputed.</summary>
    public void Refresh() => Changed?.Invoke();

    private void OnConnectionChanged(bool connected)
    {
        IsConnected = connected;
        if (!connected)
        {
            ScanInProgress = false;
            _scan.Reset();
            _scanRequestTimeout?.Stop();
            // Nothing will confirm the requests now; the button is disabled by IsConnected anyway.
            _updateAllKeys.Clear();
            _updateAllTimeout?.Stop();
            // An agent update that reached the service drops the pipe on purpose (the service restarts the tray),
            // so a dropped connection must not leave the buttons disabled once it comes back.
            _agentUpdateRequests.Clear();
            _agentUpdateTimeout?.Stop();
        }
        Changed?.Invoke();
    }

    private void OnMessage(IpcMessage message)
    {
        switch (message)
        {
            case StateMessage state:
                Apply(state);
                break;
            // The answer to "Check for updates"/"Update now": the text is what the user sees under the version row.
            case AckMessage ack when ack.InReplyTo is { } id && _agentUpdateRequests.Remove(id):
                _agentUpdateTimeout?.Stop();
                AgentUpdateNotice = ack.Message;
                _log.LogInformation("Agent update request answered: ok={Ok} {Message}", ack.Ok, ack.Message);
                Changed?.Invoke();
                break;
        }
    }

    /// <summary>Replaces everything the UI shows with the service's snapshot.</summary>
    public void Apply(StateMessage state)
    {
        _updates = state.Updates ?? [];
        // Null from a service that predates the install history.
        RecentInstalls = state.RecentInstalls ?? [];
        LastScanUtc = state.LastScanUtc;
        NextScanUtc = state.NextScanUtc;
        ScanInProgress = state.ScanInProgress;
        _scan.ServiceReported(state.ScanInProgress);
        ServiceVersion = state.ServiceVersion;
        Settings = state.Settings ?? new SettingsSummary();
        // Null from a service that predates client-initiated self-update; the UI then hides the status and buttons.
        AgentUpdate = state.AgentUpdate;
        HasReceivedState = true;

        // Drop local statuses for updates the service no longer reports or has already finished.
        if (_localStatus.Count > 0)
        {
            var stale = _localStatus.Keys
                .Where(k => Find(k) is null or { State: UpdateState.Installed or UpdateState.Failed })
                .ToList();
            foreach (var key in stale) _localStatus.Remove(key);
        }

        SettleUpdateAll();

        _log.LogInformation("State: {Count} update(s), scanInProgress={Scanning}, service={ServiceVersion}",
            _updates.Count, ScanInProgress, ServiceVersion ?? "?");
        Changed?.Invoke();
    }
}
