using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Arkimentum.AppMonitor.Install;
using Arkimentum.AppMonitor.Inventory;
using Arkimentum.AppMonitor.Ipc;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Native;
using Arkimentum.AppMonitor.Providers;
using Arkimentum.AppMonitor.Tray.Infrastructure;
using Arkimentum.AppMonitor.Tray.Resources;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Tray.Services;

/// <summary>
/// Runs the work that can only happen inside the user's session: per-user inventory scans and per-user installs.
/// Installs never overlap (they queue); scans never overlap (the newest request wins).
/// </summary>
public sealed class UserContextExecutor : IHostedService
{
    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(2);
    /// <summary>winget can take a while on a cold source cache; the service gives up long before this.</summary>
    private static readonly TimeSpan PackageListTimeout = TimeSpan.FromMinutes(3);
    /// <summary>
    /// How long the tray waits for the service to accept a hand-over (<see cref="RequestSystemInstallMessage"/>). An
    /// older service does not know the message and never answers, so this is what keeps the install from waiting for
    /// the whole install timeout in that case.
    /// </summary>
    private static readonly TimeSpan HandOverAcceptTimeout = TimeSpan.FromSeconds(60);
    /// <summary>
    /// On top of the install timeout, how long an accepted hand-over may take: the service checks the package
    /// registrations before and after its install (up to a minute each).
    /// </summary>
    private static readonly TimeSpan HandOverSlack = TimeSpan.FromMinutes(5);

    private readonly ILogger<UserContextExecutor> _log;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IpcClientService _ipc;
    private readonly AgentStateStore _store;
    private readonly SemaphoreSlim _installGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    private RunUserScanMessage? _pendingScan;
    private bool _scanRunning;

    /// <summary>A hand-over waiting for the service: its acknowledgement, then its result (null: disconnected).</summary>
    private sealed class HandOverWait
    {
        public TaskCompletionSource<bool> Accepted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<SystemInstallResultMessage?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Hand-overs to the service in flight, by the request's message id.</summary>
    private readonly ConcurrentDictionary<string, HandOverWait> _pendingHandOvers = new();

    public UserContextExecutor(
        ILogger<UserContextExecutor> log,
        ILoggerFactory loggerFactory,
        IpcClientService ipc,
        AgentStateStore store)
    {
        _log = log;
        _loggerFactory = loggerFactory;
        _ipc = ipc;
        _store = store;
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
        FailPendingHandOvers();
        _cts.Cancel();
        _cts.Dispose();
        _installGate.Dispose();
        return Task.CompletedTask;
    }

    private void OnMessage(IpcMessage message)
    {
        switch (message)
        {
            case RunUserInstallMessage install:
                _ = RunInstallAsync(install);
                break;
            case RunUserScanMessage scan:
                EnqueueScan(scan);
                break;
            case RunUserPackageListMessage list:
                _ = RunPackageListAsync(list);
                break;
            case SystemInstallResultMessage result:
                if (result.InReplyTo is not null && _pendingHandOvers.TryGetValue(result.InReplyTo, out var answered))
                {
                    answered.Accepted.TrySetResult(true);
                    answered.Result.TrySetResult(result);
                }
                else _log.LogWarning("Ignoring a late or unexpected install-for-all-users result for {Key}", result.UpdateKey);
                break;
            case AckMessage ack:
                if (ack.InReplyTo is not null && _pendingHandOvers.TryGetValue(ack.InReplyTo, out var accepted))
                {
                    _log.LogInformation("The service {Answer} the install for all users: {Message}", ack.Ok ? "accepted" : "rejected", ack.Message);
                    accepted.Accepted.TrySetResult(ack.Ok);
                    if (!ack.Ok) accepted.Result.TrySetResult(new SystemInstallResultMessage { InReplyTo = ack.InReplyTo, Ok = false, Message = ack.Message });
                    break;
                }
                _log.LogDebug("Ack for {InReplyTo}: ok={Ok} {Message}", ack.InReplyTo, ack.Ok, ack.Message);
                break;
        }
    }

