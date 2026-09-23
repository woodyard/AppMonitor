using Arkimentum.AppMonitor.Inventory;
using Arkimentum.AppMonitor.Ipc;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Providers;
using Arkimentum.AppMonitor.Service.Policy;
using Arkimentum.AppMonitor.Service.State;
using Arkimentum.AppMonitor.Tray.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// The applications' icons in the tray window come from the device, because the winget catalog carries none: the scan
/// passes on the matched uninstall entry's DisplayIcon (or the application's executable), the service keeps it on the
/// tracked update and in the install history, and the tray falls back to App Paths, an MSIX logo or a monogram.
/// </summary>
public class AppIconTests
{
    // ---------------------------------------------------------------- DisplayIcon parsing

    [Theory]
    [InlineData(@"C:\Program Files\App\app.exe", @"C:\Program Files\App\app.exe", 0)]
    [InlineData(@"C:\Program Files\App\app.exe,0", @"C:\Program Files\App\app.exe", 0)]
    [InlineData(@"C:\Program Files\App\app.exe,3", @"C:\Program Files\App\app.exe", 3)]
    [InlineData(@"C:\Program Files\App\app.exe, 2 ", @"C:\Program Files\App\app.exe", 2)]
    [InlineData(@"""C:\Program Files\App\app.exe"",1", @"C:\Program Files\App\app.exe", 1)]
    [InlineData(@"""C:\Program Files\App\app.exe""", @"C:\Program Files\App\app.exe", 0)]
    [InlineData(@"""C:\Program Files\App\app.exe"" , -101", @"C:\Program Files\App\app.exe", -101)]
    [InlineData(@"C:\Windows\Installer\{GUID}\icon.ico", @"C:\Windows\Installer\{GUID}\icon.ico", 0)]
    [InlineData(@"   C:\Tools\tool.exe   ", @"C:\Tools\tool.exe", 0)]
    [InlineData(@"C:\Program Files\Vendor, Inc\app.exe", @"C:\Program Files\Vendor, Inc\app.exe", 0)]
    [InlineData(@"C:\Program Files\Vendor, Inc\app.exe,-5", @"C:\Program Files\Vendor, Inc\app.exe", -5)]
    [InlineData(@"""C:\Unterminated\app.exe", @"C:\Unterminated\app.exe", 0)]
    public void DisplayIcon_is_split_into_path_and_index(string value, string path, int index)
    {
        var parsed = AppIconSource.ParseDisplayIcon(value, s => s);

        Assert.NotNull(parsed);
        Assert.Equal(path, parsed.Value.Path);
        Assert.Equal(index, parsed.Value.Index);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    [InlineData(",0")]
    [InlineData("app.exe,0")]          // not an absolute path
    public void An_empty_or_relative_DisplayIcon_gives_nothing(string? value) =>
        Assert.Null(AppIconSource.ParseDisplayIcon(value, s => s));

    [Fact]
    public void Environment_variables_in_DisplayIcon_are_expanded()
    {
        const string name = "APPMONITOR_ICON_TEST_ROOT";
        Environment.SetEnvironmentVariable(name, @"C:\Users\alice\AppData\Local");
        try
        {
            var parsed = AppIconSource.ParseDisplayIcon($"%{name}%\\Programs\\App\\app.exe,0");
            Assert.Equal(@"C:\Users\alice\AppData\Local\Programs\App\app.exe", parsed?.Path);
            Assert.Equal(0, parsed?.Index);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void An_unexpanded_variable_left_relative_gives_nothing() =>
        Assert.Null(AppIconSource.ParseDisplayIcon("%APPMONITOR_NOT_SET_ANYWHERE%\\app.exe"));

    // ---------------------------------------------------------------- what the scan passes on

    [Fact]
    public void The_sole_application_executable_is_picked_and_uninstallers_are_ignored()
    {
        Assert.Equal("app.exe", AppIconSource.PickSoleExecutable(["app.exe", "unins000.exe", "Uninstall.exe", "readme.txt", "updater.exe"]));
        Assert.Null(AppIconSource.PickSoleExecutable(["app.exe", "tool.exe"]));         // ambiguous
        Assert.Null(AppIconSource.PickSoleExecutable(["unins000.exe", "setup.exe"]));   // nothing left
        Assert.Null(AppIconSource.PickSoleExecutable([]));
    }

    [Fact]
    public void DisplayIcon_wins_then_the_install_folder_then_the_detection_file()
    {
        var dir = Directory.CreateTempSubdirectory("appmonitor-icon-");
        try
        {
            File.WriteAllBytes(Path.Combine(dir.FullName, "app.exe"), []);
            File.WriteAllBytes(Path.Combine(dir.FullName, "unins000.exe"), []);

            var withIcon = new InstalledApp { DisplayName = "App", DisplayIcon = "  %ProgramFiles%\\App\\app.exe,0 ", InstallLocation = dir.FullName };
            Assert.Equal("%ProgramFiles%\\App\\app.exe,0", AppIconSource.ForInstalledApp(withIcon));

            var withFolder = withIcon with { DisplayIcon = null };
            Assert.Equal(Path.Combine(dir.FullName, "app.exe"), AppIconSource.ForInstalledApp(withFolder, @"C:\Other\other.exe"));

            var nothing = new InstalledApp { DisplayName = "App" };
            Assert.Equal(@"C:\Other\other.exe", AppIconSource.ForInstalledApp(nothing, @"""C:\Other\other.exe"""));
            Assert.Null(AppIconSource.ForInstalledApp(nothing, @"C:\Other\version.txt"));
            Assert.Null(AppIconSource.ForInstalledApp(null));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("{23170F69-40C1-2702-2603-000001000000}", "96F071321C0420726230000010000000")]
    [InlineData("{C8B3A0BF-0F75-32C9-B05B-BE6FE9F5ED90}", "FB0A3B8C57F09C230BB5EBF69E5FDE09")]
    [InlineData("{12345678-abcd-ef01-2345-6789abcdef01}", "87654321DCBA10FE32547698BADCFE10")]
    [InlineData("Mozilla Firefox 156.0.1 (x64 en-US)", null)]
    [InlineData(null, null)]
    public void An_MSI_product_code_is_packed_the_way_Windows_Installer_keys_it(string? productCode, string? packed) =>
        Assert.Equal(packed, AppIconSource.PackedGuid(productCode));

    [Fact]
    public async Task The_checker_puts_the_matched_entry_icon_on_the_result()
    {
        var app = new AppPolicy { AppId = "demo", DisplayName = "Demo App", WingetId = "Vendor.Demo" };
        var inventory = new List<InstalledApp>
        {
            new() { DisplayName = "Demo App", DisplayVersion = "1.0", DisplayIcon = @"C:\Program Files\Demo\demo.exe,0", Context = InstallContext.System },
        };
        var checker = new UpdateChecker(NullLogger<UpdateChecker>.Instance, [new FakeProvider()]);

        var results = await checker.CheckAsync([app], inventory, ExecutionContextInfo.System, new ProviderOptions(), CancellationToken.None);

        Assert.Equal(@"C:\Program Files\Demo\demo.exe,0", Assert.Single(results).IconPath);
    }

    // ---------------------------------------------------------------- service: tracked update and history

    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    private static AppPolicy Policy() => new() { AppId = "demo", DisplayName = "Demo App", WingetId = "Vendor.Demo", DeferralOptionsMinutes = [60] };

    private static ScanOutcome Scan(string? iconPath, string available = "2.0") => new(
        new UpdateCheckResult
        {
            AppId = "demo",
            Source = UpdateSource.Winget,
            IsInstalled = true,
            InstalledVersion = "1.0",
            AvailableVersion = available,
            UpdateAvailable = true,
            WingetId = "Vendor.Demo",
            IconPath = iconPath,
            ResolvedContext = InstallContext.System,
        },
        Policy(), InstallContext.System, null);

    [Fact]
    public void The_icon_path_flows_from_the_scan_into_the_tracked_update_and_survives_a_scan_without_one()
    {
        var state = new Dictionary<string, PendingUpdate>(StringComparer.OrdinalIgnoreCase);
        var first = Scan(@"C:\Demo\demo.exe,0");
        PolicyEngine.Merge(state, [first], new HashSet<string> { first.Key }, T0);
        Assert.Equal(@"C:\Demo\demo.exe,0", state.Values.Single().IconPath);

        var blank = Scan(null);
        PolicyEngine.Merge(state, [blank], new HashSet<string> { blank.Key }, T0.AddHours(1));
        Assert.Equal(@"C:\Demo\demo.exe,0", state.Values.Single().IconPath);

        var moved = Scan(@"C:\Demo2\demo.exe,1", available: "3.0");
        PolicyEngine.Merge(state, [moved], new HashSet<string> { moved.Key }, T0.AddHours(2));
        Assert.Equal(@"C:\Demo2\demo.exe,1", state.Values.Single().IconPath);
        Assert.Equal(@"C:\Demo2\demo.exe,1", state.Values.Single().Clone().IconPath);
    }

    [Fact]
    public void The_install_history_keeps_the_icon_path_of_the_tracked_update()
    {
        var u = new PendingUpdate { AppId = "demo", DisplayName = "Demo App", InstalledVersion = "1.0", AvailableVersion = "2.0", IconPath = @"C:\Demo\demo.exe,0" };

        Assert.Equal(@"C:\Demo\demo.exe,0", InstallHistory.Succeeded(u, InstallResult.Ok() with { InstalledVersion = "2.0" }, T0).IconPath);
        Assert.Equal(@"C:\Demo\demo.exe,0", InstallHistory.Failed(u, "boom", T0).IconPath);
    }

    [Fact]
    public void The_icon_path_crosses_the_pipe_on_scan_results_updates_and_history()
    {
        var scan = new UserScanResultMessage
        {
            ScanId = "s1",
            Results = [new UpdateCheckResult { AppId = "demo", Source = UpdateSource.Winget, IconPath = @"%LOCALAPPDATA%\Demo\demo.exe,0" }],
        };
        var scanBack = Assert.IsType<UserScanResultMessage>(IpcJson.Deserialize(IpcJson.Serialize(scan)));
        Assert.Equal(@"%LOCALAPPDATA%\Demo\demo.exe,0", scanBack.Results.Single().IconPath);

        var state = new StateMessage
        {
            Updates = [new PendingUpdate { AppId = "demo", IconPath = @"C:\Demo\demo.exe,0" }],
            RecentInstalls = [new InstallHistoryEntry { AppId = "demo", IconPath = @"C:\Demo\demo.exe,0" }],
        };
        var stateBack = Assert.IsType<StateMessage>(IpcJson.Deserialize(IpcJson.Serialize(state)));
        Assert.Equal(@"C:\Demo\demo.exe,0", stateBack.Updates.Single().IconPath);
        Assert.Equal(@"C:\Demo\demo.exe,0", stateBack.RecentInstalls!.Single().IconPath);
    }

    // ---------------------------------------------------------------- tray: file-name rules

    [Fact]
    public void An_existing_logo_file_is_used_as_named()
    {
        Assert.Equal("StoreLogo.png", AppIconLookup.ResolveScaledAsset("StoreLogo.png", ["StoreLogo.png", "StoreLogo.scale-200.png"], 32));
    }

    [Fact]
    public void A_scale_qualified_logo_is_chosen_by_size_and_high_contrast_is_skipped()
    {
        string[] files =
        [
            "StoreLogo.scale-100.png", "StoreLogo.scale-200.png", "StoreLogo.scale-400.png",
            "StoreLogo.scale-100_contrast-black.png", "StoreLogo.scale-100_contrast-white.png", "Square44x44Logo.scale-100.png",
        ];
        // scale-100 is 50 px: big enough for 32 and 48; 64 needs scale-200 (100 px).
        Assert.Equal("StoreLogo.scale-100.png", AppIconLookup.ResolveScaledAsset("StoreLogo.png", files, 32));
        Assert.Equal("StoreLogo.scale-200.png", AppIconLookup.ResolveScaledAsset("StoreLogo.png", files, 64));
        // Nothing big enough: the biggest there is.
        Assert.Equal("StoreLogo.scale-400.png", AppIconLookup.ResolveScaledAsset("StoreLogo.png", files, 512));
    }

    [Fact]
    public void A_target_size_logo_prefers_the_unplated_variant()
    {
        string[] files =
        [
            "Logo.targetsize-16.png", "Logo.targetsize-32.png", "Logo.targetsize-32_altform-unplated.png",
            "Logo.targetsize-48_altform-unplated.png", "Logo.targetsize-32_altform-unplated_contrast-black.png",
        ];
        Assert.Equal("Logo.targetsize-32_altform-unplated.png", AppIconLookup.ResolveScaledAsset("Logo.png", files, 32));
        Assert.Equal("Logo.targetsize-48_altform-unplated.png", AppIconLookup.ResolveScaledAsset("Logo.png", files, 40));
        Assert.Equal("Logo.targetsize-16.png", AppIconLookup.ResolveScaledAsset("Logo.png", files, 16));
    }

    [Fact]
    public void No_logo_variant_gives_nothing()
    {
        Assert.Null(AppIconLookup.ResolveScaledAsset("StoreLogo.png", ["Other.scale-100.png", "StoreLogo.scale-100.jpg", "StoreLogo.png.bak"], 32));
        Assert.Null(AppIconLookup.ResolveScaledAsset("StoreLogo.png", [], 32));
    }

    [Theory]
    [InlineData("firefox", "firefox.exe")]
    [InlineData("firefox.exe", "firefox.exe")]
    [InlineData(" Code.EXE ", "Code.EXE")]
    [InlineData("", null)]
    [InlineData(@"C:\Tools\tool.exe", null)]
    public void Process_names_map_to_App_Paths_keys(string process, string? key) =>
        Assert.Equal(key, AppIconLookup.AppPathsKeyName(process));

    [Theory]
    [InlineData("Mozilla Firefox", "M")]
    [InlineData("7-Zip", "7")]
    [InlineData("  notepad++", "N")]
    [InlineData("", "?")]
    [InlineData(null, "?")]
    public void The_monogram_is_the_first_letter_or_digit(string? name, string monogram) =>
        Assert.Equal(monogram, AppIconLookup.Monogram(name));

    [Fact]
    public void Generic_hosts_and_unsafe_cache_names_are_handled()
    {
        Assert.True(AppIconLookup.IsGenericHost(@"C:\Windows\System32\MsiExec.exe"));
        Assert.False(AppIconLookup.IsGenericHost(@"C:\Program Files\App\app.exe"));
        Assert.Equal("Vendor_App_x64", AppIconLookup.SafeFileName("Vendor.App/x64"));
    }

    private sealed class FakeProvider : IUpdateProvider
    {
        public UpdateSource Source => UpdateSource.Winget;

        public Task<UpdateCheckResult> CheckAsync(AppPolicy app, InstalledApp? installed, ExecutionContextInfo context, CancellationToken ct) =>
            Task.FromResult(new UpdateCheckResult
            {
                AppId = app.AppId,
                Source = UpdateSource.Winget,
                IsInstalled = installed is not null,
                InstalledVersion = installed?.DisplayVersion,
                AvailableVersion = "2.0",
                UpdateAvailable = true,
                ResolvedContext = context.Context,
            });

        public Task<InstallResult> InstallAsync(AppPolicy app, PendingUpdate update, ExecutionContextInfo context, IProgress<string>? progress, CancellationToken ct) =>
            Task.FromResult(InstallResult.Ok());
    }
}
