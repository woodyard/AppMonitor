using System.Text;
using Arkimentum.AppMonitor.Install;
using Arkimentum.AppMonitor.Inventory;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Providers;
using Arkimentum.AppMonitor.Service.Policy;
using Arkimentum.AppMonitor.Service.State;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// As LocalSystem, winget lists a provisioned msixbundle with the bundle's own version (CPC-hsk-HSNE00X, 2026-11-12, after
/// the 1.1.56 take-over): <c>Microsoft.PowerShell  Microsoft.PowerShell  2026.1110.418.0</c> for the package 7.7.0.0, and
/// <c>Microsoft.WindowsTerminal  3001.25.3131.0</c> for 1.25.3131.0. The service's scan reported "PowerShell 7:
/// 2026.1110.418.0 is up to date (winget, System)", and the inventory carried that number. Such a row now reads the
/// family's package version, never offers an update in the machine scope, and corrects what was recorded with it.
/// </summary>
public class MsixBundleRowTests
{
    private const string PwshBundle = "Microsoft.PowerShell_2026.1110.418.0_neutral_~_8wekyb3d8bbwe";
    private const string PwshPackage = "Microsoft.PowerShell_7.7.0.0_x64__8wekyb3d8bbwe";
    private const string TerminalBundle = "Microsoft.WindowsTerminal_3001.25.3131.0_neutral_~_8wekyb3d8bbwe";
    private const string TerminalPackage = "Microsoft.WindowsTerminal_1.25.3131.0_x64__8wekyb3d8bbwe";
    private const string ClaudePackage = "Claude_5.1231.2.0_x64__pzs8sxrjxfjjc";

    /// <summary>The CPC's packages as <c>Get-AppxPackage -AllUsers -PackageTypeFilter Main,Bundle</c> lists them.</summary>
    private static MsixBundleIndex Device() => MsixBundleIndex.FromFullNames([PwshBundle, PwshPackage, TerminalBundle, TerminalPackage, ClaudePackage]);

    private static WingetRow Row(string name, string id, string version, string available = "", string source = "winget") =>
        new(name, id, version, available, source);

    // ---------------------------------------------------------------- the rules

    [Fact]
    public void A_full_name_splits_into_name_version_and_family()
    {
        var bundle = MsixBundleIndex.Parse(PwshBundle)!;
        Assert.Equal("Microsoft.PowerShell", bundle.Name);
        Assert.Equal("2026.1110.418.0", bundle.Version);
        Assert.Equal("Microsoft.PowerShell_8wekyb3d8bbwe", bundle.Family);
        Assert.Null(MsixBundleIndex.Parse("PowerShell 7-x64"));
        Assert.Null(MsixBundleIndex.Parse(null));
    }

    [Fact]
    public void PowerShell_bundle_row_reads_the_package_version()
    {
        var match = Device().Match(Row("Microsoft.PowerShell", "Microsoft.PowerShell", "2026.1110.418.0"));

        Assert.NotNull(match);
        Assert.Equal(PwshBundle, match.Bundle.FullName);
        Assert.Equal("7.7.0.0", match.PackageVersion);
    }

    [Fact]
    public void Terminal_bundle_row_reads_the_package_version()
    {
        var match = Device().Match(Row("Microsoft.WindowsTerminal", "Microsoft.WindowsTerminal", "3001.25.3131.0"));

        Assert.Equal("1.25.3131.0", match?.PackageVersion);
    }

    [Fact]
    public void A_name_winget_shortened_matches_by_its_start()
    {
        Assert.Equal("1.25.3131.0", Device().Match(Row("Microsoft.WindowsTerm…", "Microsoft.WindowsTerminal", "3001.25.3131.0"))?.PackageVersion);
    }

