using Arkimentum.AppMonitor.Install;
using Arkimentum.AppMonitor.Models;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

public class InstallerRunnerTests
{
    private static readonly ExecutionContextInfo SystemContext = ExecutionContextInfo.System;
    private static readonly ExecutionContextInfo UserContext = new() { IsSystem = false, SessionId = 1, UserSid = "S-1-5-21-1-2-3-1001" };

    [Theory]
    [InlineData(InstallerType.Msi)]
    [InlineData(InstallerType.Exe)]
    [InlineData(InstallerType.Msix)]
    public void ExitZeroIsSuccess(InstallerType type)
    {
        var r = InstallerRunner.InterpretExitCode(0, type);
        Assert.True(r.Success);
        Assert.False(r.RebootRequired);
        Assert.Equal(0, r.ExitCode);
    }

    [Theory]
    [InlineData(3010)]
    [InlineData(1641)]
    public void RebootExitCodesAreSuccessWithReboot(int exitCode)
    {
        var r = InstallerRunner.InterpretExitCode(exitCode, InstallerType.Msi);
        Assert.True(r.Success);
        Assert.True(r.RebootRequired);
        Assert.Equal(exitCode, r.ExitCode);
    }

    [Theory]
    [InlineData(1602)]
    [InlineData(1223)]
    public void CancelledExitCodesFail(int exitCode)
    {
        var r = InstallerRunner.InterpretExitCode(exitCode, InstallerType.Exe);
        Assert.False(r.Success);
        Assert.Equal(exitCode, r.ExitCode);
        Assert.Contains("cancel", r.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnotherInstallInProgressFailsWithAClearMessage()
    {
        var r = InstallerRunner.InterpretExitCode(1618, InstallerType.Msi);
        Assert.False(r.Success);
        Assert.Equal(1618, r.ExitCode);
        Assert.Contains("Another installation", r.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FatalErrorFails()
    {
        var r = InstallerRunner.InterpretExitCode(1603, InstallerType.Msi);
        Assert.False(r.Success);
        Assert.Equal(1603, r.ExitCode);
        Assert.Contains("1603", r.Message);
    }

    [Fact]
    public void AlreadyInstalledIsSuccess()
    {
        var r = InstallerRunner.InterpretExitCode(1638, InstallerType.Msi);
        Assert.True(r.Success);
        Assert.False(r.RebootRequired);
    }

    [Fact]
    public void UnknownExitCodeFailsAndReportsHex()
    {
        var r = InstallerRunner.InterpretExitCode(-2147024891, InstallerType.Exe);
        Assert.False(r.Success);
        Assert.Equal(-2147024891, r.ExitCode);
        Assert.Contains("80070005", r.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- command construction

    [Fact]
    public void MsiCommandUsesMsiexecWithSilentSwitches()
    {
        var (exe, args) = InstallerRunner.BuildCommand(@"C:\temp\app.msi", InstallerType.Msi, null, SystemContext);

        Assert.EndsWith("msiexec.exe", exe, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"/i ""C:\temp\app.msi""", args);
        Assert.Contains("/qn", args);
        Assert.Contains("/norestart", args);
    }

    [Fact]
    public void MsiCommandAppendsPolicyArguments()
    {
        var (_, args) = InstallerRunner.BuildCommand(@"C:\temp\app.msi", InstallerType.Msi, "ALLUSERS=1 REBOOT=ReallySuppress", SystemContext);
        Assert.Contains("ALLUSERS=1 REBOOT=ReallySuppress", args);
        Assert.Contains("/qn", args);
    }

    [Theory]
    [InlineData("/qn")]
    [InlineData("/quiet")]
    [InlineData("/passive")]
    public void MsiCommandDoesNotDuplicateQuietSwitch(string supplied)
    {
        var (_, args) = InstallerRunner.BuildCommand(@"C:\temp\app.msi", InstallerType.Msi, supplied, SystemContext);
        Assert.Equal(1, CountOccurrences(args, supplied));
        if (supplied != "/qn") Assert.DoesNotContain("/qn", args);
    }

    [Fact]
    public void MsiCommandDoesNotDuplicateInstallSwitch()
    {
        var (_, args) = InstallerRunner.BuildCommand(@"C:\temp\app.msi", InstallerType.Msi, @"/i ""C:\temp\app.msi""", SystemContext);
        Assert.Equal(1, CountOccurrences(args, "/i "));
    }

    [Fact]
    public void MsiCommandDoesNotDuplicateNorestart()
    {
        var (_, args) = InstallerRunner.BuildCommand(@"C:\temp\app.msi", InstallerType.Msi, "/norestart", SystemContext);
        Assert.Equal(1, CountOccurrences(args, "/norestart"));
    }

    [Fact]
    public void ExeCommandRunsTheFileDirectlyWithArgs()
    {
        var (exe, args) = InstallerRunner.BuildCommand(@"C:\temp\setup.exe", InstallerType.Exe, "/S", UserContext);
        Assert.Equal(@"C:\temp\setup.exe", exe);   // not quoted: ProcessStartInfo.FileName takes the raw path
        Assert.Equal("/S", args);
    }

    [Fact]
    public void MsixUserContextUsesAddAppxPackage()
    {
        var (exe, args) = InstallerRunner.BuildCommand(@"C:\temp\app.msix", InstallerType.Msix, null, UserContext);

        Assert.EndsWith("powershell.exe", exe, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-NoProfile", args);
        Assert.Contains("-ExecutionPolicy Bypass", args);
        Assert.Contains(@"Add-AppxPackage -Path 'C:\temp\app.msix'", args);
        Assert.DoesNotContain("Add-AppxProvisionedPackage", args);
    }

    [Fact]
    public void MsixSystemContextProvisionsForAllUsers()
    {
        var (exe, args) = InstallerRunner.BuildCommand(@"C:\temp\app.msix", InstallerType.Msix, null, SystemContext);

        Assert.EndsWith("powershell.exe", exe, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"Add-AppxProvisionedPackage -Online -PackagePath 'C:\temp\app.msix' -SkipLicense", args);
        Assert.Contains("Add-AppxPackage", args);
    }

    [Fact]
    public void MsixPathWithApostropheIsEscaped()
    {
        var (_, args) = InstallerRunner.BuildCommand(@"C:\te'mp\app.msix", InstallerType.Msix, null, UserContext);
        Assert.Contains(@"C:\te''mp\app.msix", args);
    }

    [Fact]
    public async Task RunAsync_ReturnsAClearFailureWhenTheFileIsMissing()
    {
        var runner = new InstallerRunner(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var result = await runner.RunAsync(@"C:\does\not\exist.msi", InstallerType.Msi, null, SystemContext, TimeSpan.FromSeconds(5));

        Assert.False(result.Success);
        Assert.Contains("not found", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.OrdinalIgnoreCase)) >= 0) { count++; i += needle.Length; }
        return count;
    }
}
