using Arkimentum.AppMonitor.Native;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// Which running processes block an update. Session 0 never does: a user cannot close what runs there, and the
/// installer handles files in use itself. Regression for the device that was asked to close a SYSTEM pwsh.
/// </summary>
public class BlockingProcessScopeTests
{
    private static readonly HashSet<string> Wanted = new(StringComparer.OrdinalIgnoreCase) { "pwsh", "code" };

    [Theory]
    [InlineData("pwsh", 1, null, true)]     // interactive session, machine-wide check
    [InlineData("pwsh", 3, null, true)]     // another interactive session still blocks a machine-wide install
    [InlineData("pwsh", 0, null, false)]    // session 0: scheduled task / service helper - left to the installer
    [InlineData("PWSH", 1, null, true)]     // case-insensitive name
    [InlineData("explorer", 1, null, false)]
    [InlineData("pwsh", 1, 1, true)]        // per-user check, same session
    [InlineData("pwsh", 2, 1, false)]       // per-user check, other session
    [InlineData("pwsh", 0, 0, true)]        // an explicit session 0 request is honoured (tests, tooling)
    public void Session_zero_only_blocks_when_asked_for_explicitly(string name, int processSession, int? wantedSession, bool expected)
    {
        Assert.Equal(expected, ProcessHelper.IsBlocking(name, processSession, Wanted, wantedSession));
    }
}
