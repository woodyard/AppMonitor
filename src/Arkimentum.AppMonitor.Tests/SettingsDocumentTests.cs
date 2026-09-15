using Arkimentum.AppMonitor.Configuration;
using Microsoft.Win32;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

public class SettingsDocumentTests
{
    private static SettingsDocument Sample()
    {
        var doc = new SettingsDocument { Description = "Sample" };
        doc.Global["ScanIntervalMinutes"] = SettingValue.From(120);
        doc.Global["NotificationsEnabled"] = SettingValue.From(false);
        doc.Global["LogLevel"] = SettingValue.From("Debug");
        doc.Global["LogDirectory"] = SettingValue.From(@"%ProgramData%\Arkimentum\AppMonitor\Logs");
        doc.Global["DefaultDeferralOptions"] = SettingValue.From(new[] { 30, 60, 240 });
        var chrome = doc.GetOrAddApp("chrome");
        chrome["Enabled"] = SettingValue.From(true);
        chrome["WingetId"] = SettingValue.From("Google.Chrome");
        chrome["Mandatory"] = SettingValue.From(true);
        chrome["DeadlineHours"] = SettingValue.From(72);
        chrome["ProcessNames"] = SettingValue.From(new[] { "chrome", "chrome_proxy" });
        chrome["DisplayName"] = SettingValue.From("Google \"Chrome\" (it's) C:\\path");
        var npp = doc.GetOrAddApp("notepadplusplus");
        npp["Source"] = SettingValue.From("web");
        npp["DeferralOptions"] = SettingValue.From("5,60");
        return doc;
    }

    [Fact]
    public void Json_round_trips_with_natural_types()
    {
        var json = Sample().ToJson();
        Assert.Contains("\"ScanIntervalMinutes\": 120", json);
        Assert.Contains("\"NotificationsEnabled\": false", json);
        Assert.Contains("\"ProcessNames\": [", json);
        var back = SettingsDocument.FromJson(json);
        Assert.Equal(120, back.Global["ScanIntervalMinutes"].AsInt());
        Assert.False(back.Global["NotificationsEnabled"].AsBool());
        Assert.Equal(["chrome", "chrome_proxy"], back.Apps["chrome"]["ProcessNames"].AsStringList());
        Assert.Equal([30, 60, 240], back.Global["DefaultDeferralOptions"].AsIntList());
        Assert.Equal("Google \"Chrome\" (it's) C:\\path", back.Apps["chrome"]["DisplayName"].AsString());
        Assert.Empty(back.Validate());
    }

    [Fact]
    public void Json_rejects_other_schemas()
    {
        Assert.Throws<InvalidDataException>(() => SettingsDocument.FromJson("{\"$schema\":\"something-else/9\"}"));
    }

    [Fact]
    public void Validate_reports_unknown_names_ranges_and_choices()
    {
        var doc = new SettingsDocument();
        doc.Global["NoSuchThing"] = SettingValue.From(1);
        doc.Global["ScanIntervalMinutes"] = SettingValue.From(1);
        doc.Global["LogLevel"] = SettingValue.From("Loud");
        doc.GetOrAddApp("x")["Source"] = SettingValue.From("ftp");
        doc.GetOrAddApp("bad\\id")["Enabled"] = SettingValue.From(true);
        var problems = doc.Validate();
        Assert.Contains(problems, p => p.Contains("NoSuchThing"));
        Assert.Contains(problems, p => p.Contains("ScanIntervalMinutes") && p.Contains("between 5"));
        Assert.Contains(problems, p => p.Contains("LogLevel"));
        Assert.Contains(problems, p => p.Contains("x\\Source"));
        Assert.Contains(problems, p => p.Contains("Invalid AppId"));
    }

