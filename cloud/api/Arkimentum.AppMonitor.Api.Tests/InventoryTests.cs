using Arkimentum.AppMonitor.Api.Contracts;
using Arkimentum.AppMonitor.Api.Security;
using Arkimentum.AppMonitor.Api.Services;
using Xunit;

namespace Arkimentum.AppMonitor.Api.Tests;

public sealed class InventoryAggregatorTests
{
    private static readonly Guid DeviceA = Guid.NewGuid();
    private static readonly Guid DeviceB = Guid.NewGuid();
    private static readonly Guid DeviceC = Guid.NewGuid();
    private static readonly DateTimeOffset Seen = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void GroupsByWingetIdWhenPresentAndByDisplayNameOtherwise()
    {
        var rows = new List<InventoryRow>
        {
            Row(DeviceA, "Google Chrome", "131.0", wingetId: "Google.Chrome"),
            Row(DeviceB, "Google Chrome (x64)", "132.0", wingetId: "Google.Chrome"),
            Row(DeviceA, "Some In-House Tool", "1.0"),
            Row(DeviceB, "Some In-House Tool", "1.0"),
            Row(DeviceC, "Some In-House Tool", "2.0"),
        };

        var items = InventoryAggregator.Build(rows, null);

        var chrome = items.Single(i => i.WingetId == "Google.Chrome");
        Assert.Equal(2, chrome.DeviceCount);
        Assert.Equal("Google Chrome", chrome.DisplayName);          // the most common spelling wins the tie-break

        var inhouse = items.Single(i => i.WingetId is null);
        Assert.Equal(3, inhouse.DeviceCount);
        Assert.Equal(["2.0", "1.0"], inhouse.Versions.Select(v => v.Version));   // newest first
        Assert.Equal(2, inhouse.Versions.Single(v => v.Version == "1.0").DeviceCount);
    }

    [Fact]
    public void CountsDevicesNotRowsWhenAnAppIsInstalledPerMachineAndPerUser()
    {
        var rows = new List<InventoryRow>
        {
            Row(DeviceA, "Visual Studio Code", "1.97.0", wingetId: "Microsoft.VisualStudioCode", context: InstallContext.System),
            Row(DeviceA, "Visual Studio Code", "1.97.0", wingetId: "Microsoft.VisualStudioCode", context: InstallContext.User),
            Row(DeviceB, "Visual Studio Code", "1.96.0", wingetId: "Microsoft.VisualStudioCode", context: InstallContext.User),
        };

        var item = Assert.Single(InventoryAggregator.Build(rows, null));

        Assert.Equal(2, item.DeviceCount);
        Assert.Equal(InstallContext.User, item.PredominantContext);
        Assert.Equal(1, item.Versions.Single(v => v.Version == "1.97.0").DeviceCount);
    }

    [Fact]
    public void CountsDevicesWithAGenuinelyNewerAvailableVersion()
    {
        var rows = new List<InventoryRow>
        {
            Row(DeviceA, "7-Zip", "23.01", wingetId: "7zip.7zip", availableVersion: "24.09"),
            Row(DeviceB, "7-Zip", "24.09", wingetId: "7zip.7zip", availableVersion: "24.09"),
            Row(DeviceC, "7-Zip", "24.09", wingetId: "7zip.7zip"),
        };

        var item = Assert.Single(InventoryAggregator.Build(rows, null));

        Assert.Equal(3, item.DeviceCount);
        Assert.Equal(1, item.DevicesWithUpdateAvailable);
    }

    [Fact]
    public void MarksAnApplicationAsMonitoredByItsWingetIdAlternatives()
    {
        var config = new SettingsDocument();
        config.GetOrAddApp("firefox")["WingetId"] = SettingValue.From("Mozilla.Firefox;Mozilla.Firefox.MSIX");
        config.GetOrAddApp("chrome")["WingetId"] = SettingValue.From("Google.Chrome,Google.Chrome.Beta|Google.Chrome.Dev");

        var rows = new List<InventoryRow>
        {
            Row(DeviceA, "Mozilla Firefox", "135.0", wingetId: "Mozilla.Firefox.MSIX"),
            Row(DeviceB, "Google Chrome", "131.0", wingetId: "Google.Chrome.Dev"),
            Row(DeviceC, "Notepad++", "8.7"),
        };

        var items = InventoryAggregator.Build(rows, config);

        Assert.Equal("firefox", items.Single(i => i.WingetId == "Mozilla.Firefox.MSIX").MonitoredAppId);
        Assert.Equal("chrome", items.Single(i => i.WingetId == "Google.Chrome.Dev").MonitoredAppId);
        Assert.Null(items.Single(i => i.DisplayName == "Notepad++").MonitoredAppId);
    }

