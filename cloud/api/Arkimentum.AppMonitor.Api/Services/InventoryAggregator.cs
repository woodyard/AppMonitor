using Arkimentum.AppMonitor.Api.Contracts;

namespace Arkimentum.AppMonitor.Api.Services;

/// <summary>One DeviceApps row joined with its device's LastSeenUtc - the input of the inventory roll-up.</summary>
public sealed record InventoryRow(
    Guid DeviceId,
    string DisplayName,
    string? Version,
    string? Publisher,
    string? WingetId,
    string? AvailableVersion,
    InstallContext Context,
    string? CatalogAppId,
    string? MonitoredAppId,
    DateTimeOffset? LastSeenUtc);

/// <summary>
/// Rolls the per-device application rows up into one row per application for the whole organization, and decides
/// which of them the organization already monitors. Kept free of EF and HTTP so it can be tested directly.
/// </summary>
public static class InventoryAggregator
{
    private static readonly char[] WingetIdSeparators = [';', ',', '|'];

    public static List<OrganizationInventoryItem> Build(
        IEnumerable<InventoryRow> rows,
        SettingsDocument? organizationConfig,
        string? search = null,
        bool onlyUnmonitored = false)
    {
        var monitored = BuildMonitoredLookup(organizationConfig);
        var items = new List<OrganizationInventoryItem>();

        foreach (var group in rows.GroupBy(r => Key(r), StringComparer.OrdinalIgnoreCase))
        {
            var all = group.ToList();

            // One device may report the same application twice (machine-wide and per user); count devices, not rows.
            var deviceCount = all.Select(r => r.DeviceId).Distinct().Count();
            var withUpdate = all.Where(HasUpdateAvailable).Select(r => r.DeviceId).Distinct().Count();

            var versions = all
                .Where(r => !string.IsNullOrWhiteSpace(r.Version))
                .GroupBy(r => r.Version!.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => new VersionCount { Version = g.Key, DeviceCount = g.Select(r => r.DeviceId).Distinct().Count() })
                .OrderByDescending(v => v.Version, VersionComparer.Instance)
                .ToList();

            var predominantContext = all
                .GroupBy(r => r.Context)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => (int)g.Key)
                .Select(g => g.Key)
                .FirstOrDefault();

            var displayName = all
                .GroupBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Key)
                .First();

            var wingetId = FirstNonEmpty(all.Select(r => r.WingetId));
            var catalogAppId = FirstNonEmpty(all.Select(r => r.CatalogAppId));
            var reportedMonitoredAppId = FirstNonEmpty(all.Select(r => r.MonitoredAppId));

            items.Add(new OrganizationInventoryItem
            {
                DisplayName = displayName,
                WingetId = wingetId,
                Publisher = FirstNonEmpty(all.Select(r => r.Publisher)),
                CatalogAppId = catalogAppId,
                MonitoredAppId = ResolveMonitoredAppId(monitored, wingetId, catalogAppId, reportedMonitoredAppId),
                DeviceCount = deviceCount,
                DevicesWithUpdateAvailable = withUpdate,
                Versions = versions,
                PredominantContext = predominantContext,
                LastSeenUtc = all.Max(r => r.LastSeenUtc) ?? default,
            });
        }

        IEnumerable<OrganizationInventoryItem> result = items;
        if (!string.IsNullOrWhiteSpace(search))
        {
            var needle = search.Trim();
            result = result.Where(i =>
                Contains(i.DisplayName, needle) || Contains(i.WingetId, needle) ||
                Contains(i.Publisher, needle) || Contains(i.CatalogAppId, needle));
        }
        if (onlyUnmonitored) result = result.Where(i => string.IsNullOrEmpty(i.MonitoredAppId));

        return result
            .OrderByDescending(i => i.DeviceCount)
            .ThenBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Maps every identifier an organization's configured application answers to onto its AppId.</summary>
    public static Dictionary<string, string> BuildMonitoredLookup(SettingsDocument? config)
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (config is null) return lookup;

        foreach (var (appId, values) in config.Apps)
        {
            if (string.IsNullOrWhiteSpace(appId)) continue;
            Add(lookup, appId, appId);

            if (values.TryGetValue("WingetId", out var wingetId))
                foreach (var alternative in (wingetId.AsString() ?? string.Empty).Split(WingetIdSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    Add(lookup, alternative, appId);
        }
        return lookup;
    }

    private static string? ResolveMonitoredAppId(Dictionary<string, string> lookup, string? wingetId, string? catalogAppId, string? reported)
    {
        foreach (var candidate in new[] { wingetId, catalogAppId, reported })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            foreach (var part in candidate.Split(WingetIdSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (lookup.TryGetValue(part, out var appId)) return appId;
        }
        return null;
    }

    private static bool HasUpdateAvailable(InventoryRow row) =>
        !string.IsNullOrWhiteSpace(row.AvailableVersion) && VersionComparer.IsNewer(row.AvailableVersion, row.Version);

    private static string Key(InventoryRow row) =>
        string.IsNullOrWhiteSpace(row.WingetId) ? row.DisplayName.Trim() : row.WingetId.Trim();

    private static string? FirstNonEmpty(IEnumerable<string?> values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static void Add(Dictionary<string, string> lookup, string key, string appId)
    {
        if (!string.IsNullOrWhiteSpace(key)) lookup.TryAdd(key.Trim(), appId);
    }

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
