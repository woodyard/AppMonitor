using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Providers;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// When a refused "winget upgrade" is answered with the configured take-over (winget uninstall + winget install).
/// The rule is deliberately narrow: only winget's "the install technology is different" refusal (0x8A15008E), only
/// when the application opted in with WingetReplaceOnMismatch, and only while the product really is still outdated -
/// the take-over removes the current install first, so it must never run on a hunch.
/// </summary>
public class WingetReplaceRuleTests
{
    private const int Mismatch = WingetOutputParser.ExitUpdateInstallTechnologyMismatch;

    [Fact]
    public void An_outdated_product_with_the_flag_set_is_replaced()
    {
        Assert.True(WingetProvider.ShouldReplace(Mismatch, replaceEnabled: true, stillOutdated: true));
    }

    [Fact]
    public void Nothing_happens_without_the_flag()
    {
        Assert.False(WingetProvider.ShouldReplace(Mismatch, replaceEnabled: false, stillOutdated: true));
    }

    [Fact]
    public void Not_when_the_product_turned_out_to_be_current()
    {
        Assert.False(WingetProvider.ShouldReplace(Mismatch, replaceEnabled: true, stillOutdated: false));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(WingetOutputParser.ExitNoApplicableUpgrade)]
    [InlineData(WingetOutputParser.ExitNoInstalledPackageFound)]
    [InlineData(WingetOutputParser.ExitRebootRequiredToFinish)]
    [InlineData(-1)]
    public void Other_outcomes_are_left_alone(int exitCode)
    {
        // Notably ExitNoApplicableUpgrade: that refusal is about scope/installer filters, not about the installed
        // technology, and uninstalling the product would be the wrong answer to it.
        Assert.False(WingetProvider.ShouldReplace(exitCode, replaceEnabled: true, stillOutdated: true));
    }

    [Fact]
    public void The_take_over_is_allowed_in_both_contexts()
    {
        // Unlike the reinstall rule, ShouldReplace takes no context: an MSI-to-exe or exe-to-MSIX migration is just
        // as common machine-wide as per user, so the caller runs it as LocalSystem too.
        Assert.True(WingetProvider.ShouldReplace(Mismatch, replaceEnabled: true, stillOutdated: true));
        Assert.False(WingetProvider.ShouldReinstall(Mismatch, ExecutionContextInfo.System, stillOutdated: true));
    }

    [Fact]
    public void Several_registered_versions_make_the_uninstall_retry_with_all_versions()
    {
        // winget refuses to choose between them ("Multiple versions of this package are installed"); replacing
        // whatever is on the device is the point of the take-over, so every registered version goes.
        Assert.True(WingetProvider.ShouldUninstallAllVersions(WingetOutputParser.ExitMultiplePackagesFound));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(WingetOutputParser.ExitNoInstalledPackageFound)]
    [InlineData(WingetOutputParser.ExitUpdateInstallTechnologyMismatch)]
    [InlineData(-1)]
    public void No_other_uninstall_outcome_is_retried_with_all_versions(int exitCode)
    {
        Assert.False(WingetProvider.ShouldUninstallAllVersions(exitCode));
    }

    [Fact]
    public void The_multiple_packages_exit_code_is_winget_s()
    {
        // APPINSTALLER_CLI_ERROR_MULTIPLE_INSTALL_FOUND, as winget prints it.
        Assert.Equal(unchecked((int)0x8A150016), WingetOutputParser.ExitMultiplePackagesFound);
    }

    [Fact]
    public void A_clean_uninstall_counts_as_removed()
    {
        Assert.True(WingetProvider.RemovalSucceeded(0, notInstalledAfter: false, versionAfter: "29.26.0", versionBefore: "29.26.0"));
    }

    [Fact]
    public void A_package_winget_no_longer_lists_counts_as_removed()
    {
        // Whatever the exit code said: nothing is installed any more, so the install step can go ahead.
        Assert.True(WingetProvider.RemovalSucceeded(-1, notInstalledAfter: true, versionAfter: null, versionBefore: "29.26.0"));
    }

    [Fact]
    public void A_moved_version_counts_as_removed_even_when_one_uninstall_failed()
    {
        // The real case: three registrations of JanDeDobbeleer.OhMyPosh, one resisted, winget failed the whole run -
        // but the registration the upgrade complained about is gone.
        Assert.True(WingetProvider.RemovalSucceeded(
            WingetOutputParser.ExitMultipleUninstallFailed, notInstalledAfter: false, versionAfter: "31.3.0", versionBefore: "29.26.0"));
    }

    [Fact]
    public void The_same_version_still_listed_is_a_failure()
    {
        Assert.False(WingetProvider.RemovalSucceeded(
            WingetOutputParser.ExitMultipleUninstallFailed, notInstalledAfter: false, versionAfter: "29.26.0", versionBefore: "29.26.0"));
    }

    [Fact]
    public void An_unreadable_listing_after_a_failed_uninstall_is_a_failure()
    {
        // "Unknown" is not "gone": the take-over must not install over an install it cannot account for.
        Assert.False(WingetProvider.RemovalSucceeded(-1, notInstalledAfter: false, versionAfter: null, versionBefore: "29.26.0"));
    }

    [Fact]
    public void The_multiple_uninstall_failed_exit_code_is_winget_s()
    {
        // APPINSTALLER_CLI_ERROR_MULTIPLE_UNINSTALL_FAILED, as winget prints it.
        Assert.Equal(unchecked((int)0x8A150066), WingetOutputParser.ExitMultipleUninstallFailed);
    }

    [Fact]
    public void With_the_flag_set_the_take_over_wins_over_the_reinstall()
    {
        // Same situation, user context: the reinstall rule would also fire here ("winget install --force"), which
        // leaves the old install in place. WingetProvider evaluates ShouldReplace first, so the take-over decides.
        var user = new ExecutionContextInfo { IsSystem = false, SessionId = 1, UserSid = "S-1-5-21-1-2-3-1001" };
        Assert.True(WingetProvider.ShouldReinstall(Mismatch, user, stillOutdated: true));
        Assert.True(WingetProvider.ShouldReplace(Mismatch, replaceEnabled: true, stillOutdated: true));
    }
}