    [Fact]
    public void MarksAnApplicationAsMonitoredByItsCatalogAppId()
    {
        var config = new SettingsDocument();
        config.GetOrAddApp("sevenzip")["Enabled"] = SettingValue.From(true);

        var rows = new List<InventoryRow> { Row(DeviceA, "7-Zip", "24.09", catalogAppId: "sevenzip") };

        Assert.Equal("sevenzip", Assert.Single(InventoryAggregator.Build(rows, config)).MonitoredAppId);
    }

    [Fact]
    public void MatchesCaseInsensitively()
    {
        var config = new SettingsDocument();
        config.GetOrAddApp("Chrome")["WingetId"] = SettingValue.From("google.chrome");

        var rows = new List<InventoryRow> { Row(DeviceA, "Google Chrome", "131.0", wingetId: "Google.Chrome") };

        Assert.Equal("Chrome", Assert.Single(InventoryAggregator.Build(rows, config)).MonitoredAppId);
    }

    [Fact]
    public void OnlyUnmonitored_FiltersOutWhatIsAlreadyConfigured()
    {
        var config = new SettingsDocument();
        config.GetOrAddApp("chrome")["WingetId"] = SettingValue.From("Google.Chrome");

        var rows = new List<InventoryRow>
        {
            Row(DeviceA, "Google Chrome", "131.0", wingetId: "Google.Chrome"),
            Row(DeviceA, "Notepad++", "8.7", wingetId: "Notepad++.Notepad++"),
        };

        var items = InventoryAggregator.Build(rows, config, onlyUnmonitored: true);

        Assert.Equal("Notepad++", Assert.Single(items).DisplayName);
    }

    [Fact]
    public void Search_LooksAtNamePublisherWingetIdAndCatalogId()
    {
        var rows = new List<InventoryRow>
        {
            Row(DeviceA, "Google Chrome", "131.0", wingetId: "Google.Chrome", publisher: "Google LLC"),
            Row(DeviceA, "7-Zip", "24.09", wingetId: "7zip.7zip", publisher: "Igor Pavlov", catalogAppId: "sevenzip"),
        };

        Assert.Single(InventoryAggregator.Build(rows, null, search: "chrome"));
        Assert.Single(InventoryAggregator.Build(rows, null, search: "igor"));
        Assert.Single(InventoryAggregator.Build(rows, null, search: "sevenzip"));
        Assert.Equal(2, InventoryAggregator.Build(rows, null, search: "o").Count);
        Assert.Empty(InventoryAggregator.Build(rows, null, search: "zzz"));
    }

    [Fact]
    public void OrdersByDeviceCountDescending()
    {
        var rows = new List<InventoryRow>
        {
            Row(DeviceA, "Rare tool", "1.0"),
            Row(DeviceA, "Common tool", "1.0"),
            Row(DeviceB, "Common tool", "1.0"),
            Row(DeviceC, "Common tool", "1.0"),
        };

        Assert.Equal(["Common tool", "Rare tool"], InventoryAggregator.Build(rows, null).Select(i => i.DisplayName));
    }

    private static InventoryRow Row(
        Guid deviceId, string displayName, string? version, string? publisher = null, string? wingetId = null,
        string? availableVersion = null, InstallContext context = InstallContext.System,
        string? catalogAppId = null, string? monitoredAppId = null) =>
        new(deviceId, displayName, version, publisher, wingetId, availableVersion, context, catalogAppId, monitoredAppId, Seen);
}

