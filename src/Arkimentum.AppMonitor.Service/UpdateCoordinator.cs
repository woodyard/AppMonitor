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
using Arkimentum.AppMonitor.Service.Update;
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
        ImpersonationGuard.EnableDebugPrivilege(_logger);
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
        ImpersonationGuard.RevertIfImpersonating(_logger, $"scan ({reason})");
        if (!await _scanLock.WaitAsync(0, ct).ConfigureAwait(false)) { _logger.LogDebug("Scan already running; ignoring request ({Reason})", reason); return; }
        var sw = Stopwatch.StartNew();
        _scanInProgress = true;
        _scanRequested = false;
        try
        {
            if (reason == "requested" && ConfigRefresh is { } refresh)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(20));
                    await refresh(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _logger.LogWarning("Refreshing the organization configuration before the requested scan timed out; scanning with the cached configuration");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Refreshing the organization configuration before the requested scan failed; scanning with the cached configuration");
                }
            }
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
                RecordPresence(apps, outcomes, now);
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

    /// <summary>
    /// Remembers, per configured application and context, whether this scan found it installed here. That is what the
    /// tray's "Monitored applications" list is built from: the configuration is fleet-wide, the list a user sees must
    /// be about their device. Entries for applications that are no longer configured (or no longer enabled) are
    /// dropped, and a failed check leaves the previous answer alone - a winget hiccup is not evidence of a removal.
    /// Called under <see cref="_policyLock"/>; the caller saves the state. The map is rebuilt and then swapped in
    /// rather than edited in place, because <see cref="BuildState"/> enumerates it from a pipe thread without the
    /// lock - a reader always sees one complete snapshot or the previous one, never a dictionary mid-edit.
    /// </summary>
    private void RecordPresence(IReadOnlyList<AppPolicy> enabledApps, IReadOnlyList<ScanOutcome> outcomes, DateTimeOffset now)
    {
        var next = new Dictionary<string, AppPresence>(_state.AppPresence, StringComparer.OrdinalIgnoreCase);
        foreach (var o in outcomes)
        {
            if (o.Result.Error is not null) continue;
            var key = AppPresence.MakeKey(o.Policy.AppId, o.Context, o.UserSid);
            next[key] = new AppPresence
            {
                AppId = o.Policy.AppId,
                DisplayName = string.IsNullOrWhiteSpace(o.Policy.DisplayName) ? o.Policy.AppId : o.Policy.DisplayName,
                Context = o.Context,
                UserSid = o.Context == InstallContext.User ? o.UserSid : null,
                Installed = o.Result.IsInstalled,
                InstalledVersion = o.Result.InstalledVersion,
                CheckedUtc = now,
            };
        }

        var configured = enabledApps.Select(a => a.AppId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stale in next.Where(kv => !configured.Contains(kv.Value.AppId)).Select(kv => kv.Key).ToList())
            next.Remove(stale);

        _state.AppPresence = next;
        _logger.LogDebug("Application presence: {Installed} of {Known} checked entries are installed on this device",
            next.Values.Count(p => p.Installed), next.Count);
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
        ImpersonationGuard.RevertIfImpersonating(_logger, "policy evaluation");
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
                var mode = PolicyEngine.NotificationModeFor(policy, settings);

                var sessionId = u.Context == InstallContext.User ? SessionFor(u.UserSid) : null;
                // Detailed, because only the service (SYSTEM) can read the session and the elevation of a process the
                // tray agent cannot even open; the tray needs both to explain the list in the close-apps dialog.
                var details = ProcessHelper.GetRunningDetails(u.ProcessNames, sessionId);
                var blocking = details.Select(d => d.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (!blocking.SequenceEqual(u.BlockingProcesses, StringComparer.OrdinalIgnoreCase)) { u.BlockingProcesses = [.. blocking]; changed = true; }
                if (!SameDetails(u.BlockingDetails, details)) { u.BlockingDetails = [.. details]; changed = true; }
                if (blocking.Count == 0 && u.State == UpdateState.WaitingForClose)
                {
                    // user closed the apps: proceed when the install was requested or is enforced, otherwise go back to Available
                    u.State = u.InstallRequested || u.IsPastDeadline(now) || u.AutoInstall || u.ForceCloseAtUtc is not null ? UpdateState.Scheduled : UpdateState.Available;
                    u.ForceCloseAtUtc = null;
                    changed = true;
                }

                var action = PolicyEngine.Decide(u, now, interval, blocking.Count > 0, mode);
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
                        PolicyEngine.MarkWaitingForClose(u, blocking, now, scheduleForcedClose: true, details);
                        if (await SendPromptCloseAsync(u, ct).ConfigureAwait(false)) { u.LastNotifiedUtc = now; }
                        changed = true;
                        break;
                    case PolicyActionKind.Notify:
                        if (settings.NotificationsEnabled && await SendNotificationAsync(u, action.Notification, ct).ConfigureAwait(false))
                        {
                            u.LastNotifiedUtc = now;
                            // The user now knows about this update; Quiet mode will not raise it again by itself.
                            u.Announced = true;
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
            // Installs and forced closes run here; a pool thread that is still impersonating a pipe client must not.
            ImpersonationGuard.RevertIfImpersonating(_logger, $"install task for {key}");
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

            // Whatever the agents reported back, anything still alive is out of their reach (elevated, or in a session
            // they do not own). The service runs as SYSTEM, so it can end it - and must, or we prompt again forever.
            if (!await TerminateBlockingAsync(u, sessionId, ct).ConfigureAwait(false)) return;
        }
        await MutateAsync(u.Key, x => { x.State = UpdateState.Scheduled; x.ForceCloseAtUtc = null; x.BlockingProcesses = []; x.BlockingDetails = []; }, ct).ConfigureAwait(false);
        await InstallAsync(u, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Terminates every remaining blocking process of <paramref name="u"/> (in <paramref name="sessionId"/>, or in
    /// every session when that is null), logging each one with pid, session and owner. Returns false - having already
    /// failed the update with a message naming what survived, why, and from which executable - when something could
    /// not be ended or came straight back, because prompting the user again for a process no one in their session can
    /// close is the loop this exists to break.
    /// </summary>
    private async Task<bool> TerminateBlockingAsync(PendingUpdate u, int? sessionId, CancellationToken ct)
    {
        // "Access is denied" from SYSTEM on a plain process means this thread is not SYSTEM right now.
        ImpersonationGuard.RevertIfImpersonating(_logger, $"forced close for {u.DisplayName}");
        var outcome = await ProcessHelper.KillAsync(u.ProcessNames, sessionId, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        if (outcome.Killed.Count == 0 && outcome.Cleared) return true;

        foreach (var p in outcome.Killed)
            _logger.LogWarning("Terminated {Process} to install {App}", p.Describe(), u.DisplayName);
        if (outcome.Killed.Count > 0)
            RecordEvent(ReportedEventKind.ForcedClose, u.AppId,
                $"Terminated {BlockingProcessInfo.Describe(outcome.Killed)} to install {u.DisplayName} {u.AvailableVersion}");

        if (outcome.Cleared) return true;

        // Each survivor carries the exception (with the Win32 code) that refused the kill and, where readable, the
        // executable behind it; a restarted one names the new pid. Both go into the log, the report and the failure.
        foreach (var p in outcome.Survivors)
            _logger.LogError("Could not close {Process} to install {App}", p.Describe(), u.DisplayName);
        foreach (var p in outcome.Restarted)
            _logger.LogError("{Process} started again while {App} was being installed", p.Describe(), u.DisplayName);

        var message = ProcessHelper.DescribeFailure(outcome, u.DisplayName);
        _logger.LogError("{App}: {Message}", u.DisplayName, message);
        var at = DateTimeOffset.UtcNow;
        await MutateAsync(u.Key, x => PolicyEngine.MarkFailed(x, message, at), ct).ConfigureAwait(false);
        RecordEvent(ReportedEventKind.InstallFailed, u.AppId, message, u.InstalledVersion, u.AvailableVersion);
        if (_settings.Current.NotificationsEnabled && Get(u.Key) is { } failed)
            await SendNotificationAsync(failed, NotificationKind.Failed, ct).ConfigureAwait(false);
        RaiseInstallCompleted(Get(u.Key) ?? u, false);
        return false;
    }

    /// <summary>True when the two detail lists describe the same running instances, so state is not saved for nothing.</summary>
    private static bool SameDetails(IReadOnlyList<BlockingProcessInfo> a, IReadOnlyList<BlockingProcessInfo> b) =>
        a.Count == b.Count && a.Zip(b).All(pair => pair.First.ProcessId == pair.Second.ProcessId
            && string.Equals(pair.First.ProcessName, pair.Second.ProcessName, StringComparison.OrdinalIgnoreCase)
            && pair.First.SessionId == pair.Second.SessionId);

    /// <summary>Installs one update (system context in-process, user context through the tray agent).</summary>
    private async Task InstallAsync(PendingUpdate snapshot, CancellationToken ct)
    {
        var settings = _settings.Current;
        var key = snapshot.PendingKey();
        var now = DateTimeOffset.UtcNow;

        // Re-check blocking processes right before we start. For a machine-wide update this looks across every session
        // (sessionId is null), which is why a pwsh running elevated, as a scheduled task or under another user used to
        // land here again and again: no tray agent can close any of those, so the prompt came straight back.
        var sessionId = snapshot.Context == InstallContext.User ? SessionFor(snapshot.UserSid) : null;
        var details = ProcessHelper.GetRunningDetails(snapshot.ProcessNames, sessionId);
        var blocking = details.Select(d => d.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (blocking.Count > 0)
        {
            var current = Get(key);
            if (current is null) return;

            if (PolicyEngine.MayServiceForceClose(current, now))
            {
                _logger.LogWarning("{App}: closing {Processes} before the install ({Reason})", current.DisplayName,
                    BlockingProcessInfo.Describe(details),
                    current.ForceCloseRequestedUtc is not null ? "the user chose Close apps and update" : "deadline enforcement");
                if (!await TerminateBlockingAsync(current, sessionId, ct).ConfigureAwait(false)) return;
            }
            else
            {
                var enforce = current.IsPastDeadline(now) && current.ForceCloseAtDeadline;
                _logger.LogInformation("{App}: waiting for the user to close {Processes}{Enforce}", current.DisplayName, BlockingProcessInfo.Describe(details),
                    enforce ? $" (forced close in {current.CloseGracePeriodMinutes} min)" : "");
                await MutateAsync(key, x => PolicyEngine.MarkWaitingForClose(x, blocking, now, scheduleForcedClose: true, details), ct).ConfigureAwait(false);
                var latest = Get(key);
                if (latest is not null && await SendPromptCloseAsync(latest, ct).ConfigureAwait(false))
                    await MutateAsync(key, x => x.LastNotifiedUtc = now, ct).ConfigureAwait(false);
                return;
            }
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
            // "Installing" is pure progress chatter: Quiet mode leaves it to the tray window and the icon badge.
            if (settings.NotificationsEnabled && PolicyEngine.NotificationModeFor(policy, settings) == NotificationMode.Reminders)
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

            case UpdateAgentMessage m:
                await HandleAgentUpdateRequestAsync(conn, m, ct).ConfigureAwait(false);
                break;

            case RequestScanMessage:
                _logger.LogInformation("{Client} requested a scan", conn);
                RequestScan();
                await _pipe.SendAsync(conn, new AckMessage { InReplyTo = message.MessageId, Message = "Scan queued" }, ct).ConfigureAwait(false);
                break;

            case InstallNowMessage m:
                // CloseBlockingProcesses means the user pressed "Close apps and update": the tray has already closed
                // and killed everything in its own session, so what is left needs SYSTEM rights and is ours to end.
                await HandleUserActionAsync(conn, m.CloseBlockingProcesses ? "close apps and update" : "install now", m.UpdateKey, m.MessageId, u =>
                {
                    PolicyEngine.RequestInstall(u);
                    if (m.CloseBlockingProcesses) PolicyEngine.RequestForcedClose(u, DateTimeOffset.UtcNow);
                    return null;
                }, ct).ConfigureAwait(false);
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
        var cloud = CloudStatusSource?.Invoke();
        var enabled = s.Apps.Where(a => a.Enabled).ToList();
        // The configuration is fleet-wide; the tray's list is about the device in front of this user - machine-wide
        // installs count for everyone, per-user installs only for the user on the other end of this connection. The
        // admin console is the opposite: it is looking at the policy, so it gets everything that is enabled.
        var monitored = conn.Kind == IpcClientKind.Admin
            ? MonitoredAppFilter.All(enabled)
            : MonitoredAppFilter.Relevant(enabled, _state.AppPresence.Values, conn.UserSid);
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
                NotificationMode = s.NotificationMode,
                WingetEnabled = s.WingetEnabled,
                WebSourcesEnabled = s.WebSourcesEnabled,
                NotificationsEnabled = s.NotificationsEnabled,
                LogDirectory = s.LogDirectory,
                LogLevel = s.LogLevel,
                MonitoredAppCount = monitored.Count,
                MonitoredApps = monitored,
                ConfiguredAppCount = enabled.Count,
                LoadedAtUtc = s.LoadedAtUtc,
                Prerequisites = _prerequisiteStatus?.Clone(),
                CloudConfigured = !string.IsNullOrWhiteSpace(s.CloudServerUrl),
                CloudEnrolled = cloud?.DeviceId is not null,
                OrganizationName = string.IsNullOrWhiteSpace(cloud?.OrganizationName) ? null : cloud!.OrganizationName,
            },
            AgentUpdate = AgentUpdateState(),
        };
    }

    /// <summary>
    /// The self-updater's last outcome for the state snapshot, or null when this process has no updater (the CLI
    /// entry points). An install this coordinator has accepted but not started yet counts as "in progress", so the
    /// tray's banner does not blink between the request and the download. A check does not: nothing is replaced.
    /// </summary>
    private AgentUpdateStatus? AgentUpdateState()
    {
        var status = SelfUpdater?.Status;
        if (status is not null && _agentUpdateInstallRunning == 1) status.InProgress = true;
        return status;
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

    /// <summary>
    /// Supplies the current cloud status for the state snapshot sent to tray agents. Set by the cloud sync service,
    /// which depends on this coordinator (so the coordinator cannot depend on it). Null when the service is not
    /// running with cloud support, e.g. in the CLI modes.
    /// </summary>
    public Func<Arkimentum.AppMonitor.Cloud.CloudStatus?>? CloudStatusSource { get; set; }

    /// <summary>
    /// Pulls the organization configuration before a scan a user asked for, so "Check now" in the tray sees what an
    /// administrator has just published instead of waiting for the next sync. Set by the cloud sync service for the
    /// same reason as <see cref="CloudStatusSource"/>; null without cloud support. A failure is logged and the scan
    /// proceeds with the cached configuration.
    /// </summary>
    public Func<CancellationToken, Task>? ConfigRefresh { get; set; }

    // =====================================================================================================================
    // Agent self-update on request (tray "Check for updates"/"Update now", admin console "Update agent")
    // =====================================================================================================================

    /// <summary>
    /// The self-updater, handed over by the cloud sync service for the same reason as <see cref="CloudStatusSource"/>:
    /// the updater takes this coordinator in its constructor, so it cannot be injected here. Null in the CLI entry
    /// points, where a client-initiated update is refused.
    /// </summary>
    public IAgentSelfUpdate? SelfUpdater { get; set; }

    /// <summary>1 while a client-initiated check or update is running; a second request is refused rather than queued.</summary>
    private int _agentUpdateRequestRunning;

    /// <summary>1 from the moment a client-initiated install is accepted until it has finished (or failed).</summary>
    private int _agentUpdateInstallRunning;

    /// <summary>
    /// Why a client-initiated agent update is refused, or null when it may run. Pure, so the rules are testable and
    /// the tray and the admin console get exactly the same answer. The agent replaces its own binaries as SYSTEM, so
    /// an administrator who turned <c>AgentAutoUpdate</c> off (Intune or an RMM owns the binaries) must not be
    /// overruled from a client, and nothing may run while an application install or another agent update is going on.
    /// </summary>
    public static string? RefuseAgentUpdate(bool updaterAvailable, bool autoUpdateEnabled, bool applicationInstallRunning, bool agentUpdateRunning) =>
        !updaterAvailable ? "Agent updates are not available in this mode"
        : !autoUpdateEnabled ? "Agent updates are disabled by policy"
        : applicationInstallRunning ? "An application is being updated; try the agent update again when it has finished"
        : agentUpdateRunning ? "An agent update is already running"
        : null;

    /// <summary>The acknowledgement a client gets for an outcome: what happened, in one line the UI can show as it is.</summary>
    public static (bool Ok, string Message) DescribeAgentUpdate(AgentUpdateOutcome outcome, bool checkOnly)
    {
        var offered = outcome.Manifest?.Version ?? "?";
        return outcome.Action switch
        {
            AgentUpdateAction.UpToDate => (true, $"Up to date: {ServiceVersion}"),
            AgentUpdateAction.UpdateAvailable => checkOnly
                ? (true, $"Update {offered} available")
                : (true, $"Updating to {offered} - the agent will restart"),
            AgentUpdateAction.Launched => (true, $"Updating to {offered} - the agent will restart"),
            AgentUpdateAction.WouldLaunch => (true, $"Verified {offered}; the installer is not started in testing mode"),
            // Pinned or on another channel is a deliberate configuration, not a failure: say so without an error.
            AgentUpdateAction.PinnedByTargetVersion or AgentUpdateAction.ChannelMismatch => (true, outcome.Reason),
            AgentUpdateAction.NoManifest => (false, "Check failed: " + outcome.Reason),
            AgentUpdateAction.Failed => (false, "Update failed: " + outcome.Reason),
            _ => (false, outcome.Reason),
        };
    }

    /// <summary>
    /// Handles <see cref="UpdateAgentMessage"/>: refuses it outright, or runs the check (and the update) on a
    /// background task so the pipe's read loop stays free while the feed is read and the package downloaded.
    /// </summary>
    private async Task HandleAgentUpdateRequestAsync(PipeClientConnection conn, UpdateAgentMessage message, CancellationToken ct)
    {
        var updater = SelfUpdater;
        var refusal = RefuseAgentUpdate(updater is not null, _settings.Current.AgentAutoUpdate, InstallInProgress,
            _agentUpdateRequestRunning == 1 || updater?.Status.InProgress == true);
        _logger.LogInformation("{Client} asked the agent to {Kind}{Refusal}", conn,
            message.CheckOnly ? "check for a newer release" : "update itself",
            refusal is null ? "" : $"; refused: {refusal}");
        if (refusal is not null || updater is null)
        {
            await _pipe.SendAsync(conn, new AckMessage { InReplyTo = message.MessageId, Ok = false, Message = refusal }, ct).ConfigureAwait(false);
            return;
        }

        if (Interlocked.Exchange(ref _agentUpdateRequestRunning, 1) == 1)
        {
            await _pipe.SendAsync(conn, new AckMessage { InReplyTo = message.MessageId, Ok = false, Message = "An agent update is already running" }, ct).ConfigureAwait(false);
            return;
        }
        _ = Task.Run(() => RunAgentUpdateRequestAsync(conn, message, updater, conn.ToString()), CancellationToken.None);
    }

    private async Task RunAgentUpdateRequestAsync(PipeClientConnection conn, UpdateAgentMessage message, IAgentSelfUpdate updater, string requestedBy)
    {
        var reason = $"requested by {requestedBy}";
        try
        {
            var outcome = await updater.CheckAsync(reason, null, CancellationToken.None).ConfigureAwait(false);
            var installing = !message.CheckOnly && outcome.Action == AgentUpdateAction.UpdateAvailable;
            if (installing) Interlocked.Exchange(ref _agentUpdateInstallRunning, 1);

            var (ok, text) = DescribeAgentUpdate(outcome, message.CheckOnly);
            await _pipe.SendAsync(conn, new AckMessage { InReplyTo = message.MessageId, Ok = ok, Message = text }, CancellationToken.None).ConfigureAwait(false);
            // Every tray should see the result of the check, not just the one that asked for it.
            await BroadcastStateAsync(CancellationToken.None).ConfigureAwait(false);

            if (!installing) return;
            var applied = await updater.UpdateAsync(reason, null, CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation("Agent update {Reason}: {Outcome}", reason, applied);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The agent update {Reason} failed", reason);
            await _pipe.SendAsync(conn, new AckMessage { InReplyTo = message.MessageId, Ok = false, Message = "Check failed: " + ex.Message }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _agentUpdateRequestRunning, 0);
            Interlocked.Exchange(ref _agentUpdateInstallRunning, 0);
            // A launched update leaves InProgress up (the updater keeps it up until the service restarts); a failed
            // one clears it here.
            await BroadcastStateAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

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
