using Arkimentum.AppMonitor.Tray.Infrastructure;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// The tray agent lives in the notification area; its window opens when the user asks for it. At every logon the agent
/// is started twice - by the service's launcher and by the HKLM Run value the installer writes as the fallback - and
/// the later start, finding the first one running, used to tell it to come forward: the window was on the screen of
/// every user about half a minute after signing in (owner's device, 2026-10-01: started 12.18.42, "A second instance
/// asked this one to come forward ()" at 12.19.14).
/// </summary>
public class TrayStartTests
{
    [Fact]
    public void A_plain_start_opens_no_window()
    {
        var options = CommandLineOptions.Parse([]);

        Assert.False(options.Show);
        Assert.True(options.Minimized);
    }

    private static string[] Args(string commandLine) => commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    [Theory]
    [InlineData("")]                    // the Run value and the service's launcher: no arguments
    [InlineData("--minimized")]
    [InlineData("--debug")]
    [InlineData("--something-new")]
    public void A_second_start_without_a_request_leaves_the_running_agent_in_the_tray(string commandLine)
    {
        Assert.False(CommandLineOptions.OpensWindowOfRunningAgent(Args(commandLine)));
    }

    [Theory]
    [InlineData("--show")]
    [InlineData("/show")]
    [InlineData("-Show --debug")]
    [InlineData("-ToastActivated")]     // the user clicked a notification
    public void A_second_start_that_asks_for_the_window_gets_it(string commandLine)
    {
        Assert.True(CommandLineOptions.OpensWindowOfRunningAgent(Args(commandLine)));
    }
}
