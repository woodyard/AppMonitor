using Arkimentum.AppMonitor.Configuration;
using Arkimentum.AppMonitor.Prerequisites;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

public class PrerequisiteTests
{
    [Fact]
    public void Repair_script_is_valid_PowerShell()
    {
        System.Management.Automation.Language.Parser.ParseInput(PrerequisiteManager.RepairScript, out _, out var errors);
        Assert.Empty(errors);
        // Windows PowerShell 5.1 compatibility: no PowerShell 7-only operators
        Assert.DoesNotContain("??", PrerequisiteManager.RepairScript);
        Assert.DoesNotContain("?.", PrerequisiteManager.RepairScript);
        Assert.Contains("Add-AppxProvisionedPackage", PrerequisiteManager.RepairScript);
        Assert.Contains("Repair-WinGetPackageManager", PrerequisiteManager.RepairScript);
    }

    [Fact]
    public void Status_summary_reflects_health()
    {
        var s = new PrerequisiteStatus { WingetAvailable = false };
        Assert.False(s.IsHealthy);
        Assert.Contains("not available", s.Summary);
        s = new PrerequisiteStatus { WingetAvailable = true, WingetVersion = "1.5.0", MinimumVersion = "1.6.0", WingetMeetsMinimum = false };
        Assert.False(s.IsHealthy);
        Assert.Contains("older", s.Summary);
        s = new PrerequisiteStatus { WingetAvailable = true, WingetVersion = "1.30.1", WingetMeetsMinimum = true };
        Assert.True(s.IsHealthy);
    }

    [Fact]
    public void Schema_contains_prerequisite_settings()
    {
        Assert.NotNull(SettingsSchema.FindGlobal("AutoInstallPrerequisites"));
        Assert.NotNull(SettingsSchema.FindGlobal("WingetMinimumVersion"));
        Assert.Equal(24, SettingsSchema.FindGlobal("PrerequisiteCheckIntervalHours")!.Default);
    }
}

public class InstalledAppDiscoveryTests
{
    [Theory]
    [InlineData("7-Zip 26.02 (x64 edition)", "7-Zip", true)]
    [InlineData("Microsoft Visual Studio Code (User)", "Microsoft Visual Studio Code", true)]
    [InlineData("Google Chrome", "Google Chrome", true)]
    [InlineData("Notepad++ (64-bit x64)", "Notepad++", true)]
    [InlineData("Mozilla Firefox", "Mozilla Thunderbird", false)]
    public void Names_match_loosely(string a, string b, bool expected) =>
        Assert.Equal(expected, Arkimentum.AppMonitor.Inventory.InstalledAppDiscovery.NamesMatch(a, b));

    [Fact]
    public void Suggested_app_id_prefers_catalog_then_winget()
    {
        var withCatalog = new Arkimentum.AppMonitor.Inventory.DiscoveredApp { DisplayName = "x", WingetId = "Vendor.X", CatalogAppId = "x" };
        Assert.Equal("x", withCatalog.SuggestedAppId);
        Assert.True(withCatalog.CanQuickAdd);
        var wingetOnly = new Arkimentum.AppMonitor.Inventory.DiscoveredApp { DisplayName = "y", WingetId = "Vendor.Y" };
        Assert.Equal("Vendor.Y", wingetOnly.SuggestedAppId);
        var none = new Arkimentum.AppMonitor.Inventory.DiscoveredApp { DisplayName = "z" };
        Assert.False(none.CanQuickAdd);
        var configured = new Arkimentum.AppMonitor.Inventory.DiscoveredApp { DisplayName = "c", WingetId = "V.C", ConfiguredAppId = "c" };
        Assert.False(configured.CanQuickAdd);
    }
}

public class InstalledAppDiscoveryPseudoIdTests
{
    [Theory]
    [InlineData(@"MSIX\Microsoft.GetHelp_10.2409.42162.0_x64__8wekyb3d8bbwe", true)]
    [InlineData(@"ARP\Machine\X64\{404D5DA5-822C-478B-BAC0-DDF937F0D693}", true)]
    [InlineData("Google.Chrome", false)]
    [InlineData(null, false)]
    public void Pseudo_ids_are_recognised(string? id, bool expected) =>
        Assert.Equal(expected, Arkimentum.AppMonitor.Inventory.InstalledAppDiscovery.IsPseudoId(id));
}

public class WingetIdAlternativeTests
{
    [Fact]
    public void Pending_update_carries_all_configured_ids()
    {
        var policy = new Arkimentum.AppMonitor.Models.AppPolicy { AppId = "firefox", DisplayName = "Firefox", WingetId = "Mozilla.Firefox;Mozilla.Firefox.MSIX" };
        var result = new Arkimentum.AppMonitor.Models.UpdateCheckResult
        {
            AppId = "firefox", Source = Arkimentum.AppMonitor.Models.UpdateSource.Winget, IsInstalled = true,
            InstalledVersion = "154.0.1.0", AvailableVersion = "155.0.1", UpdateAvailable = true, WingetId = "Mozilla.Firefox",
        };
        var p = new Arkimentum.AppMonitor.Models.PendingUpdate { AppId = "firefox" };
        Arkimentum.AppMonitor.Service.Policy.PolicyEngine.ApplyPolicy(p, policy, result, DateTimeOffset.UtcNow);
        Assert.Equal("Mozilla.Firefox", p.WingetId);
        Assert.Equal("Mozilla.Firefox;Mozilla.Firefox.MSIX", p.WingetIdAlternatives);
        Assert.Equal(["Mozilla.Firefox", "Mozilla.Firefox.MSIX"], Arkimentum.AppMonitor.Providers.WingetProvider.SplitIds(p.WingetIdAlternatives));
    }
}
