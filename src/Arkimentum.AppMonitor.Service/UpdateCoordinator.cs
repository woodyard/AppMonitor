using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Arkimentum.AppMonitor.Cloud;
using Arkimentum.AppMonitor.Inventory;
using Arkimentum.AppMonitor.Ipc;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Native;
using Arkimentum.AppMonitor.Providers;
using Arkimentum.AppMonitor.Service.Policy;
using Arkimentum.AppMonitor.Service.State;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Service;

/// <summary>
/// The service brain: runs scans (system context itself, user context via the tray agents), keeps the tracked update
/// state, evaluates policy on a timer, executes installs and drives notifications/prompts through the pipe.
/// </summary>
public sealed class UpdateCoordinator : IAsyncDisposable
{
    private readonly ILogger<UpdateCoordinator> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SettingsProvider _settings;
    private readonly StateStore _store;
    private readonly PipeServer _pipe;
    private readonly InstalledAppScanner _scanner;
    private readonly TrayLauncher _trayLauncher;

    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly SemaphoreSlim _policyLock = new(1, 1);
    private readonly SemaphoreSlim _installLock = new(1, 1);
    private readonly ConcurrentDictionary<string, (TaskCompletionSource<UserScanResultMessage> Tcs, string ConnectionId)> _pendingUserScans = new();
    private readonly ConcurrentDictionary<string, (TaskCompletionSource<InstallResult> Tcs, string ConnectionId)> _pendingUserInstalls = new();
    private readonly ConcurrentDictionary<string, (TaskCompletionSource<ProcessesClosedMessage> Tcs, string ConnectionId)> _pendingCloses = new();
    private readonly HashSet<string> _installsInFlight = new(StringComparer.OrdinalIgnoreCase);

    private ServiceState _state = new();
    private volatile bool _scanInProgress;
    private volatile bool _scanRequested;

    public static readonly string ServiceVersion =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";

    private readonly Prerequisites.PrerequisiteManager _prerequisites;
    private Prerequisites.PrerequisiteStatus? _prerequisiteStatus;
    private int _prerequisiteRepairRunning;

    public static readonly bool IsRunningAsSystem = DetectSystem();

    private static bool DetectSystem()
    {
        try { using var id = System.Security.Principal.WindowsIdentity.GetCurrent(); return id.IsSystem; } catch { return false; }
    }

    public UpdateCoordinator(ILogger<UpdateCoordinator> logger, ILoggerFactory loggerFactory, SettingsProvider settings,
        StateStore store, PipeServer pipe, InstalledAppScanner scanner, TrayLauncher trayLauncher, Prerequisites.PrerequisiteManager prerequisites)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _settings = settings;
        _store = store;
        _pipe = pipe;
        _scanner = scanner;
        _trayLauncher = trayLauncher;
        _prerequisites = prerequisites;