    [Theory]
    // A classic install that happens to carry the bundle's name, at its own version.
    [InlineData("Microsoft.PowerShell", "7.6.1")]
    // The package rows of a user's listing (and Claude, which is a package, not a bundle).
    [InlineData("PowerShell", "7.7.0.0")]
    [InlineData("Claude", "5.1231.2.0")]
    // The bundle's version under another name.
    [InlineData("PowerShell 7-x64", "2026.1110.418.0")]
    public void Rows_that_are_not_the_bundle_are_left_alone(string name, string version)
    {
        var row = Row(name, "Microsoft.PowerShell", version, available: "7.7.1");
        var index = Device();

        Assert.Null(index.Match(row));
        Assert.Same(row, Assert.Single(index.Resolve([row])));
    }

    [Fact]
    public void A_bundle_with_no_package_for_anyone_reads_no_version_never_the_bundles()
    {
        var index = MsixBundleIndex.FromFullNames([PwshBundle]);
        var row = Row("Microsoft.PowerShell", "Microsoft.PowerShell", "2026.1110.418.0");

        Assert.Null(index.Match(row)!.PackageVersion);
        var resolved = Assert.Single(index.Resolve([row]));
        Assert.Equal(string.Empty, resolved.Version);
        Assert.Equal("2026.1110.418.0", resolved.MsixBundleVersion);
    }

    [Fact]
    public void Resolving_rewrites_bundle_rows_only_and_drops_their_offers()
    {
        var sevenZip = Row("7-Zip 26.03 (x64 edition)", "7zip.7zip", "26.03", "26.04");
        var rows = Device().Resolve([
            sevenZip,
            Row("Microsoft.PowerShell", "Microsoft.PowerShell", "2026.1110.418.0", available: "7.7.1"),
            Row("Microsoft.WindowsTerminal", "Microsoft.WindowsTerminal", "3001.25.3131.0"),
        ]);

        Assert.Same(sevenZip, rows[0]);
        Assert.Equal(("7.7.0.0", "", "2026.1110.418.0"), (rows[1].Version, rows[1].Available, rows[1].MsixBundleVersion));
        Assert.Equal(("1.25.3131.0", "3001.25.3131.0"), (rows[2].Version, rows[2].MsixBundleVersion));
        // Idempotent: a resolved row is not resolved again.
        Assert.Equal(rows, Device().Resolve(rows));
    }

    [Fact]
    public void The_upgrade_listing_loses_its_bundle_rows()
    {
        var sevenZip = Row("7-Zip 26.03 (x64 edition)", "7zip.7zip", "26.03", "26.04");
        var rows = Device().WithoutBundles([sevenZip, Row("Microsoft.PowerShell", "Microsoft.PowerShell", "2026.1110.418.0", "7.7.1")]);

        Assert.Same(sevenZip, Assert.Single(rows));
    }

    [Theory]
    [InlineData("Microsoft.PowerShell", true)]
    [InlineData("Microsoft.WindowsTermin…", true)]
    [InlineData("PowerShell 7-x64", false)]
    [InlineData("PowerShell", false)]
    [InlineData("Claude", false)]
    [InlineData("7-Zip 26.03 (x64 edition)", false)]
    public void Only_rows_named_like_a_package_are_candidates(string name, bool candidate) =>
        Assert.Equal(candidate, MsixBundleIndex.IsCandidate(Row(name, "x", "1.0")));

    // ---------------------------------------------------------------- the enumeration

    [Fact]
    public void The_scripts_output_is_read_line_by_line()
    {
        var (names, errors) = MsixBundleIndex.ParseOutput($"\uFEFFP {PwshBundle}\r\nP {PwshPackage}\r\nnoise\r\nE Get-AppxPackage: Access is denied.\r\n");

        Assert.Equal([PwshBundle, PwshPackage], names);
        Assert.Equal(["Get-AppxPackage: Access is denied."], errors);
        Assert.DoesNotContain('"', MsixBundleIndex.Script);
    }

