using Arkimentum.AppMonitor.Web.Catalog;

namespace Arkimentum.AppMonitor.Web.Services;

/// <summary>
/// The application catalog shipped with the agent, fetched once from <c>/catalog.json</c>.
///
/// <para>
/// The catalog supplies identity and detection only, never behaviour: it is what an application's unset rows inherit
/// and what "Add from catalog" offers. A catalog that fails to load is not an error worth blocking the console for -
/// the editor then simply shows built-in defaults instead of catalog values.
/// </para>
/// </summary>
public sealed class CatalogService
{
    private readonly HttpClient _http;
    private Dictionary<string, CatalogEntry> _byId = new(StringComparer.OrdinalIgnoreCase);
    private Task? _loading;

    public CatalogService(HttpClient http) => _http = http;

    public IReadOnlyList<CatalogEntry> Entries { get; private set; } = [];

    /// <summary>Null when the catalog could not be read; the console works without it.</summary>
    public string? LoadError { get; private set; }

    /// <summary>Loads the catalog once. Concurrent callers join the load in flight.</summary>
    public Task EnsureLoadedAsync() => _loading ??= LoadAsync();

    private async Task LoadAsync()
    {
        try
        {
            var json = await _http.GetStringAsync("catalog.json").ConfigureAwait(false);
            Entries = CatalogEntry.Parse(json);
            _byId = Entries.ToDictionary(e => e.AppId!, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            LoadError = ApiException.Describe(ex);
        }
    }

    /// <summary>The catalog entry for an AppId, or null when the catalog does not know it.</summary>
    public CatalogEntry? Find(string appId) => _byId.GetValueOrDefault(appId);

    /// <summary>Catalog entries not yet in the document, for the "Add from catalog" picker.</summary>
    public IEnumerable<CatalogEntry> NotConfigured(Func<string, bool> exists, string? filter) =>
        Entries.Where(e => !exists(e.AppId!) && e.Matches(filter)).OrderBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase);
}