    [Fact]
    public void Reg_file_uses_correct_types_and_escaping()
    {
        var reg = SettingsExporter.ToRegFile(Sample());
        Assert.StartsWith("Windows Registry Editor Version 5.00", reg);
        Assert.Contains(@"[HKEY_LOCAL_MACHINE\SOFTWARE\Arkimentum\AppMonitor]", reg);
        Assert.Contains("\"ScanIntervalMinutes\"=dword:00000078", reg);
        Assert.Contains("\"NotificationsEnabled\"=dword:00000000", reg);
        Assert.Contains("\"LogLevel\"=\"Debug\"", reg);
        Assert.Contains("\"LogDirectory\"=hex(2):", reg);
        Assert.Contains("\"DefaultDeferralOptions\"=\"30,60,240\"", reg);
        Assert.Contains(@"[HKEY_LOCAL_MACHINE\SOFTWARE\Arkimentum\AppMonitor\Apps\chrome]", reg);
        Assert.Contains("\"ProcessNames\"=hex(7):63,00,68,00,72,00,6f,00,6d,00,65,00,00,00,63,00", reg);
        Assert.Contains("\"DisplayName\"=\"Google \\\"Chrome\\\" (it's) C:\\\\path\"", reg);
        Assert.Contains(@"[-HKEY_LOCAL_MACHINE\SOFTWARE\Arkimentum\AppMonitor\Apps]", reg);

        var policy = SettingsExporter.ToRegFile(Sample(), new SettingsExportOptions { TargetLayer = SettingsLayer.Policy, ReplaceApps = false });
        Assert.Contains(@"[HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Arkimentum\AppMonitor]", policy);
        Assert.DoesNotContain("[-HKEY", policy);
    }

    [Fact]
    public void PowerShell_script_parses_and_contains_every_value()
    {
        var script = SettingsExporter.ToPowerShell(Sample());
        Assert.Contains("Set-Value $RegistryPath 'ScanIntervalMinutes' 120 DWord", script);
        Assert.Contains("Set-Value $RegistryPath 'NotificationsEnabled' 0 DWord", script);
        Assert.Contains("'LogDirectory' '%ProgramData%\\Arkimentum\\AppMonitor\\Logs' ExpandString", script);
        Assert.Contains("'ProcessNames' @('chrome', 'chrome_proxy') MultiString", script);
        Assert.Contains("'DisplayName' 'Google \"Chrome\" (it''s) C:\\path' String", script);
        Assert.Contains("\"$RegistryPath\\Apps\\notepadplusplus\"", script);
        Assert.Contains("exit 0", script);

        // must be syntactically valid PowerShell
        System.Management.Automation.Language.Parser.ParseInput(script, out _, out var errors);
        Assert.Empty(errors);
    }

