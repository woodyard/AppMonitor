using Arkimentum.AppMonitor.Models;

namespace Arkimentum.AppMonitor.Configuration;

/// <summary>
/// Supplies the app catalog: a list of <see cref="AppPolicy"/> templates (winget ids, official-site version/download
/// URLs, process names, detection rules) that registry entries can reference by AppId without repeating every value.
/// </summary>
public interface ICatalogProvider
{
    /// <summary>Returns catalog entries. <paramref name="catalogPath"/> is the configured override path or empty for the built-in file.</summary>
    IReadOnlyList<AppPolicy> GetCatalog(string catalogPath);
}
