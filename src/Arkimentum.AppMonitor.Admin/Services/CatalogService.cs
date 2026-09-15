using System.IO;
using System.Linq;
using Arkimentum.AppMonitor.Configuration;
using Arkimentum.AppMonitor.Models;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Admin.Services;

/// <summary>
/// Finds and loads <c>catalog.json</c> for the console.
/// <para>
/// The file ships next to the service executable, so the SCM's ImagePath is the first place to look. During
/// development the console runs out of <c>bin\</c>, so the repository's <c>catalog\</c> folder is searched next,
/// and finally the folder holding the console itself.
/// </para>
/// </summary>
public sealed class CatalogService : ICatalogProvider
{
    private readonly ILogger<CatalogService> _log;
    private readonly JsonCatalogProvider _inner;
    private IReadOnlyList<AppPolicy>? _entries;

    public CatalogService(ILogger<CatalogService> log, ILogger<JsonCatalogProvider> innerLog)
    {
        _log = log;
        _inner = new JsonCatalogProvider(innerLog);
    }

    /// <summary>The resolved catalog file, or null when none was found.</summary>
    public string? Path { get; private set; }

    public IReadOnlyList<AppPolicy> Entries => _entries ??= Load();

    public void Invalidate()
    {
        _entries = null;
        _inner.InvalidateCache();
    }

    public AppPolicy? Find(string appId) =>
        Entries.FirstOrDefault(e => e.AppId.Equals(appId, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    /// <remarks>The configured CatalogPath wins, exactly as it does for the service.</remarks>
    public IReadOnlyList<AppPolicy> GetCatalog(string catalogPath)
    {
        if (!string.IsNullOrWhiteSpace(catalogPath)) return _inner.GetCatalog(catalogPath);
        return Entries;
    }

    private IReadOnlyList<AppPolicy> Load()
    {
        Path = Resolve();
        if (Path is null)
        {
            _log.LogWarning("catalog.json was not found; catalog-based features are unavailable.");
            return [];
        }
        var entries = _inner.GetCatalog(Path);
        _log.LogInformation("Catalog: {Count} entr{Plural} from {Path}.", entries.Count, entries.Count == 1 ? "y" : "ies", Path);
        return entries;
    }

    private static string? Resolve()
    {
        // 1. Next to the installed service executable.
        var image = ServiceControlService.ReadImagePath();
        if (!string.IsNullOrWhiteSpace(image))
        {
            var directory = SafeDirectory(image);
            var candidate = Combine(directory, JsonCatalogProvider.DefaultFileName);
            if (candidate is not null) return candidate;
        }

        // 2. The repository's catalog folder, walking up from the console's own location.
        var dir = SafeDirectory(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Combine(System.IO.Path.Combine(dir, "catalog"), JsonCatalogProvider.DefaultFileName);
            if (candidate is not null) return candidate;
            dir = SafeDirectory(dir);
        }

        // 3. Next to the console itself.
        return Combine(AppContext.BaseDirectory, JsonCatalogProvider.DefaultFileName);
    }

    private static string? SafeDirectory(string path)
    {
        try { return System.IO.Path.GetDirectoryName(path.TrimEnd('\\', '/')); }
        catch { return null; }
    }

    private static string? Combine(string? directory, string fileName)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;
        try
        {
            var full = System.IO.Path.Combine(directory, fileName);
            return File.Exists(full) ? full : null;
        }
        catch { return null; }
    }
}
