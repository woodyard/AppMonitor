using Arkimentum.AppMonitor.Configuration;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Service.Policy;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// The WingetReplaceOnMismatch setting from the registry to the pending update the tray agent installs from:
/// it is behaviour, so it is read per application (never from the catalog) and must survive the trip through
/// <see cref="PolicyEngine"/>, otherwise a user-context install would silently fall back to the old behaviour.
/// </summary>
public sealed class WingetReplaceOnMismatchSettingTests : IDisposable
{
    private readonly string _rootPath = @"SOFTWARE\Arkimentum.AppMonitor.Tests\" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _hive;

    public WingetReplaceOnMismatchSettingTests()
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

    [Fact]
    public void The_per_app_value_is_read_and_defaults_to_off()
    {
        using (var on = Pref(@"Apps\on")) { on.SetValue("WingetId", "Vendor.On"); on.SetValue("WingetReplaceOnMismatch", 1, RegistryValueKind.DWord); }
        using (var text = Pref(@"Apps\text")) { text.SetValue("WingetId", "Vendor.Text"); text.SetValue("WingetReplaceOnMismatch", "true"); }
        using (var off = Pref(@"Apps\off")) { off.SetValue("WingetId", "Vendor.Off"); }

        var s = new RegistryConfigurationReader(NullLogger<RegistryConfigurationReader>.Instance, null, _hive).Read();

        Assert.True(s.Apps.Single(a => a.AppId == "on").WingetReplaceOnMismatch);
        Assert.True(s.Apps.Single(a => a.AppId == "text").WingetReplaceOnMismatch);
        Assert.False(s.Apps.Single(a => a.AppId == "off").WingetReplaceOnMismatch);
    }

    [Fact]
    public void It_is_not_taken_from_the_catalog()
    {
        // Behaviour never comes from the catalog: a catalog entry with the flag set must not enable the take-over.
        var catalog = new SingleEntryCatalog(new AppPolicy { AppId = "demo", WingetId = "Vendor.Demo", WingetReplaceOnMismatch = true });
        using (var k = Pref(@"Apps\demo")) { k.SetValue("WingetId", "Vendor.Demo"); }

        var s = new RegistryConfigurationReader(NullLogger<RegistryConfigurationReader>.Instance, catalog, _hive).Read();

        Assert.False(s.Apps.Single(a => a.AppId == "demo").WingetReplaceOnMismatch);
    }

    [Fact]
    public void A_pending_update_created_from_the_policy_carries_the_flag()
    {
        Assert.True(Pending(replace: true).WingetReplaceOnMismatch);
        Assert.False(Pending(replace: false).WingetReplaceOnMismatch);
    }

    private static PendingUpdate Pending(bool replace)
    {
        var policy = new AppPolicy
        {
            AppId = "app",
            DisplayName = "App",
            WingetId = "Vendor.App",
            WingetReplaceOnMismatch = replace,
            DeferralOptionsMinutes = [60, 240, 1440],
        };
        var result = new UpdateCheckResult
        {
            AppId = "app",
            Source = UpdateSource.Winget,
            IsInstalled = true,
            InstalledVersion = "1.0",
            AvailableVersion = "2.0",
            UpdateAvailable = true,
            WingetId = "Vendor.App",
            ResolvedContext = InstallContext.System,
        };

        var state = new Dictionary<string, PendingUpdate>(StringComparer.OrdinalIgnoreCase);
        var outcome = new ScanOutcome(result, policy, InstallContext.System, null);
        PolicyEngine.Merge(state, [outcome], new HashSet<string> { outcome.Key }, new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero));
        return state.Values.Single();
    }

    private sealed class SingleEntryCatalog(AppPolicy entry) : ICatalogProvider
    {
        public IReadOnlyList<AppPolicy> GetCatalog(string catalogPath) => [entry];
    }
}
