using Arkimentum.AppMonitor.Install;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Providers;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// The agent must never cause a UAC prompt in a user's session. Incident: the tray retried a refused Firefox upgrade
/// as an unscoped "winget install --force Mozilla.Firefox", whose only installer is machine-wide, so Windows asked
/// for elevation - while the second configured id (Mozilla.Firefox.MSIX) would have upgraded the MSIX build silently.
/// </summary>
public class NoElevationInUserContextTests
{
    private static readonly ExecutionContextInfo User = new() { IsSystem = false, SessionId = 1, UserSid = "S-1-5-21-1-2-3-1001" };

    [Theory]
    [InlineData(WingetOutputParser.ExitNoApplicableUpgrade)]
    [InlineData(WingetOutputParser.ExitUpdateInstallTechnologyMismatch)]
    public void Refusals_send_the_install_on_to_the_next_id(int exitCode)
    {
        Assert.True(WingetProvider.IsRefusal(exitCode));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(WingetOutputParser.ExitNoApplicableInstaller)]
    [InlineData(-1)]
    public void Other_exit_codes_are_not_refusals(int exitCode)
    {
        Assert.False(WingetProvider.IsRefusal(exitCode));
    }

    [Fact]
    public void No_applicable_installer_has_wingets_value()
    {
        Assert.Equal(unchecked((int)0x8A150010), WingetOutputParser.ExitNoApplicableInstaller);
    }

