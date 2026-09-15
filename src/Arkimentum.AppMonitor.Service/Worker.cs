using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Service;

/// <summary>Timer loop: startup delay, periodic scans, policy ticks, tray agent supervision, on-demand scans.</summary>
public sealed class Worker : BackgroundService
{
    private static readonly TimeSpan LoopInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TraySupervisionInterval = TimeSpan.FromSeconds(30);

    private readonly ILogger<Worker> _logger;
    private readonly UpdateCoordinator _coordinator;
    private readonly SettingsProvider _settings;
    private readonly WorkerOptions _options;
    private readonly IHostApplicationLifetime _lifetime;

    public Worker(ILogger<Worker> logger, UpdateCoordinator coordinator, SettingsProvider settings, WorkerOptions options, IHostApplicationLifetime lifetime)
    {
        _logger = logger;
        _coordinator = coordinator;
        _settings = settings;
        _options = options;
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _coordinator.Initialize();
            var settings = _settings.Current;

            if (_options.ScanOnce)
            {
                await _coordinator.RunScanAsync("command line", stoppingToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
                _lifetime.StopApplication();
                return;
            }

            var delay = TimeSpan.FromSeconds(_options.IgnoreStartupDelay ? 3 : settings.StartupDelaySeconds);
            var persistedNext = _coordinator.NextScanUtc;
            var nextScan = settings.ScanOnStartup || persistedNext is null || persistedNext <= DateTimeOffset.UtcNow
                ? DateTimeOffset.UtcNow + delay
                : persistedNext.Value;
            _coordinator.NextScanUtc = nextScan;
            _logger.LogInformation("First scan at {Time} (startup delay {Delay})", nextScan.ToLocalTime().ToString("g"), delay);

            var lastTray = DateTimeOffset.MinValue;
            var lastPolicy = DateTimeOffset.MinValue;
            var lastPrerequisites = DateTimeOffset.MinValue;

            // Prerequisites (winget) are checked once before the first scan so a fresh image gets winget without any user action.
            if (settings.WingetEnabled)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds, 10)), stoppingToken).ConfigureAwait(false);
                using var prereqTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                prereqTimeout.CancelAfter(TimeSpan.FromMinutes(25));
                try { await _coordinator.EnsurePrerequisitesAsync("service start", force: false, prereqTimeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { _logger.LogWarning("Prerequisite check timed out"); }
                lastPrerequisites = DateTimeOffset.UtcNow;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(LoopInterval, stoppingToken).ConfigureAwait(false);
                var now = DateTimeOffset.UtcNow;
                try
                {
                    var prereqInterval = TimeSpan.FromHours(Math.Max(1, _settings.Current.PrerequisiteCheckIntervalHours));
                    if (_settings.Current.WingetEnabled && now - lastPrerequisites >= prereqInterval)
                    {
                        lastPrerequisites = now;
                        _ = Task.Run(() => _coordinator.EnsurePrerequisitesAsync("scheduled", force: false, stoppingToken), stoppingToken);
                    }

                    if (now - lastTray >= TraySupervisionInterval)
                    {
                        lastTray = now;
                        _coordinator.EnsureTrayAgents();
                    }

                    if (_coordinator.ScanRequested || (_coordinator.NextScanUtc is { } due && now >= due))
                    {
                        var reason = _coordinator.ScanRequested ? "requested" : "scheduled";
                        await _coordinator.RunScanAsync(reason, stoppingToken).ConfigureAwait(false);
                        if (_coordinator.NextScanUtc is null || _coordinator.NextScanUtc <= DateTimeOffset.UtcNow)
                            _coordinator.NextScanUtc = DateTimeOffset.UtcNow + _settings.Current.ScanInterval;
                        lastPolicy = DateTimeOffset.UtcNow;
                        continue;
                    }

                    if (now - lastPolicy >= _settings.Current.PolicyTick)
                    {
                        lastPolicy = now;
                        _settings.Reload();
                        await _coordinator.EvaluatePoliciesAsync(stoppingToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Worker loop iteration failed");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Worker crashed");
            throw;
        }
        finally
        {
            _logger.LogInformation("Service stopping");
        }
    }
}

public sealed class WorkerOptions
{
    public bool ScanOnce { get; set; }
    public bool IgnoreStartupDelay { get; set; }
}
