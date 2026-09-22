using System.Text.RegularExpressions;

namespace Arkimentum.AppMonitor.Providers;

/// <summary>One row of a winget <c>list</c> / <c>upgrade</c> table.</summary>
/// <param name="Name">Package display name (may be truncated with an ellipsis by winget).</param>
/// <param name="Id">Package identifier, e.g. <c>7zip.7zip</c>.</param>
/// <param name="Version">Installed version, or <c>Unknown</c>.</param>
/// <param name="Available">Available version; empty when winget did not print one.</param>
/// <param name="Source">Source name, e.g. <c>winget</c>; empty for packages without a source.</param>
public sealed record WingetRow(string Name, string Id, string Version, string Available, string Source)
{
    public bool HasAvailable => !string.IsNullOrWhiteSpace(Available);

    /// <summary>True when winget printed a truncated (ellipsised) cell for this row.</summary>
    public bool IsTruncated =>
        Name.EndsWith('…') || Id.EndsWith('…') || Version.EndsWith('…') || Available.EndsWith('…');
}

/// <summary>
/// Pure parser for winget's fixed-width console tables. winget left-aligns and space-pads every column, so the only
/// reliable way to split a row is to take the column start offsets from the header line and slice by them; splitting on
/// whitespace breaks on names that contain spaces ("7-Zip 26.02 (x64 edition)").
/// </summary>
public static partial class WingetOutputParser
{
    /// <summary>Ellipsis character winget uses when a cell does not fit the column.</summary>
    public const char Ellipsis = '…';

    private const char Esc = '\u001b';
    private const char Bom = '\uFEFF';

    private static readonly string[] KnownColumns = ["Name", "Id", "Version", "Available", "Source"];

