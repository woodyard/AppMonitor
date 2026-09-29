using System.Text.RegularExpressions;
using Arkimentum.AppMonitor.Api.Contracts;
using Arkimentum.AppMonitor.Web.Catalog;
using Arkimentum.AppMonitor.Web.Editing;
using Xunit;

namespace Arkimentum.AppMonitor.Web.Tests;

/// <summary>
/// An installed application winget on the devices could not map to a package (Node.js: winget as SYSTEM finds
/// OpenJS.NodeJS.LTS and OpenJS.NodeJS.22 with equally strong matches and reports no id) can still be put under
/// monitoring from the inventory. It is added with a display-name slug as AppId and an anchored display-name rule,
/// without a WingetId, and the editor flags the missing package id - blocking Publish - until it is entered.
/// </summary>
public sealed class InventoryWithoutWingetIdTests
{
    private static readonly IReadOnlyDictionary<string, CatalogEntry> Catalog = new Dictionary<string, CatalogEntry>(StringComparer.OrdinalIgnoreCase)
    {
        ["7zip"] = new CatalogEntry { AppId = "7zip", DisplayName = "7-Zip", Source = "winget", WingetId = "7zip.7zip" },
        ["contoso"] = new CatalogEntry
        {
            AppId = "contoso",
            DisplayName = "Contoso Tool",
            Source = "web",
            VersionUrl = "https://contoso.example/version",
            VersionRegex = "(\\d+\\.\\d+)",
            DownloadUrl = "https://contoso.example/setup-{version}.exe",
        },
        ["node-js-2"] = new CatalogEntry { AppId = "node-js-2", DisplayName = "Something else", WingetId = "Vendor.Other" },
    };

    private static readonly OrganizationInventoryItem NodeJs = new()
    {
        DisplayName = "Node.js",
        Publisher = "Node.js Foundation",
    };

    private static ConfigDocumentEditor NewEditor(SettingsDocument? document = null)
    {
        var editor = new ConfigDocumentEditor(id => Catalog.GetValueOrDefault(id));
        editor.Load(document ?? new SettingsDocument());
        return editor;
    }

    // ---------------------------------------------------------------- slug

    [Theory]
    [InlineData("Node.js", "node-js")]
    [InlineData("Mozilla Firefox", "mozilla-firefox")]
    [InlineData("  Notepad++  ", "notepad")]
    [InlineData("Microsoft .NET Runtime - 8.0", "microsoft-net-runtime-8-0")]
    [InlineData("Förster Tööl", "forster-tool")]
    [InlineData("VLC_media player", "vlc-media-player")]
    public void The_slug_is_lower_case_letters_and_digits_joined_by_single_dashes(string displayName, string expected)
    {
        var slug = ConfigDocumentEditor.Slug(displayName);

        Assert.Equal(expected, slug);
        Assert.Matches(ConfigDocumentEditor.AppIdPattern, slug!);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("++ --")]
    public void Nothing_usable_is_no_slug(string? displayName) => Assert.Null(ConfigDocumentEditor.Slug(displayName));

    [Fact]
    public void The_suggested_app_id_of_an_item_without_winget_id_is_the_slug_of_the_cleaned_name()
    {
        Assert.Equal("node-js", ConfigDocumentEditor.SuggestAppId(NodeJs));
        Assert.Equal("contoso-designer", ConfigDocumentEditor.SuggestAppId(new OrganizationInventoryItem { DisplayName = "Contoso Designer 4.10.2 (x64)" }));
    }

    [Fact]
    public void A_colliding_slug_gets_the_first_free_number_and_skips_catalog_ids()
    {
        var editor = NewEditor();
        editor.AddCustom("node-js");

        // "node-js" is taken by the document, "node-js-2" by the catalog.
        Assert.Equal("node-js-3", editor.UniqueAppId("node-js"));
        Assert.Equal("7zip-2", editor.UniqueAppId("7zip"));
        Assert.Equal("free", editor.UniqueAppId("free"));
    }

    [Fact]
    public void Adding_an_item_whose_slug_is_taken_appends_a_number()
    {
        var editor = NewEditor();
        editor.AddCustom("NODE-JS");

        var app = editor.AddFromInventory(NodeJs);

        Assert.NotNull(app);
        Assert.Equal("node-js-3", app!.AppId);
    }

    // ---------------------------------------------------------------- adding

