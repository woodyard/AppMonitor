using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Versioning;
using Microsoft.Win32;

namespace Arkimentum.AppMonitor.Inventory;

/// <summary>
/// Finds an Uninstall entry by its exact display name, for the dependency check of an install (see
/// <see cref="Providers.WingetProvider"/>). Unlike <see cref="InstalledAppScanner"/> it does not skip entries marked
/// <c>SystemComponent</c>: the Microsoft Edge WebView2 Runtime registers itself with <c>SystemComponent = 1</c> (HKLM,
/// WOW6432Node, key "Microsoft EdgeWebView"), which is why it shows in no inventory and winget does not list it.
/// </summary>
public static class UninstallEntryLookup
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string Uninstall32Path = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>
    /// The Uninstall entry whose DisplayName equals <paramref name="displayName"/> (case-insensitive, trimmed) in HKLM
    /// (64-bit and WOW6432Node) and, with <paramref name="includeCurrentUser"/>, in HKCU; the highest DisplayVersion when
    /// several match. Null when none does. Entries of updates (<c>ParentKeyName</c>, a <c>ReleaseType</c> of an update) do
    /// not count. Never throws for an unreadable key.
    /// </summary>
    public static InstalledApp? FindByDisplayName(string displayName, bool includeCurrentUser)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return null;
        var found = new List<InstalledApp>();
        Read(RegistryHive.LocalMachine, UninstallPath, InstallContext.System, displayName, found);
        Read(RegistryHive.LocalMachine, Uninstall32Path, InstallContext.System, displayName, found);
        if (includeCurrentUser)
        {
            Read(RegistryHive.CurrentUser, UninstallPath, InstallContext.User, displayName, found);
            Read(RegistryHive.CurrentUser, Uninstall32Path, InstallContext.User, displayName, found);
        }
        return found
            .OrderBy(e => VersionComparer.IsUnknown(e.DisplayVersion))
            .ThenByDescending(e => e.DisplayVersion, VersionComparer.Instance)
            .FirstOrDefault();
    }

    private static void Read(RegistryHive hive, string path, InstallContext context, string displayName, List<InstalledApp> found)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(path, false);
            if (key is null) return;
            foreach (var sub in key.GetSubKeyNames())
            {
                try
                {
                    using var k = key.OpenSubKey(sub, false);
                    if (k?.GetValue("DisplayName") is not string name || !string.Equals(name.Trim(), displayName.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                    if (k.GetValue("ParentKeyName") is string) continue;
                    if (k.GetValue("ReleaseType") is "Update" or "Hotfix" or "Security Update") continue;
                    found.Add(new InstalledApp
                    {
                        DisplayName = name.Trim(),
                        DisplayVersion = (k.GetValue("DisplayVersion") as string)?.Trim(),
                        Publisher = (k.GetValue("Publisher") as string)?.Trim(),
                        Context = context,
                        RegistryKeyPath = $"{root.Name}\\{path}\\{sub}",
                    });
                }
                catch { /* an unreadable entry is not the one */ }
            }
        }
        catch { /* an unreadable hive has no entry */ }
    }
}
