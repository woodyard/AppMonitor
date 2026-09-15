using Arkimentum.AppMonitor.Configuration;
using Arkimentum.AppMonitor.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// Exercises the reader against a throw-away key under HKCU that mimics the HKLM layout
/// (SOFTWARE\Arkimentum\AppMonitor and SOFTWARE\Policies\Arkimentum\AppMonitor).
/// </summary>
public sealed class RegistryConfigurationReaderTests : IDisposable
{
    private readonly string _rootPath = @"SOFTWARE\Arkimentum.AppMonitor.Tests\" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _hive;

    public RegistryConfigurationReaderTests()
    {
        _hive = Registry.CurrentUser.CreateSubKey(_rootPath, writable: true)!;
    }

    public void Dispose()
    {
        _hive.Dispose();
        try { Registry.CurrentUser.DeleteSubKeyTree(_rootPath, throwOnMissingSubKey: false); } catch { }
        try { Registry.CurrentUser.DeleteSubKey(@"SOFTWARE\Arkimentum.AppMonitor.Tests", throwOnMissingSubKey: false); } catch { }
    }

    private RegistryKey Pref(string sub = "") => _hive.CreateSubKey(Path.Combine(AgentSettings.RegistryRoot, sub).TrimEnd('\\'))!;
    private RegistryKey Pol(string sub = "") => _hive.CreateSubKey(Path.Combine(AgentSettings.PolicyRegistryRoot, sub).TrimEnd('\\'))!;

    private sealed class FakeCatalog(params AppPolicy[] entries) : ICatalogProvider
    {
        public IReadOnlyList<AppPolicy> GetCatalog(string catalogPath) => entries;
    }

    private RegistryConfigurationReader Reader(ICatalogProvider? catalog = null) =>
        new(NullLogger<RegistryConfigurationReader>.Instance, catalog, _hive);

    [Fact]
    public void Defaults_apply_when_nothing_is_configured()
    {
        var s = Reader().Read();
        Assert.Equal(240, s.ScanIntervalMinutes);
        Assert.Equal(240, s.NotificationIntervalMinutes);
        Assert.True(s.WingetEnabled);
        Assert.Empty(s.Apps);
        Assert.Equal("Default", s.ValueSources["ScanIntervalMinutes"]);
    }

    [Fact]
    public void Policy_overrides_preference_and_values_are_clamped()
    {
        using (var p = Pref()) { p.SetValue("ScanIntervalMinutes", 30, RegistryValueKind.DWord); p.SetValue("LogLevel", "Debug"); p.SetValue("NotificationIntervalMinutes", 1_000_000, RegistryValueKind.DWord); }
        using (var q = Pol()) { q.SetValue("ScanIntervalMinutes", 90, RegistryValueKind.DWord); }

        var s = Reader().Read();
        Assert.Equal(90, s.ScanIntervalMinutes);
        Assert.Equal("Policy", s.ValueSources["ScanIntervalMinutes"]);
        Assert.Equal("Debug", s.LogLevel);
        Assert.Equal("Preference", s.ValueSources["LogLevel"]);
        Assert.Equal(10080, s.NotificationIntervalMinutes); // clamped to one week
    }

    [Fact]
    public void Strings_and_multistrings_are_accepted_for_lists_and_booleans()
    {
        using (var p = Pref()) { p.SetValue("DefaultDeferralOptions", "30, 120;1440"); p.SetValue("WingetEnabled", "false"); }
        using (var a = Pref(@"Apps\demo"))
        {
            a.SetValue("WingetId", "Vendor.Demo");
            a.SetValue("ProcessNames", new[] { "demo.exe", "DemoHelper" }, RegistryValueKind.MultiString);
            a.SetValue("Mandatory", "1");
        }

        var s = Reader().Read();
        Assert.Equal([30, 120, 1440], s.DefaultDeferralOptionsMinutes);
        Assert.False(s.WingetEnabled);
        var app = Assert.Single(s.Apps);
        Assert.Equal(["demo", "DemoHelper"], app.ProcessNames);
        Assert.True(app.Mandatory);
        Assert.Equal([30, 120, 1440], app.DeferralOptionsMinutes); // inherits the global default
    }