    [Fact]
    public void An_item_without_winget_id_is_added_with_a_display_name_rule_and_no_WingetId()
    {
        var editor = NewEditor();

        var app = editor.AddFromInventory(NodeJs);

        Assert.NotNull(app);
        Assert.Equal("node-js", app!.AppId);
        Assert.False(app.IsFromCatalog);
        var values = editor.ToDocument().Apps["node-js"];
        Assert.Equal(["DetectDisplayNameRegex", "DisplayName", "Enabled", "Source"], values.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.DoesNotContain("WingetId", values.Keys);
        Assert.True(values["Enabled"].AsBool());
        Assert.Equal("Node.js", values["DisplayName"].AsString());
        Assert.Equal("winget", values["Source"].AsString());
        Assert.Equal(@"^Node\.js$", values["DetectDisplayNameRegex"].AsString());
    }

    [Fact]
    public void The_display_name_rule_is_a_tight_identity_rule()
    {
        var rule = new Regex(ConfigDocumentEditor.DisplayNameRule("Node.js"));
        Assert.Matches(rule, "Node.js");
        Assert.DoesNotMatch(rule, "NodeXjs");
        Assert.DoesNotMatch(rule, "Node.js Helper");
        Assert.DoesNotMatch(rule, "My Node.js");

        Assert.Equal("^Mozilla Firefox$", ConfigDocumentEditor.DisplayNameRule("Mozilla Firefox"));
        Assert.Equal(@"^Notepad\+\+$", ConfigDocumentEditor.DisplayNameRule("Notepad++"));
    }

    [Fact]
    public void A_name_with_a_version_suffix_keeps_matching_after_an_update()
    {
        var rule = new Regex(ConfigDocumentEditor.DisplayNameRule("Contoso Designer 4.10.2 (x64)"));

        Assert.Matches(rule, "Contoso Designer 4.10.2 (x64)");
        Assert.Matches(rule, "Contoso Designer 5.0.1 (x64)");
        Assert.Matches(rule, "Contoso Designer");
        Assert.DoesNotMatch(rule, "Contoso Designer Viewer 4.10.2");
    }

    [Fact]
    public void The_same_inventory_row_is_not_added_twice()
    {
        var editor = NewEditor();
        Assert.NotNull(editor.AddFromInventory(NodeJs));

        Assert.Null(editor.AddFromInventory(NodeJs));
        Assert.Single(editor.Apps);
    }

    // ---------------------------------------------------------------- validation

    [Fact]
    public void A_winget_application_without_a_package_id_is_flagged_on_the_WingetId_row()
    {
        var editor = NewEditor();
        var app = editor.AddFromInventory(NodeJs)!;

        var row = app.Row("WingetId");
        Assert.True(row.HasError);
        Assert.Equal(AppEditor.MissingWingetIdMessage, row.Error);
        Assert.StartsWith("Required for winget applications: the package id", row.Error);
        Assert.Contains(editor.Problems, p => p.StartsWith("node-js:") && p.Contains("Required for winget applications"));
    }

    [Fact]
    public void Publish_is_blocked_while_the_package_id_is_missing_and_allowed_once_it_is_entered()
    {
        var editor = NewEditor();
        var app = editor.AddFromInventory(NodeJs)!;

        Assert.True(editor.IsDirty);
        Assert.True(editor.HasProblems);
        Assert.False(editor.CanPublish);

        var row = app.Row("WingetId");
        row.IsOverridden = true;
        Assert.False(editor.CanPublish);         // set, but still empty

        row.TextValue = "OpenJS.NodeJS.LTS";

        Assert.False(row.HasError);
        Assert.False(editor.HasProblems);
        Assert.True(editor.CanPublish);
        Assert.Equal("OpenJS.NodeJS.LTS", editor.ToDocument().Apps["node-js"]["WingetId"].AsString());
    }

    [Fact]
    public void A_hand_made_winget_application_without_an_id_is_flagged_too()
    {
        var editor = NewEditor();
        var app = editor.AddCustom("contoso-tool");

        Assert.True(app.Row("WingetId").HasError);
        Assert.False(editor.CanPublish);
    }

    [Fact]
    public void A_catalog_application_added_with_only_Enabled_inherits_its_id_and_is_not_flagged()
    {
        var editor = NewEditor();
        var app = editor.AddFromCatalog("7zip");

        Assert.False(app.Row("WingetId").HasError);
        Assert.Empty(app.Problems);
        Assert.True(editor.CanPublish);
    }

    [Fact]
    public void A_catalog_application_whose_id_is_set_to_empty_still_inherits_the_catalog_id()
    {
        var editor = NewEditor();
        var app = editor.AddFromCatalog("7zip");
        app.Row("WingetId").IsOverridden = true;
        app.Row("WingetId").TextValue = "";

        Assert.False(app.Row("WingetId").HasError);
    }

    [Fact]
    public void A_web_source_application_is_not_flagged()
    {
        var editor = NewEditor();
        var catalogWeb = editor.AddFromCatalog("contoso");
        Assert.False(catalogWeb.Row("WingetId").HasError);
        Assert.Empty(catalogWeb.Problems);

        var custom = editor.AddValues("vendor-tool", new SortedDictionary<string, SettingValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["Source"] = SettingValue.From("web"),
            ["VersionUrl"] = SettingValue.From("https://vendor.example/v"),
            ["VersionRegex"] = SettingValue.From("(\\d+)"),
            ["DownloadUrl"] = SettingValue.From("https://vendor.example/setup.exe"),
        });
        Assert.False(custom.Row("WingetId").HasError);
        Assert.True(editor.CanPublish);
    }

    [Fact]
    public void Switching_the_source_to_web_clears_the_flag_and_back_to_winget_raises_it()
    {
        var editor = NewEditor();
        var app = editor.AddFromInventory(NodeJs)!;
        Assert.True(app.Row("WingetId").HasError);

        app.Row("Source").TextValue = "web";
        Assert.False(app.Row("WingetId").HasError);

        app.Row("Source").TextValue = "winget";
        Assert.True(app.Row("WingetId").HasError);
    }

    [Fact]
    public void A_loaded_document_with_a_winget_application_without_id_cannot_be_published_until_fixed()
    {
        var document = new SettingsDocument();
        document.Apps["node-js"] = new SortedDictionary<string, SettingValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["Enabled"] = SettingValue.From(true),
            ["Source"] = SettingValue.From("winget"),
        };
        var editor = NewEditor(document);

        Assert.True(editor.HasProblems);
        Assert.False(editor.CanPublish);
    }
}
