using System.Diagnostics;
using System.Text.RegularExpressions;
using Arkimentum.AppMonitor.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Arkimentum.AppMonitor.Inventory;

/// <summary>
/// Enumerates installed applications from the Uninstall registry keys.
/// Running as LocalSystem it sees HKLM (64+32-bit) and every loaded user hive under HKEY_USERS, so per-user installs
/// of all logged-on users are visible. Running as a user it sees HKLM and HKCU.
/// </summary>
public sealed class InstalledAppScanner
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string Uninstall32Path = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
    private readonly ILogger _logger;

    public InstalledAppScanner(ILogger<InstalledAppScanner> logger) => _logger = logger;

    public IReadOnlyList<InstalledApp> Scan(bool includeMachine = true, bool includeUsers = true, string? onlyUserSid = null)
    {
        var list = new List<InstalledApp>();
        if (includeMachine)
        {
            ReadHive(Registry.LocalMachine, UninstallPath, InstallContext.System, null, true, list);
            ReadHive(Registry.LocalMachine, Uninstall32Path, InstallContext.System, null, false, list);
        }
        if (includeUsers)
        {
            foreach (var sid in EnumerateUserSids())
            {
                if (onlyUserSid is not null && !sid.Equals(onlyUserSid, StringComparison.OrdinalIgnoreCase)) continue;
                using var hive = SafeOpen(Registry.Users, sid);
                if (hive is null) continue;
                ReadHive(hive, UninstallPath, InstallContext.User, sid, true, list);
                ReadHive(hive, Uninstall32Path, InstallContext.User, sid, false, list);
            }
        }
        return list;
    }

    /// <summary>Finds installed entries matching the app policy's detection rules (display-name / publisher regex).</summary>
    public static IReadOnlyList<InstalledApp> Match(AppPolicy app, IEnumerable<InstalledApp> inventory)
    {
        Regex? name = TryRegex(app.DetectDisplayNameRegex);
        Regex? pub = TryRegex(app.DetectPublisherRegex);
        if (name is null && pub is null)
        {
            // fall back to the display name (whole-word, case-insensitive)
            if (string.IsNullOrWhiteSpace(app.DisplayName)) return [];
            name = new Regex("^" + Regex.Escape(app.DisplayName) + @"(\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        return inventory.Where(i =>
                (name is null || name.IsMatch(i.DisplayName)) &&
                (pub is null || (i.Publisher is not null && pub.IsMatch(i.Publisher))))
            .OrderByDescending(i => i.DisplayVersion, Versioning.VersionComparer.Instance)
            .ToList();
    }

    /// <summary>Reads the file version from a configured DetectFilePath, if any.</summary>
    public static string? FileVersion(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var full = Environment.ExpandEnvironmentVariables(path);
            if (!File.Exists(full)) return null;
            var vi = FileVersionInfo.GetVersionInfo(full);
            return string.IsNullOrWhiteSpace(vi.ProductVersion) ? vi.FileVersion : vi.ProductVersion;
        }
        catch { return null; }
    }

    private static Regex? TryRegex(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return null;
        try { return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)); }
        catch { return null; }
    }

    private static IEnumerable<string> EnumerateUserSids()
    {
        string[] names;
        try { names = Registry.Users.GetSubKeyNames(); } catch { yield break; }
        foreach (var n in names)
        {
            if (!n.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase) && !n.StartsWith("S-1-12-1-", StringComparison.OrdinalIgnoreCase)) continue; // local/domain users, Azure AD users
            if (n.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase)) continue;
            yield return n;
        }
    }

    private static RegistryKey? SafeOpen(RegistryKey root, string path)
    {
        try { return root.OpenSubKey(path, false); } catch { return null; }
    }

    private void ReadHive(RegistryKey root, string path, InstallContext context, string? sid, bool is64, List<InstalledApp> list)
    {
        using var key = SafeOpen(root, path);
        if (key is null) return;
        string[] names;
        try { names = key.GetSubKeyNames(); } catch { return; }
        foreach (var sub in names)
        {
            try
            {
                using var k = key.OpenSubKey(sub, false);
                if (k is null) continue;
                var displayName = k.GetValue("DisplayName") as string;
                if (string.IsNullOrWhiteSpace(displayName)) continue;
                if (k.GetValue("SystemComponent") is int sc && sc == 1) continue;
                var releaseType = k.GetValue("ReleaseType") as string;
                if (releaseType is "Update" or "Hotfix" or "Security Update") continue;
                if (k.GetValue("ParentKeyName") is string) continue;

                list.Add(new InstalledApp
                {
                    DisplayName = displayName.Trim(),
                    DisplayVersion = (k.GetValue("DisplayVersion") as string)?.Trim(),
                    Publisher = (k.GetValue("Publisher") as string)?.Trim(),
                    InstallLocation = k.GetValue("InstallLocation") as string,
                    UninstallString = k.GetValue("UninstallString") as string,
                    QuietUninstallString = k.GetValue("QuietUninstallString") as string,
                    ProductCode = sub.StartsWith('{') ? sub : null,
                    Context = context,
                    UserSid = sid,
                    RegistryKeyPath = $"{root.Name}\\{path}\\{sub}",
                    Is64Bit = is64,
                });
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "Skipping uninstall key {Key}", sub);
            }
        }
    }
}