    [GeneratedRegex(@"\S+", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderTokenRegex();

    [GeneratedRegex(@"^\s*\d{1,3}(\.\d+)?\s*%", RegexOptions.CultureInvariant)]
    private static partial Regex PercentRegex();

    [GeneratedRegex(@"^\s*\d+\s+(upgrades?|packages?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FooterCountRegex();

    /// <summary>Messages winget prints when nothing matched the query.</summary>
    private static readonly string[] NotInstalledMarkers =
    [
        "No installed package found matching input criteria",
        "No installed packages found matching input criteria",
        "No package found matching input criteria",
        "No packages found matching input criteria",
        "No applicable installed packages",
    ];

    /// <summary>winget exit code for "no installed package found matching input criteria" (0x8A150014).</summary>
    public const int ExitNoInstalledPackageFound = unchecked((int)0x8A150014);

    /// <summary>winget exit code for "no applicable upgrade found" (0x8A15002B), i.e. already up to date.</summary>
    public const int ExitNoApplicableUpgrade = unchecked((int)0x8A15002B);

    /// <summary>
    /// winget exit code for "the installed package type does not match the installer type" (0x8A15008E): the product was
    /// installed with one technology (a per-user MSI, say) and the manifest only offers another (an exe wrapper).
    /// </summary>
    public const int ExitUpdateInstallTechnologyMismatch = unchecked((int)0x8A15008E);

    /// <summary>
    /// winget exit code for "multiple packages found matching input criteria" (0x8A150016,
    /// APPINSTALLER_CLI_ERROR_MULTIPLE_INSTALL_FOUND): more than one installed package matched the query, so winget
    /// asks for <c>--version</c> or <c>--all-versions</c> instead of picking one.
    /// </summary>
    public const int ExitMultiplePackagesFound = unchecked((int)0x8A150016);

    /// <summary>
    /// winget exit code for "multiple uninstall failed" (0x8A150066, APPINSTALLER_CLI_ERROR_MULTIPLE_UNINSTALL_FAILED):
    /// several registrations were uninstalled in one run and at least one of them failed, so winget reports the whole
    /// run as failed even when the others succeeded. Used for logging only; the removal is judged by what winget lists
    /// afterwards.
    /// </summary>
    public const int ExitMultipleUninstallFailed = unchecked((int)0x8A150066);

    /// <summary>winget exit codes meaning "installed, reboot required".</summary>
    public const int ExitRebootRequiredToFinish = unchecked((int)0x8A150109);
    public const int ExitRebootRequiredForInstall = unchecked((int)0x8A15010A);
    public const int ExitRebootInitiated = unchecked((int)0x8A15010B);

    /// <summary>True when the output says the package is not installed / not found.</summary>
    public static bool IsNotInstalledOutput(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return false;
        foreach (var marker in NotInstalledMarkers)
        {
            if (output.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Parses a winget <c>list</c> / <c>upgrade</c> table into rows. Never throws; returns an empty list when no table is present.</summary>
    public static IReadOnlyList<WingetRow> ParseListOutput(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return [];

        var lines = CleanLines(output);
        if (lines.Count == 0) return [];

        // The table is "<header>\n<dashes>\n<rows...>". Locate the dashed separator; the header is the line above it.
        var sepIndex = -1;
        for (var i = 1; i < lines.Count; i++)
        {
            if (IsSeparatorLine(lines[i])) { sepIndex = i; break; }
        }
        if (sepIndex <= 0) return [];

        var columns = ParseHeader(lines[sepIndex - 1]);
        if (columns.Count < 2) return [];

        var rows = new List<WingetRow>();
        for (var i = sepIndex + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (IsSeparatorLine(line)) continue;
            if (FooterCountRegex().IsMatch(line)) break;          // "4 upgrades available."
            if (IsFooterLine(line)) continue;

            var row = SliceRow(line, columns);
            if (row is not null) rows.Add(row);
        }
        return rows;
    }

    /// <summary>Parses a single-package lookup: returns the row whose Id matches <paramref name="id"/> (case-insensitive), else the only row.</summary>
    public static WingetRow? FindById(IReadOnlyList<WingetRow> rows, string? id)
    {
        if (rows.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(id))
        {
            var exact = rows.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;

            // winget may truncate the Id cell; fall back to a prefix match on the non-ellipsised part.
            var truncated = rows.FirstOrDefault(r =>
                r.Id.EndsWith(Ellipsis) && id.StartsWith(r.Id.TrimEnd(Ellipsis), StringComparison.OrdinalIgnoreCase));
            if (truncated is not null) return truncated;
        }
        return rows.Count == 1 ? rows[0] : null;
    }

    /// <summary>Removes spinner/progress artefacts, banners and empty lines. Exposed for diagnostics and tests.</summary>
    public static IReadOnlyList<string> CleanLines(string output)
    {
        var result = new List<string>();
        foreach (var raw in output.Split('\n'))
        {
            // A carriage return means the console line was overwritten; every segment is a candidate line.
            foreach (var segment in raw.Split('\r'))
            {
                var line = segment.TrimEnd().TrimStart(Bom);
                if (IsNoise(line)) continue;
                result.Add(line);
            }
        }
        return result;
    }

    /// <summary>True for spinner frames, progress bars, download counters and the winget banner.</summary>
    public static bool IsNoise(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return true;

        var t = line.Trim();

        // Spinner frames: "-", "\", "|", "/" (possibly a couple of them) - but never the long dashed separator.
        if (t.Length <= 3 && t.All(c => c is '-' or '\\' or '|' or '/')) return true;

        // Block progress bars, ANSI escapes and the winget "downloading" line.
        if (t.IndexOf(Esc) >= 0 || t.IndexOf('\b') >= 0) return true;
        // Progress-bar block characters; these never occur in a package name or version.
        foreach (var c in t)
        {
            if (c is '█' or '▓' or '▒' or '░') return true;
            if (c is >= '⠀' and <= '⣿') return true;   // braille spinner frames
        }

        if (PercentRegex().IsMatch(t)) return true;
        if (t.Contains(" KB / ", StringComparison.Ordinal) || t.Contains(" MB / ", StringComparison.Ordinal) ||
            t.Contains(" GB / ", StringComparison.Ordinal) || t.Contains(" B / ", StringComparison.Ordinal)) return true;

        // Banner / legal lines.
        if (t.StartsWith("Windows Package Manager", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.StartsWith("Copyright (c)", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.StartsWith("Microsoft Corporation. All rights reserved", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Contains("v1.0.0.0 is now available", StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    private static bool IsSeparatorLine(string line)
    {
        var t = line.Trim();
        return t.Length >= 8 && t.All(c => c == '-');
    }

    private static bool IsFooterLine(string line)
    {
        var t = line.Trim();
        return t.StartsWith("The following packages", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("No newer package versions", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("No installed package", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("No available upgrade", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("package(s) have version numbers", StringComparison.OrdinalIgnoreCase)
            || t.Contains("have version numbers that cannot be determined", StringComparison.OrdinalIgnoreCase)
            || t.Contains("explicitly excluded from upgrade", StringComparison.OrdinalIgnoreCase)
            || t.Contains("upgrades available", StringComparison.OrdinalIgnoreCase)
            || t.Contains("cannot be upgraded", StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct Column(string Name, int Start);

    private static List<Column> ParseHeader(string header)
    {
        var columns = new List<Column>();
        foreach (Match m in HeaderTokenRegex().Matches(header))
        {
            columns.Add(new Column(m.Value, m.Index));
        }
        return columns;
    }

    private static WingetRow? SliceRow(string line, List<Column> columns)
    {
        var cells = new string[columns.Count];
        var start = columns[0].Start;

        for (var c = 0; c < columns.Count; c++)
        {
            // Where the next column starts in this particular row. winget pads with spaces, but a value that is wider
            // than the header-derived column (rare; usually winget widens the column instead) would bleed rightwards,
            // so push the boundary to the start of the next real token when the boundary lands mid-word.
            var end = c + 1 < columns.Count ? Boundary(line, columns[c + 1].Start, start) : line.Length;
            if (start >= line.Length)
            {
                cells[c] = string.Empty;
                start = end;
                continue;
            }
            if (end > line.Length) end = line.Length;
            cells[c] = end > start ? line[start..end].Trim() : string.Empty;
            start = end;
        }

        var name = Get(columns, cells, "Name", 0);
        var id = Get(columns, cells, "Id", 1);
        if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(name)) return null;

        return new WingetRow(
            name,
            id,
            Get(columns, cells, "Version", 2),
            Get(columns, cells, "Available", 3),
            Get(columns, cells, "Source", 4));
    }

    /// <summary>
    /// Where the cell that starts at <paramref name="cellStart"/> ends. Normally that is the next column's header
    /// offset, but a value wider than its header-derived column bleeds rightwards; in that case (and in every column
    /// after it, whose header offsets are then behind the cursor) the boundary is pushed to the start of the next
    /// whitespace-separated token instead of chopping a value in half.
    /// </summary>
    private static int Boundary(string line, int headerStart, int cellStart)
    {
        if (headerStart > cellStart && headerStart < line.Length && line[headerStart - 1] == ' ')
            return headerStart;                                  // clean boundary: the previous cell ended with padding
        if (headerStart >= line.Length && headerStart > cellStart) return line.Length;

        var i = Math.Max(headerStart, cellStart);
        while (i < line.Length && line[i] != ' ') i++;            // skip the rest of the overflowing value
        while (i < line.Length && line[i] == ' ') i++;            // skip the padding
        return i;
    }

    private static string Get(List<Column> columns, string[] cells, string name, int fallbackIndex)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i].Name, name, StringComparison.OrdinalIgnoreCase)) return cells[i];
        }
        // Unknown/localised header: fall back to the canonical column order, but only when the table is wide enough
        // that the position is unambiguous (a 4-column table without "Available" must not report Source as Available).
        if (columns.Count == KnownColumns.Length && fallbackIndex < cells.Length) return cells[fallbackIndex];
        return string.Empty;
    }
}
