using System.Text.RegularExpressions;

namespace Arkimentum.AppMonitor.Providers;

/// <summary>A package dependency of the installer <c>winget show</c> selected: the package id and the minimum version, if one is required.</summary>
public sealed record WingetPackageDependency(string Id, string? MinVersion);

/// <summary>
/// The dependencies the installer <c>winget show</c> selected declares (the "Dependencies:" section of its
/// "Installer:" block): the package dependencies, and the names of every other dependency kind present ("Windows
/// Features", "Windows Libraries", "External Dependencies", or a kind winget names in another language).
/// <see cref="InstallerFound"/> is false when the output has no "Installer:" block at all.
/// </summary>
public sealed record WingetInstallerDependencies(bool InstallerFound, IReadOnlyList<WingetPackageDependency> Packages, IReadOnlyList<string> OtherKinds)
{
    public static readonly WingetInstallerDependencies NoInstaller = new(false, [], []);
}

public static partial class WingetOutputParser
{
    /// <summary>A package dependency item: <c>Microsoft.VCLibs.Desktop.14</c>, optionally followed by <c>[&gt;= 14.0.30704.0]</c>.</summary>
    [GeneratedRegex(@"^(?<id>[^\s\[\]]+)(?:\s*\[\s*>=\s*(?<min>[^\]\s]+)\s*\])?", RegexOptions.CultureInvariant)]
    private static partial Regex PackageDependencyRegex();

    /// <summary>
    /// Parses the dependencies of the installer <c>winget show</c> selected. Pure and never throws, so the parsing is
    /// testable. Only the last "Installer:" line at column 0 opens the installer block (release notes and descriptions
    /// above it are indented, and may contain "  - " lines of their own); the block ends at the next non-blank line at
    /// column 0. Inside it, the "Dependencies:" line and everything indented deeper than it is the section; each
    /// "- &lt;Kind&gt;:" line starts a kind, and the lines indented deeper than that are its items. Indentation is compared,
    /// not counted, so another layout of the same nesting reads the same. Winget 1.30 (TechSmith.Snagit.2026, 26.4.0):
    /// <code>
    /// Installer:
    ///   Installer Type: wix
    ///   Dependencies:
    ///     - Package Dependencies:
    ///         Microsoft.EdgeWebView2Runtime
    /// </code>
    /// A kind whose name does not contain "Package" counts as another kind; an item without a kind line above it as well.
    /// </summary>
    public static WingetInstallerDependencies ParseInstallerDependencies(string? showOutput)
    {
        if (string.IsNullOrWhiteSpace(showOutput)) return WingetInstallerDependencies.NoInstaller;
        var lines = CleanLines(showOutput);

        var start = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (Indent(lines[i]) == 0 && string.Equals(lines[i].Trim(), "Installer:", StringComparison.OrdinalIgnoreCase)) start = i;
        }
        if (start < 0) return WingetInstallerDependencies.NoInstaller;

        var packages = new List<WingetPackageDependency>();
        var others = new List<string>();
        var depIndent = -1;
        int? kindIndent = null;
        string? kind = null;
        for (var i = start + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            var indent = Indent(line);
            if (indent == 0) break;   // the end of the installer block
            var text = line.Trim();

            if (depIndent < 0)
            {
                if (text.StartsWith("Dependencies:", StringComparison.OrdinalIgnoreCase)) depIndent = indent;
                continue;
            }
            if (indent <= depIndent) { depIndent = -1; kind = null; kindIndent = null; continue; }   // the next installer field

            if (text.StartsWith("- ", StringComparison.Ordinal) || text == "-")
            {
                var label = text.TrimStart('-').Trim();
                var colon = label.IndexOf(':');
                var value = colon >= 0 ? label[(colon + 1)..].Trim() : string.Empty;
                kind = (colon >= 0 ? label[..colon] : label).Trim();
                kindIndent = indent;
                if (!IsPackageKind(kind) && !others.Contains(kind, StringComparer.OrdinalIgnoreCase)) others.Add(kind);
                if (value.Length > 0) AddItem(value);
                continue;
            }
            if (kind is null || kindIndent is null || indent <= kindIndent)
            {
                // An item without a kind line above it: nothing says what it is.
                if (!others.Contains("Dependencies", StringComparer.OrdinalIgnoreCase)) others.Add("Dependencies");
                continue;
            }
            AddItem(text);
        }
        return new WingetInstallerDependencies(true, packages, others);

        void AddItem(string item)
        {
            if (kind is null || !IsPackageKind(kind)) return;
            var m = PackageDependencyRegex().Match(item);
            if (!m.Success) return;
            var id = m.Groups["id"].Value;
            var min = m.Groups["min"].Success ? m.Groups["min"].Value : null;
            if (!packages.Any(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))) packages.Add(new WingetPackageDependency(id, min));
        }
    }

    private static bool IsPackageKind(string kind) => kind.Contains("Package", StringComparison.OrdinalIgnoreCase);

    private static int Indent(string line)
    {
        var n = 0;
        while (n < line.Length && (line[n] == ' ' || line[n] == '\t')) n++;
        return n;
    }

    /// <summary>
    /// The package name in <c>winget show</c>'s first line, <c>Found Microsoft Edge WebView2 Runtime
    /// [Microsoft.EdgeWebView2Runtime]</c>: the text before <c>[&lt;id&gt;]</c>, without the leading "Found". Null when no
    /// line ends with the id in brackets. Pure, so the parsing is testable.
    /// </summary>
    public static string? ParseShowName(string? showOutput, string wingetId)
    {
        if (string.IsNullOrWhiteSpace(showOutput) || string.IsNullOrWhiteSpace(wingetId)) return null;
        var suffix = "[" + wingetId.Trim() + "]";
        foreach (var raw in CleanLines(showOutput))
        {
            var line = raw.Trim();
            if (!line.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            var name = line[..^suffix.Length].Trim();
            if (name.StartsWith("Found ", StringComparison.OrdinalIgnoreCase)) name = name["Found ".Length..].Trim();
            return name.Length == 0 ? null : name;
        }
        return null;
    }
}
