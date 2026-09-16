using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Providers;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// When a refused "winget upgrade" is retried as "winget install --force". The retry exists for per-user installs
/// winget did not create (a manifest that only declares a machine-scope installer, or an installer type that differs
/// from the technology the ARP entry was created with); it must never run as LocalSystem, where a user-scope
/// installer would land in SYSTEM's profile, and never when the product is already current.
/// </summary>
public class WingetReinstallRuleTests
{
    private static readonly ExecutionContextInfo User = new() { IsSystem = false, SessionId = 1, UserSid = "S-1-5-21-1-2-3-1001" };

    [Theory]
    [InlineData(WingetOutputParser.ExitNoApplicableUpgrade)]
    [InlineData(WingetOutputParser.ExitUpdateInstallTechnologyMismatch)]
    public void A_refused_upgrade_of_an_outdated_per_user_install_is_reinstalled(int exitCode)
    {
        Assert.True(WingetProvider.ShouldReinstall(exitCode, User, stillOutdated: true));
    }

    [Theory]
    [InlineData(WingetOutputParser.ExitNoApplicableUpgrade)]
    [InlineData(WingetOutputParser.ExitUpdateInstallTechnologyMismatch)]
    public void Never_as_LocalSystem(int exitCode)
    {
        Assert.False(WingetProvider.ShouldReinstall(exitCode, ExecutionContextInfo.System, stillOutdated: true));
    }

    [Fact]
    public void Not_when_the_product_turned_out_to_be_current()
    {
        Assert.False(WingetProvider.ShouldReinstall(WingetOutputParser.ExitNoApplicableUpgrade, User, stillOutdated: false));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(WingetOutputParser.ExitNoInstalledPackageFound)]
    [InlineData(WingetOutputParser.ExitRebootRequiredToFinish)]
    [InlineData(-1)]
    public void Other_outcomes_are_left_alone(int exitCode)
    {
        Assert.False(WingetProvider.ShouldReinstall(exitCode, User, stillOutdated: true));
    }

    [Fact]
    public void The_technology_mismatch_code_is_wingets()
    {
        // APPINSTALLER_CLI_ERROR_UPDATE_INSTALL_TECHNOLOGY_MISMATCH in winget's AppInstallerErrors.h.
        Assert.Equal(unchecked((int)0x8A15008E), WingetOutputParser.ExitUpdateInstallTechnologyMismatch);
    }
}