    [Fact]
    public async Task A_bundle_only_provisioned_is_known_from_the_registry_and_a_failed_enumeration_never_yields_the_bundle_version()
    {
        var index = await MsixBundleIndex.LoadAsync(NullLogger.Instance, CancellationToken.None,
            runner: (_, _, _, _) => Task.FromResult(new ProcessRunResult(1, "E Get-AppxPackage: Access is denied.\r\n", string.Empty)),
            provisioned: () => [PwshBundle, "Microsoft.BingNews_4.7.6002.0_x64__8wekyb3d8bbwe"]);

        Assert.NotNull(index.Failure);
        var bundle = Assert.Single(index.Bundles);
        Assert.Equal(PwshBundle, bundle.FullName);
        Assert.Equal(string.Empty, Assert.Single(index.Resolve([Row("Microsoft.PowerShell", "Microsoft.PowerShell", "2026.1110.418.0")])).Version);
    }

    [Fact]
    public async Task The_enumeration_merges_the_registry_and_PowerShell()
    {
        var index = await MsixBundleIndex.LoadAsync(NullLogger.Instance, CancellationToken.None,
            runner: (_, args, _, _) => Task.FromResult(new ProcessRunResult(0, $"P {PwshBundle}\r\nP {PwshPackage}\r\nP {TerminalPackage}\r\n", string.Empty)),
            provisioned: () => [TerminalBundle]);

        Assert.Null(index.Failure);
        Assert.Equal(2, index.Bundles.Count);
        Assert.Equal("1.25.3131.0", index.Match(Row("Microsoft.WindowsTerminal", "Microsoft.WindowsTerminal", "3001.25.3131.0"))?.PackageVersion);
    }

    // ---------------------------------------------------------------- the service's scan

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

    private static readonly AppPolicy PowerShell = new()
    {
        AppId = "powershell", DisplayName = "PowerShell 7", WingetId = "Microsoft.PowerShell", DetectDisplayNameRegex = "^PowerShell(?: 7(?!.*preview)|$)",
    };

    private static readonly AppPolicy Terminal = new()
    {
        AppId = "windowsterminal", DisplayName = "Windows Terminal", WingetId = "Microsoft.WindowsTerminal", DetectDisplayNameRegex = "^Windows Terminal$",
    };

    private static readonly AppPolicy SevenZip = new() { AppId = "7zip", DisplayName = "7-Zip", WingetId = "7zip.7zip", DetectDisplayNameRegex = "^7-Zip" };

    private static readonly ExecutionContextInfo User = new() { IsSystem = false, UserSid = "S-1-5-21-1", SessionId = 1 };

    /// <summary>SYSTEM's listings on the CPC; the upgrade listing with a (hypothetical) offer for the bundle row, which must never count.</summary>
    private static Task<ProcessRunResult> SystemWinget(string args, TimeSpan timeout, ExecutionContextInfo context, CancellationToken ct)
    {
        var pwsh = new[] { "Microsoft.PowerShell", "Microsoft.PowerShell", "2026.1110.418.0", "", "winget" };
        var terminal = new[] { "Microsoft.WindowsTerminal", "Microsoft.WindowsTerminal", "3001.25.3131.0", "", "winget" };
        var sevenZip = new[] { "7-Zip 26.03 (x64 edition)", "7zip.7zip", "26.03", "", "winget" };
        var output = args switch
        {
            _ when args.StartsWith("upgrade", StringComparison.Ordinal) =>
                Table(["Microsoft.PowerShell", "Microsoft.PowerShell", "2026.1110.418.0", "7.7.1", "winget"]) + "1 upgrades available.\r\n",
            _ when args.StartsWith("list --id Microsoft.PowerShell ", StringComparison.Ordinal) => Table(pwsh),
            _ when args.StartsWith("list --id Microsoft.WindowsTerminal ", StringComparison.Ordinal) => Table(terminal),
            _ when args.StartsWith("list --id 7zip.7zip ", StringComparison.Ordinal) => Table(sevenZip),
            _ when args.StartsWith("list --accept-source-agreements", StringComparison.Ordinal) => Table(sevenZip, pwsh, terminal),
            _ => throw new InvalidOperationException($"unexpected winget call: {args}"),
        };
        return Task.FromResult(new ProcessRunResult(0, output, string.Empty));
    }

