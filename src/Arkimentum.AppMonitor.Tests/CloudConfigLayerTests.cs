using Arkimentum.AppMonitor.Configuration;
using Arkimentum.AppMonitor.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

public sealed class CloudConfigLayerTests : IDisposable
{
    private readonly string _rootPath = @"SOFTWARE\Arkimentum.AppMonitor.Tests\" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _hive;

    public CloudConfigLayerTests() => _hive = Registry.CurrentUser.CreateSubKey(_rootPath, writable: true)!;

    public void Dispose()
    {
        _hive.Dispose();
        try { Registry.CurrentUser.DeleteSubKeyTree(_rootPath, false); } catch { }
    }

    private RegistryKey Pref(string sub = "") => _hive.CreateSubKey(Path.Combine(AgentSettings.RegistryRoot, sub).TrimEnd('\\'))!;
    private RegistryKey Pol(string sub = "") => _hive.CreateSubKey(Path.Combine(AgentSettings.PolicyRegistryRoot, sub).TrimEnd('\\'))!;

    private static SettingsDocument Cloud()
    {
        var doc = new SettingsDocument();
        doc.Global["ScanIntervalMinutes"] = SettingValue.From(45);
        doc.Global["NotificationIntervalMinutes"] = SettingValue.From(90);
        doc.Global["CloudServerUrl"] = SettingValue.From("https://evil.example");   // must be ignored
        doc.Global["NoSuchSetting"] = SettingValue.From(1);                          // unknown: ignored
        var chrome = doc.GetOrAddApp("chrome");
        chrome["WingetId"] = SettingValue.From("Google.Chrome");
        chrome["Mandatory"] = SettingValue.From(true);
        chrome["DeadlineHours"] = SettingValue.From(48);
        chrome["ProcessNames"] = SettingValue.From(new[] { "chrome" });
        return doc;
    }

    private RegistryConfigurationReader Reader(SettingsDocument? cloud) =>
        new(NullLogger<RegistryConfigurationReader>.Instance, null, _hive, () => cloud);

    [Fact]
    public void Cloud_layer_sits_between_policy_and_preference()
    {
        using (var p = Pref()) { p.SetValue("ScanIntervalMinutes", 240, RegistryValueKind.DWord); p.SetValue("NotificationIntervalMinutes", 10, RegistryValueKind.DWord); p.SetValue("CloudServerUrl", "https://api.example"); }
        using (var q = Pol()) { q.SetValue("NotificationIntervalMinutes", 5, RegistryValueKind.DWord); }

        var s = Reader(Cloud()).Read();
        Assert.Equal(45, s.ScanIntervalMinutes);            // cloud beats preference
        Assert.Equal("Cloud", s.ValueSources["ScanIntervalMinutes"]);
        Assert.Equal(5, s.NotificationIntervalMinutes);      // policy beats cloud
        Assert.Equal("Policy", s.ValueSources["NotificationIntervalMinutes"]);
        Assert.Equal("https://api.example", s.CloudServerUrl); // cloud cannot change the connection itself

        var chrome = Assert.Single(s.Apps);
        Assert.Equal("chrome", chrome.AppId);
        Assert.Equal("Google.Chrome", chrome.WingetId);
        Assert.True(chrome.Mandatory);
        Assert.Equal(48, chrome.DeadlineHours);
        Assert.Equal(["chrome"], chrome.ProcessNames);
        Assert.Equal("Cloud", s.ValueSources[@"Apps\chrome\WingetId"]);
    }

    [Fact]
    public void Local_app_values_are_overridden_by_cloud_but_policy_wins()
    {
        using (var a = Pref(@"Apps\chrome")) { a.SetValue("WingetId", "Google.Chrome"); a.SetValue("DeadlineHours", 1, RegistryValueKind.DWord); }
        using (var b = Pol(@"Apps\chrome")) { b.SetValue("Mandatory", 0, RegistryValueKind.DWord); }
        var chrome = Assert.Single(Reader(Cloud()).Read().Apps);
        Assert.Equal(48, chrome.DeadlineHours);   // cloud over preference
        Assert.False(chrome.Mandatory);            // policy over cloud
    }

    [Fact]
    public void Cloud_layer_is_ignored_when_disabled_or_absent()
    {
        using (var p = Pref()) { p.SetValue("ScanIntervalMinutes", 240, RegistryValueKind.DWord); p.SetValue("CloudConfigEnabled", 0, RegistryValueKind.DWord); }
        var s = Reader(Cloud()).Read();
        Assert.Equal(240, s.ScanIntervalMinutes);
        Assert.Empty(s.Apps);

        var none = Reader(null).Read();
        Assert.Equal(240, none.ScanIntervalMinutes);
    }
}
