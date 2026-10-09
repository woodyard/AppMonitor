using System.Text.Json.Nodes;
using Arkimentum.AppMonitor.Configuration;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Providers;
using Arkimentum.AppMonitor.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// WingetDownloader: the setting, the edit of winget's settings file (network.downloader) and the service's controller
/// that keeps SYSTEM's file on it. Measured 2026-10-09 on H-SurfaceLap5: through Delivery Optimization (DownloadMode
/// Simple, no peers) a 35 MB MSI took 63 s, the same URL with curl 1.7 s. Everything here works on temp files: the
/// real SYSTEM file and the developer's own winget settings are never touched.
/// </summary>
public sealed class WingetDownloaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AppMonitorTests", "WingetSettings-" + Guid.NewGuid().ToString("N"));
    private readonly string _rootPath = @"SOFTWARE\Arkimentum.AppMonitor.Tests\" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _hive;

    public WingetDownloaderTests()
    {
        _hive = Registry.CurrentUser.CreateSubKey(_rootPath, writable: true)!;
    }

    public void Dispose()
    {
        _hive.Dispose();
        try { Registry.CurrentUser.DeleteSubKeyTree(_rootPath, throwOnMissingSubKey: false); } catch { }
        try { Registry.CurrentUser.DeleteSubKey(@"SOFTWARE\Arkimentum.AppMonitor.Tests", throwOnMissingSubKey: false); } catch { }
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>What winget writes as the default settings.json: the schema and commented-out examples.</summary>
    private const string DefaultFile = """
        {
            "$schema": "https://aka.ms/winget-settings.schema.json",

            // For documentation on these settings, see: https://aka.ms/winget-settings
            // "source": {
            //    "autoUpdateIntervalInMinutes": 5
            // },
        }
        """;

    private static JsonObject Parse(string text) => (JsonObject)JsonNode.Parse(text)!;

    // ---------------------------------------------------------------- winget settings export

    [Fact]
    public void The_settings_file_is_read_from_winget_settings_export()
    {
        const string output = """{"$schema":"https://aka.ms/winget-settings-export.schema.json","adminSettings":{"BypassCertificatePinningForMicrosoftStore":false,"LocalManifestFiles":false},"userSettingsFile":"C:\\Users\\X\\AppData\\Local\\Packages\\Microsoft.DesktopAppInstaller_8wekyb3d8bbwe\\LocalState\\settings.json"}""";

        Assert.Equal(@"C:\Users\X\AppData\Local\Packages\Microsoft.DesktopAppInstaller_8wekyb3d8bbwe\LocalState\settings.json",
            WingetUserSettings.ParseUserSettingsFile(output));
        // Text around the object (a banner, a trailing newline) does not matter.
        Assert.Equal(@"C:\Windows\System32\config\systemprofile\AppData\Local\Microsoft\WinGet\Settings\defaultState\settings.json",
            WingetUserSettings.ParseUserSettingsFile("note\r\n{\"userSettingsFile\":\"C:\\\\Windows\\\\System32\\\\config\\\\systemprofile\\\\AppData\\\\Local\\\\Microsoft\\\\WinGet\\\\Settings\\\\defaultState\\\\settings.json\"}\r\n"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Unrecognized command: 'settings export'")]
    [InlineData("{\"adminSettings\":{}}")]
    [InlineData("{\"userSettingsFile\":42}")]
    [InlineData("{\"userSettingsFile\":\"settings.json\"}")]
    [InlineData("{not json")]
    public void Export_output_without_a_usable_path_gives_none(string? output) =>
        Assert.Null(WingetUserSettings.ParseUserSettingsFile(output));

    // ---------------------------------------------------------------- the merge

    [Fact]
    public void A_missing_file_is_created_with_the_schema_and_the_downloader()
    {
        var merge = WingetUserSettings.Merge(null, "wininet");

        Assert.Equal(WingetSettingsMergeOutcome.Changed, merge.Outcome);
        var json = Parse(merge.Text!);
        Assert.Equal(WingetUserSettings.SchemaUrl, (string?)json["$schema"]);
        Assert.Equal("wininet", (string?)json["network"]!["downloader"]);
        Assert.Equal("wininet", merge.Downloader);
    }

    [Fact]
    public void The_default_file_with_comments_and_a_trailing_comma_is_read_and_its_schema_kept()
    {
        var merge = WingetUserSettings.Merge(DefaultFile, "do");

        Assert.Equal(WingetSettingsMergeOutcome.Changed, merge.Outcome);
        var json = Parse(merge.Text!);
        Assert.Equal(WingetUserSettings.SchemaUrl, (string?)json["$schema"]);
        Assert.Equal("do", (string?)json["network"]!["downloader"]);
    }

    [Fact]
    public void An_existing_network_block_keeps_its_other_keys_and_every_other_key_stays()
    {
        const string existing = """
            {
              "$schema": "https://aka.ms/winget-settings.schema.json",
              "visual": { "progressBar": "rainbow" },
              "network": { "doProgressTimeoutInSeconds": 60, "downloader": "do", },
              "installBehavior": { "preferences": { "scope": "machine" } }
            }
            """;

        var merge = WingetUserSettings.Merge(existing, "wininet");

        Assert.Equal(WingetSettingsMergeOutcome.Changed, merge.Outcome);
        var json = Parse(merge.Text!);
        Assert.Equal("rainbow", (string?)json["visual"]!["progressBar"]);
        Assert.Equal(60, (int)json["network"]!["doProgressTimeoutInSeconds"]!);
        Assert.Equal("wininet", (string?)json["network"]!["downloader"]);
        Assert.Equal("machine", (string?)json["installBehavior"]!["preferences"]!["scope"]);
        // The order of the keys is kept.
        Assert.Equal(["$schema", "visual", "network", "installBehavior"], json.Select(p => p.Key).ToArray());
    }

    [Fact]
    public void A_file_that_already_says_it_is_not_rewritten()
    {
        var merge = WingetUserSettings.Merge("""{ "network": { "downloader": "wininet" } // set by us """ + "\n}", "wininet");

        Assert.Equal(WingetSettingsMergeOutcome.Unchanged, merge.Outcome);
        Assert.Null(merge.Text);
        Assert.Equal("wininet", merge.Downloader);
    }

    [Fact]
    public void Default_removes_the_downloader_and_a_network_block_left_empty()
    {
        var merge = WingetUserSettings.Merge("""{ "$schema": "x", "network": { "downloader": "wininet" } }""", "default");
        Assert.Equal(WingetSettingsMergeOutcome.Changed, merge.Outcome);
        var json = Parse(merge.Text!);
        Assert.False(json.ContainsKey("network"));
        Assert.Equal("x", (string?)json["$schema"]);
        Assert.Null(merge.Downloader);

        var keep = WingetUserSettings.Merge("""{ "network": { "downloader": "do", "doProgressTimeoutInSeconds": 30 } }""", "default");
        Assert.Equal(30, (int)Parse(keep.Text!)["network"]!["doProgressTimeoutInSeconds"]!);
        Assert.False(((JsonObject)Parse(keep.Text!)["network"]!).ContainsKey("downloader"));

        // Nothing to remove: nothing written, and no file is created for it.
        Assert.Equal(WingetSettingsMergeOutcome.Unchanged, WingetUserSettings.Merge(DefaultFile, "default").Outcome);
        Assert.Equal(WingetSettingsMergeOutcome.Unchanged, WingetUserSettings.Merge(null, "default").Outcome);
    }

    [Theory]
    [InlineData("{ \"network\": ")]
    [InlineData("[1, 2]")]
    [InlineData("{ \"network\": \"wininet\" }")]
    public void Malformed_content_is_never_overwritten(string existing)
    {
        var merge = WingetUserSettings.Merge(existing, "wininet");

        Assert.Equal(WingetSettingsMergeOutcome.Malformed, merge.Outcome);
        Assert.Null(merge.Text);
        Assert.False(string.IsNullOrEmpty(merge.Error));
    }

    // ---------------------------------------------------------------- the file

    [Fact]
    public void Applying_creates_the_file_and_its_folder_then_writes_only_on_a_change()
    {
        var path = Path.Combine(_dir, "defaultState", "settings.json");

        var first = WingetUserSettings.ApplyToFile(path, "wininet");
        Assert.True(first.Ok);
        Assert.True(first.Written);
        Assert.Equal("wininet", (string?)Parse(File.ReadAllText(path))["network"]!["downloader"]);

        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);
        var again = WingetUserSettings.ApplyToFile(path, "wininet");
        Assert.True(again.Ok);
        Assert.False(again.Written);
        Assert.Equal("wininet", again.Downloader);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));

        var changed = WingetUserSettings.ApplyToFile(path, "do");
        Assert.True(changed.Written);
        Assert.Equal("do", (string?)Parse(File.ReadAllText(path))["network"]!["downloader"]);
        // No temporary file is left next to it.
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void A_malformed_file_is_left_as_it_is()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        const string broken = "{ \"network\": { \"downloader\": ";
        File.WriteAllText(path, broken);

        var result = WingetUserSettings.ApplyToFile(path, "wininet");

        Assert.False(result.Ok);
        Assert.False(result.Written);
        Assert.Equal(broken, File.ReadAllText(path));
    }

    // ---------------------------------------------------------------- the setting

    private RegistryKey Pref() => _hive.CreateSubKey(AgentSettings.RegistryRoot)!;

    private AgentSettings Read(SettingsDocument? cloud = null) =>
        new RegistryConfigurationReader(NullLogger<RegistryConfigurationReader>.Instance, null, _hive, () => cloud).Read();

    [Fact]
    public void The_setting_defaults_to_wininet_and_unknown_text_falls_back()
    {
        Assert.Equal("wininet", Read().WingetDownloader);

        using (var p = Pref()) p.SetValue("WingetDownloader", "DO");
        Assert.Equal("do", Read().WingetDownloader);

        using (var p = Pref()) p.SetValue("WingetDownloader", "Default");
        Assert.Equal("default", Read().WingetDownloader);

        using (var p = Pref()) p.SetValue("WingetDownloader", "bits");
        Assert.Equal("wininet", Read().WingetDownloader);
    }

    [Fact]
    public void The_organization_may_set_it()
    {
        var doc = new SettingsDocument();
        doc.Global["WingetDownloader"] = SettingValue.From("do");

        var s = Read(doc);

        Assert.Equal("do", s.WingetDownloader);
        Assert.Equal("Cloud", s.ValueSources["WingetDownloader"]);
    }

    [Fact]
    public void The_schema_offers_the_three_values_with_wininet_as_default()
    {
        var def = SettingsSchema.FindGlobal("WingetDownloader");
        Assert.NotNull(def);
        Assert.Equal(SettingKind.Choice, def!.Kind);
        Assert.Equal("wininet", def.Default);
        Assert.Equal(["wininet", "do", "default"], def.Choices!.ToArray());
        Assert.Equal(AgentSettings.DefaultWingetDownloader, def.Default);
    }

    // ---------------------------------------------------------------- the service's controller

    private sealed class FakeLookup(Func<string?> path)
    {
        public int Calls;

        public Task<(string? Path, string? Error)> Find(AgentSettings _, CancellationToken __)
        {
            Interlocked.Increment(ref Calls);
            var p = path();
            return Task.FromResult(p is null ? ((string?)null, (string?)"winget.exe was not found") : (p, (string?)null));
        }
    }

    private static AgentSettings Settings(string downloader = "wininet", bool winget = true) =>
        new() { WingetDownloader = downloader, WingetEnabled = winget };

    [Fact]
    public async Task Outside_the_real_service_or_with_winget_off_nothing_is_looked_up_or_written()
    {
        var path = Path.Combine(_dir, "settings.json");
        var lookup = new FakeLookup(() => path);

        var notAllowed = new WingetDownloaderController(NullLogger<WingetDownloaderController>.Instance, new WingetDownloaderOptions(Allowed: false), lookup.Find);
        await notAllowed.EnsureAppliedAsync(Settings(), CancellationToken.None);
        Assert.Equal(0, lookup.Calls);
        Assert.False(File.Exists(path));
        Assert.False(notAllowed.WinInetInEffect);

        var wingetOff = new WingetDownloaderController(NullLogger<WingetDownloaderController>.Instance, new WingetDownloaderOptions(Allowed: true), lookup.Find);
        await wingetOff.EnsureAppliedAsync(Settings(winget: false), CancellationToken.None);
        Assert.Equal(0, lookup.Calls);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task The_path_is_looked_up_once_and_each_change_is_applied()
    {
        var path = Path.Combine(_dir, "defaultState", "settings.json");
        var lookup = new FakeLookup(() => path);
        var controller = new WingetDownloaderController(NullLogger<WingetDownloaderController>.Instance, new WingetDownloaderOptions(Allowed: true), lookup.Find);

        await controller.EnsureAppliedAsync(Settings("wininet"), CancellationToken.None);
        Assert.True(controller.WinInetInEffect);
        Assert.Equal("wininet", (string?)Parse(File.ReadAllText(path))["network"]!["downloader"]);

        await controller.EnsureAppliedAsync(Settings("wininet"), CancellationToken.None);
        await controller.EnsureAppliedAsync(Settings("do"), CancellationToken.None);
        Assert.False(controller.WinInetInEffect);
        Assert.Equal("do", (string?)Parse(File.ReadAllText(path))["network"]!["downloader"]);

        await controller.EnsureAppliedAsync(Settings("default"), CancellationToken.None);
        Assert.False(Parse(File.ReadAllText(path)).ContainsKey("network"));
        Assert.Equal(1, lookup.Calls);
    }

    [Fact]
    public async Task A_failed_lookup_is_retried_only_after_the_retry_interval()
    {
        var now = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        string? found = null;
        var lookup = new FakeLookup(() => found);
        var controller = new WingetDownloaderController(NullLogger<WingetDownloaderController>.Instance, new WingetDownloaderOptions(Allowed: true), lookup.Find, () => now);

        await controller.EnsureAppliedAsync(Settings(), CancellationToken.None);
        await controller.EnsureAppliedAsync(Settings(), CancellationToken.None);
        Assert.Equal(1, lookup.Calls);
        Assert.False(controller.WinInetInEffect);

        found = Path.Combine(_dir, "settings.json");
        now += WingetDownloaderController.RetryInterval;
        await controller.EnsureAppliedAsync(Settings(), CancellationToken.None);
        Assert.Equal(2, lookup.Calls);
        Assert.True(controller.WinInetInEffect);
    }

    [Fact]
    public async Task A_malformed_file_is_not_touched_and_wininet_is_not_assumed()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ broken");
        var controller = new WingetDownloaderController(NullLogger<WingetDownloaderController>.Instance, new WingetDownloaderOptions(Allowed: true),
            new FakeLookup(() => path).Find);

        await controller.EnsureAppliedAsync(Settings(), CancellationToken.None);

        Assert.Equal("{ broken", File.ReadAllText(path));
        Assert.False(controller.WinInetInEffect);
    }
}
