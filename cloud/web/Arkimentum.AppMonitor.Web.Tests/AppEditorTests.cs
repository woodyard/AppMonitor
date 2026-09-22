using Arkimentum.AppMonitor.Api.Contracts;
using Arkimentum.AppMonitor.Web.Catalog;
using Arkimentum.AppMonitor.Web.Editing;
using Xunit;

namespace Arkimentum.AppMonitor.Web.Tests;

/// <summary>
/// The two rules that make the application editor readable: only the fields of the effective source are shown, and
/// an unset row says what the agent would use instead - the global <c>Default*</c> value for behaviour, the catalog
/// entry for identity and detection.
/// </summary>
public sealed class AppEditorTests
{
    private static readonly CatalogEntry SevenZip = new()
    {
        AppId = "7zip",
        DisplayName = "7-Zip",
        Source = "winget",
        Context = "auto",
        WingetId = "7zip.7zip",
        ProcessNames = ["7zFM", "7zG"],
        DetectDisplayNameRegex = "^7-Zip\\b",
    };

    private static readonly CatalogEntry WebApp = new()
    {
        AppId = "contoso",
        DisplayName = "Contoso Tool",
        Source = "web",
        VersionUrl = "https://contoso.example/version",
        DownloadUrl = "https://contoso.example/setup-{version}.exe",
    };

    private static AppEditor Editor(CatalogEntry? catalog, Dictionary<string, SettingValue>? values = null,
        Dictionary<string, string>? globals = null) =>
        new(catalog?.AppId ?? "custom", values ?? [], catalog, name => globals?.GetValueOrDefault(name) ?? string.Empty);

    [Fact]
    public void A_winget_application_hides_the_web_fields()
    {
        var app = Editor(SevenZip);

        Assert.True(app.Row("WingetId").IsVisible);
        Assert.False(app.Row("DownloadUrl").IsVisible);
        Assert.False(app.Row("InstallerArgs").IsVisible);
    }

    [Fact]
    public void A_web_application_hides_the_winget_fields()
    {
        var app = Editor(WebApp);

        Assert.True(app.IsWeb);
        Assert.False(app.Row("WingetId").IsVisible);
        Assert.True(app.Row("DownloadUrl").IsVisible);
        Assert.True(app.Row("VersionRegex").IsVisible);
    }

    [Fact]
    public void Switching_the_source_switches_which_fields_are_shown()
    {
        var app = Editor(SevenZip);
        var source = app.Row("Source");

        source.IsOverridden = true;
        source.TextValue = "web";

        Assert.False(app.Row("WingetId").IsVisible);
        Assert.True(app.Row("DownloadUrl").IsVisible);
    }

    [Fact]
    public void Behaviour_rows_inherit_from_the_matching_global_setting()
    {
        var app = Editor(SevenZip, globals: new Dictionary<string, string>
        {
            ["DefaultDeadlineHours"] = "48",
            ["DefaultMandatory"] = "1",
        });

        var deadline = app.Row("DeadlineHours");
        Assert.Equal("48", deadline.InheritedText);
        Assert.Equal(SettingRow.InheritedFromGlobal, deadline.InheritedLabel);
        Assert.True(app.IsMandatory);
        Assert.Equal("Yes · 48 h", app.MandatoryText);
    }

    [Fact]
    public void Identity_and_detection_rows_inherit_from_the_catalog()
    {
        var app = Editor(SevenZip);

        Assert.Equal("7zip.7zip", app.Row("WingetId").InheritedText);
        Assert.Equal(SettingRow.InheritedFromCatalog, app.Row("WingetId").InheritedLabel);
        Assert.Equal("7zFM, 7zG", app.Row("ProcessNames").InheritedText);
        Assert.Equal("^7-Zip\\b", app.Row("DetectDisplayNameRegex").InheritedText);
    }

    [Fact]
    public void A_custom_application_inherits_the_built_in_defaults()
    {
        var app = Editor(null);

        Assert.Equal(SettingRow.InheritedFromBuiltIn, app.Row("WingetId").InheritedLabel);
        Assert.Equal("winget", app.SourceText);
        Assert.Equal("auto", app.ContextText);
    }

    [Fact]
    public void An_overridden_display_name_wins_over_the_catalog_one()
    {
        var app = Editor(SevenZip);
        Assert.Equal("7-Zip", app.DisplayName);

        var name = app.Row("DisplayName");
        name.IsOverridden = true;
        name.TextValue = "7-Zip (packaged)";

        Assert.Equal("7-Zip (packaged)", app.DisplayName);
    }

    [Fact]
    public void Enabled_follows_the_catalog_until_it_is_set()
    {
        var app = Editor(SevenZip);
        Assert.True(app.IsEnabled);

        var enabled = app.Row("Enabled");
        enabled.IsOverridden = true;
        enabled.BoolValue = false;

        Assert.False(app.IsEnabled);
    }

    [Fact]
    public void Only_set_rows_are_written()
    {
        var app = Editor(SevenZip, new Dictionary<string, SettingValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["Enabled"] = SettingValue.From(true),
        });

        Assert.Equal(["Enabled"], app.ToValues().Keys);
    }

    [Fact]
    public void Matching_looks_at_the_name_the_id_and_the_source()
    {
        var app = Editor(SevenZip);

        Assert.True(app.Matches(null));
        Assert.True(app.Matches("7-zip"));
        Assert.True(app.Matches("ZIP"));
        Assert.True(app.Matches("winget"));
        Assert.False(app.Matches("firefox"));
    }
}