    private sealed class CountingIndex
    {
        public int Loads;
        public Task<MsixBundleIndex> Load(CancellationToken ct) { Interlocked.Increment(ref Loads); return Task.FromResult(Device()); }
    }

    private static WingetProvider Provider(CountingIndex index, bool perApp = false) =>
        new(NullLogger<WingetProvider>.Instance, new ProviderOptions())
        {
            LookupRunner = SystemWinget,
            InstallRunner = SystemWinget,
            MsixBundleIndexRunner = index.Load,
            ForcePerAppLookups = perApp,
        };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_service_scan_reports_PowerShell_and_Terminal_at_their_package_versions_without_an_update(bool perApp)
    {
        var index = new CountingIndex();
        var provider = Provider(index, perApp);
        await provider.PrepareScanAsync([SevenZip, PowerShell, Terminal], ExecutionContextInfo.System, CancellationToken.None);

        var sevenZip = await provider.CheckAsync(SevenZip, null, ExecutionContextInfo.System, CancellationToken.None);
        Assert.Equal(0, index.Loads); // nothing that looks like a bundle yet: nothing enumerated
        var pwsh = await provider.CheckAsync(PowerShell, null, ExecutionContextInfo.System, CancellationToken.None);
        var terminal = await provider.CheckAsync(Terminal, null, ExecutionContextInfo.System, CancellationToken.None);

        Assert.Equal(("26.03", (string?)null), (sevenZip.InstalledVersion, sevenZip.MsixBundleVersion));
        Assert.True(pwsh.IsInstalled);
        Assert.Equal("7.7.0.0", pwsh.InstalledVersion);
        Assert.False(pwsh.UpdateAvailable);
        Assert.Null(pwsh.AvailableVersion);
        Assert.Equal("2026.1110.418.0", pwsh.MsixBundleVersion);
        Assert.Equal(("1.25.3131.0", "3001.25.3131.0"), (terminal.InstalledVersion, terminal.MsixBundleVersion));
        Assert.False(terminal.UpdateAvailable);
        Assert.Equal(1, index.Loads); // once per scan
    }

    [Fact]
    public async Task A_users_scan_never_enumerates()
    {
        var index = new CountingIndex();
        var provider = new WingetProvider(NullLogger<WingetProvider>.Instance, new ProviderOptions())
        {
            LookupRunner = SystemWinget, InstallRunner = SystemWinget, MsixBundleIndexRunner = index.Load,
        };

        var pwsh = await provider.CheckAsync(PowerShell, null, User, CancellationToken.None);

        Assert.Equal(0, index.Loads);
        Assert.Null(pwsh.MsixBundleVersion);
    }

    // ---------------------------------------------------------------- the inventory

    [Fact]
    public void The_inventory_shows_the_package_version_of_a_bundle_row()
    {
        var rows = Device().Resolve([Row("Microsoft.PowerShell", "Microsoft.PowerShell", "2026.1110.418.0")]);

        var found = InstalledAppDiscovery.Combine([], rows.Select(r => (r, InstallContext.System)).ToList(), null, [], [PowerShell]);

        var app = Assert.Single(found);
        Assert.Equal("7.7.0.0", app.Version);
        Assert.Equal(InstallContext.System, app.Context);
    }

    // ---------------------------------------------------------------- records written with the bundle's version

    private static PendingUpdate TakeOver(UpdateState state = UpdateState.Installed) => new()
    {
        AppId = "powershell", Context = InstallContext.System, State = state,
        InstalledVersion = "2026.1110.418.0", AvailableVersion = "7.7.0",
    };

