using System.Text;
using Arkimentum.AppMonitor.Install;
using Arkimentum.AppMonitor.Ipc;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// H-SURFACELAP5 (owner's Surface, agent 1.1.43, 2026-10-06): the user's Firefox is the MSIX package 157.0.0.0, whose own
/// winget package (Mozilla.Firefox.MSIX) has nothing newer (157.0). winget also correlates it with Mozilla.Firefox, next to
/// a stale HKCU entry 156.0.1 that an unelevated run of the machine-wide installer left behind, and Mozilla.Firefox's exe
/// is at 157.0.1. The scan offered "157.0.0.0 -> 157.0.1 (Winget, User)", the upgrade refused, and "winget install
/// --force --scope user" tried to put the exe Firefox next to the MSIX one; three failures in three minutes. An MSIX copy
/// is only updated by an MSIX package (added after 1.1.46).
/// </summary>
public class MsixCopyUpdateTests
{
    private static readonly ExecutionContextInfo User = new() { IsSystem = false, UserSid = "S-1-5-21-1-2-3-1001", SessionId = 1 };

    private const string Exe = "Mozilla.Firefox";
    private const string Msix = "Mozilla.Firefox.MSIX";

    /// <summary>The catalog's Firefox entry, as far as winget is concerned.</summary>
    private static readonly AppPolicy Firefox = new()
    {
        AppId = "firefox",
        DisplayName = "Mozilla Firefox",
        WingetId = "Mozilla.Firefox;Mozilla.Firefox.MSIX",
        DetectDisplayNameRegex = "^Mozilla Firefox",
        Context = InstallContext.Auto,
    };

    // ---------------------------------------------------------------- winget's output

    private static readonly string[] ListHeader = ["Name", "Id", "Version", "Available", "Source"];

    private static string Table(params string[][] rows)
    {
        var widths = Enumerable.Range(0, ListHeader.Length)
            .Select(c => Math.Max(ListHeader[c].Length, rows.Select(r => r[c].Length).DefaultIfEmpty(0).Max()) + 1).ToArray();
        string Line(string[] cells) => string.Concat(cells.Select((v, c) => c == cells.Length - 1 ? v : v.PadRight(widths[c]))).TrimEnd();
        var sb = new StringBuilder();
        sb.Append(Line(ListHeader)).Append("\r\n").Append(new string('-', widths.Sum())).Append("\r\n");
        foreach (var r in rows) sb.Append(Line(r)).Append("\r\n");
        return sb.ToString();
    }

    private static string[] Row(string name, string id, string version, string available = "", string source = "winget") => [name, id, version, available, source];

    private static readonly string[] MsixRow = Row("Mozilla Firefox", Msix, "157.0.0.0");
    private static readonly string[] MsixRowUnderExeId = Row("Mozilla Firefox", Exe, "157.0.0.0", "157.0.1");
    private static readonly string[] StaleRow = Row("Mozilla Firefox (x64 en-US)", Exe, "156.0.1", "157.0.1");

    /// <summary>What the device printed for <c>winget list --id Mozilla.Firefox --exact --details --scope user</c>.</summary>
    private const string ExeIdDetails =
        "(1/2) Mozilla Firefox [Mozilla.Firefox]\r\n" +
        "Version: 157.0.0.0\r\n" +
        "Local Identifier: MSIX\\Mozilla.MozillaFirefox_157.0.0.0_x64__jag0gd4e3s9p2\r\n" +
        "Package Family Name: mozilla.mozillafirefox_jag0gd4e3s9p2\r\n" +
        "Installer Category: msix\r\n" +
        "Installed Architecture: X64\r\n" +
        "Installed Location: C:\\Program Files\\WindowsApps\\Mozilla.MozillaFirefox_157.0.0.0_x64__jag0gd4e3s9p2\r\n" +
        "Origin Source: winget\r\n" +
        "Available Upgrades:\r\n" +
        "  winget [157.0.1]\r\n" +
        "(2/2) Mozilla Firefox (x64 en-US) [Mozilla.Firefox]\r\n" +
        "Version: 156.0.1\r\n" +
        "Publisher: Mozilla\r\n" +
        "Local Identifier: ARP\\User\\X64\\Mozilla Firefox 156.0.1 (x64 en-US)\r\n" +
        "Product Code: mozilla firefox 156.0.1 (x64 en-us)\r\n" +
        "Installer Category: exe\r\n" +
        "Installed Scope: User\r\n" +
        "Installed Architecture: X64\r\n" +
        "Installed Location: C:\\Program Files\\Mozilla Firefox\r\n" +
        "Origin Source: winget\r\n" +
        "Available Upgrades:\r\n" +
        "  winget [157.0.1]\r\n";

