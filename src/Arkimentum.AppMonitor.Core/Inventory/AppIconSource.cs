using System.Globalization;
using Arkimentum.AppMonitor.Models;
using Microsoft.Win32;

namespace Arkimentum.AppMonitor.Inventory;

/// <summary>A file holding an icon and which one in it: a zero-based index, or a negative resource id.</summary>
public readonly record struct IconLocation(string Path, int Index);

/// <summary>
/// Where an application's icon comes from on the device. The winget catalog carries no icons, so the tray window takes
/// them from what the scan matched: the uninstall entry's <c>DisplayIcon</c>, else an MSI product's registered icon, else
/// the one executable in its install folder, else the configured detection file. Generic by design - no per-application rules. Display only: nothing here
/// may fail a scan, so every file-system access is guarded.
/// </summary>
public static class AppIconSource
{
    /// <summary>
    /// File names that are clearly not the application itself when guessing the executable of an install folder.
    /// Matched as case-insensitive substrings of the file name.
    /// </summary>
    private static readonly string[] NotTheApplication = ["unins", "setup", "update", "crash", "helper"];

    /// <summary>
    /// The icon reference the scan passes on for <paramref name="installed"/>: its raw <c>DisplayIcon</c> when set, else
    /// the Windows Installer product icon of an MSI install (MSI uninstall keys usually carry no <c>DisplayIcon</c>), else
    /// the only executable in <c>InstallLocation</c> (top level, uninstallers and updaters ignored), else
    /// <paramref name="detectFilePath"/> when that is an executable. Null when none of them applies.
    /// </summary>
    public static string? ForInstalledApp(InstalledApp? installed, string? detectFilePath = null)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(installed?.DisplayIcon)) return installed.DisplayIcon.Trim();
            if (MsiProductIcon(installed) is { } productIcon) return productIcon;
            if (FromInstallLocation(installed?.InstallLocation) is { } exe) return exe;
            if (!string.IsNullOrWhiteSpace(detectFilePath) &&
                detectFilePath.Trim().Trim('"').EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return detectFilePath.Trim().Trim('"');
        }
        catch
        {
            // display only
        }
        return null;
    }

    /// <summary>
    /// The <c>ProductIcon</c> Windows Installer registered for an MSI product (its <c>ARPPRODUCTICON</c>, cached under
    /// <c>%WINDIR%\Installer\{ProductCode}</c>): under <c>HKLM\SOFTWARE\Classes\Installer\Products</c> for a per-machine
    /// install, under the owner's <c>Software\Microsoft\Installer\Products</c> for a per-user one. Null for anything else.
    /// </summary>
    public static string? MsiProductIcon(InstalledApp? installed)
    {
        if (installed?.ProductCode is not { } productCode || PackedGuid(productCode) is not { } packed) return null;
        try
        {
            using var key = installed.Context == InstallContext.User
                ? installed.UserSid is { } sid ? Registry.Users.OpenSubKey($@"{sid}\Software\Microsoft\Installer\Products\{packed}", false) : null
                : Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Classes\Installer\Products\{packed}", false);
            var icon = key?.GetValue("ProductIcon", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            return string.IsNullOrWhiteSpace(icon) ? null : icon.Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// A product code in the packed form Windows Installer uses for registry key names: the first three groups reversed,
    /// every byte of the last two swapped ({12345678-ABCD-EF01-2345-6789ABCDEF01} -> 87654321DCBA10FE32547698BADCFE10).
    /// Null when the text is not a GUID.
    /// </summary>
    public static string? PackedGuid(string? productCode)
    {
        if (!Guid.TryParse(productCode, out var guid)) return null;
        var hex = guid.ToString("N").ToUpperInvariant();
        var packed = new char[32];
        Reverse(0, 8);
        Reverse(8, 4);
        Reverse(12, 4);
        for (var i = 16; i < 32; i += 2)
        {
            packed[i] = hex[i + 1];
            packed[i + 1] = hex[i];
        }
        return new string(packed);

        void Reverse(int start, int length)
        {
            for (var i = 0; i < length; i++) packed[start + i] = hex[start + length - 1 - i];
        }
    }

    /// <summary>The single application executable directly in <paramref name="installLocation"/>, or null when there is none or more than one.</summary>
    public static string? FromInstallLocation(string? installLocation)
    {
        if (string.IsNullOrWhiteSpace(installLocation)) return null;
        try
        {
            var dir = Environment.ExpandEnvironmentVariables(installLocation.Trim().Trim('"'));
            if (!Path.IsPathRooted(dir) || !Directory.Exists(dir)) return null;
            var sole = PickSoleExecutable(Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly).Select(f => Path.GetFileName(f)));
            return sole is null ? null : Path.Combine(dir, sole);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Of the executable names in a folder, the one that is unambiguously the application: exactly one left after the
    /// uninstallers, setups, updaters, crash reporters and helpers are set aside. Null otherwise.
    /// </summary>
    public static string? PickSoleExecutable(IEnumerable<string?> fileNames)
    {
        var candidates = fileNames
            .Where(n => !string.IsNullOrWhiteSpace(n) && n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .Where(n => !NotTheApplication.Any(t => n!.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    /// <summary>
    /// Parses a <c>DisplayIcon</c>-style value: <c>path</c>, <c>path,index</c>, <c>"path",index</c> or <c>"path"</c>, with
    /// surrounding spaces, environment variables (expanded by <paramref name="expand"/>, by default in this process) and a
    /// negative index meaning a resource id. The index is only split off when what follows the last comma is an integer, so
    /// a folder name with a comma survives. Null for an empty value or one that is not an absolute path.
    /// </summary>
    public static IconLocation? ParseDisplayIcon(string? value, Func<string, string>? expand = null)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = value.Trim();
        string path;
        var index = 0;

        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            if (end < 0)
            {
                path = s.Trim('"');
            }
            else
            {
                path = s[1..end];
                var rest = s[(end + 1)..].Trim();
                if (rest.StartsWith(',') && TryIndex(rest[1..], out var i)) index = i;
            }
        }
        else
        {
            var comma = s.LastIndexOf(',');
            if (comma > 0 && TryIndex(s[(comma + 1)..], out var i))
            {
                path = s[..comma];
                index = i;
            }
            else
            {
                path = s;
            }
        }

        try
        {
            path = (expand ?? Environment.ExpandEnvironmentVariables)(path.Trim()).Trim().Trim('"').Trim();
            if (path.Length == 0 || !Path.IsPathRooted(path)) return null;
        }
        catch
        {
            return null;
        }
        return new IconLocation(path, index);
    }

    private static bool TryIndex(string text, out int index) =>
        int.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out index);
}