    /// <summary>A dropped pipe ends every hand-over wait: the service can no longer answer on this connection.</summary>
    private void OnConnectionChanged(bool connected)
    {
        if (!connected) FailPendingHandOvers();
    }

    private void FailPendingHandOvers()
    {
        foreach (var wait in _pendingHandOvers.Values)
        {
            wait.Accepted.TrySetResult(false);
            wait.Result.TrySetResult(null);
        }
    }

    // ---------------------------------------------------------------- installs

    private async Task RunInstallAsync(RunUserInstallMessage message)
    {
        var update = message.Update;
        var key = update.Key;
        var queued = _installGate.CurrentCount == 0;
        _log.LogInformation("User-context install requested for {App} ({Key}); queued={Queued}",
            update.DisplayName, key, queued);
        _store.SetLocalStatus(key, queued ? Strings.UserInstallQueued : Strings.UserInstallPreparing);

        try
        {
            await _installGate.WaitAsync(_cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            _store.SetLocalStatus(key, null);
            return;
        }

        try
        {
            _store.SetLocalStatus(key, Strings.UserInstallPreparing);
            var result = await ExecuteInstallAsync(message).ConfigureAwait(true);
            _log.LogInformation("User-context install of {App} finished: success={Success} exitCode={ExitCode} {Message}",
                update.DisplayName, result.Success, result.ExitCode, result.Message);
            await _ipc.SendAsync(new UserInstallResultMessage { UpdateKey = key, Result = result }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "User-context install of {App} failed unexpectedly", update.DisplayName);
            await _ipc.SendAsync(new UserInstallResultMessage
            {
                UpdateKey = key,
                Result = InstallResult.Fail(ex.Message),
            }).ConfigureAwait(true);
        }
        finally
        {
            _store.SetLocalStatus(key, null);
            _installGate.Release();
        }
    }

    private async Task<InstallResult> ExecuteInstallAsync(RunUserInstallMessage message)
    {
        var update = message.Update;
        var key = update.Key;
        var token = _cts.Token;

        // The service should already have had the processes closed, but never install over a running application.
        if (update.ProcessNames.Count > 0)
        {
            var running = ProcessHelper.GetRunning(update.ProcessNames, AppInfo.SessionId);
            if (running.Count > 0)
            {
                _log.LogInformation("Closing {Processes} before installing {App}", string.Join(", ", running), update.DisplayName);
                SetStatus(key, Strings.UserInstallClosingApps);
                var stillRunning = await Task.Run(
                    () => ProcessHelper.CloseAsync(running, AppInfo.SessionId, CloseWait, force: false, token), token)
                    .ConfigureAwait(true);
                if (stillRunning.Count > 0)
                {
                    var text = Strings.UserInstallProcessesStillRunning(string.Join(", ", stillRunning));
                    _log.LogWarning("Not installing {App}: {Reason}", update.DisplayName, text);
                    return InstallResult.Fail(text);
                }
            }
        }

        SetStatus(key, Strings.StateInstalling);
        using var reporter = new InstallProgressReporter(this, update);

        var options = new ProviderOptions
        {
            WingetEnabled = true,
            WebSourcesEnabled = true,
            DownloadDirectory = AppInfo.DownloadDirectory,
            InstallTimeout = TimeSpan.FromMinutes(Math.Max(1, message.TimeoutMinutes)),
            // An installed MSIX package winget offers no per-user installer for is handed to the service, which
            // installs it for all users as SYSTEM (see WingetProvider.HandOverToSystemAsync).
            SystemInstallHandOver = (request, ct) =>
            {
                // The service installs it now and broadcasts its own progress; the tray's reading would only hide that.
                reporter.HandOver();
                return RequestSystemInstallAsync(request, message.TimeoutMinutes, ct);
            },
        };
        AppInfo.EnsureDirectories();

        var context = CurrentContext();
        // The detection rule travels with the message (not the update): an install winget cannot see
        // (WingetUncorrelated) is verified against this user's Uninstall entries with it.
        var policy = ToPolicy(update) with
        {
            DetectDisplayNameRegex = string.IsNullOrWhiteSpace(message.DetectDisplayNameRegex) ? null : message.DetectDisplayNameRegex,
            DetectPublisherRegex = string.IsNullOrWhiteSpace(message.DetectPublisherRegex) ? null : message.DetectPublisherRegex,
        };
        try
        {
            return await Task.Run(async () =>
            {
                var providers = ProviderFactory.Create(_loggerFactory, options);
                var checker = new UpdateChecker(_loggerFactory.CreateLogger<UpdateChecker>(), providers);
                return await checker.InstallAsync(policy, update, context, reporter.Lines, token).ConfigureAwait(false);
            }, token).ConfigureAwait(true);
        }
        finally
        {
            // The result follows at once; the reporter is disposed with this method, before any posted snapshot runs.
            _store.SetLocalProgress(key, null);
        }
    }

    /// <summary>
    /// The progress of one user-context install. Every output line goes into an <see cref="InstallProgressTracker"/>,
    /// whose snapshots (phase, download bytes) reach the window and the service through an
    /// <see cref="InstallProgressThrottle"/>: a phase change at once, a moving byte count at most every two seconds.
    /// The line itself still travels as <see cref="UserInstallProgressMessage.Status"/> for an older service. When the
    /// tracker cannot be created the lines are shown as they come, as before.
    /// </summary>
    private sealed class InstallProgressReporter : IDisposable
    {
        private readonly UserContextExecutor _owner;
        private readonly string _key;
        private readonly InstallProgressTracker? _tracker;
        private readonly InstallProgressThrottle _throttle = new(ProgressInterval);
        private readonly DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;
        private string? _lastLine;
        private DateTimeOffset _lastLineSentUtc = DateTimeOffset.MinValue;
        private readonly SynchronizationContext _ui;
        private bool _flushScheduled;
        private bool _handedOver;
        private bool _disposed;

        /// <summary>Created on the UI thread: both progress sinks post back to it, so everything below runs there.</summary>
        public InstallProgressReporter(UserContextExecutor owner, PendingUpdate update)
        {
            _owner = owner;
            _key = update.Key;
            _ui = SynchronizationContext.Current ?? new SynchronizationContext();
            var snapshots = new Progress<InstallProgress>(OnSnapshot);
            try
            {
                // The id and version name the download folder before winget's "Found" line does, and on a Windows whose
                // winget speaks another language they are the only way the tracker finds it.
                _tracker = new InstallProgressTracker(p => ((IProgress<InstallProgress>)snapshots).Report(p),
                    owner._loggerFactory.CreateLogger<InstallProgressTracker>(),
                    wingetId: update.Source == UpdateSource.Winget ? WingetProvider.SplitIds(update.WingetId).FirstOrDefault() : null,
                    version: update.Source == UpdateSource.Winget ? update.AvailableVersion : null);
            }
            catch (Exception ex)
            {
                owner._log.LogWarning(ex, "No install progress tracking for {Key}; showing the installer's lines instead", _key);
            }
            Lines = new Progress<string>(OnLine);
            if (_tracker is not null) OnSnapshot(new InstallProgress(InstallPhase.Starting));
        }

        /// <summary>What the provider reports its output lines to.</summary>
        public IProgress<string> Lines { get; }

        private void OnLine(string line)
        {
            if (_disposed || string.IsNullOrWhiteSpace(line)) return;
            _lastLine = line;
            if (_tracker is null)
            {
                _owner.SetStatus(_key, line);
                var now = DateTimeOffset.UtcNow;
                if (now - _lastLineSentUtc < ProgressInterval) return;
                _lastLineSentUtc = now;
                _ = _owner._ipc.SendAsync(new UserInstallProgressMessage { UpdateKey = _key, Status = line });
                return;
            }
            _owner._log.LogDebug("Install progress for {Key}: {Line}", _key, line);
            try { _tracker.Report(line); }
            catch (Exception ex) { _owner._log.LogDebug(ex, "The install progress tracker could not read a line for {Key}", _key); }
        }

        private void OnSnapshot(InstallProgress progress)
        {
            if (_disposed || _handedOver) return;
            var now = DateTimeOffset.UtcNow;
            if (_throttle.Offer(progress, now)) Publish(progress);
            else ScheduleFlush(now);
        }

        /// <summary>A byte count held back by the throttle goes out once it is due, even if nothing newer comes.</summary>
        private async void ScheduleFlush(DateTimeOffset now)
        {
            if (_flushScheduled || _throttle.DueIn(now) is not { } due) return;
            _flushScheduled = true;
            try { await Task.Delay(due).ConfigureAwait(true); }
            catch (Exception) { return; }
            finally { _flushScheduled = false; }
            if (!_disposed && !_handedOver && _throttle.TakeDue(DateTimeOffset.UtcNow) is { } held) Publish(held);
        }

        private void Publish(InstallProgress progress)
        {
            _owner._store.SetLocalProgress(_key, new LocalInstallProgress(progress.Phase, progress.DownloadedBytes, progress.DownloadTotalBytes, _startedUtc));
            _owner._log.LogDebug("Install progress for {Key}: {Phase} {Downloaded}/{Total} bytes",
                _key, progress.Phase, progress.DownloadedBytes?.ToString() ?? "?", progress.DownloadTotalBytes?.ToString() ?? "?");
            _ = _owner._ipc.SendAsync(new UserInstallProgressMessage
            {
                UpdateKey = _key,
                Status = _lastLine ?? Strings.StateInstalling,
                Phase = progress.Phase,
                DownloadedBytes = progress.DownloadedBytes,
                DownloadTotalBytes = progress.DownloadTotalBytes,
            });
        }

        /// <summary>
        /// The install was handed to the service (an MSIX package for all users): from now on its broadcast says how far
        /// it is, so the tray stops reporting and drops its own reading. Called from the provider's thread.
        /// </summary>
        public void HandOver() => _ui.Post(_ =>
        {
            if (_disposed || _handedOver) return;
            _handedOver = true;
            _owner._store.SetLocalProgress(_key, null);
        }, null);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _tracker?.Dispose(); }
            catch (Exception ex) { _owner._log.LogDebug(ex, "Disposing the install progress tracker for {Key} failed", _key); }
        }
    }

