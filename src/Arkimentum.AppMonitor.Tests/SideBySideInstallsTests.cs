using Arkimentum.AppMonitor.Inventory;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Providers;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// Several installs of one application registered at once. winget lists a package once per installed version, so
/// <c>winget list --id X --exact</c> can return more than one row: the .NET runtimes keep every patch release side by
/// side (8.0.30 stays registered after 8.0.31 is installed), and a device can carry two PuTTY builds. The rule, the
/// same for the scan and for the check after an install: the highest installed version is the package's version, and
/// an update is only offered when a version newer than that highest install is available. Cases from H-SURFACELAP5
/// (2026-09-24): "winget reported success ... but 'Microsoft.DotNet.DesktopRuntime.8' is still at 8.0.30, expected
/// 8.0.31" and "... 'PuTTY.PuTTY' is still at 0.78.0.0, expected 0.85.0.0" after a scan that had read 0.84.0.0.
/// </summary>
public class SideBySideInstallsTests
{
    /// <summary><c>winget list --id Microsoft.DotNet.DesktopRuntime.8 --exact</c> after 8.0.31 was installed next to 8.0.30 (winget's format).</summary>
    private const string DotNetAfterInstall =
        "Name                                             Id                                Version Available Source\r\n" +
        "----------------------------------------------------------------------------------------------------------\r\n" +
        "Microsoft Windows Desktop Runtime - 8.0.30 (x64) Microsoft.DotNet.DesktopRuntime.8 8.0.30  8.0.31    winget\r\n" +
        "Microsoft Windows Desktop Runtime - 8.0.31 (x64) Microsoft.DotNet.DesktopRuntime.8 8.0.31            winget\r\n";

    /// <summary><c>winget list --id PuTTY.PuTTY --exact</c> with two builds registered, before the install (winget's format).</summary>
    private const string PuttyBeforeInstall =
        "Name                          Id          Version  Available Source\r\n" +
        "-------------------------------------------------------------------\r\n" +
        "PuTTY release 0.78 (64-bit)   PuTTY.PuTTY 0.78.0.0 0.85.0.0  winget\r\n" +
        "PuTTY release 0.84 (64-bit)   PuTTY.PuTTY 0.84.0.0 0.85.0.0  winget\r\n";

    /// <summary>The same device after winget upgraded the 0.84 build: 0.78 stays registered.</summary>
    private const string PuttyAfterInstall =
        "Name                          Id          Version  Available Source\r\n" +
        "-------------------------------------------------------------------\r\n" +
        "PuTTY release 0.78 (64-bit)   PuTTY.PuTTY 0.78.0.0 0.85.0.0  winget\r\n" +
        "PuTTY release 0.85 (64-bit)   PuTTY.PuTTY 0.85.0.0           winget\r\n";

    // ---------------------------------------------------------------- the lookup by id

    [Fact]
    public void All_rows_of_the_id_are_found()
    {
        var rows = WingetOutputParser.ParseListOutput(DotNetAfterInstall);
        Assert.Equal(2, WingetOutputParser.FindAllById(rows, "microsoft.dotnet.desktopruntime.8").Count);
    }

    [Fact]
    public void An_older_runtime_left_next_to_the_new_one_does_not_hide_the_install()
    {
        // The Surface's first failure: the check after the install read the 8.0.30 row winget printed first.
        var row = WingetOutputParser.FindById(WingetOutputParser.ParseListOutput(DotNetAfterInstall), "Microsoft.DotNet.DesktopRuntime.8");

        Assert.NotNull(row);
        Assert.Equal("8.0.31", row!.Version);
        Assert.False(row.HasAvailable);   // winget still offers 8.0.31 for the 8.0.30 row; the package is up to date
        Assert.Equal("Microsoft Windows Desktop Runtime - 8.0.31 (x64)", row.Name);
        Assert.False(WingetProvider.IsStillOutdated(row.Version, "8.0.31"));
    }

    [Fact]
    public void The_row_order_does_not_matter()
    {
        var rows = WingetOutputParser.ParseListOutput(DotNetAfterInstall).Reverse().ToList();
        Assert.Equal("8.0.31", WingetOutputParser.FindById(rows, "Microsoft.DotNet.DesktopRuntime.8")!.Version);
    }

    [Fact]
    public void Two_outdated_builds_still_offer_the_update_for_the_newest()
    {
        var row = WingetOutputParser.FindById(WingetOutputParser.ParseListOutput(PuttyBeforeInstall), "PuTTY.PuTTY");

        Assert.Equal("0.84.0.0", row!.Version);
        Assert.Equal("0.85.0.0", row.Available);
    }

    [Fact]
    public void The_update_is_verified_when_the_expected_version_is_among_the_installs()
    {
        // The Surface's second failure: 0.78 stayed registered and was read instead of the new 0.85.
        var row = WingetOutputParser.FindById(WingetOutputParser.ParseListOutput(PuttyAfterInstall), "PuTTY.PuTTY");

        Assert.Equal("0.85.0.0", row!.Version);
        Assert.False(row.HasAvailable);
        Assert.False(WingetProvider.IsStillOutdated(row.Version, "0.85.0.0"));
    }

    [Fact]
    public void An_install_that_did_not_move_the_highest_version_is_still_a_failure()
    {
        // Never report success when the version did not move: 0.84 is still the newest install.
        var row = WingetOutputParser.FindById(WingetOutputParser.ParseListOutput(PuttyBeforeInstall), "PuTTY.PuTTY");
        Assert.True(WingetProvider.IsStillOutdated(row!.Version, "0.85.0.0"));
    }

    // ---------------------------------------------------------------- the combination rule

