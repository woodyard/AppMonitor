using Arkimentum.AppMonitor.Api.Contracts;
using Arkimentum.AppMonitor.Web.Editing;
using Xunit;

namespace Arkimentum.AppMonitor.Web.Tests;

public sealed class InventorySortTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);

    private static readonly List<OrganizationInventoryItem> Items =
    [
        new() { DisplayName = "zoom", WingetId = "Zoom.Zoom", DeviceCount = 3, DevicesWithUpdateAvailable = 1, LastSeenUtc = T0.AddHours(-1) },
        new() { DisplayName = "Git", WingetId = "Git.Git", DeviceCount = 2, DevicesWithUpdateAvailable = 0, LastSeenUtc = T0 },
        new() { DisplayName = "IntunewinBuilder", WingetId = null, DeviceCount = 1, DevicesWithUpdateAvailable = 0, LastSeenUtc = T0.AddDays(-3) },
        new() { DisplayName = "7-Zip", WingetId = "7zip.7zip", DeviceCount = 3, DevicesWithUpdateAvailable = 2, LastSeenUtc = T0.AddMinutes(-5) },
    ];

    private static string[] Names(IEnumerable<OrganizationInventoryItem> items) => items.Select(i => i.DisplayName).ToArray();

    [Fact]
    public void Name_sorts_case_insensitively_in_both_directions()
    {
        Assert.Equal(["7-Zip", "Git", "IntunewinBuilder", "zoom"], Names(InventorySort.Apply(Items, InventorySortKey.Name, false)));
        Assert.Equal(["zoom", "IntunewinBuilder", "Git", "7-Zip"], Names(InventorySort.Apply(Items, InventorySortKey.Name, true)));
    }

    [Fact]
    public void Missing_winget_ids_sort_last_in_both_directions()
    {
        Assert.Equal(["7-Zip", "Git", "zoom", "IntunewinBuilder"], Names(InventorySort.Apply(Items, InventorySortKey.WingetId, false)));
        Assert.Equal(["zoom", "Git", "7-Zip", "IntunewinBuilder"], Names(InventorySort.Apply(Items, InventorySortKey.WingetId, true)));
    }

    [Fact]
    public void Counts_and_dates_sort_numerically_with_the_name_as_tie_break()
    {
        // 7-Zip and zoom both have 3 devices: the name decides between them.
        Assert.Equal(["7-Zip", "zoom", "Git", "IntunewinBuilder"], Names(InventorySort.Apply(Items, InventorySortKey.Devices, true)));
        Assert.Equal(["7-Zip", "zoom", "Git", "IntunewinBuilder"], Names(InventorySort.Apply(Items, InventorySortKey.Updatable, true)));
        Assert.Equal(["Git", "7-Zip", "zoom", "IntunewinBuilder"], Names(InventorySort.Apply(Items, InventorySortKey.LastSeen, true)));
        Assert.Equal(["IntunewinBuilder", "zoom", "7-Zip", "Git"], Names(InventorySort.Apply(Items, InventorySortKey.LastSeen, false)));
    }

    [Fact]
    public void First_click_direction_follows_the_column_kind()
    {
        Assert.False(InventorySort.DefaultDescending(InventorySortKey.Name));
        Assert.False(InventorySort.DefaultDescending(InventorySortKey.WingetId));
        Assert.True(InventorySort.DefaultDescending(InventorySortKey.Devices));
        Assert.True(InventorySort.DefaultDescending(InventorySortKey.Updatable));
        Assert.True(InventorySort.DefaultDescending(InventorySortKey.LastSeen));
    }
}