    [Fact]
    public void The_take_overs_tracked_update_is_corrected_once()
    {
        var state = new Dictionary<string, PendingUpdate>(StringComparer.OrdinalIgnoreCase)
        {
            ["powershell|System"] = TakeOver(),
            ["windowsterminal|System"] = new() { AppId = "windowsterminal", Context = InstallContext.System, InstalledVersion = "2026.1110.418.0" },
            ["powershell|User|S-1-5-21-1"] = new() { AppId = "powershell", Context = InstallContext.User, UserSid = "S-1-5-21-1", InstalledVersion = "7.7.0.0" },
        };

        var corrected = PolicyEngine.CorrectMsixBundleVersions(state, "powershell", "2026.1110.418.0", "7.7.0.0");

        Assert.Equal(["powershell|System"], corrected);
        Assert.Equal("7.7.0.0", state["powershell|System"].InstalledVersion);
        Assert.Equal("7.7.0", state["powershell|System"].AvailableVersion);
        Assert.Equal("2026.1110.418.0", state["windowsterminal|System"].InstalledVersion); // another app
        Assert.Empty(PolicyEngine.CorrectMsixBundleVersions(state, "powershell", "2026.1110.418.0", "7.7.0.0"));
    }

    [Fact]
    public void An_install_in_flight_and_a_version_that_differs_are_left_alone()
    {
        var installing = TakeOver(UpdateState.Installing);
        var other = TakeOver();
        other.InstalledVersion = "2026.1110.418";
        var state = new Dictionary<string, PendingUpdate> { ["a"] = installing, ["b"] = other };

        Assert.Empty(PolicyEngine.CorrectMsixBundleVersions(state, "powershell", "2026.1110.418.0", "7.7.0.0"));
        Assert.Equal("2026.1110.418.0", installing.InstalledVersion);
    }

    [Fact]
    public void The_take_overs_history_entry_is_corrected_once()
    {
        var at = new DateTimeOffset(2026, 11, 12, 19, 17, 0, TimeSpan.Zero);
        var takeOver = new InstallHistoryEntry
        {
            AppId = "powershell", DisplayName = "PowerShell 7", FromVersion = "7.6.10", ToVersion = "2026.1110.418.0",
            Succeeded = true, CompletedUtc = at, StartedUtc = at.AddMinutes(-2), Context = InstallContext.System, IconPath = @"C:\icon.png",
        };
        var older = new InstallHistoryEntry { AppId = "powershell", FromVersion = "7.6.9", ToVersion = "7.6.10", Succeeded = true, CompletedUtc = at.AddDays(-5) };
        var terminal = new InstallHistoryEntry { AppId = "windowsterminal", ToVersion = "2026.1110.418.0", CompletedUtc = at.AddDays(-1) };
        List<InstallHistoryEntry> history = [takeOver, terminal, older];

        var result = InstallHistory.CorrectMsixBundleVersions(history, "powershell", "2026.1110.418.0", "7.7.0.0");

        Assert.NotNull(result);
        Assert.Equal(1, result.Value.Corrected);
        var fixedEntry = result.Value.History[0];
        Assert.Equal(("7.6.10", "7.7.0.0"), (fixedEntry.FromVersion, fixedEntry.ToVersion));
        Assert.Equal((takeOver.CompletedUtc, takeOver.StartedUtc, takeOver.IconPath, takeOver.Succeeded), (fixedEntry.CompletedUtc, fixedEntry.StartedUtc, fixedEntry.IconPath, fixedEntry.Succeeded));
        Assert.Same(terminal, result.Value.History[1]);
        Assert.Same(older, result.Value.History[2]);
        Assert.Equal("2026.1110.418.0", takeOver.ToVersion); // the input is never edited
        Assert.Null(InstallHistory.CorrectMsixBundleVersions(result.Value.History, "powershell", "2026.1110.418.0", "7.7.0.0"));
    }
}
