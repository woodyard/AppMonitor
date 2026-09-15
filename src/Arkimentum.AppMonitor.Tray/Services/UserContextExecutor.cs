using System.Collections.Generic;
using System.Linq;
using System.Threading;
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

    private readonly ILogger<UserContextExecutor> _log;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IpcClientService _ipc;
    private readonly AgentStateStore _store;
    private readonly SemaphoreSlim _installGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    private RunUserScanMessage? _pendingScan;
    private bool _scanRunning;

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
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _ipc.MessageReceived -= OnMessage;
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
            case AckMessage ack:
                _log.LogDebug("Ack for {InReplyTo}: ok={Ok} {Message}", ack.InReplyTo, ack.Ok, ack.Message);
                break;
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

        var options = new ProviderOptions
        {
            WingetEnabled = true,
            WebSourcesEnabled = true,
            DownloadDirectory = AppInfo.DownloadDirectory,
            InstallTimeout = TimeSpan.FromMinutes(Math.Max(1, message.TimeoutMinutes)),
        };
        AppInfo.EnsureDirectories();

        var context = CurrentContext();
        var policy = ToPolicy(update);
        var progress = CreateProgress(key);

        SetStatus(key, Strings.StateInstalling);
        return await Task.Run(async () =>
        {
            var providers = ProviderFactory.Create(_loggerFactory, options);
            var checker = new UpdateChecker(_loggerFactory.CreateLogger<UpdateChecker>(), providers);
            return await checker.InstallAsync(policy, update, context, progress, token).ConfigureAwait(false);
        }, token).ConfigureAwait(true);
    }

    /// <summary>Progress lines go to the service at most once every two seconds, and straight into the UI.</summary>
    private IProgress<string> CreateProgress(string key)
    {
        var lastSentUtc = DateTimeOffset.MinValue;
        return new Progress<string>(line =>
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            SetStatus(key, line);
            var now = DateTimeOffset.UtcNow;
            if (now - lastSentUtc < ProgressInterval) return;
            lastSentUtc = now;
            _ = _ipc.SendAsync(new UserInstallProgressMessage { UpdateKey = key, Status = line });
        });
    }

    private void SetStatus(string key, string status)
    {
        _store.SetLocalStatus(key, status);
        _log.LogDebug("Install progress for {Key}: {Status}", key, status);
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
        DownloadUrl = update.DownloadUrl,
        InstallerArgs = update.InstallerArgs,
        InstallerType = update.InstallerType,
        Sha256 = update.Sha256,
        ProcessNames = update.ProcessNames.ToList(),
    };
}
