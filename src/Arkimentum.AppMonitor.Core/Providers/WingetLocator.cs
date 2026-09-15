using System.Text.RegularExpressions;
using Arkimentum.AppMonitor.Versioning;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Providers;

/// <summary>
/// Finds <c>winget.exe</c> in both user and LocalSystem context.
/// <para>
/// In a user session winget is reachable through the App Execution Alias
/// <c>%LOCALAPPDATA%\Microsoft\WindowsApps\winget.exe</c> (also on PATH). Under LocalSystem that alias does not exist
/// (it is a per-user reparse point), so the real package payload under
/// <c>C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_&lt;version&gt;_&lt;arch&gt;__8wekyb3d8bbwe\winget.exe</c>
/// has to be located directly. Enumerating that directory requires SYSTEM/TrustedInstaller-level rights, which the
/// service has and an ordinary user does not.
/// </para>
/// </summary>
public static partial class WingetLocator
{
    /// <summary>Package family name of the App Installer (winget) MSIX package.</summary>
    public const string PackageFamilySuffix = "8wekyb3d8bbwe";

    private const string ExeName = "winget.exe";

    private static readonly object Gate = new();
    private static string? _cachedPath;
    private static string? _cachedForExplicit;
    private static bool _resolved;

    [GeneratedRegex(@"^Microsoft\.DesktopAppInstaller_(?<version>[0-9][0-9.]*)_(?<arch>x64|arm64|x86)__" + PackageFamilySuffix + "$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PackageFolderRegex();

    /// <summary>Clears the cached result (used by tests and after an App Installer update).</summary>
    public static void ResetCache()
    {
        lock (Gate)
        {
            _cachedPath = null;
            _cachedForExplicit = null;
            _resolved = false;
        }
    }

    /// <summary>
    /// Returns the full path to winget.exe, or <c>null</c> when it cannot be found (a clear reason is logged).
    /// The result is cached for the lifetime of the process (per <paramref name="explicitPath"/>).
    /// </summary>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="isSystem">True when running as LocalSystem; changes the search order.</param>
    /// <param name="explicitPath">Optional configured override; used first when it exists.</param>
    public static string? Find(ILogger logger, bool isSystem, string? explicitPath = null)
    {
        lock (Gate)
        {
            if (_resolved && string.Equals(_cachedForExplicit, explicitPath, StringComparison.OrdinalIgnoreCase))
                return _cachedPath;

            var path = Locate(logger, isSystem, explicitPath);
            _cachedPath = path;
            _cachedForExplicit = explicitPath;
            _resolved = true;
            return path;
        }
    }

    private static string? Locate(ILogger logger, bool isSystem, string? explicitPath)
    {
        var probed = new List<string>();

        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var expanded = Environment.ExpandEnvironmentVariables(explicitPath.Trim().Trim('"'));
            if (Directory.Exists(expanded)) expanded = Path.Combine(expanded, ExeName);
            probed.Add(expanded);
            if (File.Exists(expanded))
            {
                logger.LogDebug("winget located via configured path: {Path}", expanded);
                return expanded;
            }
            logger.LogWarning("Configured winget path {Path} does not exist; falling back to automatic discovery.", expanded);
        }

        // In SYSTEM context prefer the package payload; the per-user alias is not available there.
        if (isSystem)
        {
            var pkg = FindInWindowsApps(logger, probed);
            if (pkg is not null) return pkg;
        }

        foreach (var candidate in UserCandidates())
        {
            probed.Add(candidate);
            if (File.Exists(candidate))
            {
                logger.LogDebug("winget located at {Path}", candidate);
                return candidate;
            }
        }

        var onPath = FindOnPath(probed);
        if (onPath is not null)
        {
            logger.LogDebug("winget located on PATH: {Path}", onPath);
            return onPath;
        }

        if (!isSystem)
        {
            var pkg = FindInWindowsApps(logger, probed);
            if (pkg is not null) return pkg;
        }

        logger.LogWarning(
            "winget.exe was not found ({Context} context). Probed: {Probed}. Install/repair the 'App Installer' " +
            "(Microsoft.DesktopAppInstaller) package, or set the explicit winget path in configuration. winget-sourced apps will be skipped.",
            isSystem ? "SYSTEM" : "user", string.Join("; ", probed));
        return null;
    }

    private static IEnumerable<string> UserCandidates()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local))
            yield return Path.Combine(local, "Microsoft", "WindowsApps", ExeName);

        // Some SYSTEM/service profiles expose a usable copy here as well.
        var windir = Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows";
        yield return Path.Combine(windir, "System32", "config", "systemprofile", "AppData", "Local", "Microsoft", "WindowsApps", ExeName);
    }

    private static string? FindOnPath(List<string> probed)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar)) return null;
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string full;
            try { full = Path.Combine(Environment.ExpandEnvironmentVariables(dir.Trim('"')), ExeName); }
            catch { continue; }
            if (probed.Contains(full, StringComparer.OrdinalIgnoreCase)) continue;
            probed.Add(full);
            try { if (File.Exists(full)) return full; } catch { }
        }
        return null;
    }

    /// <summary>
    /// Enumerates <c>%ProgramFiles%\WindowsApps\Microsoft.DesktopAppInstaller_*__8wekyb3d8bbwe</c> and returns the
    /// winget.exe of the highest-versioned package, preferring the native architecture.
    /// </summary>
    private static string? FindInWindowsApps(ILogger logger, List<string> probed)
    {
        var root = WindowsAppsRoot();
        probed.Add(Path.Combine(root, "Microsoft.DesktopAppInstaller_*__" + PackageFamilySuffix, ExeName));

        string[] dirs;
        try
        {
            if (!Directory.Exists(root)) return null;
            dirs = Directory.GetDirectories(root, "Microsoft.DesktopAppInstaller_*__" + PackageFamilySuffix);
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogDebug("Cannot enumerate {Root} ({Message}); this is expected outside LocalSystem.", root, ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to enumerate {Root} for the App Installer package.", root);
            return null;
        }

        var preferredArch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            _ => "x64",
        };

        var best = dirs
            .Select(d => new { Dir = d, M = PackageFolderRegex().Match(Path.GetFileName(d)) })
            .Where(x => x.M.Success)
            .Select(x => new
            {
                Exe = Path.Combine(x.Dir, ExeName),
                Version = x.M.Groups["version"].Value,
                Arch = x.M.Groups["arch"].Value,
            })
            .Where(x => File.Exists(x.Exe))
            .OrderByDescending(x => string.Equals(x.Arch, preferredArch, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x => x.Version, VersionComparer.Instance)
            .FirstOrDefault();

        if (best is null)
        {
            logger.LogDebug("No usable Microsoft.DesktopAppInstaller package found under {Root} ({Count} candidate folders).", root, dirs.Length);
            return null;
        }

        logger.LogDebug("winget located in the App Installer package: {Path} (version {Version}, {Arch}).", best.Exe, best.Version, best.Arch);
        return best.Exe;
    }

    private static string WindowsAppsRoot()
    {
        var pf = Environment.GetEnvironmentVariable("ProgramFiles");
        if (string.IsNullOrWhiteSpace(pf)) pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(pf)) pf = @"C:\Program Files";
        return Path.Combine(pf, "WindowsApps");
    }
}