        _pipe.MessageReceived += OnMessageAsync;
        _pipe.ClientConnected += OnClientConnectedAsync;
        _pipe.ClientDisconnected += OnClientDisconnectedAsync;
        _settings.Changed += OnSettingsChanged;
    }

    public bool ScanRequested => _scanRequested;
    public DateTimeOffset? NextScanUtc { get => _state.NextScanUtc; set => _state.NextScanUtc = value; }
    public DateTimeOffset? LastScanUtc => _state.LastScanUtc;

    public void Initialize()
    {
        var s = _settings.Reload();
        _state = _store.Load(s);
        _pipe.Start();
        _logger.LogInformation("{Product} service {Version} started (pid {Pid}, user {User})",
            AgentSettings.ProductName, ServiceVersion, Environment.ProcessId, Environment.UserName);
    }

    /// <summary>
    /// Loads the persisted state without starting the named-pipe server, for command-line entry points
    /// (<c>--report-now</c>) that must not collide with the Windows service instance already serving the pipe.
    /// </summary>
    public void LoadStateForCli() => _state = _store.Load(_settings.Reload());

    // =====================================================================================================================
    // Observation surface for the cloud sync service (additive; nothing else in the service uses these)
    // =====================================================================================================================

    private readonly ConcurrentQueue<ReportedEvent> _reportEvents = new();
    private const int MaxBufferedEvents = 500;

    /// <summary>Raised after every scan (successful or not) once the state has been merged and policies evaluated.</summary>
    public event Action? ScanCompleted;

    /// <summary>Raised after every install attempt with the tracked update and whether the install succeeded.</summary>
    public event Action<PendingUpdate, bool>? InstallCompleted;

    /// <summary>Clones of every tracked update, including the ones already installed (the report needs their outcome).</summary>
    public IReadOnlyList<PendingUpdate> TrackedUpdates() => _state.Updates.Values.Select(u => u.Clone()).ToList();

    /// <summary>True while an install of a monitored application is running (the agent must not replace itself then).</summary>
    public bool InstallInProgress { get { lock (_installsInFlight) return _installsInFlight.Count > 0; } }

    /// <summary>The user of the most recently connected tray agent, else the interactive console session's user.</summary>
    public string? LastLogonUser
    {
        get
        {
            var tray = _pipe.Clients.Where(c => c.IsTray && c.UserName is not null).OrderByDescending(c => c.ConnectedUtc).FirstOrDefault();
            if (tray?.UserName is { } name) return name;
            try
            {
                var session = NativeMethods.EnumerateSessions()
                    .Where(s => s.IsInteractiveUser)
                    .OrderBy(s => s.State == NativeMethods.WTS_CONNECTSTATE_CLASS.WTSActive ? 0 : 1)
                    .FirstOrDefault();
                return session?.QualifiedUser;
            }
            catch { return null; }
        }
    }

    /// <summary>Appends an event to the in-memory buffer the cloud report drains. Never throws; the oldest entries are dropped.</summary>
    public void RecordEvent(ReportedEventKind kind, string? appId = null, string? message = null, string? fromVersion = null, string? toVersion = null)
    {
        _reportEvents.Enqueue(new ReportedEvent
        {
            OccurredUtc = DateTimeOffset.UtcNow,
            Kind = kind,
            AppId = appId,
            Message = message,
            FromVersion = fromVersion,
            ToVersion = toVersion,
        });
        while (_reportEvents.Count > MaxBufferedEvents) _reportEvents.TryDequeue(out _);
    }

    /// <summary>Removes and returns the buffered events (used when a report is built).</summary>
    public IReadOnlyList<ReportedEvent> DrainEvents()
    {
        var list = new List<ReportedEvent>();
        while (_reportEvents.TryDequeue(out var e)) list.Add(e);
        return list;
    }

    private void RaiseScanCompleted()
    {
        try { ScanCompleted?.Invoke(); } catch (Exception ex) { _logger.LogDebug(ex, "A ScanCompleted handler threw"); }
    }

    private void RaiseInstallCompleted(PendingUpdate update, bool success)
    {
        try { InstallCompleted?.Invoke(update.Clone(), success); } catch (Exception ex) { _logger.LogDebug(ex, "An InstallCompleted handler threw"); }
    }

    public void RequestScan() => _scanRequested = true;

    private string _lastAppFingerprint = string.Empty;
    private bool _settingsSeen;

    /// <summary>
    /// A configuration change that adds, removes, enables or disables applications triggers a scan right away, so an
    /// administrator does not have to wait for the next scheduled scan to see the effect.
    /// </summary>
    private void OnSettingsChanged(AgentSettings s)
    {
        var fingerprint = string.Join("|", s.Apps.Where(a => a.Enabled).Select(a => $"{a.AppId}:{a.Source}:{a.Context}:{a.WingetId}:{a.VersionUrl}").OrderBy(x => x));
        var changed = _settingsSeen && fingerprint != _lastAppFingerprint;
        _lastAppFingerprint = fingerprint;
        _settingsSeen = true;
        if (!changed || _scanInProgress) return;
        _logger.LogInformation("Monitored application set changed; scheduling a scan");
        RequestScan();
    }

    // =====================================================================================================================
    // Scanning
    // =====================================================================================================================

    public async Task RunScanAsync(string reason, CancellationToken ct)
    {
        if (!await _scanLock.WaitAsync(0, ct).ConfigureAwait(false)) { _logger.LogDebug("Scan already running; ignoring request ({Reason})", reason); return; }
        var sw = Stopwatch.StartNew();
        _scanInProgress = true;
        _scanRequested = false;
        try
        {
            var settings = _settings.Reload();
            var now = DateTimeOffset.UtcNow;
            _logger.LogInformation("Scan started ({Reason}); {Count} configured app(s)", reason, settings.Apps.Count);
            await BroadcastStateAsync(ct).ConfigureAwait(false);

            var apps = settings.Apps.Where(a => a.Enabled).ToList();
            var inventory = _scanner.Scan(includeMachine: true, includeUsers: true);
            _logger.LogDebug("Inventory: {Machine} machine-wide and {User} per-user entries", inventory.Count(i => i.Context == InstallContext.System), inventory.Count(i => i.Context == InstallContext.User));

            // Partition work by context
            var systemApps = new List<AppPolicy>();
            var userApps = new Dictionary<string, List<AppPolicy>>(StringComparer.OrdinalIgnoreCase); // sid -> apps
            var connectedSids = _pipe.Clients.Where(c => c.IsTray && c.UserSid is not null).Select(c => c.UserSid!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var userSidsInInventory = inventory.Where(i => i.Context == InstallContext.User).Select(i => i.UserSid!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            foreach (var app in apps)
            {
                switch (app.Context)
                {
                    case InstallContext.System:
                        systemApps.Add(app);
                        break;
                    case InstallContext.User:
                        foreach (var sid in connectedSids) AddUserApp(userApps, sid, app);
                        break;
                    default: // Auto
                        var machineMatch = InstalledAppScanner.Match(app, inventory.Where(i => i.Context == InstallContext.System)).Count > 0;
                        var userMatches = userSidsInInventory
                            .Where(sid => InstalledAppScanner.Match(app, inventory.Where(i => i.Context == InstallContext.User && i.UserSid == sid)).Count > 0)
                            .ToList();
                        if (machineMatch) systemApps.Add(app);
                        foreach (var sid in userMatches.Where(s => connectedSids.Contains(s, StringComparer.OrdinalIgnoreCase))) AddUserApp(userApps, sid, app);
                        if (!machineMatch && userMatches.Count == 0)
                        {
                            // Not visible in the inventory (e.g. MSIX/winget-only knowledge): let both contexts look.
                            systemApps.Add(app);
                            foreach (var sid in connectedSids) AddUserApp(userApps, sid, app);
                        }
                        break;
                }
            }

            var outcomes = new List<ScanOutcome>();
            var checkedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // --- system context (in-process)
            if (systemApps.Count > 0)
            {
                var options = ProviderOptions.From(settings);
                var providers = ProviderFactory.Create(_loggerFactory, options);
                List<UpdateCheckResult> results;
                try
                {
                    var checker = new UpdateChecker(_loggerFactory.CreateLogger<UpdateChecker>(), providers);
                    results = await checker.CheckAsync(systemApps, inventory, ExecutionContextInfo.System, options, ct).ConfigureAwait(false);
                }
                finally { providers.DisposeAll(); }
                foreach (var r in results)
                {
                    var policy = systemApps.First(a => a.AppId.Equals(r.AppId, StringComparison.OrdinalIgnoreCase));
                    outcomes.Add(new ScanOutcome(r, policy, InstallContext.System, null));
                }
                foreach (var a in systemApps) checkedKeys.Add(PendingUpdate.MakeKey(a.AppId, InstallContext.System, null));
            }

            // --- user context (delegated to tray agents, in parallel per user)
            var userTasks = userApps.Select(async kv =>
            {
                var (sid, list) = (kv.Key, kv.Value);
                var client = ClientFor(sid);
                if (client is null) return;
                var msg = new RunUserScanMessage
                {
                    Apps = list,
                    WingetEnabled = settings.WingetEnabled,
                    WebSourcesEnabled = settings.WebSourcesEnabled,
                    ProxyUrl = string.IsNullOrWhiteSpace(settings.ProxyUrl) ? null : settings.ProxyUrl,
                    WingetGlobalArgs = string.IsNullOrWhiteSpace(settings.WingetGlobalArgs) ? null : settings.WingetGlobalArgs,
                    WingetIncludeUnknown = settings.WingetIncludeUnknown,
                };
                var tcs = new TaskCompletionSource<UserScanResultMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingUserScans[msg.ScanId] = (tcs, client.ConnectionId);
                try
                {
                    _logger.LogInformation("Requesting user-context scan of {Count} app(s) from {Client}", list.Count, client);
                    if (!await _pipe.SendAsync(client, msg, ct).ConfigureAwait(false)) return;
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromMinutes(10));
                    var reply = await tcs.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
                    lock (outcomes)
                    {
                        foreach (var r in reply.Results)
                        {
                            var policy = list.FirstOrDefault(a => a.AppId.Equals(r.AppId, StringComparison.OrdinalIgnoreCase));
                            if (policy is null) continue;
                            outcomes.Add(new ScanOutcome(r, policy, InstallContext.User, sid));
                            _logger.LogInformation("{App} ({User}): {Outcome}", policy.DisplayName, client,
                                r.Error is not null ? $"check failed - {r.Error}"
                                : !r.IsInstalled ? "not installed (User)"
                                : r.UpdateAvailable ? $"{r.InstalledVersion} -> {r.AvailableVersion} available ({r.Source.ToString().ToLowerInvariant()}, User)"
                                : $"{r.InstalledVersion} is up to date ({r.Source.ToString().ToLowerInvariant()}, User)");
                        }
                        foreach (var a in list) checkedKeys.Add(PendingUpdate.MakeKey(a.AppId, InstallContext.User, sid));
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _logger.LogWarning("User-context scan for {Client} timed out", client);
                }
                finally { _pendingUserScans.TryRemove(msg.ScanId, out _); }
            }).ToList();
            await Task.WhenAll(userTasks).ConfigureAwait(false);

            // --- merge
            MergeSummary summary;
            await _policyLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                summary = PolicyEngine.Merge(_state.Updates, outcomes, checkedKeys, now);
                _state.LastScanUtc = now;
                _state.NextScanUtc = now + settings.ScanInterval;
                _state.LastScanSummary = $"{summary.Added} new, {summary.Updated} updated, {summary.Resolved} resolved, {summary.Removed} removed";
                _store.Save(settings, _state);
            }
            finally { _policyLock.Release(); }

            var errors = outcomes.Where(o => o.Result.Error is not null).ToList();
            _logger.LogInformation("Scan finished in {Elapsed:F1}s: {Summary}; {Pending} pending update(s); {Errors} check error(s)",
                sw.Elapsed.TotalSeconds, _state.LastScanSummary, _state.Updates.Values.Count(u => u.IsActive), errors.Count);
            foreach (var e in errors) _logger.LogWarning("Check failed for {App} ({Context}): {Error}", e.Policy.AppId, e.Context, e.Result.Error);
            foreach (var n in summary.NewUpdates)
            {
                _logger.LogInformation("New update: {App} {From} -> {To} ({Source}, {Context}{Mandatory}{Deadline})", n.DisplayName, n.InstalledVersion, n.AvailableVersion,
                    n.Source, n.Context, n.Mandatory ? ", mandatory" : "", n.DeadlineUtc is { } d ? $", deadline {d.ToLocalTime():g}" : "");
                RecordEvent(ReportedEventKind.UpdateDetected, n.AppId, $"{n.DisplayName} {n.AvailableVersion} available ({n.Source}, {n.Context})", n.InstalledVersion, n.AvailableVersion);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scan failed");
        }
        finally
        {
            _scanInProgress = false;
            _scanLock.Release();
        }

        await BroadcastStateAsync(ct).ConfigureAwait(false);
        await EvaluatePoliciesAsync(ct).ConfigureAwait(false);
        RaiseScanCompleted();
    }

    private static void AddUserApp(Dictionary<string, List<AppPolicy>> map, string sid, AppPolicy app)
    {
        if (!map.TryGetValue(sid, out var list)) map[sid] = list = [];
        if (!list.Any(a => a.AppId.Equals(app.AppId, StringComparison.OrdinalIgnoreCase))) list.Add(app);
    }

    // =====================================================================================================================
    // Policy tick
    // =====================================================================================================================

    public async Task EvaluatePoliciesAsync(CancellationToken ct)
    {
        if (!await _policyLock.WaitAsync(0, ct).ConfigureAwait(false)) return;
        var toInstall = new List<PendingUpdate>();
        var toForceClose = new List<PendingUpdate>();
        try
        {
            var settings = _settings.Current;
            var now = DateTimeOffset.UtcNow;
            var changed = false;

            foreach (var u in _state.Updates.Values.Where(u => u.IsActive).ToList())
            {
                lock (_installsInFlight) { if (_installsInFlight.Contains(u.Key)) continue; }
                var policy = settings.Apps.FirstOrDefault(a => a.AppId.Equals(u.AppId, StringComparison.OrdinalIgnoreCase));
                var interval = PolicyEngine.NotificationIntervalFor(u, policy, settings);

                var sessionId = u.Context == InstallContext.User ? SessionFor(u.UserSid) : null;
                var blocking = ProcessHelper.GetRunning(u.ProcessNames, sessionId);
                if (!blocking.SequenceEqual(u.BlockingProcesses, StringComparer.OrdinalIgnoreCase)) { u.BlockingProcesses = [.. blocking]; changed = true; }
                if (blocking.Count == 0 && u.State == UpdateState.WaitingForClose)
                {
                    // user closed the apps: proceed when the install was requested or is enforced, otherwise go back to Available
                    u.State = u.InstallRequested || u.IsPastDeadline(now) || u.AutoInstall || u.ForceCloseAtUtc is not null ? UpdateState.Scheduled : UpdateState.Available;
                    u.ForceCloseAtUtc = null;
                    changed = true;
                }

                var action = PolicyEngine.Decide(u, now, interval, blocking.Count > 0);
                if (action.Kind == PolicyActionKind.None) continue;
                if (action.Kind is PolicyActionKind.Notify or PolicyActionKind.PromptClose && !TargetsFor(u).Any())
                {
                    _logger.LogTrace("Policy: {App} ({Context}) -> {Action}, but no tray agent is connected for it yet", u.DisplayName, u.Context, action.Kind);
                    continue;
                }
                _logger.LogDebug("Policy: {App} ({Context}) state={State} -> {Action}", u.DisplayName, u.Context, u.State, action.Kind);

                switch (action.Kind)
                {
                    case PolicyActionKind.Install:
                        toInstall.Add(u);
                        break;
                    case PolicyActionKind.ForceClose:
                        toForceClose.Add(u);
                        break;
                    case PolicyActionKind.PromptClose:
                        PolicyEngine.MarkWaitingForClose(u, blocking, now, scheduleForcedClose: true);
                        if (await SendPromptCloseAsync(u, ct).ConfigureAwait(false)) { u.LastNotifiedUtc = now; }
                        changed = true;
                        break;
                    case PolicyActionKind.Notify:
                        if (settings.NotificationsEnabled && await SendNotificationAsync(u, action.Notification, ct).ConfigureAwait(false))
                        {
                            u.LastNotifiedUtc = now;
                            u.Dismissed = false;
                            changed = true;
                        }
                        break;
                }
            }

            if (changed) _store.Save(settings, _state);
        }
        finally { _policyLock.Release(); }

        foreach (var u in toForceClose) _ = RunGuardedAsync(() => ForceCloseAndInstallAsync(u, ct), u.Key);
        foreach (var u in toInstall) _ = RunGuardedAsync(() => InstallAsync(u, ct), u.Key);
    }

    private Task RunGuardedAsync(Func<Task> work, string key)
    {
        lock (_installsInFlight)
        {
            if (!_installsInFlight.Add(key)) return Task.CompletedTask;
        }
        return Task.Run(async () =>
        {
            try { await work().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogError(ex, "Install task for {Key} failed", key); }
            finally { lock (_installsInFlight) _installsInFlight.Remove(key); }
        });
    }

    // =====================================================================================================================
    // Install flows
    // =====================================================================================================================

    private async Task ForceCloseAndInstallAsync(PendingUpdate u, CancellationToken ct)
    {
        var sessionId = u.Context == InstallContext.User ? SessionFor(u.UserSid) : null;
        var still = ProcessHelper.GetRunning(u.ProcessNames, sessionId);
        if (still.Count > 0)
        {
            _logger.LogWarning("Deadline enforcement for {App}: closing {Processes}", u.DisplayName, string.Join(", ", still));
            RecordEvent(ReportedEventKind.ForcedClose, u.AppId, $"Closing {string.Join(", ", still)} to install {u.DisplayName} {u.AvailableVersion}");
            // Ask the agents in the affected sessions to close gracefully first, then kill survivors ourselves.
            var targets = TargetsFor(u).ToList();
            var closeTasks = targets.Select(async c =>
            {
                var msg = new CloseProcessesMessage { UpdateKey = u.Key, ProcessNames = [.. still], Force = true, GracefulWaitSeconds = 30 };
                var tcs = new TaskCompletionSource<ProcessesClosedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingCloses[msg.MessageId] = (tcs, c.ConnectionId);
                try
                {
                    if (!await _pipe.SendAsync(c, msg, ct).ConfigureAwait(false)) return;
                    await tcs.Task.WaitAsync(TimeSpan.FromSeconds(75), ct).ConfigureAwait(false);
                }
                catch (TimeoutException) { }
                finally { _pendingCloses.TryRemove(msg.MessageId, out _); }
            });
            await Task.WhenAll(closeTasks).ConfigureAwait(false);

            var survivors = await ProcessHelper.CloseAsync(u.ProcessNames, sessionId, TimeSpan.FromSeconds(5), force: true, ct).ConfigureAwait(false);
            if (survivors.Count > 0)
            {
                _logger.LogError("Could not terminate {Processes} for {App}; install postponed", string.Join(", ", survivors), u.DisplayName);
                await MutateAsync(u.Key, x => { x.ForceCloseAtUtc = DateTimeOffset.UtcNow.AddMinutes(Math.Max(5, x.CloseGracePeriodMinutes)); }, ct).ConfigureAwait(false);
                return;
            }
        }
        await MutateAsync(u.Key, x => { x.State = UpdateState.Scheduled; x.ForceCloseAtUtc = null; x.BlockingProcesses = []; }, ct).ConfigureAwait(false);
        await InstallAsync(u, ct).ConfigureAwait(false);
    }

    /// <summary>Installs one update (system context in-process, user context through the tray agent).</summary>
    private async Task InstallAsync(PendingUpdate snapshot, CancellationToken ct)
    {
        var settings = _settings.Current;
        var key = snapshot.PendingKey();
        var now = DateTimeOffset.UtcNow;

        // Re-check blocking processes right before we start.
        var sessionId = snapshot.Context == InstallContext.User ? SessionFor(snapshot.UserSid) : null;
        var blocking = ProcessHelper.GetRunning(snapshot.ProcessNames, sessionId);
        if (blocking.Count > 0)
        {
            var current = Get(key);
            if (current is null) return;
            var enforce = current.IsPastDeadline(now) && current.ForceCloseAtDeadline;
            _logger.LogInformation("{App}: waiting for the user to close {Processes}{Enforce}", current.DisplayName, string.Join(", ", blocking),
                enforce ? $" (forced close in {current.CloseGracePeriodMinutes} min)" : "");
            await MutateAsync(key, x => PolicyEngine.MarkWaitingForClose(x, blocking, now, scheduleForcedClose: true), ct).ConfigureAwait(false);
            var latest = Get(key);
            if (latest is not null && await SendPromptCloseAsync(latest, ct).ConfigureAwait(false))
                await MutateAsync(key, x => x.LastNotifiedUtc = now, ct).ConfigureAwait(false);
            return;
        }

        await _installLock.WaitAsync(ct).ConfigureAwait(false);
        InstallResult result;
        try
        {
            var u = Get(key);
            if (u is null || u.State is UpdateState.Installing or UpdateState.Installed) return;
            var policy = settings.Apps.FirstOrDefault(a => a.AppId.Equals(u.AppId, StringComparison.OrdinalIgnoreCase));
            if (policy is null) { _logger.LogWarning("{App}: no longer configured; skipping install", u.DisplayName); return; }

            await MutateAsync(key, PolicyEngine.MarkInstalling, ct).ConfigureAwait(false);
            u = Get(key)!;
            _logger.LogInformation("Installing {App} {From} -> {To} ({Source}, {Context}{User})", u.DisplayName, u.InstalledVersion, u.AvailableVersion, u.Source, u.Context,
                u.Context == InstallContext.User ? $" for {u.UserSid}" : "");
            if (settings.NotificationsEnabled)
                await SendNotificationAsync(u, NotificationKind.Installing, ct).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(settings.InstallTimeoutMinutes + 5));

            if (u.Context == InstallContext.System)
            {
                var options = ProviderOptions.From(settings);
                var providers = ProviderFactory.Create(_loggerFactory, options);
                var checker = new UpdateChecker(_loggerFactory.CreateLogger<UpdateChecker>(), providers);
                var progress = new Progress<string>(line => _logger.LogDebug("[{App}] {Line}", u.DisplayName, line));
                try { result = await checker.InstallAsync(policy, u, ExecutionContextInfo.System, progress, timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { result = InstallResult.Fail("Install timed out"); }
                catch (Exception ex) { result = InstallResult.Fail(ex.Message); _logger.LogError(ex, "Installer threw for {App}", u.DisplayName); }
                finally { providers.DisposeAll(); }
            }
            else
            {
                var client = ClientFor(u.UserSid);
                if (client is null)
                {
                    _logger.LogWarning("{App}: user {Sid} has no connected tray agent; install postponed", u.DisplayName, u.UserSid);
                    await MutateAsync(key, x => x.State = UpdateState.Scheduled, ct).ConfigureAwait(false);
                    return;
                }
                var tcs = new TaskCompletionSource<InstallResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingUserInstalls[key] = (tcs, client.ConnectionId);
                try
                {
                    var msg = new RunUserInstallMessage { Update = u.Clone(), TimeoutMinutes = settings.InstallTimeoutMinutes };
                    if (!await _pipe.SendAsync(client, msg, ct).ConfigureAwait(false)) result = InstallResult.Fail("Could not reach the tray agent");
                    else result = await tcs.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { result = InstallResult.Fail("Install timed out waiting for the user session"); }
                finally { _pendingUserInstalls.TryRemove(key, out _); }
            }
        }
        finally { _installLock.Release(); }

        // Defence in depth: never record "installed" when the provider itself tells us the version did not move.
        if (result.Success && !string.IsNullOrWhiteSpace(result.InstalledVersion) && !string.IsNullOrWhiteSpace(snapshot.AvailableVersion)
            && !Versioning.VersionComparer.IsUnknown(result.InstalledVersion)
            && Versioning.VersionComparer.Compare(result.InstalledVersion, snapshot.AvailableVersion) < 0 && !result.RebootRequired)
        {
            result = InstallResult.Fail($"The installer reported success but {snapshot.DisplayName} is still at {result.InstalledVersion} (expected {snapshot.AvailableVersion}).", result.ExitCode);
        }

        var doneAt = DateTimeOffset.UtcNow;
        if (result.Success)
        {
            _logger.LogInformation("Installed {App} {Version}{Reboot}: {Message}", snapshot.DisplayName, result.InstalledVersion ?? snapshot.AvailableVersion,
                result.RebootRequired ? " (reboot required)" : "", result.Message ?? "ok");
            await MutateAsync(key, x => PolicyEngine.MarkInstalled(x, result, doneAt), ct).ConfigureAwait(false);
            RecordEvent(ReportedEventKind.InstallSucceeded, snapshot.AppId,
                $"{snapshot.DisplayName} installed{(result.RebootRequired ? " (reboot required)" : "")}", snapshot.InstalledVersion, result.InstalledVersion ?? snapshot.AvailableVersion);
            if (settings.NotificationsEnabled && settings.ShowInstalledNotifications && Get(key) is { } done)
                await SendNotificationAsync(done, NotificationKind.Installed, ct, result.RebootRequired ? "A restart is required to finish the update." : null).ConfigureAwait(false);
        }
        else
        {
            _logger.LogError("Install of {App} failed (exit {Code}): {Message}", snapshot.DisplayName, result.ExitCode, result.Message);
            await MutateAsync(key, x => PolicyEngine.MarkFailed(x, result.Message ?? $"exit code {result.ExitCode}", doneAt), ct).ConfigureAwait(false);
            RecordEvent(ReportedEventKind.InstallFailed, snapshot.AppId,
                $"{snapshot.DisplayName}: {result.Message ?? $"exit code {result.ExitCode}"}", snapshot.InstalledVersion, snapshot.AvailableVersion);
            if (settings.NotificationsEnabled && Get(key) is { } failed)
            {
                if (await SendNotificationAsync(failed, NotificationKind.Failed, ct).ConfigureAwait(false))
                    await MutateAsync(key, x => x.LastNotifiedUtc = DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
            }
        }

        RaiseInstallCompleted(Get(key) ?? snapshot, result.Success);
    }

    // =====================================================================================================================
    // Notifications / prompts
    // =====================================================================================================================

    private IEnumerable<PipeClientConnection> TargetsFor(PendingUpdate u) =>
        u.Context == InstallContext.System
            ? _pipe.Clients.Where(c => c.IsTray)
            : _pipe.Clients.Where(c => c.IsTray && string.Equals(c.UserSid, u.UserSid, StringComparison.OrdinalIgnoreCase));

    private async Task<bool> SendNotificationAsync(PendingUpdate u, NotificationKind kind, CancellationToken ct, string? extra = null)
    {
        var (title, body) = ComposeNotification(u, kind);
        if (extra is not null) body = body + " " + extra;
        var sent = false;
        foreach (var c in TargetsFor(u).ToList())
        {
            if (await _pipe.SendAsync(c, new NotifyMessage { Kind = kind, Title = title, Body = body, Update = u.Clone() }, ct).ConfigureAwait(false)) sent = true;
        }
        if (sent) _logger.LogInformation("Notified {Kind} for {App}: {Body}", kind, u.DisplayName, body);
        else _logger.LogDebug("No tray agent to notify for {App} ({Kind})", u.DisplayName, kind);
        return sent;
    }

    private async Task<bool> SendPromptCloseAsync(PendingUpdate u, CancellationToken ct)
    {
        var sent = false;
        foreach (var c in TargetsFor(u).ToList())
        {
            if (await _pipe.SendAsync(c, new PromptCloseMessage { Update = u.Clone() }, ct).ConfigureAwait(false)) sent = true;
        }
        if (sent) _logger.LogInformation("Prompted to close {Processes} for {App}{Force}", string.Join(", ", u.BlockingProcesses), u.DisplayName,
            u.ForceCloseAtUtc is { } f ? $" (forced close at {f.ToLocalTime():t})" : "");
        return sent;
    }

    public static (string Title, string Body) ComposeNotification(PendingUpdate u, NotificationKind kind)
    {
        var version = string.IsNullOrEmpty(u.AvailableVersion) ? "" : $" {u.AvailableVersion}";
        return kind switch
        {
            NotificationKind.UpdateAvailable => ($"Update available: {u.DisplayName}",
                $"{u.DisplayName}{version} is ready to install." + (u.Mandatory && u.DeadlineUtc is { } d ? $" Required by {d.ToLocalTime():f}." : "")),
            NotificationKind.DeadlineApproaching => ($"Update required soon: {u.DisplayName}",
                $"{u.DisplayName}{version} must be installed by {u.DeadlineUtc?.ToLocalTime():f}. Install now to avoid interruption."),
            NotificationKind.CloseApplications => ($"Close {u.DisplayName} to update",
                $"Please close {string.Join(", ", u.BlockingProcesses)} so {u.DisplayName}{version} can be installed." +
                (u.ForceCloseAtUtc is { } f ? $" It will be closed automatically at {f.ToLocalTime():t}." : "")),
            NotificationKind.Installing => ($"Installing {u.DisplayName}", $"{u.DisplayName}{version} is being installed."),
            NotificationKind.Installed => ($"{u.DisplayName} updated", $"{u.DisplayName} {u.InstalledVersion ?? u.AvailableVersion} was installed successfully."),
            NotificationKind.Failed => ($"Update failed: {u.DisplayName}", $"{u.DisplayName}{version} could not be installed. {u.LastError}".Trim()),
            _ => (AgentSettings.ProductName, u.DisplayName),
        };
    }

    // =====================================================================================================================
    // IPC
    // =====================================================================================================================

    private static readonly TimeSpan ScanOnConnectThreshold = TimeSpan.FromMinutes(10);

    private async Task OnClientConnectedAsync(PipeClientConnection conn)
    {
        // A tray agent for a user who was not present at the last scan: their per-user applications have not been
        // checked yet, so run a scan soon instead of waiting for the next scheduled one.
        if (conn.IsTray && !_scanInProgress && (_state.LastScanUtc is null || DateTimeOffset.UtcNow - _state.LastScanUtc.Value > ScanOnConnectThreshold))
        {
            _logger.LogInformation("Tray agent {Client} connected and the last scan is {Age}; scheduling a scan", conn,
                _state.LastScanUtc is null ? "unknown" : $"{(DateTimeOffset.UtcNow - _state.LastScanUtc.Value).TotalMinutes:F0} min old");
            RequestScan();
        }
        await _pipe.SendAsync(conn, BuildState(conn)).ConfigureAwait(false);
        // Re-issue outstanding close prompts so a freshly started agent shows the dialog again.
        foreach (var u in _state.Updates.Values.Where(u => u.State == UpdateState.WaitingForClose && IsVisibleTo(u, conn)).ToList())
            await _pipe.SendAsync(conn, new PromptCloseMessage { Update = u.Clone() }).ConfigureAwait(false);
    }

    /// <summary>A tray agent went away: fail every wait that depended on it instead of running into the long timeouts.</summary>
    private Task OnClientDisconnectedAsync(PipeClientConnection conn)
    {
        foreach (var kv in _pendingUserScans.Where(kv => kv.Value.ConnectionId == conn.ConnectionId))
            kv.Value.Tcs.TrySetResult(new UserScanResultMessage { ScanId = kv.Key, Results = [] });
        foreach (var kv in _pendingUserInstalls.Where(kv => kv.Value.ConnectionId == conn.ConnectionId))
            kv.Value.Tcs.TrySetResult(InstallResult.Fail("The tray agent disconnected during the install"));
        foreach (var kv in _pendingCloses.Where(kv => kv.Value.ConnectionId == conn.ConnectionId))
            kv.Value.Tcs.TrySetResult(new ProcessesClosedMessage { UpdateKey = string.Empty });
        return Task.CompletedTask;
    }

    private async Task OnMessageAsync(PipeClientConnection conn, IpcMessage message)
    {
        var ct = CancellationToken.None;
        switch (message)
        {
            case HelloMessage hello:
                _logger.LogInformation("Hello from {Client}: agent {Version}, session {Session} (pipe says {PipeSession})", conn, hello.AgentVersion, hello.SessionId, conn.SessionId);
                await _pipe.SendAsync(conn, BuildState(conn), ct).ConfigureAwait(false);
                break;

            case GetStateMessage:
                await _pipe.SendAsync(conn, BuildState(conn), ct).ConfigureAwait(false);
                break;

            case RepairPrerequisitesMessage:
                if (conn.Kind != IpcClientKind.Admin)
                {
                    _logger.LogWarning("{Client} requested a prerequisite repair but is not an admin client; ignored", conn);
                    await _pipe.SendAsync(conn, new AckMessage { InReplyTo = message.MessageId, Ok = false, Message = "Only the admin console may repair prerequisites" }, ct).ConfigureAwait(false);
                    break;
                }
                if (_prerequisiteRepairRunning == 1)
                {
                    await _pipe.SendAsync(conn, new AckMessage { InReplyTo = message.MessageId, Ok = true, Message = "Repair already in progress" }, ct).ConfigureAwait(false);
                    break;
                }
                _logger.LogInformation("{Client} requested a prerequisite repair", conn);
                _ = Task.Run(() => EnsurePrerequisitesAsync($"requested by {conn}", force: true, CancellationToken.None));
                await _pipe.SendAsync(conn, new AckMessage { InReplyTo = message.MessageId, Ok = true, Message = "Repair started" }, ct).ConfigureAwait(false);
                break;

            case RequestScanMessage:
                _logger.LogInformation("{Client} requested a scan", conn);
                RequestScan();
                await _pipe.SendAsync(conn, new AckMessage { InReplyTo = message.MessageId, Message = "Scan queued" }, ct).ConfigureAwait(false);
                break;

            case InstallNowMessage m:
                await HandleUserActionAsync(conn, "install now", m.UpdateKey, m.MessageId, u => { PolicyEngine.RequestInstall(u); return null; }, ct).ConfigureAwait(false);
                await EvaluatePoliciesAsync(ct).ConfigureAwait(false);
                break;

            case DeferMessage m:
                await HandleUserActionAsync(conn, $"defer {m.Minutes} min", m.UpdateKey, m.MessageId, u =>
                {
                    if (!PolicyEngine.TryDefer(u, m.Minutes, DateTimeOffset.UtcNow, out var reason)) return reason ?? "Deferral not allowed";
                    RecordEvent(ReportedEventKind.Deferred, u.AppId,
                        $"{u.DisplayName} deferred {m.Minutes} min by {conn} (deferral {u.DeferralCount}/{(u.MaxDeferrals == 0 ? "unlimited" : u.MaxDeferrals.ToString())})");
                    return null;
                }, ct).ConfigureAwait(false);
                break;

            case DismissMessage m:
                await HandleUserActionAsync(conn, "dismiss", m.UpdateKey, m.MessageId, u => { PolicyEngine.Dismiss(u, DateTimeOffset.UtcNow); return null; }, ct).ConfigureAwait(false);
                break;

            case UserInstallProgressMessage m:
                _logger.LogDebug("[{Key}] {Status}", m.UpdateKey, m.Status);
                break;

            case UserInstallResultMessage m:
                if (_pendingUserInstalls.TryGetValue(m.UpdateKey, out var pending) && IsOwner(conn, m.UpdateKey)) pending.Tcs.TrySetResult(m.Result);
                else _logger.LogWarning("Unexpected install result for {Key} from {Client}", m.UpdateKey, conn);
                break;

            case UserScanResultMessage m:
                if (m.ScanId is not null && _pendingUserScans.TryGetValue(m.ScanId, out var scan) && scan.ConnectionId == conn.ConnectionId) scan.Tcs.TrySetResult(m);
                else _logger.LogWarning("Unexpected scan result {ScanId} from {Client}", m.ScanId, conn);
                break;

            case ProcessesClosedMessage m:
                foreach (var kv in _pendingCloses) { if (kv.Value.ConnectionId != conn.ConnectionId || kv.Value.Tcs.Task.IsCompleted) continue; kv.Value.Tcs.TrySetResult(m); }
                if (m.Declined) _logger.LogInformation("{Client} declined to close processes for {Key}", conn, m.UpdateKey);
                break;

            default:
                _logger.LogDebug("Ignoring {Type} from {Client}", message.GetType().Name, conn);
                break;
        }
    }

    private async Task HandleUserActionAsync(PipeClientConnection conn, string actionName, string key, string replyTo, Func<PendingUpdate, string?> action, CancellationToken ct)
    {
        string? error;
        await _policyLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_state.Updates.TryGetValue(key, out var u) || !IsVisibleTo(u, conn)) error = "Unknown update";
            else
            {
                error = action(u);
                if (error is null)
                {
                    _logger.LogInformation("{Client} chose '{Action}' for {App}: state={State}, deferrals={Deferrals}/{Max}, deferred until={Until}", conn, actionName, u.DisplayName,
                        u.State, u.DeferralCount, u.MaxDeferrals == 0 ? "∞" : u.MaxDeferrals, u.DeferredUntilUtc?.ToLocalTime().ToString("g") ?? "-");
                    _store.Save(_settings.Current, _state);
                }
                else
                {
                    _logger.LogInformation("{Client} chose '{Action}' for {App} but it was rejected: {Reason}", conn, actionName, u.DisplayName, error);
                }
            }
        }
        finally { _policyLock.Release(); }

        await _pipe.SendAsync(conn, new AckMessage { InReplyTo = replyTo, Ok = error is null, Message = error }, ct).ConfigureAwait(false);
        await BroadcastStateAsync(ct).ConfigureAwait(false);
    }

    private static bool IsVisibleTo(PendingUpdate u, PipeClientConnection conn) =>
        u.Context == InstallContext.System || string.Equals(u.UserSid, conn.UserSid, StringComparison.OrdinalIgnoreCase);

    private bool IsOwner(PipeClientConnection conn, string key) =>
        _state.Updates.TryGetValue(key, out var u) && IsVisibleTo(u, conn);

    private StateMessage BuildState(PipeClientConnection conn)
    {
        var s = _settings.Current;
        return new StateMessage
        {
            // Installed updates are kept internally (post-install grace, retention) but are no longer shown to users or admins.
            Updates = _state.Updates.Values.Where(u => IsVisibleTo(u, conn) && u.IsActive).OrderBy(u => u.DisplayName).Select(u => u.Clone()).ToList(),
            LastScanUtc = _state.LastScanUtc,
            NextScanUtc = _state.NextScanUtc,
            ScanInProgress = _scanInProgress,
            ServiceVersion = ServiceVersion,
            Settings = new SettingsSummary
            {
                ScanIntervalMinutes = s.ScanIntervalMinutes,
                NotificationIntervalMinutes = s.NotificationIntervalMinutes,
                WingetEnabled = s.WingetEnabled,
                WebSourcesEnabled = s.WebSourcesEnabled,
                NotificationsEnabled = s.NotificationsEnabled,
                LogDirectory = s.LogDirectory,
                LogLevel = s.LogLevel,
                MonitoredAppCount = s.Apps.Count(a => a.Enabled),
                MonitoredApps = s.Apps.Where(a => a.Enabled).Select(a => a.DisplayName).OrderBy(x => x).ToList(),
                LoadedAtUtc = s.LoadedAtUtc,
                Prerequisites = _prerequisiteStatus?.Clone(),
            },
        };
    }

    public Task BroadcastStateAsync(CancellationToken ct) => _pipe.BroadcastAsync(BuildState, ct);

    // =====================================================================================================================
    // helpers
    // =====================================================================================================================

    private PendingUpdate? Get(string key) => _state.Updates.TryGetValue(key, out var u) ? u : null;

    private async Task MutateAsync(string key, Action<PendingUpdate> mutate, CancellationToken ct)
    {
        await _policyLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_state.Updates.TryGetValue(key, out var u)) { mutate(u); _store.Save(_settings.Current, _state); }
        }
        finally { _policyLock.Release(); }
        await BroadcastStateAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The tray agent to talk to for a user: the most recently connected client with that SID (a restarted agent supersedes a stale one).</summary>
    private PipeClientConnection? ClientFor(string? sid) =>
        sid is null ? null : _pipe.Clients.Where(c => c.IsTray && string.Equals(c.UserSid, sid, StringComparison.OrdinalIgnoreCase)).OrderByDescending(c => c.ConnectedUtc).FirstOrDefault();

    private int? SessionFor(string? sid)
    {
        if (sid is null) return null;
        var c = ClientFor(sid);
        if (c is not null) return c.SessionId;
        try
        {
            foreach (var s in NativeMethods.EnumerateSessions().Where(s => s.IsInteractiveUser))
                if (string.Equals(NativeMethods.GetSessionUserSid(s.SessionId), sid, StringComparison.OrdinalIgnoreCase)) return s.SessionId;
        }
        catch { }
        return null;
    }

    public void EnsureTrayAgents() => _trayLauncher.EnsureRunning(_settings.Current);

    public Prerequisites.PrerequisiteStatus? PrerequisiteStatus => _prerequisiteStatus;

    /// <summary>LastAction values <see cref="Prerequisites.PrerequisiteManager.EnsureAsync"/> sets when it did NOT repair anything.</summary>
    private static readonly HashSet<string> SkippedRepairActions = new(StringComparer.Ordinal)
    {
        "Nothing to repair",
        "winget source disabled; prerequisite not installed",
        "Automatic prerequisite installation is disabled (AutoInstallPrerequisites=0)",
        "Cannot install prerequisites without administrative rights",
    };

    /// <summary>
    /// Checks the winget prerequisite and repairs it (as SYSTEM) when allowed. <paramref name="force"/> repairs even when
    /// auto-install is disabled (used by the admin console's explicit request).
    /// </summary>
    public async Task<Prerequisites.PrerequisiteStatus> EnsurePrerequisitesAsync(string reason, bool force, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _prerequisiteRepairRunning, 1) == 1)
        {
            _logger.LogDebug("Prerequisite check already running; ignoring request ({Reason})", reason);
            return _prerequisiteStatus ?? new Prerequisites.PrerequisiteStatus { RepairInProgress = true };
        }
        try
        {
            _logger.LogInformation("Prerequisite check ({Reason})", reason);
            if (_prerequisiteStatus is not null) { _prerequisiteStatus.RepairInProgress = true; await BroadcastStateAsync(ct).ConfigureAwait(false); }
            var settings = _settings.Reload();
            var status = await _prerequisites.EnsureAsync(settings, IsRunningAsSystem, ct, force).ConfigureAwait(false);
            _prerequisiteStatus = status;
            if (status.IsHealthy) Providers.WingetLocator.ResetCache();
            if (status.LastAction is { } action && !SkippedRepairActions.Contains(action))
                RecordEvent(ReportedEventKind.PrerequisiteRepaired, null,
                    $"{action}: {status.Summary}{(status.LastError is null ? "" : " - " + status.LastError)}", toVersion: status.WingetVersion);
            return status;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Prerequisite check failed");
            _prerequisiteStatus = new Prerequisites.PrerequisiteStatus { CheckedUtc = DateTimeOffset.UtcNow, LastError = ex.Message };
            return _prerequisiteStatus;
        }
        finally
        {
            Interlocked.Exchange(ref _prerequisiteRepairRunning, 0);
            if (_prerequisiteStatus is not null) _prerequisiteStatus.RepairInProgress = false;
            await BroadcastStateAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _pipe.MessageReceived -= OnMessageAsync;
        _pipe.ClientConnected -= OnClientConnectedAsync;
        _pipe.ClientDisconnected -= OnClientDisconnectedAsync;
        _settings.Changed -= OnSettingsChanged;
        _store.Save(_settings.Current, _state);
        await Task.CompletedTask;
    }
}

internal static class PendingUpdateExtensions
{
    public static string PendingKey(this PendingUpdate u) => u.Key;
}
