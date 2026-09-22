using System.Text;

namespace Arkimentum.AppMonitor.Web.Services;

/// <summary>
/// The ready-to-paste enrolment artefacts the Enrollment page offers. Pure string generation: three values
/// (<c>CloudServerUrl</c>, <c>CloudOrganizationId</c>, <c>CloudEnrollmentKey</c>) written as REG_SZ under
/// <c>HKLM\SOFTWARE\Arkimentum\AppMonitor</c>, in the shapes an administrator actually deploys with.
///
/// <para>
/// Ported verbatim from <c>src/Arkimentum.AppMonitor.Admin/Infrastructure/ProvisioningSnippets.cs</c> - the text is
/// the contract here, so the two consoles hand out identical snippets. The PowerShell snippet re-launches itself
/// through <c>%WINDIR%\SysNative</c> exactly as the settings exporter does, so a 32-bit Intune agent cannot land the
/// values in the WOW6432Node redirected view, where the agent does not look.
/// </para>
/// </summary>
public static class ProvisioningSnippets
{
    /// <summary>Mirrors <c>AgentSettings.ProductName</c> in Core.</summary>
    public const string ProductName = "Arkimentum AppMonitor";

    /// <summary>Mirrors <c>AgentSettings.RegistryRoot</c> in Core.</summary>
    public const string RegistryRoot = @"SOFTWARE\Arkimentum\AppMonitor";

    /// <summary>What a snippet gets when the key has not been revealed (rotate to see it once).</summary>
    public const string KeyPlaceholder = "<enrollment key>";

    /// <summary>The key path the snippets write, for the page's caption.</summary>
    public static string RegistryPath => @"HKLM\" + RegistryRoot;

    /// <summary>(a) Intune platform script / Win32 app install command; idempotent, SYSTEM, 64-bit.</summary>
    public static string PowerShell(string serverUrl, Guid organizationId, string? enrollmentKey)
    {
        var key = enrollmentKey ?? KeyPlaceholder;
        var sb = new StringBuilder();
        sb.AppendLine("<#");
        sb.AppendLine($"  Enrols this device with {ProductName}.");
        sb.AppendLine("  Run as SYSTEM or an administrator (Intune platform script, Win32 app, GPO startup script, RMM).");
        sb.AppendLine("  Idempotent: safe to run repeatedly. The agent enrols on its next sync and then keeps its own device key.");
        sb.AppendLine("#>");
        sb.AppendLine("[CmdletBinding()]");
        sb.AppendLine("param(");
        sb.AppendLine($"    [string]$CloudServerUrl     = '{PsEscape(serverUrl)}',");
        sb.AppendLine($"    [string]$CloudOrganizationId = '{organizationId}',");
        sb.AppendLine($"    [string]$CloudEnrollmentKey  = '{PsEscape(key)}'");
        sb.AppendLine(")");
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine();
        sb.AppendLine(@"# Re-launch in the 64-bit host when started from a 32-bit agent, so HKLM\SOFTWARE is not redirected to WOW6432Node.");
        sb.AppendLine("if ($env:PROCESSOR_ARCHITEW6432 -eq 'AMD64' -and [IntPtr]::Size -eq 4) {");
        sb.AppendLine("    & \"$env:WINDIR\\SysNative\\WindowsPowerShell\\v1.0\\powershell.exe\" -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath `");
        sb.AppendLine("        -CloudServerUrl $CloudServerUrl -CloudOrganizationId $CloudOrganizationId -CloudEnrollmentKey $CloudEnrollmentKey");
        sb.AppendLine("    exit $LASTEXITCODE");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine($"$key = '{PsRegistryPath()}'");
        sb.AppendLine("if (-not (Test-Path -LiteralPath $key)) { New-Item -Path $key -Force | Out-Null }");
        sb.AppendLine("New-ItemProperty -LiteralPath $key -Name 'CloudServerUrl'      -Value $CloudServerUrl      -PropertyType String -Force | Out-Null");
        sb.AppendLine("New-ItemProperty -LiteralPath $key -Name 'CloudOrganizationId' -Value $CloudOrganizationId -PropertyType String -Force | Out-Null");
        sb.AppendLine("New-ItemProperty -LiteralPath $key -Name 'CloudEnrollmentKey'  -Value $CloudEnrollmentKey  -PropertyType String -Force | Out-Null");
        sb.AppendLine($"Write-Host \"Enrolled with $CloudServerUrl (organization $CloudOrganizationId).\"");
        return sb.ToString();
    }

    /// <summary>(b) A .reg file for reg import, a GPO Preference item or a double-click.</summary>
    public static string RegFile(string serverUrl, Guid organizationId, string? enrollmentKey)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Windows Registry Editor Version 5.00");
        sb.AppendLine();
        sb.AppendLine($"; {ProductName} enrolment. Import in the 64-bit view: reg import <file>");
        sb.AppendLine();
        sb.AppendLine($"[HKEY_LOCAL_MACHINE\\{RegistryRoot}]");
        sb.AppendLine($"\"CloudServerUrl\"=\"{RegEscape(serverUrl)}\"");
        sb.AppendLine($"\"CloudOrganizationId\"=\"{organizationId}\"");
        sb.AppendLine($"\"CloudEnrollmentKey\"=\"{RegEscape(enrollmentKey ?? KeyPlaceholder)}\"");
        return sb.ToString();
    }

    /// <summary>(c) The installer's own switches, for a fresh install that enrols on first start.</summary>
    public static string InstallerCommandLine(string serverUrl, Guid organizationId, string? enrollmentKey) =>
        $".\\Install-ArkimentumAppMonitor.ps1 -CloudServerUrl '{PsEscape(serverUrl)}' " +
        $"-CloudOrganizationId '{organizationId}' -CloudEnrollmentKey '{PsEscape(enrollmentKey ?? KeyPlaceholder)}'";

    /// <summary>(d) The Intune Win32 detection rule that proves the enrolment values landed.</summary>
    public static string IntuneDetection(Guid organizationId)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Detection rule: Manually configure detection rules -> Rule type: Registry");
        sb.AppendLine();
        sb.AppendLine($"Key path              : HKEY_LOCAL_MACHINE\\{RegistryRoot}");
        sb.AppendLine("Value name            : CloudOrganizationId");
        sb.AppendLine("Detection method      : String comparison -> Equals");
        sb.AppendLine($"Value                 : {organizationId}");
        sb.AppendLine("Associated with a 32-bit app on 64-bit clients : No");
        sb.AppendLine();
        sb.AppendLine("Install command   : powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\\Enroll.ps1");
        sb.AppendLine("Uninstall command : powershell.exe -NoProfile -ExecutionPolicy Bypass -Command \"Remove-ItemProperty " +
                      $"'HKLM:\\{RegistryRoot}' -Name CloudOrganizationId,CloudEnrollmentKey,CloudServerUrl -ErrorAction SilentlyContinue\"");
        sb.AppendLine("Install behaviour : System");
        return sb.ToString();
    }

    private static string PsRegistryPath() => "HKLM:\\" + RegistryRoot;

    private static string PsEscape(string value) => value.Replace("'", "''");

    private static string RegEscape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
