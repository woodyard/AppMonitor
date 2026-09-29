using System.Text.RegularExpressions;
using Arkimentum.AppMonitor.Versioning;

namespace Arkimentum.AppMonitor.Providers;

/// <summary>
/// One installed row of <c>winget list --id X --exact --details</c>: what winget knows about one registration of the
/// package on this device (an MSIX package lists one row per architecture).
/// </summary>
/// <param name="Name">Package display name, from the row's <c>Name [Id]</c> header line.</param>
/// <param name="Id">Package identifier, from the same header line.</param>
/// <param name="Version">winget's version of the package ("Version:"), not the MSIX package version.</param>
/// <param name="LocalIdentifier">"Local Identifier:", e.g. <c>MSIX\Microsoft.WindowsAppRuntime.1.6_6000.519.329.0_x64__8wekyb3d8bbwe</c>.</param>
/// <param name="PackageFamilyName">"Package Family Name:" (winget prints it in lower case).</param>
/// <param name="InstallerCategory">"Installer Category:", e.g. <c>msix</c>, <c>exe</c>.</param>
/// <param name="Architecture">"Installed Architecture:", e.g. <c>X64</c>.</param>
public sealed record WingetInstalledDetails(
    string Name,
    string Id,
    string? Version,
    string? LocalIdentifier,
    string? PackageFamilyName,
    string? InstallerCategory,
    string? Architecture);

public static partial class WingetOutputParser
{
    /// <summary>
    /// A row header of <c>winget list --details</c>: <c>Name [Id]</c>, optionally preceded by the row counter winget
    /// prints when several registrations match (<c>(1/2) </c>) or by <c>Found </c>.
    /// </summary>
    [GeneratedRegex(@"^(?:\(\d+/\d+\)\s*)?(?:Found\s+)?(?<name>\S.*?)\s*\[(?<id>[^\[\]\s]+)\]$", RegexOptions.CultureInvariant)]
    private static partial Regex DetailsHeaderRegex();

    /// <summary>A <c>Key: Value</c> line of a details record (the key is letters and spaces only).</summary>
    [GeneratedRegex(@"^(?<key>[A-Za-z][A-Za-z ]*?)\s*:\s*(?<value>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex DetailsFieldRegex();

    /// <summary>An MSIX package version: four dot-separated numbers (fewer tolerated).</summary>
    [GeneratedRegex(@"^\d+(\.\d+){0,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageVersionRegex();

    /// <summary>The fields of a details record the agent reads; every other line is ignored.</summary>
    private static readonly string[] DetailsFields =
        ["Version", "Local Identifier", "Package Family Name", "Installer Category", "Installed Architecture"];

    /// <summary>
    /// Parses the output of <c>winget list --id X --exact --details</c> into one record per installed row. Never throws;
    /// returns an empty list when no record is found. Pure, so the parsing is testable. A record starts at a line that
    /// looks like <c>Name [Id]</c> (optionally <c>(n/m) Name [Id]</c>) and collects the known <c>Key: Value</c> lines
    /// after it; progress noise, banners and unknown lines are ignored, and a missing field stays null.
    /// </summary>
    public static IReadOnlyList<WingetInstalledDetails> ParseListDetails(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return [];

        var records = new List<WingetInstalledDetails>();
        string? name = null, id = null;
        Dictionary<string, string>? fields = null;

        void Flush()
        {
            if (name is null || id is null || fields is null) return;
            records.Add(new WingetInstalledDetails(name, id,
                Field(fields, "Version"), Field(fields, "Local Identifier"), Field(fields, "Package Family Name"),
                Field(fields, "Installer Category"), Field(fields, "Installed Architecture")));
        }

        foreach (var raw in CleanLines(output))
        {
            var line = raw.Trim();
            var field = DetailsFieldRegex().Match(line);
            if (field.Success && fields is not null)
            {
                var key = DetailsFields.FirstOrDefault(k => string.Equals(k, field.Groups["key"].Value.Trim(), StringComparison.OrdinalIgnoreCase));
                if (key is not null)
                {
                    // The first occurrence wins: a later "Version:" can only belong to a nested section.
                    fields.TryAdd(key, field.Groups["value"].Value.Trim());
                    continue;
                }
            }

            var header = DetailsHeaderRegex().Match(line);
            if (header.Success && !field.Success)
            {
                Flush();
                name = header.Groups["name"].Value.Trim();
                id = header.Groups["id"].Value.Trim();
                fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }
        Flush();
        return records;
    }

    private static string? Field(Dictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var value) && value.Length > 0 ? value : null;

    /// <summary>
    /// The MSIX package version in a winget Local Identifier or a package full name
    /// (<c>MSIX\&lt;Name&gt;_&lt;Version&gt;_&lt;Arch&gt;_&lt;ResourceId&gt;_&lt;PublisherId&gt;</c>, e.g. 6000.519.329.0), or null
    /// when it is not one. Pure, so the parsing is testable. A package name never contains an underscore, so the version
    /// is always the second of the five underscore-separated parts.
    /// </summary>
    public static string? MsixPackageVersion(string? localIdentifier)
    {
        if (string.IsNullOrWhiteSpace(localIdentifier)) return null;
        var fullName = localIdentifier.Trim();
        const string prefix = @"MSIX\";
        if (fullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) fullName = fullName[prefix.Length..];
        else if (fullName.Contains('\\')) return null;   // ARP\… and other pseudo ids

        var parts = fullName.Split('_');
        if (parts.Length != 5 || parts[0].Length == 0) return null;
        return PackageVersionRegex().IsMatch(parts[1]) ? parts[1] : null;
    }

    /// <summary>The highest MSIX package version among the rows' Local Identifiers, or null when none has one.</summary>
    public static string? HighestMsixPackageVersion(IEnumerable<WingetInstalledDetails> rows) =>
        rows.Select(r => MsixPackageVersion(r.LocalIdentifier))
            .Where(v => v is not null)
            .OrderByDescending(v => v, VersionComparer.Instance)
            .FirstOrDefault();
}