    [Fact]
    public void A_single_row_is_returned_as_is_and_no_rows_give_null()
    {
        var row = new WingetRow("7-Zip", "7zip.7zip", "26.02", "26.03", "winget");
        Assert.Same(row, WingetOutputParser.CombineInstalls([row]));
        Assert.Null(WingetOutputParser.CombineInstalls([]));
    }

    [Fact]
    public void A_known_version_beats_unknown()
    {
        var unknown = new WingetRow("Tool", "Contoso.Tool", "Unknown", "3.0", "winget");
        var known = new WingetRow("Tool", "Contoso.Tool", "2.0", "", "winget");

        var row = WingetOutputParser.CombineInstalls([unknown, known])!;
        Assert.Equal("2.0", row.Version);
        Assert.Equal("3.0", row.Available);
    }

    [Fact]
    public void The_highest_available_version_is_kept_when_it_is_newer_than_the_newest_install()
    {
        var a = new WingetRow("Tool", "Contoso.Tool", "1.0", "1.5", "winget");
        var b = new WingetRow("Tool", "Contoso.Tool", "1.2", "2.0", "winget");

        var row = WingetOutputParser.CombineInstalls([a, b])!;
        Assert.Equal("1.2", row.Version);
        Assert.Equal("2.0", row.Available);
    }

    [Fact]
    public void A_missing_source_is_taken_from_another_row()
    {
        var sourceless = new WingetRow("Tool", "Contoso.Tool", "2.0", "", "");
        var older = new WingetRow("Tool", "Contoso.Tool", "1.0", "2.0", "winget");
        Assert.Equal("winget", WingetOutputParser.CombineInstalls([sourceless, older])!.Source);
    }

    // ---------------------------------------------------------------- the scan: upgrade listing and name resolution

    [Fact]
    public void An_upgrade_offered_for_an_older_install_is_not_an_update_when_the_newer_one_is_installed()
    {
        // winget upgrade keeps listing 8.0.30 -> 8.0.31 while 8.0.31 is installed too. The scan combines the upgrade row
        // with the list row exactly like this (WingetProvider.CheckAsync), so no endless re-install results.
        var upgradeRow = new WingetRow("Microsoft Windows Desktop Runtime - 8.0.30 (x64)", "Microsoft.DotNet.DesktopRuntime.8", "8.0.30", "8.0.31", "winget");
        var listRow = WingetOutputParser.FindById(WingetOutputParser.ParseListOutput(DotNetAfterInstall), "Microsoft.DotNet.DesktopRuntime.8")!;

        var picked = WingetProvider.PickFromUpgradeListing([upgradeRow], ["Microsoft.DotNet.DesktopRuntime.8"]);
        var row = WingetOutputParser.CombineInstalls([picked!, listRow])!;

        Assert.Equal("8.0.31", row.Version);
        Assert.False(row.HasAvailable);
    }

    [Fact]
    public void The_scan_and_the_check_after_the_install_read_the_same_version()
    {
        // Before: the scan took 0.84 from the upgrade listing, the check after the install the first list row (0.78).
        var upgradeRows = new List<WingetRow>
        {
            new("PuTTY release 0.84 (64-bit)", "PuTTY.PuTTY", "0.84.0.0", "0.85.0.0", "winget"),
            new("PuTTY release 0.78 (64-bit)", "PuTTY.PuTTY", "0.78.0.0", "0.85.0.0", "winget"),
        };
        var listRow = WingetOutputParser.FindById(WingetOutputParser.ParseListOutput(PuttyBeforeInstall), "PuTTY.PuTTY")!;
        var picked = WingetProvider.PickFromUpgradeListing(upgradeRows, ["PuTTY.PuTTY"])!;
        var scanned = WingetOutputParser.CombineInstalls([picked, listRow])!;

        Assert.Equal("0.84.0.0", picked.Version);
        Assert.Equal("0.84.0.0", scanned.Version);
        Assert.Equal("0.85.0.0", scanned.Available);
        Assert.Equal(listRow.Version, scanned.Version);
    }

    [Fact]
    public void Resolution_by_name_combines_the_installs_of_the_chosen_id()
    {
        var app = new AppPolicy { AppId = "putty", DisplayName = "PuTTY release", WingetId = "SimonTatham.PuTTY" };
        var rows = new List<WingetRow>
        {
            new("PuTTY release 0.78 (64-bit)", "PuTTY.PuTTY", "0.78.0.0", "0.85.0.0", "winget"),
            new("PuTTY release 0.84 (64-bit)", "PuTTY.PuTTY", "0.84.0.0", "0.85.0.0", "winget"),
        };

        var row = WingetProvider.ResolveByName(rows, app, WingetProvider.SplitIds(app.WingetId));
        Assert.Equal("PuTTY.PuTTY", row!.Id);
        Assert.Equal("0.84.0.0", row.Version);
        Assert.Equal("0.85.0.0", row.Available);
    }

    [Fact]
    public void The_registry_inventory_also_takes_the_highest_matching_install()
    {
        var app = new AppPolicy { AppId = "desktopruntime8", DisplayName = "Microsoft Windows Desktop Runtime", DetectDisplayNameRegex = @"^Microsoft Windows Desktop Runtime - 8\." };
        var older = new InstalledApp { DisplayName = "Microsoft Windows Desktop Runtime - 8.0.30 (x64)", DisplayVersion = "8.0.30", Context = InstallContext.System };
        var newer = new InstalledApp { DisplayName = "Microsoft Windows Desktop Runtime - 8.0.31 (x64)", DisplayVersion = "8.0.31", Context = InstallContext.System };

        Assert.Same(newer, InstalledAppScanner.Match(app, [older, newer]).First());
    }
}
