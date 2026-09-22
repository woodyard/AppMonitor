using Arkimentum.AppMonitor.Web.Services;
using Xunit;

namespace Arkimentum.AppMonitor.Web.Tests;

/// <summary>
/// The enrolment snippets are copied and pasted into Intune and Group Policy, so their text is the contract. These
/// tests pin what an administrator would notice if it broke: the right registry hive, the 64-bit re-launch, and an
/// honest placeholder when the server did not return the key.
/// </summary>
public sealed class ProvisioningSnippetsTests
{
    private static readonly Guid Organization = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private const string ServerUrl = "https://appmon-prod-func.azurewebsites.net";

    [Fact]
    public void The_PowerShell_snippet_writes_the_three_values_to_the_64_bit_hive()
    {
        var script = ProvisioningSnippets.PowerShell(ServerUrl, Organization, "SECRET-KEY");

        Assert.Contains(@"HKLM:\SOFTWARE\Arkimentum\AppMonitor", script);
        Assert.Contains("CloudServerUrl", script);
        Assert.Contains("CloudOrganizationId", script);
        Assert.Contains("CloudEnrollmentKey", script);
        Assert.Contains("SECRET-KEY", script);
        Assert.Contains(Organization.ToString(), script);
        // A 32-bit Intune agent must not land the values in the WOW6432Node redirected view.
        Assert.Contains("SysNative", script);
    }

    [Fact]
    public void A_missing_key_becomes_a_placeholder_rather_than_an_empty_string()
    {
        var script = ProvisioningSnippets.PowerShell(ServerUrl, Organization, null);
        var reg = ProvisioningSnippets.RegFile(ServerUrl, Organization, null);
        var install = ProvisioningSnippets.InstallerCommandLine(ServerUrl, Organization, null);

        Assert.Contains(ProvisioningSnippets.KeyPlaceholder, script);
        Assert.Contains(ProvisioningSnippets.KeyPlaceholder, reg);
        Assert.Contains(ProvisioningSnippets.KeyPlaceholder, install);
    }

    [Fact]
    public void The_reg_file_escapes_backslashes_in_the_key_path()
    {
        var reg = ProvisioningSnippets.RegFile(ServerUrl, Organization, "KEY");

        Assert.StartsWith("Windows Registry Editor Version 5.00", reg);
        Assert.Contains(@"[HKEY_LOCAL_MACHINE\SOFTWARE\Arkimentum\AppMonitor]", reg);
        Assert.Contains($"\"CloudOrganizationId\"=\"{Organization}\"", reg);
    }

    [Fact]
    public void Single_quotes_in_a_value_are_doubled_for_PowerShell()
    {
        var script = ProvisioningSnippets.PowerShell("https://o'brien.example", Organization, "it's-a-key");

        Assert.Contains("https://o''brien.example", script);
        Assert.Contains("it''s-a-key", script);
    }

    [Fact]
    public void The_Intune_detection_rule_names_the_organization_id()
    {
        var detection = ProvisioningSnippets.IntuneDetection(Organization);

        Assert.Contains("CloudOrganizationId", detection);
        Assert.Contains(Organization.ToString(), detection);
        Assert.Contains("Install behaviour : System", detection);
    }
}
