using Arkimentum.AppMonitor.Install;
using Arkimentum.AppMonitor.Models;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// The rule that lets the winget take-over clear a leftover ARP entry: an Uninstall key whose Windows Installer
/// product is gone (msiexec answers 1605, winget 0x8A150066, and "winget list" keeps reporting the old version for
/// ever). Deleting registry keys is not something to get wrong, so the decision is a pure function of the entry, the
/// name winget listed and an injected Windows Installer state lookup - tested here without msi.dll.
/// </summary>
public class StaleMsiRegistrationTests
{
    private const string Code = "{AC22692D-6117-4F48-A28F-AD3EB9686817}";

    private static InstalledApp Entry(string name = "Oh My Posh", string? productCode = Code, string? uninstall = null) =>
        new()
        {
            DisplayName = name,
            DisplayVersion = "29.26.0",
            ProductCode = productCode,
            UninstallString = uninstall,
            Context = InstallContext.System,
            RegistryKeyPath = $@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{productCode ?? "OhMyPosh"}",
        };

    private static AppPolicy Policy(string? detectRegex = null) => new()
    {
        AppId = "ohmyposh",
        DisplayName = "Oh My Posh",
        DetectDisplayNameRegex = detectRegex,
    };

    private static Func<string, int> State(int state) => _ => state;

    // ---- product code extraction

    [Fact]
    public void The_key_name_is_the_product_code()
    {
        Assert.True(WindowsInstallerState.TryGetProductCode(Entry(), out var code));
        Assert.Equal(Code, code);
    }

    [Theory]
    [InlineData("MsiExec.exe /I{AC22692D-6117-4F48-A28F-AD3EB9686817}")]
    [InlineData("MsiExec.exe /X{AC22692D-6117-4F48-A28F-AD3EB9686817}")]
    [InlineData("\"C:\\Windows\\System32\\msiexec.exe\" /x {AC22692D-6117-4F48-A28F-AD3EB9686817} /qn")]
    public void The_product_code_is_read_out_of_an_msiexec_uninstall_string(string uninstallString)
    {
        Assert.True(WindowsInstallerState.TryGetProductCode(Entry(productCode: null, uninstall: uninstallString), out var code));
        Assert.Equal(Code, code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\"C:\\Program Files\\oh-my-posh\\unins000.exe\"")]
    [InlineData("powershell.exe -Command Remove-Item {AC22692D-6117-4F48-A28F-AD3EB9686817}")]
    public void An_entry_that_is_not_a_windows_installer_registration_has_no_product_code(string? uninstallString)
    {
        Assert.False(WindowsInstallerState.TryGetProductCode(Entry(productCode: null, uninstall: uninstallString), out var code));
        Assert.Equal(string.Empty, code);
    }

    [Fact]
    public void A_key_name_that_is_not_a_guid_is_not_a_product_code()
    {
        Assert.False(WindowsInstallerState.TryGetProductCode(Entry(productCode: "{not-a-guid}"), out _));
    }

    // ---- the whole rule

    [Fact]
    public void A_registration_windows_installer_does_not_know_is_stale()
    {
        Assert.True(WindowsInstallerState.IsStaleMsiRegistration(Entry(), "Oh My Posh", Policy(), State(WindowsInstallerState.InstallStateUnknown)));
    }

    [Theory]
    [InlineData(WindowsInstallerState.InstallStateAbsent)]
    [InlineData(WindowsInstallerState.InstallStateAdvertised)]
    [InlineData(WindowsInstallerState.InstallStateUnknown)]
    public void Any_state_but_installed_makes_it_stale(int state)
    {
        Assert.True(WindowsInstallerState.IsStaleMsiRegistration(Entry(), "Oh My Posh", Policy(), State(state)));
    }

    [Fact]
    public void An_installed_product_is_never_stale()
    {
        Assert.False(WindowsInstallerState.IsStaleMsiRegistration(Entry(), "Oh My Posh", Policy(), State(WindowsInstallerState.InstallStateDefault)));
    }

    [Fact]
    public void A_non_msi_registration_is_never_stale()
    {
        var inno = Entry(productCode: null, uninstall: "\"C:\\Program Files\\oh-my-posh\\unins000.exe\" /SILENT");
        Assert.False(WindowsInstallerState.IsStaleMsiRegistration(inno, "Oh My Posh", Policy(), State(WindowsInstallerState.InstallStateAbsent)));
    }

    // ---- name matching

    [Fact]
    public void The_name_is_compared_ignoring_case_and_surrounding_whitespace()
    {
        Assert.True(WindowsInstallerState.IsStaleMsiRegistration(Entry(name: "  oh my posh  "), "Oh My Posh ", Policy(), State(WindowsInstallerState.InstallStateAbsent)));
    }

    [Fact]
    public void Another_product_is_left_alone()
    {
        Assert.False(WindowsInstallerState.IsStaleMsiRegistration(Entry(name: "Oh My Posh Themes"), "Oh My Posh", Policy(), State(WindowsInstallerState.InstallStateAbsent)));
        Assert.False(WindowsInstallerState.IsStaleMsiRegistration(Entry(name: "7-Zip 26.02 (x64 edition)"), "Oh My Posh", Policy(), State(WindowsInstallerState.InstallStateAbsent)));
    }

    [Fact]
    public void A_configured_detection_regex_also_identifies_the_entry()
    {
        Assert.True(WindowsInstallerState.IsStaleMsiRegistration(Entry(name: "Oh My Posh (x64)"), "Oh My Posh", Policy("^Oh My Posh"), State(WindowsInstallerState.InstallStateAbsent)));
    }

    [Fact]
    public void A_detection_regex_that_does_not_match_leaves_the_entry_alone()
    {
        Assert.False(WindowsInstallerState.IsStaleMsiRegistration(Entry(name: "Oh My Posh (x64)"), null, Policy("^Windows Terminal"), State(WindowsInstallerState.InstallStateAbsent)));
    }

    [Fact]
    public void Without_a_winget_name_and_without_a_regex_nothing_is_deleted()
    {
        Assert.False(WindowsInstallerState.IsStaleMsiRegistration(Entry(), null, Policy(), State(WindowsInstallerState.InstallStateAbsent)));
    }
}