    [Fact]
    public void App_specific_values_override_global_defaults()
    {
        using (var p = Pref()) { p.SetValue("DefaultMaxDeferrals", 5, RegistryValueKind.DWord); p.SetValue("DefaultMandatory", 1, RegistryValueKind.DWord); }
        using (var ka = Pref(@"Apps\a")) { ka.SetValue("WingetId", "Vendor.A"); ka.SetValue("MaxDeferrals", 1, RegistryValueKind.DWord); ka.SetValue("Mandatory", 0, RegistryValueKind.DWord); ka.SetValue("Context", "user"); ka.SetValue("NotificationIntervalMinutes", 15, RegistryValueKind.DWord); }
        using (var kb = Pref(@"Apps\b")) { kb.SetValue("WingetId", "Vendor.B"); }

        var s = Reader().Read();
        var a = s.Apps.Single(x => x.AppId == "a");
        var b = s.Apps.Single(x => x.AppId == "b");
        Assert.Equal(1, a.MaxDeferrals);
        Assert.False(a.Mandatory);
        Assert.Equal(InstallContext.User, a.Context);
        Assert.Equal(15, a.NotificationIntervalMinutes);
        Assert.Equal(5, b.MaxDeferrals);
        Assert.True(b.Mandatory);
        Assert.Null(b.NotificationIntervalMinutes);
    }

    [Fact]
    public void Web_source_requires_urls_and_winget_source_requires_id()
    {
        using (var a = Pref(@"Apps\broken-web")) { a.SetValue("Source", "web"); a.SetValue("VersionUrl", "https://example.test/v"); }
        using (var b = Pref(@"Apps\broken-winget")) { b.SetValue("Source", "winget"); }
        using (var c = Pref(@"Apps\ok-web"))
        {
            c.SetValue("Source", "web");
            c.SetValue("VersionUrl", "https://example.test/v");
            c.SetValue("VersionRegex", @"(\d+\.\d+)");
            c.SetValue("DownloadUrl", "https://example.test/{version}/setup.msi");
            c.SetValue("InstallerType", "msi");
        }

        var s = Reader().Read();
        var app = Assert.Single(s.Apps);
        Assert.Equal("ok-web", app.AppId);
        Assert.Equal(UpdateSource.Web, app.Source);
        Assert.Equal(InstallerType.Msi, app.InstallerType);
    }

    [Fact]
    public void AppList_flat_format_is_parsed_and_loses_to_subkeys()
    {
        using (var l = Pref("AppList"))
        {
            l.SetValue("flat", "Source=winget;WingetId=Vendor.Flat;Mandatory=1;DeadlineHours=72;ProcessNames=flat|flathelper.exe;DeferralOptions=15|60");
            l.SetValue("both", "WingetId=Vendor.Both;MaxDeferrals=9");
        }
        using (var a = Pref(@"Apps\both")) { a.SetValue("MaxDeferrals", 2, RegistryValueKind.DWord); }

        var s = Reader().Read();
        var flat = s.Apps.Single(x => x.AppId == "flat");
        Assert.Equal("Vendor.Flat", flat.WingetId);
        Assert.True(flat.Mandatory);
        Assert.Equal(72, flat.DeadlineHours);
        Assert.Equal(["flat", "flathelper"], flat.ProcessNames);
        Assert.Equal([15, 60], flat.DeferralOptionsMinutes);
        Assert.Equal("PreferenceAppList", s.ValueSources[@"Apps\flat\WingetId"]);

        var both = s.Apps.Single(x => x.AppId == "both");
        Assert.Equal("Vendor.Both", both.WingetId);   // from AppList
        Assert.Equal(2, both.MaxDeferrals);           // subkey wins over AppList in the same hive
    }

