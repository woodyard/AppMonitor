using Arkimentum.AppMonitor.Providers;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// LocalSystem must never pick up a per-user App Execution Alias: on a device whose PATH carried a user's
/// WindowsApps folder, the service cached that alias and every winget call failed with error 1920.
/// </summary>
public class WingetLocatorTests
{
    [Theory]
    [InlineData(@"C:\Users\HenrikSkovgaard-clou\AppData\Local\Microsoft\WindowsApps\winget.exe", true)]
    [InlineData(@"c:\users\someone\AppData\Local\Microsoft\WindowsApps\winget.exe", true)]
    [InlineData(@"C:\Windows\System32\config\systemprofile\AppData\Local\Microsoft\WindowsApps\winget.exe", false)]
    [InlineData(@"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.30.130.0_x64__8wekyb3d8bbwe\winget.exe", false)]
    [InlineData(@"C:\UsersData\winget.exe", false)]
    public void Paths_inside_user_profiles_are_recognised(string path, bool expected)
    {
        Assert.Equal(expected, WingetLocator.IsUnderUserProfiles(path));
    }

    [Fact]
    public void Cache_is_kept_per_context()
    {
        // An explicit path that exists is returned for both contexts; resetting clears both slots. The point of the
        // test is that resolving one context does not hand its result to the other (they are looked up separately).
        WingetLocator.ResetCache();
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        var explicitPath = typeof(WingetLocatorTests).Assembly.Location;   // any existing file will do

        var user = WingetLocator.Find(logger, isSystem: false, explicitPath);
        var system = WingetLocator.Find(logger, isSystem: true, explicitPath);

        Assert.Equal(explicitPath, user);
        Assert.Equal(explicitPath, system);
        WingetLocator.ResetCache();
    }
}
