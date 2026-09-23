using Arkimentum.AppMonitor.Install;
using Arkimentum.AppMonitor.Inventory;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Providers;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// Generic winget id resolution: the winget id is the id of the row in winget's own listing whose name passes the
/// app's identity rule; configured ids are hints that take precedence. Cases verified on devices: the Firefox MSIX
/// build (Mozilla.Firefox.MSIX), Chrome installed per user (Google.Chrome.EXE), Adobe Reader 32-bit
/// (Adobe.Acrobat.Reader.32-bit for a policy naming the 64-bit id).
/// </summary>
public class GenericWingetIdResolutionTests
{
    private static readonly AppPolicy Firefox = new() { AppId = "firefox", DisplayName = "Mozilla Firefox", WingetId = "Mozilla.Firefox", DetectDisplayNameRegex = "^Mozilla Firefox" };
    private static readonly AppPolicy Chrome = new() { AppId = "chrome", DisplayName = "Google Chrome", WingetId = "Google.Chrome", DetectDisplayNameRegex = "^Google Chrome$" };
    private static readonly AppPolicy Reader = new() { AppId = "acrobatreader", DisplayName = "Adobe Acrobat Reader", WingetId = "Adobe.Acrobat.Reader.64-bit", DetectDisplayNameRegex = "^Adobe Acrobat" };
    private static readonly AppPolicy Git = new() { AppId = "git", DisplayName = "Git", WingetId = "Git.Git", DetectDisplayNameRegex = @"^Git\b" };

    private static IReadOnlyList<string> Ids(AppPolicy app) => WingetProvider.SplitIds(app.WingetId);

    // ---------------------------------------------------------------- the name rule

