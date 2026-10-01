using System.Text;
using Arkimentum.AppMonitor.Install;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Providers;
using Arkimentum.AppMonitor.Service;
using Arkimentum.AppMonitor.Service.Policy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// A leftover registration of an older build next to the current install (2026-10-01, the owner's device). Firefox ran
/// as the MSIX package 157.0.0.0, and HKCU still held an Uninstall entry "Mozilla Firefox 156.0.1 (x64 en-US)" for a
/// classic install that was never there (written on 2026-09-23 by an unelevated run of the machine-wide installer).
/// winget's full listing shows the two under different ids, so the scan took the first configured id, read 156.0.1
/// and offered 157.0. The install then asked winget by id, where the MSIX build is listed under Mozilla.Firefox as
/// well, found 157.0.0.0 and reported success: "installed" every hour, with a toast and a history entry each time.
/// The scan now counts every configured id that is installed, and an install that ran nothing is no install.
/// </summary>
public class LeftoverRegistrationTests
{
    private static readonly ExecutionContextInfo User = new() { IsSystem = false, UserSid = "S-1-5-21-1", SessionId = 1 };

    private static readonly AppPolicy Firefox = new()
    {
        AppId = "firefox",
        DisplayName = "Mozilla Firefox",
        WingetId = "Mozilla.Firefox;Mozilla.Firefox.MSIX",
        DetectDisplayNameRegex = "^Mozilla Firefox",
        AutoInstall = true,
    };

    private static readonly string[] ListHeader = ["Name", "Id", "Version", "Available", "Source"];
    private static readonly string[] ByIdHeader = ["Name", "Id", "Version"];