    [Fact]
    public void Policy_AppList_beats_preference_subkey()
    {
        using (var a = Pref(@"Apps\x")) { a.SetValue("WingetId", "Vendor.X"); a.SetValue("Mandatory", 0, RegistryValueKind.DWord); }
        using (var l = Pol("AppList")) { l.SetValue("x", "Mandatory=1;DeadlineHours=24"); }

        var s = Reader().Read();
        var x = Assert.Single(s.Apps);
        Assert.True(x.Mandatory);
        Assert.Equal(24, x.DeadlineHours);
        Assert.Equal("PolicyAppList", s.ValueSources[@"Apps\x\Mandatory"]);
    }

    [Fact]
    public void Catalog_supplies_identity_but_not_behaviour()
    {
        var catalog = new FakeCatalog(new AppPolicy
        {
            AppId = "cat",
            DisplayName = "Catalog App",
            WingetId = "Vendor.Cat",
            Source = UpdateSource.Web,
            VersionUrl = "https://example.test/v",
            VersionRegex = "(.*)",
            DownloadUrl = "https://example.test/d",
            ProcessNames = ["cat"],
            Mandatory = true,          // must be ignored: behaviour comes from registry / global defaults
            MaxDeferrals = 99,
            DeferralOptionsMinutes = [1],
        });
        using (var p = Pref()) { p.SetValue("DefaultMaxDeferrals", 4, RegistryValueKind.DWord); }
        using (var a = Pref(@"Apps\cat")) { a.SetValue("Enabled", 1, RegistryValueKind.DWord); }

        var s = Reader(catalog).Read();
        var cat = Assert.Single(s.Apps);
        Assert.Equal("Catalog App", cat.DisplayName);
        Assert.Equal(UpdateSource.Web, cat.Source);
        Assert.Equal("https://example.test/v", cat.VersionUrl);
        Assert.Equal(["cat"], cat.ProcessNames);
        Assert.False(cat.Mandatory);
        Assert.Equal(4, cat.MaxDeferrals);
        Assert.Equal([60, 240, 1440], cat.DeferralOptionsMinutes);
    }

    [Fact]
    public void Registry_source_overrides_catalog_source_and_can_switch_to_winget()
    {
        var catalog = new FakeCatalog(new AppPolicy
        {
            AppId = "cat", DisplayName = "Cat", WingetId = "Vendor.Cat", Source = UpdateSource.Web,
            VersionUrl = "https://example.test/v", VersionRegex = "(.*)", DownloadUrl = "https://example.test/d",
        });
        using (var a = Pref(@"Apps\cat")) { a.SetValue("Source", "winget"); }
        var s = Reader(catalog).Read();
        Assert.Equal(UpdateSource.Winget, Assert.Single(s.Apps).Source);
    }

    [Fact]
    public void EnableAllCatalogApps_monitors_every_catalog_entry()
    {
        var catalog = new FakeCatalog(
            new AppPolicy { AppId = "one", DisplayName = "One", WingetId = "V.One" },
            new AppPolicy { AppId = "two", DisplayName = "Two", WingetId = "V.Two" });
        using (var p = Pref()) { p.SetValue("EnableAllCatalogApps", 1, RegistryValueKind.DWord); }
        using (var a = Pref(@"Apps\two")) { a.SetValue("Enabled", 0, RegistryValueKind.DWord); }

        var s = Reader(catalog).Read();
        Assert.Equal(2, s.Apps.Count);
        Assert.True(s.Apps.Single(x => x.AppId == "one").Enabled);
        Assert.False(s.Apps.Single(x => x.AppId == "two").Enabled);
    }

    [Fact]
    public void Paths_expand_environment_variables()
    {
        using (var p = Pref()) { p.SetValue("LogDirectory", @"%TEMP%\cou-logs", RegistryValueKind.ExpandString); }
        var s = Reader().Read();
        Assert.Equal(Environment.ExpandEnvironmentVariables(@"%TEMP%\cou-logs"), s.LogDirectory);
        Assert.DoesNotContain("%", s.LogDirectory);
    }
}
