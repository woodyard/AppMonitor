using Arkimentum.AppMonitor.Prerequisites;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Tray.Services;

/// <summary>
/// The service provisions the App Installer (winget) package for all users, but a package provisioned while a user is
/// logged on only becomes available to that user at the next logon. This background task registers it for the current
/// user right away (no administrative rights needed) and retries hourly while the winget alias is missing.
/// </summary>
public sealed class WingetUserRegistrationService : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromHours(1);

    private readonly ILogger<WingetUserRegistrationService> _logger;

    public WingetUserRegistrationService(ILogger<WingetUserRegistrationService> logger) => _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                bool ok;
                try { ok = await PrerequisiteManager.RegisterForCurrentUserAsync(_logger, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex) { _logger.LogWarning(ex, "winget user registration failed"); ok = false; }

                if (ok) { _logger.LogDebug("winget is available for this user"); return; }
                _logger.LogInformation("winget is not available for this user yet; retrying in {Interval}", RetryInterval);
                await Task.Delay(RetryInterval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }
}
