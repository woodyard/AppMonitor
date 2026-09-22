using System.Text.Json;
using Arkimentum.AppMonitor.Api.Contracts;
using Arkimentum.AppMonitor.Web.Catalog;
using Arkimentum.AppMonitor.Web.Editing;
using Xunit;

namespace Arkimentum.AppMonitor.Web.Tests;

/// <summary>
/// Runs a real organization configuration (captured from the production API on 2026-09-22, e-mail address replaced)
/// through exactly the path the Applications and Settings pages take: deserialize with <see cref="CloudJson"/>,
/// normalize, and build the editor over the shipped catalog. The pages showed an endless spinner on this payload the
/// first time; whatever the cause, this is where it has to reproduce.
/// </summary>
public sealed class LiveDocumentTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static string RepoCatalog()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "catalog", "catalog.json");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }
        throw new FileNotFoundException("catalog/catalog.json not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void The_live_configuration_deserializes_and_normalizes()
    {
        var response = JsonSerializer.Deserialize<OrganizationConfigResponse>(Fixture("organization-config.json"), CloudJson.Options);

        Assert.NotNull(response);
        Assert.Equal("15-55a164b9", response.ConfigVersion);
        response.Settings.Normalize();
        Assert.True(response.Settings.Global.TryGetValue("defaultautoinstall", out var autoInstall));
        Assert.True(autoInstall!.AsBool());
        Assert.True(response.Settings.Apps.Count > 40);
        Assert.Empty(response.Settings.Validate());
    }

    [Fact]
    public void The_live_configuration_builds_the_editor_over_the_shipped_catalog()
    {
        var response = JsonSerializer.Deserialize<OrganizationConfigResponse>(Fixture("organization-config.json"), CloudJson.Options)!;
        response.Settings.Normalize();
        var catalog = CatalogEntry.Parse(RepoCatalog()).ToDictionary(e => e.AppId!, StringComparer.OrdinalIgnoreCase);

        var editor = new ConfigDocumentEditor(id => catalog.GetValueOrDefault(id));
        editor.Load(response.Settings);

        Assert.Equal(response.Settings.Apps.Count, editor.Apps.Count);
        Assert.False(editor.IsDirty);
        Assert.Contains(editor.Apps, a => a.AppId == "vscode");
        Assert.Contains(editor.Apps, a => a.AppId == "Cloudflare.Warp");
        // Round trip: what the editor would publish is what it loaded.
        Assert.Empty(editor.ToDocument().Validate());
    }

    [Fact]
    public void The_live_history_page_deserializes()
    {
        var page = JsonSerializer.Deserialize<PagedResult<ConfigHistoryEntry>>(Fixture("organization-config-history.json"), CloudJson.Options);

        Assert.NotNull(page);
        Assert.Equal(14, page.Total);
        Assert.Equal(14, page.Items.Count);
        Assert.Equal("15-55a164b9", page.Items.OrderByDescending(e => e.UpdatedUtc).First().ConfigVersion);
    }
}