public sealed class InventoryEndpointTests : IDisposable
{
    private readonly TestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task Inventory_AggregatesTheLatestReportsOfTheOrganizationOnly()
    {
        var (contoso, contosoKey) = await _harness.SeedOrganizationAsync("Contoso", TestHarness.CustomerTenant);
        var (fabrikam, fabrikamKey) = await _harness.SeedOrganizationAsync("Fabrikam", TestHarness.OtherTenant);

        var pc1 = await EnrollAsync(contoso.Id, contosoKey, "PC-1", "guid-1");
        var pc2 = await EnrollAsync(contoso.Id, contosoKey, "PC-2", "guid-2");
        var other = await EnrollAsync(fabrikam.Id, fabrikamKey, "PC-X", "guid-x");

        await ReportAppsAsync(pc1, ("Google Chrome", "131.0", "Google.Chrome", "132.0"), ("7-Zip", "24.09", "7zip.7zip", null));
        await ReportAppsAsync(pc2, ("Google Chrome", "132.0", "Google.Chrome", "132.0"));
        await ReportAppsAsync(other, ("Google Chrome", "120.0", "Google.Chrome", "132.0"));

        // The organization already monitors Chrome.
        var settings = new SettingsDocument();
        settings.GetOrAddApp("chrome")["WingetId"] = SettingValue.From("Google.Chrome");
        await _harness.Admin.PutConfigAsync(TestHarness.CustomerAdmin, contoso.Id,
            new OrganizationConfigUpdateRequest { Settings = settings }, null, default);

        var outcome = await _harness.Admin.InventoryAsync(TestHarness.CustomerAdmin, contoso.Id, null, false, default);

        Assert.Equal(200, outcome.Status);
        var chrome = outcome.Value!.Single(i => i.WingetId == "Google.Chrome");
        Assert.Equal(2, chrome.DeviceCount);                 // Fabrikam's device is not counted
        Assert.Equal(1, chrome.DevicesWithUpdateAvailable);
        Assert.Equal("chrome", chrome.MonitoredAppId);

        var unmonitored = await _harness.Admin.InventoryAsync(TestHarness.CustomerAdmin, contoso.Id, null, true, default);
        Assert.Equal("7-Zip", Assert.Single(unmonitored.Value!).DisplayName);
    }

    [Fact]
    public async Task Inventory_IgnoresDeletedDevices()
    {
        var (contoso, key) = await _harness.SeedOrganizationAsync();
        var pc1 = await EnrollAsync(contoso.Id, key, "PC-1", "guid-1");
        var pc2 = await EnrollAsync(contoso.Id, key, "PC-2", "guid-2");
        await ReportAppsAsync(pc1, ("7-Zip", "24.09", "7zip.7zip", null));
        await ReportAppsAsync(pc2, ("7-Zip", "24.09", "7zip.7zip", null));

        await _harness.Admin.DeleteDeviceAsync(TestHarness.CustomerAdmin, contoso.Id, pc2.DeviceId, default);

        var outcome = await _harness.Admin.InventoryAsync(TestHarness.CustomerAdmin, contoso.Id, null, false, default);

        Assert.Equal(1, Assert.Single(outcome.Value!).DeviceCount);
    }

    private async Task<DeviceIdentity> EnrollAsync(Guid organizationId, string key, string name, string machineGuid)
    {
        var response = (await _harness.Devices.EnrollAsync(new EnrollRequest
        {
            OrganizationId = organizationId, EnrollmentKey = key, DeviceName = name, MachineGuid = machineGuid,
        }, "203.0.113.1", default)).Value!;
        return (await _harness.Authenticator.AuthenticateAsync($"Device {response.DeviceId}:{response.DeviceKey}", default))!;
    }

    private Task ReportAppsAsync(DeviceIdentity device, params (string Name, string Version, string? WingetId, string? Available)[] apps) =>
        _harness.Devices.ReportAsync(device, new DeviceReport
        {
            ReportedUtc = _harness.Time.GetUtcNow(),
            AgentVersion = "1.0.0",
            InstalledApps = [.. apps.Select(a => new ReportedApp
            {
                DisplayName = a.Name,
                Version = a.Version,
                WingetId = a.WingetId,
                AvailableVersion = a.Available,
                Context = InstallContext.System,
            })],
        }, "{}", default);
}