    private static string MsixIdDetails(string version = "157.0.0.0") =>
        "Mozilla Firefox [Mozilla.Firefox.MSIX]\r\n" +
        $"Version: {version}\r\n" +
        $"Local Identifier: MSIX\\Mozilla.MozillaFirefox_{version}_x64__jag0gd4e3s9p2\r\n" +
        "Package Family Name: mozilla.mozillafirefox_jag0gd4e3s9p2\r\n" +
        "Installer Category: msix\r\n" +
        "Installed Architecture: X64\r\n";

    private static string ExeOnlyDetails(params string[] versions) => string.Concat(versions.Select((v, i) =>
        $"({i + 1}/{versions.Length}) Mozilla Firefox (x64 en-US) [Mozilla.Firefox]\r\n" +
        $"Version: {v}\r\n" +
        $"Local Identifier: ARP\\User\\X64\\Mozilla Firefox {v} (x64 en-US)\r\n" +
        "Installer Category: exe\r\n" +
        "Installed Scope: User\r\n"));

    private static ProcessRunResult Shown(string id, string version, string installerType) => new(0,
        $"Found Mozilla Firefox [{id}]\r\nVersion: {version}\r\nPublisher: Mozilla\r\nInstaller:\r\n  Installer Type: {installerType}\r\n  Installer Url: https://example.invalid/firefox\r\n", string.Empty);

    /// <summary>winget 1.30's <c>winget show</c> when no installer matches the filter: the message under "Installer:", exit 0.</summary>
    private static ProcessRunResult ShownWithoutInstaller(string id, string version) => new(0,
        $"Found Mozilla Firefox [{id}]\r\nVersion: {version}\r\nPublisher: Mozilla\r\nInstaller:\r\n  No applicable installer found; see logs for more details.\r\n", string.Empty);

    private static readonly ProcessRunResult NotInstalled =
        new(WingetOutputParser.ExitNoInstalledPackageFound, "No installed package found matching input criteria.\r\n", string.Empty);

    private static readonly ProcessRunResult NoApplicableInstaller =
        new(WingetOutputParser.ExitNoApplicableInstaller, "No applicable installer found; see logs for more details.\r\n", string.Empty);

