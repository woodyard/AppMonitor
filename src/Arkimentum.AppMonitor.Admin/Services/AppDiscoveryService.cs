using System.Linq;
using System.Threading;
using Arkimentum.AppMonitor.Inventory;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Admin.Services;

/// <summary>Lists what is installed on this machine so the administrator can pick applications to monitor.</summary>
public interface IAppDiscoveryService
{
    /// <summary>
    /// Runs the discovery on a background thread. Progress lines are reported on the calling (UI) context;
    /// winget is queried only when the effective configuration has winget enabled, and takes a few seconds.
    /// </summary>
    Task<IReadOnlyList<DiscoveredApp>> DiscoverAsync(IProgress<string>? progress, CancellationToken ct);
}

/// <inheritdoc />
public sealed class AppDiscoveryService : IAppDiscoveryService
{
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<AppDiscoveryService> _log;
    private readonly SettingsStoreService _settings;
    private readonly CatalogService _catalog;

    public AppDiscoveryService(ILoggerFactory loggers, ILogger<AppDiscoveryService> log, SettingsStoreService settings, CatalogService catalog)
    {
        _loggers = loggers;
        _log = log;
        _settings = settings;
        _catalog = catalog;
    }

    public async Task<IReadOnlyList<DiscoveredApp>> DiscoverAsync(IProgress<string>? progress, CancellationToken ct)
    {
        // The effective configuration decides whether winget is consulted and where winget.exe lives; the
        // already-configured applications come from the same merged view the service reads.
        var effective = _settings.ReadEffective(_loggers, _catalog);
        var catalog = _catalog.Entries;
        _log.LogInformation("Discovery starting: {Configured} configured application(s), {Catalog} catalog entr{Plural}, winget={Winget}.",
            effective.Apps.Count, catalog.Count, catalog.Count == 1 ? "y" : "ies", effective.WingetEnabled);

        var discovery = new InstalledAppDiscovery(_loggers.CreateLogger<InstalledAppDiscovery>(),
            new InstalledAppScanner(_loggers.CreateLogger<InstalledAppScanner>()));

        var found = await Task.Run(() => discovery.DiscoverAsync(
            effective.Apps,
            catalog,
            string.IsNullOrWhiteSpace(effective.WingetPath) ? null : effective.WingetPath,
            effective.WingetEnabled,
            progress,
            ct), ct).ConfigureAwait(true);

        _log.LogInformation("Discovery finished: {Total} application(s), {QuickAdd} addable, {Configured} already monitored.",
            found.Count, found.Count(a => a.CanQuickAdd), found.Count(a => a.IsConfigured));
        return found;
    }
}
