using System.Globalization;
using System.IO;
using System.Linq;

namespace Arkimentum.AppMonitor.Tray.Services;

/// <summary>
/// The file-name rules behind <see cref="AppIconProvider"/>: which App Paths key a process name maps to, which
/// scale-qualified variant of an MSIX logo to load, which icon files only ever show a generic picture, and the monogram
/// shown when nothing was found. Pure and WPF-free, so the rules are covered by tests.
/// </summary>
public static class AppIconLookup
{
    /// <summary>
    /// Executables whose icon is a generic Windows picture, never the application's: an uninstall entry whose
    /// <c>DisplayIcon</c> points at the Windows Installer or rundll32 would otherwise put that on the card.
    /// </summary>
    private static readonly string[] GenericHosts = ["msiexec.exe", "rundll32.exe"];

    /// <summary>The base size of a package's StoreLogo at scale-100, used to compare scale-qualified variants with a target size.</summary>
    public const int StoreLogoBasePixels = 50;

    /// <summary>True when <paramref name="path"/> is a system host whose icon says nothing about the application.</summary>
    public static bool IsGenericHost(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        GenericHosts.Contains(Path.GetFileName(path.Trim()), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The App Paths key name for a configured process name: "firefox" and "firefox.exe" both give "firefox.exe". Null for
    /// an empty name or one with a folder in it (process names are bare names).
    /// </summary>
    public static string? AppPathsKeyName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return null;
        var name = processName.Trim().Trim('"');
        if (name.Length == 0 || name.IndexOfAny(['\\', '/', ':']) >= 0) return null;
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
    }

    /// <summary>
    /// The file to load for an MSIX logo. <c>Package.Logo</c> names a file such as <c>StoreLogo.png</c> that usually does
    /// not exist as such: the package ships scale- or size-qualified variants next to it (<c>StoreLogo.scale-100.png</c>,
    /// <c>StoreLogo.targetsize-32_altform-unplated.png</c>). Given the names in that folder, returns the requested name when
    /// present, else the variant closest to <paramref name="desiredPixels"/> - the smallest that is at least that big, or
    /// the biggest there is - skipping high-contrast variants and preferring unplated ones. Null when nothing matches.
    /// </summary>
    public static string? ResolveScaledAsset(string requestedFileName, IEnumerable<string> fileNamesInFolder, int desiredPixels)
    {
        if (string.IsNullOrWhiteSpace(requestedFileName)) return null;
        var names = fileNamesInFolder.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => Path.GetFileName(n)).ToList();

        var exact = names.FirstOrDefault(n => string.Equals(n, requestedFileName, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        var stem = Path.GetFileNameWithoutExtension(requestedFileName) + ".";
        var extension = Path.GetExtension(requestedFileName);

        var variants = new List<(string Name, int Pixels, bool Unplated, int QualifierCount)>();
        foreach (var name in names)
        {
            if (!name.StartsWith(stem, StringComparison.OrdinalIgnoreCase) || !name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) continue;
            var middle = name[stem.Length..^extension.Length];
            if (middle.Length == 0) continue;
            var qualifiers = middle.Split('_', StringSplitOptions.RemoveEmptyEntries);

            int? pixels = null;
            var unplated = false;
            var skip = false;
            foreach (var q in qualifiers)
            {
                if (q.StartsWith("contrast-", StringComparison.OrdinalIgnoreCase)) { skip = true; break; }
                if (TryQualifier(q, "targetsize-", out var size)) pixels = size;
                else if (TryQualifier(q, "scale-", out var scale)) pixels ??= StoreLogoBasePixels * scale / 100;
                else if (q.Equals("altform-unplated", StringComparison.OrdinalIgnoreCase)) unplated = true;
            }
            if (skip || pixels is not { } px || px <= 0) continue;
            variants.Add((name, px, unplated, qualifiers.Length));
        }
        if (variants.Count == 0) return null;

        var bigEnough = variants.Where(v => v.Pixels >= desiredPixels).ToList();
        var pool = bigEnough.Count > 0 ? bigEnough : variants;
        var ordered = bigEnough.Count > 0
            ? pool.OrderBy(v => v.Pixels)
            : pool.OrderByDescending(v => v.Pixels);
        return ordered.ThenByDescending(v => v.Unplated).ThenBy(v => v.QualifierCount).ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .First().Name;
    }

    /// <summary>The monogram tile's letter: the first letter or digit of the name, upper case; "?" when it has none.</summary>
    public static string Monogram(string? displayName)
    {
        var c = (displayName ?? string.Empty).FirstOrDefault(char.IsLetterOrDigit);
        return c == default ? "?" : char.ToUpper(c, CultureInfo.CurrentCulture).ToString();
    }

    /// <summary>A file name for the per-user icon cache: the AppId with anything unsafe for a file name replaced.</summary>
    public static string SafeFileName(string appId)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string((appId ?? string.Empty).Trim().Select(ch => invalid.Contains(ch) || ch == '.' ? '_' : ch).ToArray());
        return safe.Length == 0 ? "_" : safe;
    }

    private static bool TryQualifier(string qualifier, string prefix, out int value)
    {
        value = 0;
        return qualifier.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
               int.TryParse(qualifier[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
