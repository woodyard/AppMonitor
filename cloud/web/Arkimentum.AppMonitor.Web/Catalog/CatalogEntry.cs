using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arkimentum.AppMonitor.Web.Catalog;

/// <summary>
/// One entry of <c>catalog/catalog.json</c> as this console needs it.
///
/// <para>
/// A deliberately minimal mirror of <c>JsonCatalogProvider.CatalogEntry</c> in
/// <c>src/Arkimentum.AppMonitor.Core</c>: the console never executes a catalog entry, it only shows the values an
/// application inherits from it when the corresponding row is not overridden. Behaviour fields (deadline, deferrals,
/// …) are deliberately absent - those inherit from the global <c>Default*</c> settings, never from the catalog.
/// Unknown members are ignored, so the agent's catalog can grow without breaking this.
/// </para>
/// </summary>
public sealed class CatalogEntry
{
    public string? AppId { get; set; }
    public string? DisplayName { get; set; }
    public bool? Enabled { get; set; }
    /// <summary>"winget" or "web"; absent means winget.</summary>
    public string? Source { get; set; }
    /// <summary>"auto", "system" or "user"; absent means auto.</summary>
    public string? Context { get; set; }

    public string? WingetId { get; set; }
    public string? WingetSourceName { get; set; }
    public string? WingetExtraArgs { get; set; }

    public string? VersionUrl { get; set; }
    public string? VersionRegex { get; set; }
    public string? DownloadUrl { get; set; }
    public string? InstallerArgs { get; set; }
    /// <summary>"exe", "msi" or "msix"; absent means exe.</summary>
    public string? InstallerType { get; set; }
    public string? Sha256Url { get; set; }
    public string? Sha256 { get; set; }
    public string? UserDownloadUrl { get; set; }
    public string? UserInstallerArgs { get; set; }

    public string? DetectDisplayNameRegex { get; set; }
    public string? DetectPublisherRegex { get; set; }
    public string? DetectFilePath { get; set; }
    public string? MinimumVersion { get; set; }

    public List<string>? ProcessNames { get; set; }

    /// <summary>Free-form note for administrators reading the catalog file; shown as the entry's subtitle.</summary>
    public string? Notes { get; set; }

    /// <summary>The name the picker shows: the catalog display name, or the AppId when it has none.</summary>
    public string Title => string.IsNullOrWhiteSpace(DisplayName) ? AppId ?? string.Empty : DisplayName!.Trim();

    public bool Matches(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        var needle = filter.Trim();
        return Contains(AppId) || Contains(DisplayName) || Contains(WingetId);
        bool Contains(string? value) => value is not null && value.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>Parses a catalog document. Entries without an appId are skipped; a duplicate appId keeps the first.</summary>
    public static IReadOnlyList<CatalogEntry> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        List<CatalogEntry?>? entries;
        try { entries = JsonSerializer.Deserialize<List<CatalogEntry?>>(json, Options); }
        catch (JsonException) { return []; }
        if (entries is null) return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<CatalogEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry?.AppId is not { } id || string.IsNullOrWhiteSpace(id)) continue;
            entry.AppId = id.Trim();
            if (!seen.Add(entry.AppId)) continue;
            result.Add(entry);
        }
        return result;
    }
}