    [Theory]
    [InlineData("Google Chrome", true)]
    [InlineData("google chrome", true)]
    [InlineData("Google Chrome Beta", false)]
    [InlineData("Google Chrome…", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Name_rule_uses_the_detection_regex_when_set(string? name, bool expected)
    {
        Assert.Equal(expected, InstalledAppScanner.NameMatches(Chrome, name));
    }

    [Theory]
    [InlineData("Notepad++", true)]
    [InlineData("Notepad++ (64-bit x64)", true)]
    [InlineData("notepad++ 8.9.8", true)]
    [InlineData("Notepad++Portable", false)]
    [InlineData("My Notepad++", false)]
    public void Without_a_regex_the_display_name_is_an_anchored_whole_word_prefix(string name, bool expected)
    {
        var app = new AppPolicy { AppId = "npp", DisplayName = "Notepad++" };
        Assert.Equal(expected, InstalledAppScanner.NameMatches(app, name));
    }

    [Fact]
    public void An_invalid_regex_matches_nothing()
    {
        var app = new AppPolicy { AppId = "broken", DisplayName = "Google Chrome", DetectDisplayNameRegex = "^Google (Chrome" };
        Assert.False(InstalledAppScanner.NameMatches(app, "Google Chrome"));
    }

    [Fact]
    public void An_app_without_regex_or_display_name_matches_nothing()
    {
        Assert.False(InstalledAppScanner.NameMatches(new AppPolicy { AppId = "empty" }, "Anything"));
    }

    [Fact]
    public void The_publisher_regex_is_ignored_for_winget_rows()
    {
        // winget's listings have no publisher column: a policy that also pins the publisher still resolves by name.
        var app = Chrome with { DetectPublisherRegex = "^Google LLC$" };
        Assert.True(InstalledAppScanner.NameMatches(app, "Google Chrome"));
        var row = WingetProvider.ResolveByName([new WingetRow("Google Chrome", "Google.Chrome.EXE", "154.0.8037.58", "", "winget")], app, Ids(app));
        Assert.Equal("Google.Chrome.EXE", row?.Id);
    }

    [Fact]
    public void The_registry_inventory_match_still_applies_the_publisher()
    {
        var app = Chrome with { DetectPublisherRegex = "^Google LLC$" };
        var google = new InstalledApp { DisplayName = "Google Chrome", Publisher = "Google LLC", Context = InstallContext.System };
        var other = new InstalledApp { DisplayName = "Google Chrome", Publisher = "Someone Else", Context = InstallContext.System };
        Assert.Same(google, Assert.Single(InstalledAppScanner.Match(app, [google, other])));
    }

    // ---------------------------------------------------------------- the three real cases

    [Fact]
    public void Firefox_msix_row_is_picked_by_id_when_configured()
    {
        var msix = new WingetRow("Mozilla Firefox", "Mozilla.Firefox.MSIX", "156.0.0.0", "156.0.1", "winget");
        var app = Firefox with { WingetId = "Mozilla.Firefox;Mozilla.Firefox.MSIX" };
        var installed = new WingetRow("Mozilla Firefox", "Mozilla.Firefox", "156.0.0.0", "", "winget");
        Assert.Same(msix, WingetProvider.PickFromUpgradeListing([msix], Ids(app), app, installed));
    }

    [Fact]
    public void Firefox_msix_row_is_picked_by_name_without_the_hand_added_alternative()
    {
        // "winget list --id Mozilla.Firefox" correlates the MSIX build; the upgrade listing names the MSIX id.
        var msix = new WingetRow("Mozilla Firefox", "Mozilla.Firefox.MSIX", "156.0.0.0", "156.0.1", "winget");
        var installed = new WingetRow("Mozilla Firefox", "Mozilla.Firefox", "156.0.0.0", "", "winget");
        Assert.Same(msix, WingetProvider.PickFromUpgradeListing([msix], Ids(Firefox), Firefox, installed));
    }

    [Fact]
    public void Per_user_chrome_is_picked_by_name_for_configured_google_chrome()
    {
        // Only Google.Chrome.EXE has a user-scope installer; Google.Chrome is machine MSI only.
        var exe = new WingetRow("Google Chrome", "Google.Chrome.EXE", "154.0.8037.58", "155.0.7000.10", "winget");
        var rows = new List<WingetRow> { new("7-Zip", "7zip.7zip", "26.02", "26.03", "winget"), exe };

        Assert.Same(exe, WingetProvider.PickFromUpgradeListing(rows, Ids(Chrome), Chrome, installed: null));
        Assert.Same(exe, WingetProvider.ResolveByName(rows, Chrome, Ids(Chrome)));
    }

    [Fact]
    public void Reader_32_bit_is_resolved_for_a_policy_naming_the_64_bit_id()
    {
        var listing = new List<WingetRow>
        {
            new("Microsoft Edge", "Microsoft.Edge", "140.0.3485.54", "", "winget"),
            new("Adobe Acrobat Reader (32-bit)", "Adobe.Acrobat.Reader.32-bit", "25.001.20531", "26.002.21931", "winget"),
        };
        var row = WingetProvider.ResolveByName(listing, Reader, Ids(Reader));
        Assert.NotNull(row);
        Assert.Equal("Adobe.Acrobat.Reader.32-bit", row!.Id);
        Assert.Equal("25.001.20531", row.Version);

        // ... and the upgrade listing then names the same id for that install.
        Assert.Same(listing[1], WingetProvider.PickFromUpgradeListing(listing, Ids(Reader), Reader, row));
    }

    [Fact]
    public void Chrome_beta_is_not_google_chrome()
    {
        var beta = new WingetRow("Google Chrome Beta", "Google.Chrome.Beta", "155.0.7000.10", "156.0.7100.5", "winget");
        Assert.Null(WingetProvider.PickFromUpgradeListing([beta], Ids(Chrome), Chrome, installed: null));
        Assert.Null(WingetProvider.ResolveByName([beta], Chrome, Ids(Chrome)));
    }

    // ---------------------------------------------------------------- precedence and exclusions

    [Fact]
    public void A_configured_id_beats_a_name_match()
    {
        var byName = new WingetRow("Mozilla Firefox", "Mozilla.Firefox.MSIX", "156.0.0.0", "156.0.1", "winget");
        var byId = new WingetRow("Firefox Browser", "Mozilla.Firefox", "155.0", "156.0.1", "winget");
        Assert.Same(byId, WingetProvider.PickFromUpgradeListing([byName, byId], Ids(Firefox), Firefox, installed: null));
    }

    [Fact]
    public void Without_an_app_only_configured_ids_are_picked()
    {
        var msix = new WingetRow("Mozilla Firefox", "Mozilla.Firefox.MSIX", "156.0.0.0", "156.0.1", "winget");
        Assert.Null(WingetProvider.PickFromUpgradeListing([msix], ["Mozilla.Firefox"]));
    }

    [Fact]
    public void Rows_without_an_available_version_are_not_picked_from_the_upgrade_listing()
    {
        var row = new WingetRow("Google Chrome", "Google.Chrome.EXE", "154.0.8037.58", "", "winget");
        Assert.Null(WingetProvider.PickFromUpgradeListing([row], Ids(Chrome), Chrome, installed: null));
    }

    [Theory]
    [InlineData(@"MSIX\GoogleChrome_154.0.8037.58_x64__abc")]
    [InlineData(@"ARP\User\X64\Google Chrome")]
    [InlineData("Google.Chrome.E…")]
    public void Pseudo_and_truncated_ids_are_never_picked(string id)
    {
        var row = new WingetRow("Google Chrome", id, "154.0.8037.58", "155.0.7000.10", "winget");
        Assert.Null(WingetProvider.PickFromUpgradeListing([row], Ids(Chrome), Chrome, installed: null));
        Assert.Null(WingetProvider.ResolveByName([row], Chrome, Ids(Chrome)));
    }

    [Fact]
    public void Sourceless_rows_are_not_resolved_from_the_full_listing()
    {
        var row = new WingetRow("Google Chrome", "Google.Chrome.EXE", "154.0.8037.58", "", "");
        Assert.Null(WingetProvider.ResolveByName([row], Chrome, Ids(Chrome)));
    }

    [Fact]
    public void A_related_product_in_the_upgrade_listing_does_not_take_over_an_up_to_date_install()
    {
        // Git.Git is installed and current; "Git Extensions" passes ^Git\b and has an upgrade.
        var installed = new WingetRow("Git", "Git.Git", "2.55.0.3", "", "winget");
        var extensions = new WingetRow("Git Extensions", "GitExtensionsTeam.GitExtensions", "5.2.1", "5.3.0", "winget");
        Assert.Null(WingetProvider.PickFromUpgradeListing([extensions], Ids(Git), Git, installed));
    }

    // ---------------------------------------------------------------- tie-break

    [Fact]
    public void Several_name_matches_prefer_the_id_closest_to_the_configured_one()
    {
        var app = new AppPolicy { AppId = "chrome", DisplayName = "Google Chrome", WingetId = "Google.Chrome" };  // whole-word rule
        var other = new WingetRow("Google Chrome Helper", "Acme.ChromeHelper", "9.0", "9.1", "winget");
        var exe = new WingetRow("Google Chrome", "Google.Chrome.EXE", "154.0", "155.0", "winget");
        Assert.Same(exe, WingetProvider.PickByName([other, exe], app, Ids(app)));
        Assert.Same(exe, WingetProvider.PickByName([exe, other], app, Ids(app)));
    }

    [Fact]
    public void Equal_prefixes_prefer_the_highest_installed_version_then_the_id()
    {
        var older = new WingetRow("Adobe Acrobat Reader (32-bit)", "Adobe.Acrobat.Reader.32-bit", "24.0", "", "winget");
        var newer = new WingetRow("Adobe Acrobat (64-bit)", "Adobe.Acrobat.Pro", "26.0", "", "winget");
        // The id prefix comes before the version: Adobe.Acrobat.Reader.32-bit shares three segments with the configured
        // Adobe.Acrobat.Reader.64-bit, Adobe.Acrobat.Pro only two.
        Assert.Same(older,WingetProvider.PickByName([newer, older], Reader, Ids(Reader)));

        var a = new WingetRow("Adobe Acrobat", "Adobe.Acrobat.Reader.A", "26.0", "", "winget");
        var b = new WingetRow("Adobe Acrobat", "Adobe.Acrobat.Reader.B", "25.0", "", "winget");
        var c = new WingetRow("Adobe Acrobat", "Adobe.Acrobat.Reader.C", "26.0", "", "winget");
        Assert.Same(a, WingetProvider.PickByName([b, c, a], Reader, Ids(Reader)));
    }

    [Theory]
    [InlineData("Google.Chrome.EXE", "Google.Chrome", 2)]
    [InlineData("google.chrome", "Google.Chrome", 2)]
    [InlineData("Adobe.Acrobat.Reader.32-bit", "Adobe.Acrobat.Reader.64-bit", 3)]
    [InlineData("Acme.Chrome", "Google.Chrome", 0)]
    [InlineData("Google.Chrome", "", 0)]
    public void Shared_id_segments_are_counted_from_the_start(string a, string b, int expected)
    {
        Assert.Equal(expected, WingetProvider.SharedIdSegments(a, b));
    }

    // ---------------------------------------------------------------- install candidates

    [Fact]
    public async Task A_configured_id_that_is_not_installed_is_skipped_and_the_resolved_ids_fallback_runs()
    {
        // Scan resolved Adobe.Acrobat.Reader.32-bit; the configured 64-bit id is not installed here.
        var calls = new List<string>();
        var result = await WingetProvider.RunCandidatesAsync(
            ["Adobe.Acrobat.Reader.32-bit", "Adobe.Acrobat.Reader.64-bit"],
            id =>
            {
                calls.Add("upgrade " + id);
                return Task.FromResult(id.EndsWith("32-bit")
                    ? WingetProvider.UpgradeOutcome.Refused(new WingetProvider.UpgradeRefusal(id, WingetOutputParser.ExitNoApplicableUpgrade, "25.0", InstallResult.Fail("refused", WingetOutputParser.ExitNoApplicableUpgrade)))
                    : WingetProvider.UpgradeOutcome.NotInstalled(InstallResult.Fail("not installed", WingetOutputParser.ExitNoInstalledPackageFound)));
            },
            refusal =>
            {
                calls.Add("fallback " + refusal.WingetId);
                return Task.FromResult<InstallResult?>(InstallResult.Ok("Installed successfully."));
            });

        Assert.True(result.Success);
        Assert.Equal(["upgrade Adobe.Acrobat.Reader.32-bit", "upgrade Adobe.Acrobat.Reader.64-bit", "fallback Adobe.Acrobat.Reader.32-bit"], calls);
    }

    [Fact]
    public async Task When_no_id_is_installed_the_first_not_installed_answer_is_the_result()
    {
        var result = await WingetProvider.RunCandidatesAsync(
            ["A", "B"],
            id => Task.FromResult(WingetProvider.UpgradeOutcome.NotInstalled(InstallResult.Fail("not installed " + id, WingetOutputParser.ExitNoInstalledPackageFound))),
            _ => Task.FromResult<InstallResult?>(InstallResult.Ok("must not run")));

        Assert.False(result.Success);
        Assert.Equal("not installed A", result.Message);
    }

    [Theory]
    [InlineData(unchecked((int)0x8A150014), "", true)]
    [InlineData(1, "No installed package found matching input criteria.", true)]
    [InlineData(0, "No installed package found matching input criteria.", false)]
    [InlineData(unchecked((int)0x8A15002B), "No applicable installed packages", false)]
    [InlineData(unchecked((int)0x8A150010), "No applicable installer found", false)]
    public void Not_installed_is_recognised_by_code_or_message(int exitCode, string output, bool expected)
    {
        Assert.Equal(expected, WingetProvider.IsIdNotInstalled(exitCode, output));
    }

    // ---------------------------------------------------------------- portable installers

    private const string NotepadShow = """
        Found Notepad++ [Notepad++.Notepad++]
        Version: 8.9.8
        Publisher: Notepad++ Team
        Tags:
          editor
        Installer:
          Installer Type: portable (zip)
          Installer Url: https://github.com/notepad-plus-plus/notepad-plus-plus/releases/download/v8.9.8/npp.8.9.8.portable.x64.zip
          Installer SHA256: b269383239464a945d17cfabfccf53935b83d80d907922310fdfd50d80274c66
        """;

    [Fact]
    public void Portable_zip_installer_type_is_parsed_and_recognised()
    {
        var type = WingetProvider.ParseInstallerType(NotepadShow);
        Assert.Equal("portable (zip)", type);
        Assert.True(WingetProvider.IsPortable(type));
    }

    [Theory]
    [InlineData("Installer:\r\n  Installer Type: exe\r\n  Installer Url: https://x/y.exe", "exe")]
    [InlineData("Installer:\n  Installer Type: msix\n", "msix")]
    [InlineData("Installer:\n  Installer Type: Portable\n", "Portable")]
    [InlineData("Installer:\n  Nested Installer Type: portable\n  Installer Type: zip\n", "zip")]
    public void Installer_type_is_read_from_its_own_line(string output, string expected)
    {
        Assert.Equal(expected, WingetProvider.ParseInstallerType(output));
    }

    [Theory]
    [InlineData("Installer:\n  No applicable installer found; see logs for more details.")]
    [InlineData("")]
    [InlineData(null)]
    public void A_missing_installer_type_line_yields_null(string? output)
    {
        Assert.Null(WingetProvider.ParseInstallerType(output));
        Assert.False(WingetProvider.IsPortable(WingetProvider.ParseInstallerType(output)));
    }

    [Theory]
    [InlineData("exe", false)]
    [InlineData("msix", false)]
    [InlineData("nullsoft", false)]
    [InlineData("zip", false)]
    [InlineData("portable", true)]
    [InlineData("portable (zip)", true)]
    [InlineData(null, false)]
    public void Only_portable_types_are_portable(string? type, bool expected)
    {
        Assert.Equal(expected, WingetProvider.IsPortable(type));
    }

    [Fact]
    public void The_portable_message_names_the_second_copy()
    {
        var message = WingetProvider.MachineOnlyMessage("Notepad++.Notepad++", portableSkipped: true);
        Assert.Contains("'Notepad++.Notepad++'", message);
        Assert.Contains("portable", message);
        Assert.Contains("administrator rights", message);
    }
}