    [Fact]
    public void Store_round_trips_through_registry_and_replace_mode_prunes()
    {
        var path = @"SOFTWARE\Arkimentum.AppMonitor.Tests\" + Guid.NewGuid().ToString("N");
        using var hive = Registry.CurrentUser.CreateSubKey(path)!;
        try
        {
            var store = new RegistrySettingsStore(hive);
            Assert.False(store.Exists(SettingsLayer.Preference));

            store.Write(Sample());
            var back = store.Read(SettingsLayer.Preference);
            Assert.Equal(120, back.Global["ScanIntervalMinutes"].AsInt());
            Assert.False(back.Global["NotificationsEnabled"].AsBool());
            Assert.Equal(["chrome", "chrome_proxy"], back.Apps["chrome"]["ProcessNames"].AsStringList());
            Assert.Equal("web", back.Apps["notepadplusplus"]["Source"].AsString());
            Assert.Equal("Google \"Chrome\" (it's) C:\\path", back.Apps["chrome"]["DisplayName"].AsString());

            // stored with the right registry kinds
            using (var key = hive.OpenSubKey(@"SOFTWARE\Arkimentum\AppMonitor"))
            {
                Assert.Equal(RegistryValueKind.DWord, key!.GetValueKind("ScanIntervalMinutes"));
                Assert.Equal(RegistryValueKind.ExpandString, key.GetValueKind("LogDirectory"));
                Assert.Equal(RegistryValueKind.String, key.GetValueKind("DefaultDeferralOptions"));
            }
            using (var key = hive.OpenSubKey(@"SOFTWARE\Arkimentum\AppMonitor\Apps\chrome"))
                Assert.Equal(RegistryValueKind.MultiString, key!.GetValueKind("ProcessNames"));

            // the real reader consumes what the store wrote
            var settings = new RegistryConfigurationReader(Microsoft.Extensions.Logging.Abstractions.NullLogger<RegistryConfigurationReader>.Instance, null, hive).Read();
            Assert.Equal(120, settings.ScanIntervalMinutes);
            var chrome = settings.Apps.Single(a => a.AppId == "chrome");
            Assert.True(chrome.Mandatory);
            Assert.Equal(72, chrome.DeadlineHours);
            Assert.Equal(["chrome", "chrome_proxy"], chrome.ProcessNames);

            // replace mode removes what is no longer in the document
            var smaller = new SettingsDocument();
            smaller.Global["ScanIntervalMinutes"] = SettingValue.From(60);
            smaller.GetOrAddApp("chrome")["WingetId"] = SettingValue.From("Google.Chrome");
            store.Write(smaller, SettingsLayer.Preference, SettingsWriteMode.Replace);
            var pruned = store.Read(SettingsLayer.Preference);
            Assert.Single(pruned.Global);
            Assert.Single(pruned.Apps);
            Assert.False(pruned.Apps["chrome"].ContainsKey("Mandatory"));

            // merge mode keeps the rest
            var merge = new SettingsDocument();
            merge.Global["LogLevel"] = SettingValue.From("Trace");
            store.Write(merge, SettingsLayer.Preference, SettingsWriteMode.Merge);
            var merged = store.Read(SettingsLayer.Preference);
            Assert.Equal(60, merged.Global["ScanIntervalMinutes"].AsInt());
            Assert.Equal("Trace", merged.Global["LogLevel"].AsString());

            // effective view marks policy locks
            var pol = new SettingsDocument();
            pol.Global["ScanIntervalMinutes"] = SettingValue.From(15);
            store.Write(pol, SettingsLayer.Policy, SettingsWriteMode.Merge);
            var eff = store.Describe().Single(e => e.Definition.Name == "ScanIntervalMinutes");
            Assert.True(eff.IsLockedByPolicy);
            Assert.Equal(15, eff.Effective!.AsInt());
            Assert.Equal(60, eff.Preference!.AsInt());

            store.DeleteApp("chrome");
            Assert.Empty(store.Read(SettingsLayer.Preference).Apps);
            store.Clear();
            Assert.False(store.Exists(SettingsLayer.Preference));
        }
        finally
        {
            hive.Dispose();
            Registry.CurrentUser.DeleteSubKeyTree(path, false);
        }
    }

    [Fact]
    public void Store_reads_flat_AppList_entries_and_subkeys_win()
    {
        var path = @"SOFTWARE\Arkimentum.AppMonitor.Tests\" + Guid.NewGuid().ToString("N");
        using var hive = Registry.CurrentUser.CreateSubKey(path)!;
        try
        {
            using (var list = hive.CreateSubKey(@"SOFTWARE\Arkimentum\AppMonitor\AppList")) list!.SetValue("flat", "WingetId=V.Flat;Mandatory=1;ProcessNames=a|b");
            using (var sub = hive.CreateSubKey(@"SOFTWARE\Arkimentum\AppMonitor\Apps\flat")) sub!.SetValue("Mandatory", 0, RegistryValueKind.DWord);
            var doc = new RegistrySettingsStore(hive).Read(SettingsLayer.Preference);
            var flat = doc.Apps["flat"];
            Assert.Equal("V.Flat", flat["WingetId"].AsString());
            Assert.False(flat["Mandatory"].AsBool());
            Assert.Equal(["a", "b"], flat["ProcessNames"].AsStringList());
        }
        finally
        {
            hive.Dispose();
            Registry.CurrentUser.DeleteSubKeyTree(path, false);
        }
    }
}