    private void SetStatus(string key, string status)
    {
        _store.SetLocalStatus(key, status);
        _log.LogDebug("Install progress for {Key}: {Status}", key, status);
    }

    /// <summary>
    /// Asks the service to install a package for all users (<see cref="RequestSystemInstallMessage"/>) and waits for its
    /// answer: first the acknowledgement (at most <see cref="HandOverAcceptTimeout"/>; an older service never gives one),
    /// then the result (at most the install timeout plus <see cref="HandOverSlack"/>). Null when there is no answer -
    /// not sent, not acknowledged, timed out, or the pipe dropped - which the provider reports as a failed hand-over.
    /// </summary>
    private async Task<SystemInstallHandOverReply?> RequestSystemInstallAsync(SystemInstallHandOverRequest request, int timeoutMinutes, CancellationToken ct)
    {
        var message = new RequestSystemInstallMessage
        {
            UpdateKey = request.UpdateKey,
            WingetId = request.WingetId,
            PackageFamilyName = request.PackageFamilyName,
            UserPackageVersion = request.UserPackageVersion,
        };
        var wait = new HandOverWait();
        _pendingHandOvers[message.MessageId] = wait;
        try
        {
            _log.LogInformation("Asking the service to install {WingetId} (package family {Family}, user package {Version}) for all users for {Key}",
                request.WingetId, request.PackageFamilyName, request.UserPackageVersion ?? "unknown", request.UpdateKey);
            if (!await _ipc.SendAsync(message).ConfigureAwait(false)) return null;

            await Task.WhenAny(wait.Accepted.Task, wait.Result.Task, Task.Delay(HandOverAcceptTimeout, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!wait.Accepted.Task.IsCompleted && !wait.Result.Task.IsCompleted)
            {
                _log.LogWarning("The service did not acknowledge the install for all users of {WingetId} within {Seconds:F0}s (a service older than this agent?)",
                    request.WingetId, HandOverAcceptTimeout.TotalSeconds);
                return null;
            }

            var result = await wait.Result.Task.WaitAsync(TimeSpan.FromMinutes(Math.Max(1, timeoutMinutes)) + HandOverSlack, ct).ConfigureAwait(false);
            if (result is null)
            {
                _log.LogWarning("The connection to the service dropped while it installed {WingetId} for all users", request.WingetId);
                return null;
            }
            _log.LogInformation("The service's install for all users of {WingetId}: ok={Ok} exitCode={ExitCode} machine package {Machine}: {Message}",
                request.WingetId, result.Ok, result.ExitCode, result.MachinePackageVersion ?? "unknown", result.Message);
            return new SystemInstallHandOverReply(result.Ok, result.Message, result.ExitCode, result.MachinePackageVersion);
        }
        catch (TimeoutException)
        {
            _log.LogWarning("The service did not finish the install for all users of {WingetId} in time", request.WingetId);
            return null;
        }
        finally
        {
            _pendingHandOvers.TryRemove(message.MessageId, out _);
        }
    }

    // ---------------------------------------------------------------- scans

    private void EnqueueScan(RunUserScanMessage message)
    {
        if (_scanRunning)
        {
            _log.LogInformation("User-context scan {ScanId} queued; a scan is already running", message.ScanId);
            _pendingScan = message;
            return;
        }
        _scanRunning = true;
        _ = RunScanLoopAsync(message);
    }

    private async Task RunScanLoopAsync(RunUserScanMessage first)
    {
        var current = first;
        try
        {
            while (current is not null)
            {
                await RunScanAsync(current).ConfigureAwait(true);
                current = _pendingScan;
                _pendingScan = null;
            }
        }
        finally
        {
            _scanRunning = false;
        }
    }

    private async Task RunScanAsync(RunUserScanMessage message)
    {
        _log.LogInformation("User-context scan {ScanId} for {Count} app(s)", message.ScanId, message.Apps.Count);
        var token = _cts.Token;
        var options = new ProviderOptions
        {
            WingetEnabled = message.WingetEnabled,
            WebSourcesEnabled = message.WebSourcesEnabled,
            ProxyUrl = string.IsNullOrWhiteSpace(message.ProxyUrl) ? null : message.ProxyUrl,
            WingetGlobalArgs = string.IsNullOrWhiteSpace(message.WingetGlobalArgs) ? null : message.WingetGlobalArgs,
            WingetIncludeUnknown = message.WingetIncludeUnknown,
            DownloadDirectory = AppInfo.DownloadDirectory,
        };
        var context = CurrentContext();

        List<UpdateCheckResult> results;
        try
        {
            results = await Task.Run(async () =>
            {
                var scanner = new InstalledAppScanner(_loggerFactory.CreateLogger<InstalledAppScanner>());
                var sid = string.IsNullOrEmpty(AppInfo.UserSid) ? null : AppInfo.UserSid;
                var inventory = scanner.Scan(includeMachine: false, includeUsers: true, onlyUserSid: sid);
                var providers = ProviderFactory.Create(_loggerFactory, options);
                var checker = new UpdateChecker(_loggerFactory.CreateLogger<UpdateChecker>(), providers);
                return await checker.CheckAsync(message.Apps, inventory, context, options, token).ConfigureAwait(false);
            }, token).ConfigureAwait(true);
            _log.LogInformation("User-context scan {ScanId} produced {Count} result(s)", message.ScanId, results.Count);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "User-context scan {ScanId} failed", message.ScanId);
            results = message.Apps
                .Select(app => UpdateCheckResult.Failed(app.AppId, app.Source, ex.Message))
                .ToList();
        }

        await _ipc.SendAsync(new UserScanResultMessage { ScanId = message.ScanId, Results = results })
            .ConfigureAwait(true);
    }