    /// <summary>
    /// Stands in for winget in the user's session. Scan: the full listing, the upgrade listing, per-id lookups,
    /// <c>--details</c> and <c>show --installer-type msix</c> per id. Install: every upgrade refused (no applicable upgrade),
    /// <c>show --scope user</c> per id, and every install recorded and answered as on the device.
    /// </summary>
    private sealed class FakeWinget
    {
        public string ListOutput { get; init; } = string.Empty;
        public string UpgradeOutput { get; init; } = "No installed package found matching input criteria.\r\n";
        public Dictionary<string, string> PerId { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Details { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary><c>winget show --id X --installer-type msix</c>; an id without an entry has no MSIX installer.</summary>
        public Dictionary<string, ProcessRunResult> MsixShow { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary><c>winget show --id X --scope user</c>; an id without an entry has no user-scope installer.</summary>
        public Dictionary<string, ProcessRunResult> UserShow { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Calls { get; } = [];

        public Task<ProcessRunResult> Run(string args, TimeSpan timeout, ExecutionContextInfo context, CancellationToken ct)
        {
            lock (Calls) Calls.Add(args);
            var id = args.Contains("--id ") ? args.Split(' ')[2] : string.Empty;
            ProcessRunResult answer;
            if (args.StartsWith("upgrade --id ", StringComparison.Ordinal))
                answer = new(WingetOutputParser.ExitNoApplicableUpgrade, "No available upgrade found.\r\n", string.Empty);
            else if (args.StartsWith("upgrade", StringComparison.Ordinal))
                answer = new(0, UpgradeOutput, string.Empty);
            else if (args.StartsWith("list --id ", StringComparison.Ordinal) && args.Contains(" --details"))
                answer = Details.TryGetValue(id, out var d) ? new(0, d, string.Empty) : NotInstalled;
            else if (args.StartsWith("list --id ", StringComparison.Ordinal))
                answer = PerId.TryGetValue(id, out var t) ? new(0, t, string.Empty) : NotInstalled;
            else if (args.StartsWith("list", StringComparison.Ordinal))
                answer = new(0, ListOutput, string.Empty);
            else if (args.StartsWith("show --id ", StringComparison.Ordinal) && args.Contains(" --installer-type msix"))
                answer = MsixShow.TryGetValue(id, out var m) ? m : ShownWithoutInstaller(id, "1.0");
            else if (args.StartsWith("show --id ", StringComparison.Ordinal) && args.Contains(" --scope user"))
                answer = UserShow.TryGetValue(id, out var u) ? u : ShownWithoutInstaller(id, "1.0");
            else if (args.StartsWith("install --id ", StringComparison.Ordinal) && args.Contains(" --scope user"))
                // What the exe installer did on the device, crashing on the stale entry.
                answer = new(unchecked((int)0x80070002), "Successfully verified installer hash\r\nStarting package install...\r\nInstaller failed with exit code: 0x80070002 : The system cannot find the file specified.\r\n", string.Empty);
            else if (args.StartsWith("install --id ", StringComparison.Ordinal))
                answer = NoApplicableInstaller;
            else
                throw new InvalidOperationException($"unexpected winget call: {args}");
            return Task.FromResult(answer);
        }

        public IReadOnlyList<string> Installs { get { lock (Calls) return Calls.Where(c => c.StartsWith("install ", StringComparison.Ordinal)).ToList(); } }
        public int ExtraScanCalls { get { lock (Calls) return Calls.Count(c => c.Contains(" --details") || c.StartsWith("show ", StringComparison.Ordinal)); } }
    }

    /// <summary>The device as captured: both ids answer the per-id lookup, the MSIX id has nothing newer, the exe id's installer is a nullsoft exe.</summary>
    private static FakeWinget Device(string listOutput, string upgradeOutput)
    {
        var winget = new FakeWinget { ListOutput = listOutput, UpgradeOutput = upgradeOutput };
        winget.PerId[Exe] = Table(MsixRowUnderExeId, StaleRow);
        winget.PerId[Msix] = Table(MsixRow);
        winget.Details[Exe] = ExeIdDetails;
        winget.Details[Msix] = MsixIdDetails();
        winget.MsixShow[Msix] = Shown(Msix, "157.0", "msix");
        winget.UserShow[Exe] = Shown(Exe, "157.0.1", "nullsoft");
        winget.UserShow[Msix] = Shown(Msix, "157.0", "msix");
        return winget;
    }

    private static WingetProvider Provider(FakeWinget winget, bool perApp = false,
        Func<SystemInstallHandOverRequest, CancellationToken, Task<SystemInstallHandOverReply?>>? handOver = null) =>
        new(NullLogger<WingetProvider>.Instance, new ProviderOptions { SystemInstallHandOver = handOver })
        {
            LookupRunner = winget.Run,
            InstallRunner = winget.Run,
            ForcePerAppLookups = perApp,
        };

    private static async Task<UpdateCheckResult> Scan(FakeWinget winget, AppPolicy app, ExecutionContextInfo context, bool perApp = false)
    {
        var provider = Provider(winget, perApp);
        await provider.PrepareScanAsync([app], context, CancellationToken.None);
        return await provider.CheckAsync(app, null, context, CancellationToken.None);
    }

    // ---------------------------------------------------------------- the scan: no false offer

    /// <summary>The ways winget may lay the device's two rows out in its listings (the upgrade listing was not captured).</summary>
    public static TheoryData<string, string, string, bool> DeviceLayouts => new()
    {
        // The full listing shows the MSIX copy under its own id and the stale entry under the exe id.
        { "MSIX under its own id; upgrade: the stale entry", Table(MsixRow, StaleRow), Table(StaleRow), false },
        { "MSIX under its own id; upgrade: the MSIX copy under the exe id", Table(MsixRow, StaleRow), Table(MsixRowUnderExeId), false },
        { "MSIX under its own id; upgrade: both under the exe id", Table(MsixRow, StaleRow), Table(MsixRowUnderExeId, StaleRow), false },
        // Both rows under the exe id, as 'winget list --id Mozilla.Firefox' shows them.
        { "both under the exe id; upgrade: both", Table(MsixRowUnderExeId, StaleRow), Table(MsixRowUnderExeId, StaleRow), false },
        { "both under the exe id; upgrade: the stale entry", Table(MsixRowUnderExeId, StaleRow), Table(StaleRow), false },
        // The full listing failed: every id with winget's own per-id lookup.
        { "per-id lookups", Table(MsixRow, StaleRow), Table(MsixRowUnderExeId, StaleRow), true },
    };

    [Theory]
    [MemberData(nameof(DeviceLayouts))]
    public async Task The_devices_msix_firefox_is_up_to_date_however_winget_lays_out_the_rows(string layout, string list, string upgrade, bool perApp)
    {
        var result = await Scan(Device(list, upgrade), Firefox, User, perApp);

        Assert.True(result.IsInstalled, layout);
        Assert.Null(result.Error);
        Assert.False(result.UpdateAvailable, $"{layout}: offered {result.AvailableVersion} under {result.WingetId}");
        Assert.Equal("157.0.0.0", result.InstalledVersion);
    }

    [Fact]
    public async Task An_older_install_of_another_package_lends_its_offer_to_nobody_and_costs_no_winget_run()
    {
        // The full listing as winget printed it in 1.1.40's case: the MSIX copy under its own id, the stale entry under
        // the exe id. The exe package's offer belongs to the stale 156.0.1, not to the MSIX 157.0.0.0.
        var winget = Device(Table(MsixRow, StaleRow), Table(StaleRow));

        var result = await Scan(winget, Firefox, User);

        Assert.False(result.UpdateAvailable);
        Assert.Equal((Msix, "157.0.0.0"), (result.WingetId, result.InstalledVersion));
        Assert.Equal(0, winget.ExtraScanCalls);
        Assert.Equal(2, winget.Calls.Count);
    }

    [Fact]
    public async Task The_check_of_an_ambiguous_offer_costs_one_details_call_and_the_msix_lookups()
    {
        var winget = Device(Table(MsixRowUnderExeId, StaleRow), Table(MsixRowUnderExeId, StaleRow));

        var result = await Scan(winget, Firefox, User);

        Assert.False(result.UpdateAvailable);
        Assert.Equal(Msix, result.WingetId);
        Assert.Single(winget.Calls, c => c.Contains(" --details"));
        Assert.Equal(
            ["show --id Mozilla.Firefox --exact --source winget --installer-type msix --accept-source-agreements --disable-interactivity",
             "show --id Mozilla.Firefox.MSIX --exact --source winget --installer-type msix --accept-source-agreements --disable-interactivity"],
            winget.Calls.Where(c => c.StartsWith("show ", StringComparison.Ordinal)));
        Assert.Equal("list --id Mozilla.Firefox --exact --source winget --details --accept-source-agreements --disable-interactivity --scope user",
            winget.Calls.Single(c => c.Contains(" --details")));
        Assert.Empty(winget.Installs);
    }

    [Theory]
    [InlineData("MSIX under its own id; upgrade: both")]
    [InlineData("MSIX under its own id; upgrade: the stale entry")]
    [InlineData("both under the exe id")]
    public async Task An_msix_copy_whose_own_package_has_a_newer_version_is_offered_under_the_msix_id(string layout)
    {
        // Mozilla.Firefox.MSIX 157.0.1.0 is out, next to the exe's 157.0.1.
        var msixWithUpdate = Row("Mozilla Firefox", Msix, "157.0.0.0", "157.0.1.0");
        var winget = layout switch
        {
            "both under the exe id" => Device(Table(MsixRowUnderExeId, StaleRow), Table(MsixRowUnderExeId, StaleRow)),
            "MSIX under its own id; upgrade: the stale entry" => Device(Table(msixWithUpdate, StaleRow), Table(StaleRow)),
            _ => Device(Table(msixWithUpdate, StaleRow), Table(msixWithUpdate, StaleRow)),
        };
        winget.MsixShow[Msix] = Shown(Msix, "157.0.1.0", "msix");

        var result = await Scan(winget, Firefox, User);

        Assert.True(result.UpdateAvailable, layout);
        Assert.Equal((Msix, "157.0.0.0", "157.0.1.0"), (result.WingetId, result.InstalledVersion, result.AvailableVersion));
        // Under its own id the listings settle it; only the shared id needs winget's word.
        Assert.Equal(layout == "both under the exe id" ? 3 : 0, winget.ExtraScanCalls);
    }

    [Fact]
    public async Task A_classic_firefox_with_both_ids_configured_is_offered_as_before()
    {
        // The machine-wide Firefox in the service's scan: the exe 157.0 -> 157.0.1, nothing else.
        var system = ExecutionContextInfo.System;
        var exe = Row("Mozilla Firefox (x64 en-US)", Exe, "157.0", "157.0.1");
        var winget = new FakeWinget { ListOutput = Table(exe), UpgradeOutput = Table(exe) };

        var result = await Scan(winget, Firefox, system);

        Assert.True(result.UpdateAvailable);
        Assert.Equal((Exe, "157.0", "157.0.1"), (result.WingetId, result.InstalledVersion, result.AvailableVersion));
        Assert.Equal(0, winget.ExtraScanCalls);
        Assert.All(winget.Calls, c => Assert.Contains("--scope machine", c));

        // The same per-user install in the tray's scan: not ambiguous either, no extra winget run.
        var user = new FakeWinget { ListOutput = Table(exe), UpgradeOutput = Table(exe) };
        var perUser = await Scan(user, Firefox, User);
        Assert.Equal((true, Exe, "157.0.1"), (perUser.UpdateAvailable, perUser.WingetId, perUser.AvailableVersion));
        Assert.Equal(0, user.ExtraScanCalls);
    }

    [Fact]
    public async Task A_classic_install_with_an_older_one_beside_it_is_still_offered_after_the_details_check()
    {
        var current = Row("Mozilla Firefox (x64 en-US)", Exe, "157.0", "157.0.1");
        var older = Row("Mozilla Firefox (x64 en-US)", Exe, "156.0.1", "157.0.1");
        var winget = new FakeWinget { ListOutput = Table(current, older), UpgradeOutput = Table(current, older) };
        winget.Details[Exe] = ExeOnlyDetails("157.0", "156.0.1");

        var result = await Scan(winget, Firefox, User);

        Assert.True(result.UpdateAvailable);
        Assert.Equal((Exe, "157.0", "157.0.1"), (result.WingetId, result.InstalledVersion, result.AvailableVersion));
        Assert.Single(winget.Calls, c => c.Contains(" --details"));
        Assert.DoesNotContain(winget.Calls, c => c.StartsWith("show ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_msix_package_winget_manages_by_its_only_id_keeps_its_offer_for_the_hand_over()
    {
        // Microsoft.WindowsAppRuntime.1.6 on DESKTOP-V1CDE3I: registered as MSIX (x86 and x64), the manifest only has an
        // exe, no MSIX installer. The update is installed by the service (SystemInstallHandOverTests), so it stays offered.
        const string id = "Microsoft.WindowsAppRuntime.1.6";
        var runtime = new AppPolicy { AppId = "windows-app-runtime-1.6", DisplayName = "Windows App Runtime 1.6", WingetId = id, Context = InstallContext.User };
        var row = Row("WindowsAppRuntime.1.6", id, "1.6.6", "1.6.9");
        var winget = new FakeWinget { ListOutput = Table(row, row), UpgradeOutput = Table(row, row) };
        winget.Details[id] = string.Concat(new[] { "X86", "X64" }.Select((arch, i) =>
            $"({i + 1}/2) WindowsAppRuntime.1.6 [{id}]\r\nVersion: 1.6.6\r\n" +
            $"Local Identifier: MSIX\\{id}_6000.519.297.0_{arch.ToLowerInvariant()}__8wekyb3d8bbwe\r\n" +
            "Package Family Name: microsoft.windowsappruntime.1.6_8wekyb3d8bbwe\r\nInstaller Category: msix\r\n" +
            $"Installed Architecture: {arch}\r\n"));

        var result = await Scan(winget, runtime, User);

        Assert.True(result.UpdateAvailable);
        Assert.Equal((id, "1.6.6", "1.6.9"), (result.WingetId, result.InstalledVersion, result.AvailableVersion));
    }

    [Fact]
    public async Task An_unreadable_details_lookup_keeps_the_offer()
    {
        var winget = Device(Table(MsixRowUnderExeId, StaleRow), Table(MsixRowUnderExeId, StaleRow));
        winget.Details.Remove(Exe);

        var result = await Scan(winget, Firefox, User);

        // "not installed" from --details is no rows: nothing says the copy is MSIX, so today's behaviour stands and the
        // install path's own guard (below) is the backstop.
        Assert.True(result.UpdateAvailable);
        Assert.Equal(Exe, result.WingetId);
    }

    // ---------------------------------------------------------------- the pure rules

    [Fact]
    public void Across_ids_only_the_install_that_counts_keeps_an_available_version()
    {
        var msix = new WingetRow("Mozilla Firefox", Msix, "157.0.0.0", "", "winget");
        var stale = new WingetRow("Mozilla Firefox (x64 en-US)", Exe, "156.0.1", "157.0.1", "winget");
        var sameUnderExe = new WingetRow("Mozilla Firefox", Exe, "157.0.0.0", "157.0.1", "winget");
        var olderSameId = new WingetRow("Mozilla Firefox", Msix, "156.0.0.0", "157.0.1.0", "winget");

        // An older install of another package: its offer is not the newest install's.
        Assert.Equal(msix, WingetOutputParser.CombineProductInstalls([stale, msix]));
        // The same version under another id is the same install: its offer counts (and is then checked, see the scan).
        Assert.Equal("157.0.1", WingetOutputParser.CombineProductInstalls([msix, sameUnderExe])!.Available);
        // An older release of the same package side by side keeps the .NET rule.
        Assert.Equal("157.0.1.0", WingetOutputParser.CombineProductInstalls([msix, olderSameId])!.Available);
        Assert.Null(WingetOutputParser.CombineProductInstalls([]));
    }

    [Fact]
    public void An_offer_is_ambiguous_only_when_the_listings_say_so()
    {
        WingetRow R(string id, string version, string available = "") => new("Name", id, version, available, "winget");
        var exe = R(Exe, "157.0", "157.0.1");

        Assert.False(WingetProvider.IsAmbiguousOffer(Exe, Exe, [exe], [exe], [exe], "157.0"));
        // The upgrade listing named another id than the one the install was listed under.
        Assert.True(WingetProvider.IsAmbiguousOffer(Exe, Msix, [exe], [exe], [exe], "157.0"));
        // Several installs under the offered id, in either listing.
        Assert.True(WingetProvider.IsAmbiguousOffer(Exe, Exe, [exe, R(Exe, "156.0.1")], [exe], [exe], "157.0"));
        Assert.True(WingetProvider.IsAmbiguousOffer(Exe, Exe, [exe], [exe, R(Exe, "156.0.1")], [exe], "157.0"));
        // One installed version under two configured ids.
        Assert.True(WingetProvider.IsAmbiguousOffer(Exe, Exe, [exe], [exe], [exe, R(Msix, "157.0.0.0")], "157.0"));
        Assert.False(WingetProvider.IsAmbiguousOffer(Exe, Exe, [exe], [exe], [exe, R(Msix, "156.0.0.0")], "157.0"));
    }

    [Fact]
    public void The_copy_that_counts_is_the_row_of_the_installed_version()
    {
        var rows = WingetOutputParser.ParseListDetails(ExeIdDetails);

        Assert.Equal(2, rows.Count);
        var copy = WingetProvider.CountingCopy(rows, Exe, "157.0.0.0");
        Assert.NotNull(copy);
        Assert.True(WingetProvider.IsMsixCopy(copy));
        Assert.Equal("mozilla.mozillafirefox_jag0gd4e3s9p2", copy.PackageFamilyName);
        Assert.False(WingetProvider.IsMsixCopy(WingetProvider.CountingCopy(rows, Exe, "156.0.1")!));
        // An unknown version: the highest row.
        Assert.Equal("157.0.0.0", WingetProvider.CountingCopy(rows, Exe, null)!.Version);
        Assert.Null(WingetProvider.CountingCopy(rows, Msix, "157.0.0.0"));
    }

    // ---------------------------------------------------------------- the install: no second copy

    private static PendingUpdate Pending(string wingetId = Exe) => new()
    {
        AppId = "firefox",
        DisplayName = "Mozilla Firefox",
        Source = UpdateSource.Winget,
        Context = InstallContext.User,
        UserSid = User.UserSid,
        InstalledVersion = "157.0.0.0",
        AvailableVersion = "157.0.1",
        WingetId = wingetId,
        WingetIdAlternatives = Firefox.WingetId,
    };

    [Fact]
    public async Task The_install_never_puts_a_classic_copy_next_to_an_msix_one()
    {
        var winget = Device(Table(MsixRow, StaleRow), Table(StaleRow));
        var handedOver = false;
        var provider = Provider(winget, handOver: (_, _) => { handedOver = true; return Task.FromResult<SystemInstallHandOverReply?>(null); });

        var result = await provider.InstallAsync(Firefox, Pending(), User, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(WingetOutputParser.ExitNoApplicableInstaller, result.ExitCode);
        Assert.Contains("Mozilla Firefox is installed as an MSIX package; winget offers no MSIX update for it", result.Message);
        Assert.Contains("'nullsoft'", result.Message);
        // No --scope user install (the exe), and no install from the MSIX id, which is at 157.0 and would only reinstall it.
        Assert.DoesNotContain(winget.Installs, c => c.Contains(" --scope user"));
        Assert.All(winget.Installs, c => Assert.Contains(" --installer-type msix", c));
        Assert.DoesNotContain(winget.Installs, c => c.StartsWith($"install --id {Msix} ", StringComparison.Ordinal));
        // Nothing for the service either: its unscoped install would add a machine-wide copy.
        Assert.False(handedOver);
    }

    [Fact]
    public async Task A_classic_copy_still_gets_the_user_scope_install()
    {
        var winget = new FakeWinget();
        winget.PerId[Exe] = Table(Row("Mozilla Firefox (x64 en-US)", Exe, "157.0", "157.0.1"));
        winget.Details[Exe] = ExeOnlyDetails("157.0");
        winget.UserShow[Exe] = Shown(Exe, "157.0.1", "nullsoft");
        var update = Pending();
        update.InstalledVersion = "157.0";
        update.WingetIdAlternatives = Exe;

        var result = await Provider(winget).InstallAsync(Firefox with { WingetId = Exe }, update, User, null, CancellationToken.None);

        // As before the fix: the per-user installer runs (here it fails as it did on the device).
        Assert.False(result.Success);
        Assert.Contains("0x80070002", result.Message);
        Assert.Single(winget.Installs, c => c.Contains(" --force") && c.Contains(" --scope user"));
    }

    [Fact]
    public void The_failure_says_what_is_installed_and_why_nothing_ran()
    {
        Assert.Equal(
            "Mozilla Firefox is installed as an MSIX package; winget offers no MSIX update for it ('Mozilla.Firefox' only has a 'nullsoft' installer for this user, which would install a second copy next to the MSIX package).",
            WingetProvider.MsixCopyMessage("Mozilla Firefox", Exe, "nullsoft"));
        Assert.True(WingetProvider.IsMsixInstallerType("msix"));
        Assert.True(WingetProvider.IsMsixInstallerType("appx"));
        Assert.False(WingetProvider.IsMsixInstallerType("nullsoft"));
        Assert.False(WingetProvider.IsMsixInstallerType(null));
    }
}
