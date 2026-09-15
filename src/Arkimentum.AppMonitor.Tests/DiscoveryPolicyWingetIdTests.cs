using Arkimentum.AppMonitor.Inventory;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Providers;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// The inventory runs as SYSTEM, whose "winget list --scope user" sees no per-user packages, so per-user installs
/// reach the row builder as registry-only entries. A catalog (or configured) policy that recognises the install by
/// its detection rules then supplies the winget id.
/// </summary>
public class DiscoveryPolicyWingetIdTests
{
    private static readonly AppPolicy VsCode = new()
    {
        AppId = "vscode",
        DisplayName = "Visual Studio Code",
        WingetId = "Microsoft.VisualStudioCode",
        DetectDisplayNameRegex = "^Microsoft Visual Studio Code",
    };

    private static readonly AppPolicy Firefox = new()
    {
        AppId = "firefox",
        DisplayName = "Mozilla Firefox",
        WingetId = "Mozilla.Firefox;Mozilla.Firefox.MSIX",
        DetectDisplayNameRegex = "^Mozilla Firefox",
    };

    [Fact]
    public void Registry_only_row_takes_winget_id_from_matching_catalog_entry()
    {
        var app = InstalledAppDiscovery.Build("Microsoft Visual Studio Code (User)", "1.137.0", "Microsoft Corporation",
            row: null, InstallContext.User, "registry", configured: [], catalog: [VsCode]);

        Assert.Equal("Microsoft.VisualStudioCode", app.WingetId);
        Assert.Equal("vscode", app.CatalogAppId);
        Assert.False(app.WingetIdTruncated);
        Assert.Equal("registry+catalog", app.Origin);
        Assert.True(app.CanQuickAdd);
    }

    [Fact]
    public void Alternative_ids_yield_the_first_one()
    {
        var app = InstalledAppDiscovery.Build("Mozilla Firefox (x64 en-US)", "155.0.1", "Mozilla",
            row: null, InstallContext.User, "registry", configured: [], catalog: [Firefox]);

        Assert.Equal("Mozilla.Firefox", app.WingetId);
    }

    [Fact]
    public void Configured_policy_wins_over_catalog()
    {
        var configured = VsCode with { AppId = "code-custom", WingetId = "Contoso.Code" };
        var app = InstalledAppDiscovery.Build("Microsoft Visual Studio Code (User)", "1.137.0", "Microsoft Corporation",
            row: null, InstallContext.User, "registry", configured: [configured], catalog: [VsCode]);

        Assert.Equal("Contoso.Code", app.WingetId);
        Assert.Equal("code-custom", app.ConfiguredAppId);
    }

    [Fact]
    public void Unknown_registry_only_row_keeps_no_winget_id()
    {
        var app = InstalledAppDiscovery.Build("Contoso Widgets", "1.0", "Contoso",
            row: null, InstallContext.User, "registry", configured: [], catalog: [VsCode, Firefox]);

        Assert.Null(app.WingetId);
        Assert.Null(app.CatalogAppId);
        Assert.Equal("registry", app.Origin);
    }

    [Fact]
    public void Real_winget_row_is_kept_as_is()
    {
        var row = new WingetRow("Microsoft Visual Studio Code", "Microsoft.VisualStudioCode.Insiders", "1.138.0", "", "winget");
        var app = InstalledAppDiscovery.Build("Microsoft Visual Studio Code", "1.138.0", "Microsoft Corporation",
            row, InstallContext.System, "winget+registry", configured: [], catalog: [VsCode]);

        Assert.Equal("Microsoft.VisualStudioCode.Insiders", app.WingetId);
        Assert.Equal("winget+registry", app.Origin);
    }

    [Fact]
    public void Pseudo_id_row_falls_back_to_the_catalog()
    {
        // winget lists Store/MSIX installs it cannot map to a source under a pseudo id; those are not package ids.
        var row = new WingetRow("Microsoft Visual Studio Code (User)", @"MSIX\Microsoft.VisualStudioCode_1.0.137.0_neutral__8wekyb3d8bbwe", "1.0.137.0", "", "");
        var app = InstalledAppDiscovery.Build("Microsoft Visual Studio Code (User)", "1.137.0", "Microsoft Corporation",
            row, InstallContext.User, "winget+registry", configured: [], catalog: [VsCode]);

        Assert.Equal("Microsoft.VisualStudioCode", app.WingetId);
    }

    [Fact]
    public void Truncated_row_is_not_replaced()
    {
        var row = new WingetRow("Microsoft Visual Studio Code (User)", "Microsoft.VisualStudioCo…", "1.137.0", "", "winget");
        var app = InstalledAppDiscovery.Build("Microsoft Visual Studio Code (User)", "1.137.0", "Microsoft Corporation",
            row, InstallContext.User, "winget+registry", configured: [], catalog: [VsCode]);

        Assert.Null(app.WingetId);
        Assert.True(app.WingetIdTruncated);
        Assert.False(app.CanQuickAdd);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("Mozilla.Firefox", "Mozilla.Firefox")]
    [InlineData("Mozilla.Firefox;Mozilla.Firefox.MSIX", "Mozilla.Firefox")]
    [InlineData(" ; Mozilla.Firefox ", "Mozilla.Firefox")]
    public void WingetIdFromPolicy_returns_first_usable_id(string? ids, string? expected)
    {
        var policy = new AppPolicy { AppId = "x", WingetId = ids };
        Assert.Equal(expected, InstalledAppDiscovery.WingetIdFromPolicy(policy));
    }
}