    [Fact]
    public void User_context_installs_are_filtered_to_user_scope_then_msix_and_never_unscoped()
    {
        Assert.Equal(2, WingetProvider.UserContextInstallAttempts);
        Assert.Equal(" --scope user", WingetProvider.UserContextInstallFilter(0));
        Assert.Equal(" --installer-type msix", WingetProvider.UserContextInstallFilter(1));
        for (var attempt = 0; attempt < WingetProvider.UserContextInstallAttempts; attempt++)
        {
            var filter = WingetProvider.UserContextInstallFilter(attempt);
            Assert.False(string.IsNullOrWhiteSpace(filter));
            Assert.DoesNotContain("machine", filter, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => WingetProvider.UserContextInstallFilter(2));
    }

    [Theory]
    [InlineData(unchecked((int)0x8A150010), "", true)]
    [InlineData(1, "No applicable installer found; see logs for more details.", true)]
    // winget show (1.30) prints the message under "Installer:" and still exits 0.
    [InlineData(0, "Installer:\n  No applicable installer found; see logs for more details.", true)]
    [InlineData(0, "Found Mozilla Firefox [Mozilla.Firefox.MSIX]", false)]
    [InlineData(unchecked((int)0x8A15002B), "No available upgrade found.", false)]
    public void No_applicable_installer_is_recognised_by_code_or_message(int exitCode, string output, bool expected)
    {
        Assert.Equal(expected, WingetProvider.IsNoApplicableInstaller(exitCode, output));
    }

    [Fact]
    public void Children_started_in_the_user_context_run_as_invoker()
    {
        var env = ProcessRunner.ChildEnvironment(User);
        Assert.NotNull(env);
        Assert.Equal("RunAsInvoker", env!["__COMPAT_LAYER"]);
    }

    [Fact]
    public void Children_started_by_the_service_get_no_extra_environment()
    {
        Assert.Null(ProcessRunner.ChildEnvironment(ExecutionContextInfo.System));
    }

    [Fact]
    public void Machine_only_message_names_the_package_and_the_reason()
    {
        var message = WingetProvider.MachineOnlyMessage("Mozilla.Firefox");
        Assert.Contains("'Mozilla.Firefox'", message);
        Assert.Contains("administrator rights", message);
    }

    // ---------------------------------------------------------------- id from winget's upgrade listing

    private static readonly WingetRow FirefoxMsixUpgradeRow = new("Mozilla Firefox", "Mozilla.Firefox.MSIX", "156.0.0.0", "156.0.1", "winget");

    [Fact]
    public void The_upgrade_listing_names_the_msix_id_although_list_matched_the_classic_one()
    {
        // "winget list --id Mozilla.Firefox" matched the MSIX build; "winget upgrade --scope user" lists it only here.
        var row = WingetProvider.PickFromUpgradeListing([FirefoxMsixUpgradeRow], ["Mozilla.Firefox", "Mozilla.Firefox.MSIX"]);

        Assert.NotNull(row);
        Assert.Equal("Mozilla.Firefox.MSIX", row!.Id);
        Assert.Equal("156.0.0.0", row.Version);
        Assert.Equal("156.0.1", row.Available);
    }

    [Fact]
    public void No_configured_id_in_the_upgrade_listing_keeps_the_list_match()
    {
        // Perplexity.Comet / Microsoft.BingWallpaper: refused by winget upgrade, still detected via winget list.
        var rows = new List<WingetRow> { FirefoxMsixUpgradeRow, new("7-Zip", "7zip.7zip", "26.02", "26.03", "winget") };
        Assert.Null(WingetProvider.PickFromUpgradeListing(rows, ["Perplexity.Comet"]));
        Assert.Null(WingetProvider.PickFromUpgradeListing([], ["Mozilla.Firefox"]));
    }

    [Fact]
    public void The_first_candidate_in_order_wins_when_several_are_listed()
    {
        var rows = new List<WingetRow> { FirefoxMsixUpgradeRow, new("Mozilla Firefox", "Mozilla.Firefox", "155.0", "156.0.1", "winget") };
        Assert.Equal("Mozilla.Firefox", WingetProvider.PickFromUpgradeListing(rows, ["Mozilla.Firefox", "Mozilla.Firefox.MSIX"])!.Id);
        Assert.Equal("Mozilla.Firefox.MSIX", WingetProvider.PickFromUpgradeListing(rows, ["Mozilla.Firefox.MSIX", "Mozilla.Firefox"])!.Id);
    }

    [Fact]
    public void Upgrade_listing_ids_are_compared_case_insensitively()
    {
        Assert.Same(FirefoxMsixUpgradeRow, WingetProvider.PickFromUpgradeListing([FirefoxMsixUpgradeRow], ["mozilla.firefox.msix"]));
    }

    [Fact]
    public void A_truncated_id_in_the_upgrade_listing_is_ignored()
    {
        var truncated = new WingetRow("Mozilla Firefox", "Mozilla.Firefox…", "156.0.0.0", "156.0.1", "winget");
        Assert.Null(WingetProvider.PickFromUpgradeListing([truncated], ["Mozilla.Firefox", "Mozilla.Firefox.MSIX"]));
    }

    // ---------------------------------------------------------------- candidate order

    private static WingetProvider.UpgradeOutcome Refused(string id, int code = WingetOutputParser.ExitNoApplicableUpgrade) =>
        WingetProvider.UpgradeOutcome.Refused(new WingetProvider.UpgradeRefusal(id, code, "156.0.0.0", InstallResult.Fail($"refused {id}", code)));

    [Fact]
    public async Task The_second_id_is_upgraded_before_any_fallback_runs()
    {
        var calls = new List<string>();
        var result = await WingetProvider.RunCandidatesAsync(
            ["Mozilla.Firefox", "Mozilla.Firefox.MSIX"],
            id =>
            {
                calls.Add("upgrade " + id);
                return Task.FromResult(id == "Mozilla.Firefox"
                    ? Refused(id)
                    : WingetProvider.UpgradeOutcome.Final(InstallResult.Ok("Installed successfully.") with { InstalledVersion = "156.0.1" }));
            },
            refusal =>
            {
                calls.Add("fallback " + refusal.WingetId);
                return Task.FromResult<InstallResult?>(InstallResult.Fail("must not run"));
            });

        Assert.True(result.Success);
        Assert.Equal(["upgrade Mozilla.Firefox", "upgrade Mozilla.Firefox.MSIX"], calls);
    }

    [Fact]
    public async Task Fallbacks_run_in_candidate_order_only_after_every_id_refused()
    {
        var calls = new List<string>();
        var result = await WingetProvider.RunCandidatesAsync(
            ["A", "B"],
            id => { calls.Add("upgrade " + id); return Task.FromResult(Refused(id)); },
            refusal =>
            {
                calls.Add("fallback " + refusal.WingetId);
                // A has only a machine-wide installer: nothing ran, so B's fallback is tried.
                return Task.FromResult<InstallResult?>(refusal.WingetId == "A"
                    ? InstallResult.Fail("machine only", WingetOutputParser.ExitNoApplicableInstaller)
                    : InstallResult.Ok("Installed successfully."));
            });

        Assert.True(result.Success);
        Assert.Equal(["upgrade A", "upgrade B", "fallback A", "fallback B"], calls);
    }

    [Fact]
    public async Task A_fallback_that_ran_something_and_failed_is_final()
    {
        var calls = new List<string>();
        var result = await WingetProvider.RunCandidatesAsync(
            ["A", "B"],
            id => Task.FromResult(Refused(id)),
            refusal =>
            {
                calls.Add("fallback " + refusal.WingetId);
                return Task.FromResult<InstallResult?>(InstallResult.Fail("installer failed", 1603));
            });

        Assert.False(result.Success);
        Assert.Equal(1603, result.ExitCode);
        Assert.Equal(["fallback A"], calls);
    }

    [Fact]
    public async Task Without_an_applicable_fallback_the_refusal_is_reported()
    {
        var result = await WingetProvider.RunCandidatesAsync(
            ["A", "B"],
            id => Task.FromResult(Refused(id)),
            _ => Task.FromResult<InstallResult?>(null));

        Assert.False(result.Success);
        Assert.Equal("refused B", result.Message);
    }

    [Fact]
    public async Task A_real_failure_of_the_first_id_is_final()
    {
        var calls = new List<string>();
        var result = await WingetProvider.RunCandidatesAsync(
            ["A", "B"],
            id => { calls.Add(id); return Task.FromResult(WingetProvider.UpgradeOutcome.Final(InstallResult.Fail("network", 5))); },
            _ => Task.FromResult<InstallResult?>(null));

        Assert.False(result.Success);
        Assert.Equal(["A"], calls);
    }
}
