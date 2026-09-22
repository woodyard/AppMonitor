using Arkimentum.AppMonitor.Admin.Infrastructure;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// The admin console's command line. The switch that matters here is <c>--local</c>: the per-machine pages are
/// deprecated, so they are off unless it is given, and it — not the mere act of opening the window — is what makes
/// the process ask for elevation.
/// </summary>
public class AdminConsoleCommandLineTests
{
    [Fact]
    public void NoArguments_LeavesTheLocalPagesHidden()
    {
        var options = CommandLineOptions.Parse([]);

        Assert.False(options.Local);
        Assert.False(options.ManagesThisMachine);
        Assert.False(options.IsHeadless);
        Assert.Empty(options.Unknown);
    }

    [Theory]
    [InlineData("--local")]
    [InlineData("-local")]
    [InlineData("/local")]
    [InlineData("--LOCAL")]
    public void Local_IsAccepted_InEveryUsualSpelling(string argument)
    {
        var options = CommandLineOptions.Parse([argument]);

        Assert.True(options.Local);
        Assert.True(options.ManagesThisMachine);
        Assert.Empty(options.Unknown);
    }

    [Fact]
    public void Local_DoesNotConsumeTheNextArgument()
    {
        var options = CommandLineOptions.Parse(["--local", "--user-config"]);

        Assert.True(options.Local);
        Assert.True(options.UserConfig);
        Assert.Empty(options.Unknown);
    }

    [Fact]
    public void LocalMisspelled_IsReportedRatherThanGuessed()
    {
        var options = CommandLineOptions.Parse(["--locale"]);

        Assert.False(options.Local);
        Assert.Equal(["--locale"], options.Unknown);
    }

    [Fact]
    public void RawArguments_SurviveForTheElevatedRelaunch()
    {
        var options = CommandLineOptions.Parse(["--local", "--user-config"]);

        Assert.Equal(["--local", "--user-config"], options.RawArguments);
    }

    // ---------------------------------------------------------------- elevation

    [Fact]
    public void OrganizationOnly_NeedsNoElevation()
    {
        Assert.False(CommandLineOptions.Parse([]).RequiresElevation);
    }

    [Fact]
    public void Local_NeedsElevation()
    {
        Assert.True(CommandLineOptions.Parse(["--local"]).RequiresElevation);
    }

    [Fact]
    public void HeadlessExportAndImport_NeedElevation_EvenWithoutLocal()
    {
        Assert.True(CommandLineOptions.Parse(["--export", @"C:\temp\profile.json"]).RequiresElevation);
        Assert.True(CommandLineOptions.Parse(["--import", @"C:\temp\profile.json"]).RequiresElevation);
    }

    [Theory]
    [InlineData("--local")]
    [InlineData("--export")]
    public void UserConfig_NeverElevates(string switchName)
    {
        var args = switchName == "--export"
            ? new[] { switchName, @"C:\temp\profile.json", "--user-config" }
            : [switchName, "--user-config"];

        var options = CommandLineOptions.Parse(args);

        Assert.True(options.ManagesThisMachine);
        Assert.False(options.RequiresElevation);
    }

    // ---------------------------------------------------------------- the switches --local must not disturb

    [Fact]
    public void TheHeadlessSwitches_AreUnchanged()
    {
        var options = CommandLineOptions.Parse(
            ["--export", @"C:\temp\policy.reg", "--policy", "--no-replace-apps"]);

        Assert.Equal(@"C:\temp\policy.reg", options.ExportPath);
        Assert.True(options.Policy);
        Assert.True(options.NoReplaceApps);
        Assert.True(options.IsHeadless);
        Assert.False(options.Local);
        Assert.Empty(options.Unknown);
    }

    [Fact]
    public void Import_WithMerge_IsUnchanged()
    {
        var options = CommandLineOptions.Parse(["--import", @"C:\temp\profile.json", "--merge"]);

        Assert.Equal(@"C:\temp\profile.json", options.ImportPath);
        Assert.True(options.Merge);
        Assert.True(options.IsHeadless);
    }
}