    /// <summary>A winget table in winget's own layout: every column padded to its widest cell plus one space.</summary>
    private static string Table(string[] header, params string[][] rows)
    {
        var widths = Enumerable.Range(0, header.Length)
            .Select(c => Math.Max(header[c].Length, rows.Select(r => r[c].Length).DefaultIfEmpty(0).Max()) + 1).ToArray();
        string Line(string[] cells) => string.Concat(cells.Select((v, c) => c == cells.Length - 1 ? v : v.PadRight(widths[c]))).TrimEnd();
        var sb = new StringBuilder();
        sb.Append(Line(header)).Append("\r\n").Append(new string('-', widths.Sum())).Append("\r\n");
        foreach (var r in rows) sb.Append(Line(r)).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>
    /// Stands in for winget with the answers captured on the device: the full listing and the upgrade listing name
    /// each install under one id, the lookup by id lists the MSIX build under Mozilla.Firefox too.
    /// </summary>
    private sealed class FakeWinget(string msix, string msixAvailable, string leftoverAvailable)
    {
        public List<string> Calls { get; } = [];

        private string FullList => Table(ListHeader,
            ["7-Zip 26.03 (x64 edition)", "7zip.7zip", "26.03", "", "winget"],
            ["Mozilla Firefox", "Mozilla.Firefox.MSIX", msix, msixAvailable, "winget"],
            ["Mozilla Firefox (x64 en-US)", "Mozilla.Firefox", "156.0.1", leftoverAvailable, "winget"]);

        private string UpgradeList
        {
            get
            {
                var rows = new List<string[]>();
                if (msixAvailable.Length > 0) rows.Add(["Mozilla Firefox", "Mozilla.Firefox.MSIX", msix, msixAvailable, "winget"]);
                rows.Add(["Mozilla Firefox (x64 en-US)", "Mozilla.Firefox", "156.0.1", leftoverAvailable, "winget"]);
                return Table(ListHeader, [.. rows]) + $"{rows.Count} upgrades available.\r\n";
            }
        }

        public Task<ProcessRunResult> Run(string args, TimeSpan timeout, ExecutionContextInfo context, CancellationToken ct)
        {
            lock (Calls) Calls.Add(args);
            string output;
            if (args.StartsWith("upgrade --id ", StringComparison.Ordinal))
                return Task.FromResult(new ProcessRunResult(WingetOutputParser.ExitNoApplicableUpgrade,
                    "No available upgrade found.\r\nNo newer package versions are available from the configured sources.\r\n", string.Empty));
            if (args.StartsWith("upgrade", StringComparison.Ordinal)) output = UpgradeList;
            else if (args.StartsWith("list --id Mozilla.Firefox.MSIX ", StringComparison.Ordinal))
                output = Table(ByIdHeader, ["Mozilla Firefox", "Mozilla.Firefox.MSIX", msix]);
            else if (args.StartsWith("list --id Mozilla.Firefox ", StringComparison.Ordinal))
                output = Table(ByIdHeader, ["Mozilla Firefox", "Mozilla.Firefox", msix], ["Mozilla Firefox (x64 en-US)", "Mozilla.Firefox", "156.0.1"]);
            else if (args.StartsWith("list --accept-source-agreements", StringComparison.Ordinal)) output = FullList;
            else throw new InvalidOperationException($"unexpected winget call: {args}");
            return Task.FromResult(new ProcessRunResult(0, output, string.Empty));
        }
    }

    /// <summary>The device as it was: the MSIX package current, the leftover registration one release behind.</summary>
    private static FakeWinget Device() => new("157.0.0.0", msixAvailable: "", leftoverAvailable: "157.0");

    private static WingetProvider Provider(FakeWinget winget, bool perApp = false) =>
        new(NullLogger<WingetProvider>.Instance, new ProviderOptions())
        {
            LookupRunner = winget.Run,
            InstallRunner = winget.Run,
            ForcePerAppLookups = perApp,
        };

    private static async Task<UpdateCheckResult> Check(WingetProvider provider)
    {
        await provider.PrepareScanAsync([Firefox], User, CancellationToken.None);
        return await provider.CheckAsync(Firefox, null, User, CancellationToken.None);
    }

    // ---------------------------------------------------------------- the scan

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_scan_offers_no_update_when_the_current_build_is_installed_under_another_configured_id(bool perApp)
    {
        var result = await Check(Provider(Device(), perApp));

        Assert.Null(result.Error);
        Assert.True(result.IsInstalled);
        Assert.False(result.UpdateAvailable);
        Assert.Equal("157.0.0.0", result.InstalledVersion);
        Assert.Null(result.AvailableVersion);
    }

    [Fact]
    public async Task The_scan_reports_the_id_of_the_install_that_counts()
    {
        var result = await Check(Provider(Device()));

        Assert.Equal("Mozilla.Firefox.MSIX", result.WingetId);
    }

    [Fact]
    public async Task The_scan_reads_both_ids_from_the_one_listing()
    {
        var winget = Device();
        await Check(Provider(winget));

        Assert.Equal(2, winget.Calls.Count);
        Assert.DoesNotContain(winget.Calls, c => c.StartsWith("list --id ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_update_for_the_current_build_is_still_found(bool perApp)
    {
        // The next release: winget offers it for the MSIX package and, as before, for the leftover registration.
        var result = await Check(Provider(new FakeWinget("157.0.0.0", msixAvailable: "158.0.0.0", leftoverAvailable: "158.0"), perApp));

        Assert.True(result.UpdateAvailable);
        Assert.Equal("157.0.0.0", result.InstalledVersion);
        Assert.Equal(0, Versioning.VersionComparer.Compare(result.AvailableVersion, "158.0"));
    }

    [Fact]
    public async Task An_update_for_the_current_build_is_installed_by_its_own_id_first()
    {
        var result = await Check(Provider(new FakeWinget("157.0.0.0", msixAvailable: "158.0.0.0", leftoverAvailable: "158.0")));

        Assert.Equal("Mozilla.Firefox.MSIX", result.WingetId);
        Assert.Equal("158.0.0.0", result.AvailableVersion);
    }

    // ---------------------------------------------------------------- the install

    private static PendingUpdate Pending() => new()
    {
        AppId = "firefox",
        DisplayName = "Mozilla Firefox",
        InstalledVersion = "156.0.1",
        AvailableVersion = "157.0",
        Source = UpdateSource.Winget,
        Context = InstallContext.User,
        UserSid = User.UserSid,
        WingetId = "Mozilla.Firefox",
        WingetIdAlternatives = "Mozilla.Firefox;Mozilla.Firefox.MSIX",
        WingetSourceName = "winget",
    };

    [Fact]
    public async Task An_install_that_finds_the_target_already_installed_says_that_nothing_was_installed()
    {
        // An update the scan of an earlier agent version left in the state: winget has nothing to upgrade.
        var winget = Device();

        var result = await Provider(winget).InstallAsync(Firefox, Pending(), User, null, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.True(result.NothingInstalled);
        Assert.Equal("157.0.0.0", result.InstalledVersion);
        Assert.DoesNotContain(winget.Calls, c => c.StartsWith("install ", StringComparison.Ordinal));
    }

    [Fact]
    public void An_ordinary_success_is_an_install()
    {
        Assert.False(InstallResult.Ok().NothingInstalled);
        Assert.False(WingetProvider.InterpretWingetExitCode(0).NothingInstalled);
    }

    // ---------------------------------------------------------------- the policy

    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 4, 14, 0, TimeSpan.Zero);

    private static ScanOutcome Scan(string installed, string? available, bool updateAvailable = true) => new(
        new UpdateCheckResult
        {
            AppId = "firefox",
            Source = UpdateSource.Winget,
            IsInstalled = true,
            InstalledVersion = installed,
            AvailableVersion = available,
            UpdateAvailable = updateAvailable,
            WingetId = "Mozilla.Firefox",
            ResolvedContext = InstallContext.User,
        },
        Firefox, InstallContext.User, User.UserSid);

    private static readonly InstallResult NothingInstalled =
        InstallResult.Ok("already up to date", WingetOutputParser.ExitNoApplicableUpgrade) with { InstalledVersion = "157.0.0.0", NothingInstalled = true };

    /// <summary>The update as the scan found it, then settled by an install that ran nothing at <paramref name="at"/>.</summary>
    private static (Dictionary<string, PendingUpdate> State, PendingUpdate Update) SettledAt(DateTimeOffset at)
    {
        var state = new Dictionary<string, PendingUpdate>(StringComparer.OrdinalIgnoreCase);
        var first = Scan("156.0.1", "157.0");
        PolicyEngine.Merge(state, [first], new HashSet<string> { first.Key }, at.AddMinutes(-1));
        var u = state.Values.Single();
        PolicyEngine.MarkInstalled(u, NothingInstalled, at);
        return (state, u);
    }

    [Fact]
    public void An_install_that_ran_nothing_settles_the_update_without_verifying_anything()
    {
        var (_, u) = SettledAt(T0);

        Assert.Equal(UpdateState.Installed, u.State);
        Assert.Equal("157.0.0.0", u.InstalledVersion);
        Assert.True(u.NothingToInstall);
        Assert.False(u.InstallVerified);
    }

    [Theory]
    [InlineData(5)]             // the next scan
    [InlineData(3 * 60)]        // after the post-install grace period
    [InlineData(26 * 60)]       // after the retention of installed entries
    [InlineData(10 * 24 * 60)]
    public void A_scan_that_still_offers_the_same_version_does_not_start_over(int minutesLater)
    {
        var (state, u) = SettledAt(T0);

        // Every scan in between, as the service runs them, and then the one at the given time.
        var again = Scan("156.0.1", "157.0");
        for (var at = T0.AddHours(1); at < T0.AddMinutes(minutesLater); at = at.AddHours(1))
            PolicyEngine.Merge(state, [again], new HashSet<string> { again.Key }, at);
        var summary = PolicyEngine.Merge(state, [again], new HashSet<string> { again.Key }, T0.AddMinutes(minutesLater));

        Assert.Same(u, Assert.Single(state.Values));
        Assert.Equal(UpdateState.Installed, u.State);
        Assert.Equal(0, u.FailureCount);
        Assert.Equal((0, 0), (summary.Added, summary.Updated));
        Assert.Empty(summary.NewUpdates);
    }

    [Fact]
    public void A_newer_version_is_a_new_update()
    {
        var (state, u) = SettledAt(T0);

        var newer = Scan("156.0.1", "158.0");
        PolicyEngine.Merge(state, [newer], new HashSet<string> { newer.Key }, T0.AddHours(1));

        Assert.Equal(UpdateState.Available, u.State);
        Assert.Equal("158.0", u.AvailableVersion);
        Assert.False(u.NothingToInstall);
        Assert.Equal(0, u.FailureCount);
    }

    [Fact]
    public void Once_the_scan_agrees_the_entry_goes_the_way_of_every_installed_one()
    {
        var (state, _) = SettledAt(T0);

        var upToDate = Scan("157.0.0.0", null, updateAvailable: false);
        PolicyEngine.Merge(state, [upToDate], new HashSet<string> { upToDate.Key }, T0.AddHours(1));
        Assert.Equal(UpdateState.Installed, state.Values.Single().State);

        PolicyEngine.Merge(state, [upToDate], new HashSet<string> { upToDate.Key }, T0 + PolicyEngine.InstalledRetention + TimeSpan.FromMinutes(1));
        Assert.Empty(state);
    }

    [Fact]
    public void A_real_install_afterwards_is_an_install_again()
    {
        var (_, u) = SettledAt(T0);

        PolicyEngine.MarkInstalled(u, InstallResult.Ok("done", 0) with { InstalledVersion = "157.0.0.0" }, T0.AddHours(1));

        Assert.False(u.NothingToInstall);
        Assert.True(u.InstallVerified);
    }
}
