using Arkimentum.AppMonitor.Api.Contracts;

namespace Arkimentum.AppMonitor.Web.Editing;

/// <summary>The columns of the Inventory page that can be sorted by clicking their header.</summary>
public enum InventorySortKey
{
    Name,
    WingetId,
    Devices,
    Updatable,
    LastSeen,
}

/// <summary>
/// Sorting for the Inventory table. Plain C#, so the order rules are testable: names compare case-insensitively in
/// the current culture, a missing winget id sorts last whichever direction is chosen, numbers and dates compare
/// naturally, and ties fall back to the name so the order is stable when a column has many equal values.
/// </summary>
public static class InventorySort
{
    public static List<OrganizationInventoryItem> Apply(IEnumerable<OrganizationInventoryItem> items, InventorySortKey key, bool descending)
    {
        var byName = StringComparer.CurrentCultureIgnoreCase;
        IOrderedEnumerable<OrganizationInventoryItem> ordered = key switch
        {
            InventorySortKey.WingetId => descending
                // "No winget package" rows stay at the bottom in both directions: they are the least useful to look at.
                ? items.OrderBy(i => string.IsNullOrWhiteSpace(i.WingetId)).ThenByDescending(i => i.WingetId, StringComparer.OrdinalIgnoreCase)
                : items.OrderBy(i => string.IsNullOrWhiteSpace(i.WingetId)).ThenBy(i => i.WingetId, StringComparer.OrdinalIgnoreCase),
            InventorySortKey.Devices => descending ? items.OrderByDescending(i => i.DeviceCount) : items.OrderBy(i => i.DeviceCount),
            InventorySortKey.Updatable => descending ? items.OrderByDescending(i => i.DevicesWithUpdateAvailable) : items.OrderBy(i => i.DevicesWithUpdateAvailable),
            InventorySortKey.LastSeen => descending ? items.OrderByDescending(i => i.LastSeenUtc) : items.OrderBy(i => i.LastSeenUtc),
            _ => descending ? items.OrderByDescending(i => i.DisplayName, byName) : items.OrderBy(i => i.DisplayName, byName),
        };
        return key == InventorySortKey.Name ? ordered.ToList() : ordered.ThenBy(i => i.DisplayName, byName).ToList();
    }

    /// <summary>
    /// The direction a first click on a column should use: text columns ascending, counts and dates descending, since
    /// "most devices", "most updatable" and "seen most recently" are what one looks for.
    /// </summary>
    public static bool DefaultDescending(InventorySortKey key) => key is InventorySortKey.Devices or InventorySortKey.Updatable or InventorySortKey.LastSeen;
}
