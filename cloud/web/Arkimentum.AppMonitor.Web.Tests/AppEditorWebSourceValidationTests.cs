using Arkimentum.AppMonitor.Api.Contracts;
using Arkimentum.AppMonitor.Web.Catalog;
using Arkimentum.AppMonitor.Web.Editing;
using Xunit;

namespace Arkimentum.AppMonitor.Web.Tests;

/// <summary>
/// Switching an application to the vendor web site without giving it URLs used to publish fine and then silently stop
/// the monitoring: the agent's configuration reader drops a "web" application that has no version URL or download URL.
/// The editor now names the missing fields and blocks publishing until they are filled in (or the source is set back).
/// </summary>
public sealed class AppEditorWebSourceValidationTests
{
    private static readonly CatalogEntry WingetOnly = new()
    {
        AppId = "JanDeDobbeleer.OhMyPosh",
        DisplayName = "Oh My Posh",
        Source = "winget",
        WingetId = "JanDeDobbeleer.OhMyPosh",
    };

    private static readonly CatalogEntry WebApp = new()
    {
        AppId = "contoso",
        DisplayName = "Contoso Tool",
        Source = "web",
        VersionUrl = "https://contoso.example/version",
        VersionRegex = "(\\d+\\.\\d+)",
        DownloadUrl = "https://contoso.example/setup-{version}.exe",
    };

    private static Dictionary<string, SettingValue> Values(params (string Name, object Value)[] values)
    {
        var dict = new Dictionary<string, SettingValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in values) dict[name] = new SettingValue(value);
        return dict;
    }

    private static AppEditor Editor(CatalogEntry? catalog, Dictionary<string, SettingValue>? values = null) =>
        new(catalog?.AppId ?? "custom", values ?? [], catalog, _ => string.Empty);

    [Fact]
    public void A_custom_application_switched_to_web_without_urls_is_a_problem_that_names_the_fields()
    {
        var app = Editor(null, Values(("Source", "web"), ("WingetId", "JanDeDobbeleer.OhMyPosh")));

        var problem = Assert.Single(app.Problems);
        Assert.Contains("Version URL", problem);
        Assert.Contains("Version regex", problem);
        Assert.Contains("Download URL", problem);
        Assert.Contains("back to winget", problem);
    }

    [Fact]
    public void Only_the_fields_still_missing_are_named()
    {
        var app = Editor(null, Values(("Source", "web"), ("VersionUrl", "https://example.test/v"), ("DownloadUrl", "https://example.test/setup.exe")));

        var problem = Assert.Single(app.Problems);
        Assert.Contains("a Version regex;", problem);
        Assert.DoesNotContain("Version URL", problem);
        Assert.DoesNotContain("Download URL", problem);
    }

    [Fact]
    public void A_web_application_with_all_three_fields_has_no_problem()
    {
        var app = Editor(null, Values(("Source", "web"), ("VersionUrl", "https://example.test/v"), ("VersionRegex", "(\\d+)"),
            ("DownloadUrl", "https://example.test/setup-{version}.exe")));

        Assert.Empty(app.Problems);
    }

    [Fact]
    public void The_catalog_entry_counts_as_supplying_the_fields()
    {
        var app = Editor(WebApp);

        Assert.True(app.IsWeb);
        Assert.Empty(app.Problems);
    }

    [Fact]
    public void A_catalog_winget_application_switched_to_web_is_a_problem()
    {
        var app = Editor(WingetOnly, Values(("Source", "web")));

        Assert.Single(app.Problems);
    }

    [Fact]
    public void A_winget_application_never_needs_the_web_fields()
    {
        Assert.Empty(Editor(WingetOnly).Problems);
        Assert.Empty(Editor(null, Values(("WingetId", "Vendor.App"))).Problems);
    }

    [Fact]
    public void The_problem_appears_and_disappears_as_the_user_edits()
    {
        var app = Editor(WingetOnly);
        Assert.Empty(app.Problems);

        var source = app.Row("Source");
        source.IsOverridden = true;
        source.TextValue = "web";
        Assert.Single(app.Problems);

        source.IsOverridden = false;
        Assert.Empty(app.Problems);
    }
}
