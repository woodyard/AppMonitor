using Microsoft.Win32;

namespace Arkimentum.AppMonitor.Service.Cloud;

/// <summary>
/// Stable identifiers of this Windows installation, used when the device enrolls with the cloud service. Every value is
/// best-effort: a missing registry key yields null/empty rather than an exception, because enrollment must still work on
/// a machine that is not Entra joined.
/// </summary>
public static class DeviceIdentity
{
    private const string CryptographyKey = @"SOFTWARE\Microsoft\Cryptography";
    private const string JoinInfoKey = @"SYSTEM\CurrentControlSet\Control\CloudDomainJoin\JoinInfo";
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    /// <summary>NetBIOS name of this machine, reported as the device name.</summary>
    public static string DeviceName => Environment.MachineName;

    /// <summary>
    /// <c>HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid</c> - stable per Windows installation, so the server can
    /// re-attach a device that re-enrolls. Falls back to the machine name when the value is unreadable.
    /// </summary>
    public static string MachineGuid { get; } = ReadMachineGuid();

    /// <summary>Entra ID (Azure AD) join information, or (null, null) when the device is not cloud joined.</summary>
    public static (string? TenantId, string? DeviceId) EntraJoinInfo { get; } = ReadJoinInfo();

    /// <summary>Human-readable OS version, e.g. <c>Windows 11 Enterprise 24H2 (10.0.26200.1234)</c>.</summary>
    public static string OsVersion { get; } = ReadOsVersion();

    private static RegistryKey? Open(string path)
    {
        try { return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(path, writable: false); }
        catch { return null; }
    }

    private static string ReadMachineGuid()
    {
        try
        {
            using var key = Open(CryptographyKey);
            var value = key?.GetValue("MachineGuid") as string;
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        catch { }
        return Environment.MachineName;
    }

    /// <summary>
    /// <c>HKLM\SYSTEM\CurrentControlSet\Control\CloudDomainJoin\JoinInfo\{thumbprint}</c>: the single subkey is named after
    /// the device certificate's thumbprint (not the device id), and carries <c>TenantId</c> and usually <c>DeviceId</c>.
    /// </summary>
    private static (string?, string?) ReadJoinInfo()
    {
        try
        {
            using var root = Open(JoinInfoKey);
            if (root is null) return (null, null);
            foreach (var thumbprint in root.GetSubKeyNames())
            {
                using var sub = root.OpenSubKey(thumbprint, writable: false);
                if (sub is null) continue;
                var tenant = sub.GetValue("TenantId") as string;
                var device = sub.GetValue("DeviceId") as string;
                if (string.IsNullOrWhiteSpace(tenant) && string.IsNullOrWhiteSpace(device)) continue;
                return (Blank(tenant), Blank(device));
            }
        }
        catch { }
        return (null, null);

        static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    private static string ReadOsVersion()
    {
        var version = Environment.OSVersion.Version.ToString();
        try
        {
            using var key = Open(CurrentVersionKey);
            if (key is not null)
            {
                var product = key.GetValue("ProductName") as string;
                var display = key.GetValue("DisplayVersion") as string;
                var build = key.GetValue("CurrentBuildNumber") as string;
                var ubr = key.GetValue("UBR");
                // Windows 11 still reports "Windows 10 ..." in ProductName; the build number disambiguates it.
                if (!string.IsNullOrWhiteSpace(product) && build is not null && int.TryParse(build, out var buildNumber) && buildNumber >= 22000)
                    product = product.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);
                var full = build is null ? version : $"{Environment.OSVersion.Version.Major}.{Environment.OSVersion.Version.Minor}.{build}{(ubr is int u ? "." + u : "")}";
                return string.IsNullOrWhiteSpace(product)
                    ? full
                    : $"{product}{(string.IsNullOrWhiteSpace(display) ? "" : " " + display)} ({full})";
            }
        }
        catch { }
        return version;
    }
}
