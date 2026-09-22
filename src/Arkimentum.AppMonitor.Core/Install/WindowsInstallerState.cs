using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Arkimentum.AppMonitor.Inventory;
using Arkimentum.AppMonitor.Models;

namespace Arkimentum.AppMonitor.Install;

/// <summary>
/// Asks Windows Installer whether a product code is actually installed, and decides from that whether an Uninstall
/// registry entry is a leftover.
/// <para>
/// The case this exists for: a product was registered by Windows Installer, the MSI product was later removed or
/// superseded another way, but its ARP (Uninstall) key survived. <c>winget list</c> reads ARP, so it keeps reporting
/// the old version; <c>winget uninstall</c> runs the entry's <c>MsiExec.exe /I{GUID}</c>, msiexec answers 1605
/// (ERROR_UNKNOWN_PRODUCT) because it does not have that product any more, and winget reports 0x8A150066. Nothing
/// winget can be asked to do ever clears it. Observed on Oh My Posh (<c>JanDeDobbeleer.OhMyPosh</c>): the Inno exe and
/// the MSIX registrations were removed by the take-over, a third, stale Windows Installer registration stayed behind.
/// </para>
/// <para>
/// The decision is a pure function of the registry entry, the name winget lists and an injected state lookup, so it is
/// testable without msi.dll; only <see cref="QueryProductState"/> touches the native API.
/// </para>
/// </summary>
public static partial class WindowsInstallerState
{
    /// <summary>INSTALLSTATE_UNKNOWN: the product code is not known to Windows Installer.</summary>
    public const int InstallStateUnknown = -1;
    /// <summary>INSTALLSTATE_ADVERTISED: advertised, not installed.</summary>
    public const int InstallStateAdvertised = 1;
    /// <summary>INSTALLSTATE_ABSENT: known but not installed.</summary>
    public const int InstallStateAbsent = 2;
    /// <summary>INSTALLSTATE_DEFAULT: installed for this user / this machine. The only state that means "installed".</summary>
    public const int InstallStateDefault = 5;

    private static readonly Regex ProductCodePattern = new(
        @"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    [LibraryImport("msi.dll", EntryPoint = "MsiQueryProductStateW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MsiQueryProductState(string product);

    /// <summary>
    /// The INSTALLSTATE Windows Installer reports for a product code. A failure to ask (msi.dll missing, native
    /// exception) is deliberately reported as <see cref="InstallStateDefault"/>: not being able to ask must never be
    /// grounds for deleting a registration.
    /// </summary>
    public static int QueryProductState(string productCode)
    {
        if (string.IsNullOrWhiteSpace(productCode)) return InstallStateDefault;
        try { return MsiQueryProductState(productCode); }
        catch { return InstallStateDefault; }
    }

    /// <summary>
    /// The Windows Installer product code of an Uninstall entry: the key name when it is a <c>{GUID}</c>, otherwise
    /// the <c>{GUID}</c> inside an <c>MsiExec.exe /I{...}</c> / <c>/X{...}</c> uninstall string. False for entries
    /// that are not Windows Installer registrations at all.
    /// </summary>
    public static bool TryGetProductCode(InstalledApp entry, out string productCode)
    {
        productCode = string.Empty;
        if (entry is null) return false;

        var fromKey = entry.ProductCode?.Trim();
        if (!string.IsNullOrEmpty(fromKey))
        {
            var m = ProductCodePattern.Match(fromKey);
            if (m.Success && m.Length == fromKey.Length)
            {
                productCode = m.Value;
                return true;
            }
        }

        var uninstall = entry.UninstallString;
        if (!string.IsNullOrWhiteSpace(uninstall) && uninstall.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
        {
            var m = ProductCodePattern.Match(uninstall);
            if (m.Success)
            {
                productCode = m.Value;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether an Uninstall entry is a stale Windows Installer registration for this package: its display name
    /// belongs to the package (equal to the name winget lists, or matched by the policy's display-name regex), it is
    /// a Windows Installer registration, and Windows Installer does not report that product as installed. The caller
    /// is responsible for only offering entries from the scope the take-over runs in.
    /// </summary>
    internal static bool IsStaleMsiRegistration(InstalledApp entry, string? wingetName, AppPolicy app, Func<string, int> queryProductState)
    {
        ArgumentNullException.ThrowIfNull(queryProductState);
        if (entry is null || app is null) return false;
        if (!NameBelongsToPackage(entry, wingetName, app)) return false;
        if (!TryGetProductCode(entry, out var productCode)) return false;
        return queryProductState(productCode) != InstallStateDefault;
    }

    /// <summary>
    /// Whether the entry's display name identifies the package: equal (trimmed, case-insensitive) to the name winget
    /// listed for the id, or matched by the policy's detection rules when it configures a display-name regex. Nothing
    /// looser - a wrong match here deletes someone else's registration.
    /// </summary>
    private static bool NameBelongsToPackage(InstalledApp entry, string? wingetName, AppPolicy app)
    {
        var displayName = entry.DisplayName?.Trim();
        if (string.IsNullOrEmpty(displayName)) return false;

        if (!string.IsNullOrWhiteSpace(wingetName)
            && string.Equals(displayName, wingetName.Trim(), StringComparison.OrdinalIgnoreCase))
            return true;

        return !string.IsNullOrWhiteSpace(app.DetectDisplayNameRegex)
            && InstalledAppScanner.Match(app, [entry]).Count > 0;
    }
}
