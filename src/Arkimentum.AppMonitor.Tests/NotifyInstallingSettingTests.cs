using Arkimentum.AppMonitor.Configuration;
using Arkimentum.AppMonitor.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// NotifyInstalling from the registry: the global default, the per-application override, and a typo that must
/// fall back instead of taking the whole configuration down.
/// </summary>
public sealed class NotifyInstallingSettingTests : IDisposable
{
    private readonly string _rootPath = @"SOFTWARE\Arkimentum.AppMonitor.Tests\" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _hive;

    public NotifyInstallingSettingTests()
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

    private AgentSettings Read() =>
        new RegistryConfigurationReader(NullLogger<RegistryConfigurationReader>.Instance, null, _hive).Read();

    [Fact]
    public void The_global_and_per_app_values_are_read()
    {
        using (var p = Pref()) { p.SetValue("DefaultNotifyInstalling", "always"); }
        using (var a = Pref(@"Apps\a")) { a.SetValue("WingetId", "Vendor.A"); a.SetValue("NotifyInstalling", "never"); }
        using (var b = Pref(@"Apps\b")) { b.SetValue("WingetId", "Vendor.B"); b.SetValue("NotifyInstalling", "Auto"); }
        using (var c = Pref(@"Apps\c")) { c.SetValue("WingetId", "Vendor.C"); }

        var s = Read();

        Assert.Equal(NotifyInstallingMode.Always, s.DefaultNotifyInstalling);
        Assert.Equal(NotifyInstallingMode.Never, s.Apps.Single(a => a.AppId == "a").NotifyInstalling);
        Assert.Equal(NotifyInstallingMode.Auto, s.Apps.Single(a => a.AppId == "b").NotifyInstalling);
        Assert.Null(s.Apps.Single(a => a.AppId == "c").NotifyInstalling);
    }

    [Fact]
    public void Unset_is_auto_and_unknown_text_falls_back()
    {
        Assert.Equal(NotifyInstallingMode.Auto, Read().DefaultNotifyInstalling);

        using (var p = Pref()) { p.SetValue("DefaultNotifyInstalling", "nonsense"); }
        using (var a = Pref(@"Apps\a")) { a.SetValue("WingetId", "Vendor.A"); a.SetValue("NotifyInstalling", "gibberish"); }

        var s = Read();

        Assert.Equal(NotifyInstallingMode.Auto, s.DefaultNotifyInstalling);
        // An app whose text is unreadable falls back to the global value rather than being left unset.
        Assert.Equal(NotifyInstallingMode.Auto, s.Apps.Single(a => a.AppId == "a").NotifyInstalling);
    }

    [Fact]
    public void It_is_not_taken_from_the_catalog()
    {
        // Behaviour never comes from the catalog: a catalog entry asking for the toast must not turn it on.
        var catalog = new SingleEntryCatalog(new AppPolicy { AppId = "demo", WingetId = "Vendor.Demo", NotifyInstalling = NotifyInstallingMode.Always });
        using (var k = Pref(@"Apps\demo")) { k.SetValue("WingetId", "Vendor.Demo"); }

        var s = new RegistryConfigurationReader(NullLogger<RegistryConfigurationReader>.Instance, catalog, _hive).Read();

        Assert.Null(s.Apps.Single(a => a.AppId == "demo").NotifyInstalling);
    }

    private sealed class SingleEntryCatalog(AppPolicy entry) : ICatalogProvider
    {
        public IReadOnlyList<AppPolicy> GetCatalog(string catalogPath) => [entry];
    }
}