    // ---------------------------------------------------------------- user-scope package list

    /// <summary>
    /// Answers a <see cref="RunUserPackageListMessage"/> with the packages winget knows about in this session. The
    /// service cannot produce this list itself: as LocalSystem its own "winget list --scope user" enumerates SYSTEM's
    /// packages, so per-user installs (GitHub Desktop, Bicep CLI, ...) would show up in the inventory without a
    /// package id. Failures are reported, never thrown: the service treats a missing answer as "no rows".
    /// </summary>
    private async Task RunPackageListAsync(RunUserPackageListMessage message)
    {
        var rows = new List<UserPackageRow>();
        string? error = null;
        try
        {
            var winget = WingetLocator.Find(_log, isSystem: false, string.IsNullOrWhiteSpace(message.WingetPath) ? null : message.WingetPath);
            if (winget is null)
            {
                error = "winget.exe was not found in this user's session";
                _log.LogWarning("User-scope package list {ListId}: {Error}", message.ListId, error);
            }
            else
            {
                var run = await Task.Run(() => ProcessRunner.RunAsync(_log, winget,
                    "list --scope user --accept-source-agreements --disable-interactivity",
                    PackageListTimeout, environment: ProcessRunner.ChildEnvironment(CurrentContext()), ct: _cts.Token), _cts.Token).ConfigureAwait(true);
                if (run.ExitCode != 0 && !WingetOutputParser.IsNotInstalledOutput(run.CombinedOutput))
                {
                    error = $"winget list --scope user exited with {run.ExitCode}: {run.LastLines(2)}";
                    _log.LogWarning("User-scope package list {ListId}: {Error}", message.ListId, error);
                }
                else
                {
                    rows = WingetOutputParser.ParseListOutput(run.StandardOutput)
                        .Where(r => !string.IsNullOrWhiteSpace(r.Id))
                        .Select(r => new UserPackageRow
                        {
                            Name = r.Name,
                            Id = r.Id,
                            Version = r.Version,
                            Available = r.Available,
                            Source = r.Source,
                            IsTruncated = r.IsTruncated,
                        })
                        .ToList();
                    _log.LogInformation("User-scope package list {ListId}: {Count} package(s)", message.ListId, rows.Count);
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _log.LogError(ex, "User-scope package list {ListId} failed", message.ListId);
        }

        await _ipc.SendAsync(new UserPackageListResultMessage { ListId = message.ListId, Rows = rows, Error = error })
            .ConfigureAwait(true);
    }

    // ---------------------------------------------------------------- helpers

    private static ExecutionContextInfo CurrentContext() => new()
    {
        IsSystem = false,
        UserSid = string.IsNullOrEmpty(AppInfo.UserSid) ? null : AppInfo.UserSid,
        SessionId = AppInfo.SessionId,
    };

    /// <summary>Rebuilds the installer plan the service put into the pending update.</summary>
    private static AppPolicy ToPolicy(PendingUpdate update) => new()
    {
        AppId = update.AppId,
        DisplayName = update.DisplayName,
        Source = update.Source,
        Context = InstallContext.User,
        WingetId = update.WingetId,
        WingetSourceName = string.IsNullOrWhiteSpace(update.WingetSourceName) ? "winget" : update.WingetSourceName!,
        WingetExtraArgs = update.WingetExtraArgs,
        WingetReplaceOnMismatch = update.WingetReplaceOnMismatch,
        DownloadUrl = update.DownloadUrl,
        InstallerArgs = update.InstallerArgs,
        InstallerType = update.InstallerType,
        Sha256 = update.Sha256,
        ProcessNames = update.ProcessNames.ToList(),
    };
}
