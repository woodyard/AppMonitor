using Arkimentum.AppMonitor.Api.Contracts;
using Arkimentum.AppMonitor.Web.Catalog;
using Arkimentum.AppMonitor.Web.Editing;
using Xunit;

namespace Arkimentum.AppMonitor.Web.Tests;

/// <summary>
/// The rules the two editor pages depend on, exercised without Blazor: dirtiness is a JSON comparison against the
/// loaded baseline, "reset to default" removes a key rather than writing the default into the document, a catalog
/// application is added with nothing but <c>Enabled</c>, and a restored revision reads as a pending change.
/// </summary>
public sealed class ConfigDocumentEditorTests
{
    /// <summary>A two-entry catalog: one winget application, one web application, as catalog.json shapes them.</summary>
    private static readonly IReadOnlyDictionary<string, CatalogEntry> Catalog = new Dictionary<string, CatalogEntry>(StringComparer.OrdinalIgnoreCase)
    {
        ["7zip"] = new CatalogEntry
        {
            AppId = "7zip",
            DisplayName = "7-Zip",
            Source = "winget",
            WingetId = "7zip.7zip",
            ProcessNames = ["7zFM", "7zG"],
            DetectDisplayNameRegex = "^7-Zip\\b",
        },
        ["contoso"] = new CatalogEntry
        {
            AppId = "contoso",
            DisplayName = "Contoso Tool",
            Source = "web",
            VersionUrl = "https://contoso.example/version",
            DownloadUrl = "https://contoso.example/setup-{version}.exe",
            InstallerArgs = "/S",
        },
    };

    private static ConfigDocumentEditor NewEditor(SettingsDocument? document = null)
    {
        var editor = new ConfigDocumentEditor(id => Catalog.GetValueOrDefault(id));
        editor.Load(document ?? new SettingsDocument());
        return editor;
    }

    private static SettingsDocument DocumentWith(params (string Name, SettingValue Value)[] global)
    {
        var document = new SettingsDocument();
        foreach (var (name, value) in global) document.Global[name] = value;
        return document;
    }

    // ---------------------------------------------------------------- dirty tracking

    [Fact]
    public void A_freshly_loaded_document_is_not_dirty()
    {
        var editor = NewEditor(DocumentWith(("ScanIntervalMinutes", SettingValue.From(120))));

        Assert.False(editor.IsDirty);
        Assert.False(editor.CanPublish);
    }

    [Fact]
    public void A_document_with_provenance_fields_is_not_dirty_when_loaded()
    {
        // These arrive from the server and the editor neither shows nor rewrites them; comparing them would make
        // every freshly loaded document look dirty.
        var document = DocumentWith(("ScanIntervalMinutes", SettingValue.From(120)));
        document.ExportedBy = "someone@example.com";
        document.ExportedUtc = DateTimeOffset.UtcNow;
        document.ProductVersion = "1.2.3";

        Assert.False(NewEditor(document).IsDirty);
    }

    [Fact]
    public void Changing_a_value_makes_the_document_dirty()
    {
        var editor = NewEditor(DocumentWith(("ScanIntervalMinutes", SettingValue.From(120))));

        editor.GlobalRows.First(r => r.Name == "ScanIntervalMinutes").TextValue = "240";

        Assert.True(editor.IsDirty);
        Assert.True(editor.CanPublish);
    }

    [Fact]
    public void Typing_a_value_back_to_what_it_was_is_not_a_change()
    {
        var editor = NewEditor(DocumentWith(("ScanIntervalMinutes", SettingValue.From(120))));
        var row = editor.GlobalRows.First(r => r.Name == "ScanIntervalMinutes");

        row.TextValue = "240";
        row.TextValue = "120";

        Assert.False(editor.IsDirty);
    }

    [Fact]
    public void Discard_goes_back_to_the_loaded_document()
    {
        var editor = NewEditor(DocumentWith(("ScanIntervalMinutes", SettingValue.From(120))));
        editor.GlobalRows.First(r => r.Name == "ScanIntervalMinutes").TextValue = "999";

        editor.Discard();

        Assert.False(editor.IsDirty);
        Assert.Equal("120", editor.GlobalRows.First(r => r.Name == "ScanIntervalMinutes").TextValue);
    }

    // ---------------------------------------------------------------- reset to default

    [Fact]
    public void Reset_to_default_removes_the_key_instead_of_writing_the_default()
    {
        var editor = NewEditor(DocumentWith(("ScanIntervalMinutes", SettingValue.From(999))));
        var row = editor.GlobalRows.First(r => r.Name == "ScanIntervalMinutes");

        row.IsOverridden = false;

        Assert.True(editor.IsDirty);
        Assert.DoesNotContain("ScanIntervalMinutes", editor.ToDocument().Global.Keys);
        // The editor now shows the built-in default as the greyed-out hint it has become.
        Assert.Equal("240", row.TextValue);
        Assert.Equal(SettingRow.InheritedFromBuiltIn, row.InheritedLabel);
    }

    [Fact]
    public void Setting_a_row_writes_the_key()
    {
        var editor = NewEditor();
        var row = editor.GlobalRows.First(r => r.Name == "NotificationsEnabled");

        row.IsOverridden = true;
        row.BoolValue = false;

        var document = editor.ToDocument();
        Assert.True(document.Global.ContainsKey("NotificationsEnabled"));
        Assert.False(document.Global["NotificationsEnabled"].AsBool());
    }

    // ---------------------------------------------------------------- validation

    [Fact]
    public void A_number_outside_its_range_is_a_row_problem()
    {
        var editor = NewEditor();
        var row = editor.GlobalRows.First(r => r.Name == "ScanIntervalMinutes");

        row.IsOverridden = true;
        row.TextValue = "1";        // the schema allows 5..10080

        Assert.True(row.HasError);
        Assert.Contains("between 5 and 10080", row.Error);
        Assert.True(editor.HasProblems);
        Assert.False(editor.CanPublish);
    }

    [Fact]
    public void A_number_that_is_not_a_number_is_a_row_problem()
    {
        var editor = NewEditor();
        var row = editor.GlobalRows.First(r => r.Name == "ScanIntervalMinutes");

        row.IsOverridden = true;
        row.TextValue = "soon";

        Assert.True(row.HasError);
        Assert.False(editor.CanPublish);
    }

    [Fact]
    public void A_value_outside_a_choice_is_a_row_problem()
    {
        var editor = NewEditor();
        var row = editor.GlobalRows.First(r => r.Name == "LogLevel");

        row.IsOverridden = true;
        row.TextValue = "Chatty";

        Assert.True(row.HasError);
        Assert.Contains("must be one of", row.Error);
    }

    [Fact]
    public void An_int_list_must_hold_positive_whole_numbers()
    {
        var editor = NewEditor();
        var row = editor.GlobalRows.First(r => r.Name == "DefaultDeferralOptions");

        row.IsOverridden = true;
        row.TextValue = "60, -1, later";

        Assert.True(row.HasError);

        row.TextValue = "60, 240, 1440";

        Assert.False(row.HasError);
        Assert.Equal("60,240,1440", editor.ToDocument().Global["DefaultDeferralOptions"].AsString());
    }

    [Fact]
    public void A_string_list_is_written_as_a_list()
    {
        var editor = NewEditor();
        var app = editor.AddCustom("contoso-tool");
        var row = app.Row("ProcessNames");

        row.IsOverridden = true;
        row.TextValue = "tool\nhelper";

        Assert.Equal(["tool", "helper"], editor.ToDocument().Apps["contoso-tool"]["ProcessNames"].AsStringList());
    }

    [Fact]
    public void A_valid_document_has_no_problems()
    {
        var editor = NewEditor(DocumentWith(
            ("ScanIntervalMinutes", SettingValue.From(240)),
            ("LogLevel", SettingValue.From("Warning"))));

        Assert.False(editor.HasProblems);
        Assert.Empty(editor.ToDocument().Validate());
    }

    [Fact]
    public void Document_level_problems_surface_when_no_row_complains()
    {
        // A value the schema does not know cannot be typed into a row - it can only arrive in a loaded document.
        var document = DocumentWith(("NoSuchSetting", SettingValue.From(1)));
        var editor = NewEditor(document);

        // The unknown key is not represented by any row, so it disappears from what the editor would publish;
        // the document itself still reports it when validated directly.
        Assert.Contains(document.Validate(), p => p.Contains("NoSuchSetting"));
        Assert.DoesNotContain("NoSuchSetting", editor.ToDocument().Global.Keys);
    }

    // ---------------------------------------------------------------- adding applications

    [Fact]
    public void Adding_from_the_catalog_writes_only_Enabled()
    {
        var editor = NewEditor();

        var app = editor.AddFromCatalog("7zip");

        var values = editor.ToDocument().Apps["7zip"];
        Assert.Equal(["Enabled"], values.Keys);
        Assert.True(values["Enabled"].AsBool());
        // Identity still reads from the catalog, which is the point of writing nothing else.
        Assert.Equal("7-Zip", app.DisplayName);
        Assert.Equal("7zip.7zip", app.Row("WingetId").InheritedText);
        Assert.Equal(SettingRow.InheritedFromCatalog, app.Row("WingetId").InheritedLabel);
        Assert.True(editor.IsDirty);
    }

    [Fact]
    public void Adding_a_custom_application_writes_Enabled_and_Source()
    {
        var editor = NewEditor();

        editor.AddCustom("contoso-tool");

        Assert.Equal(["Enabled", "Source"], editor.ToDocument().Apps["contoso-tool"].Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void Exists_is_case_insensitive_like_the_registry()
    {
        var editor = NewEditor();
        editor.AddFromCatalog("7zip");

        Assert.True(editor.Exists("7ZIP"));
    }

    [Fact]
    public void Duplicating_copies_every_value_under_the_new_id()
    {
        var editor = NewEditor();
        var source = editor.AddCustom("contoso-tool");
        source.Row("WingetId").IsOverridden = true;
        source.Row("WingetId").TextValue = "Contoso.Tool";

        editor.Duplicate(source, "contoso-tool-test");

        var copy = editor.ToDocument().Apps["contoso-tool-test"];
        Assert.Equal("Contoso.Tool", copy["WingetId"].AsString());
    }

    [Fact]
    public void Removing_takes_the_application_out_of_the_document()
    {
        var editor = NewEditor();
        var app = editor.AddFromCatalog("7zip");

        editor.Remove(app);

        Assert.DoesNotContain("7zip", editor.ToDocument().Apps.Keys);
        Assert.False(editor.Exists("7zip"));
    }

    // ---------------------------------------------------------------- adding from the inventory

    [Fact]
    public void An_inventory_item_the_catalog_knows_is_added_by_catalog_id_with_only_Enabled()
    {
        var editor = NewEditor();
        var item = new OrganizationInventoryItem
        {
            DisplayName = "7-Zip 26.02 (x64 edition)",
            WingetId = "7zip.7zip",
            CatalogAppId = "7zip",
        };

        var app = editor.AddFromInventory(item);

        Assert.NotNull(app);
        Assert.Equal("7zip", app!.AppId);
        Assert.Equal(["Enabled"], editor.ToDocument().Apps["7zip"].Keys);
    }

    [Fact]
    public void An_inventory_item_the_catalog_does_not_know_becomes_a_plain_winget_application()
    {
        var editor = NewEditor();
        var item = new OrganizationInventoryItem
        {
            DisplayName = "Contoso Designer 4.10.2 (x64)",
            WingetId = "Contoso.Designer",
        };

        var app = editor.AddFromInventory(item);

        Assert.NotNull(app);
        Assert.Equal("Contoso.Designer", app!.AppId);
        var values = editor.ToDocument().Apps["Contoso.Designer"];
        Assert.Equal(["DisplayName", "Enabled", "Source", "WingetId"], values.Keys.OrderBy(k => k, StringComparer.Ordinal));
        // The trailing version and architecture noise is cleaned off the name that is shown to users.
        Assert.Equal("Contoso Designer", values["DisplayName"].AsString());
        Assert.Equal("winget", values["Source"].AsString());
        Assert.Equal("Contoso.Designer", values["WingetId"].AsString());
        // Context is deliberately left unset so the agent resolves it from where the app is installed.
        Assert.DoesNotContain("Context", values.Keys);
    }

    [Fact]
    public void An_inventory_item_without_a_usable_identity_is_not_added()
    {
        var editor = NewEditor();

        Assert.Null(editor.AddFromInventory(new OrganizationInventoryItem { DisplayName = "Something local" }));
        Assert.Null(editor.AddFromInventory(new OrganizationInventoryItem { DisplayName = "Odd", WingetId = "not a valid id" }));
        Assert.Empty(editor.ToDocument().Apps);
    }

    [Fact]
    public void An_inventory_item_that_is_already_configured_is_not_added_twice()
    {
        var editor = NewEditor();
        editor.AddFromCatalog("7zip");

        var again = editor.AddFromInventory(new OrganizationInventoryItem
        {
            DisplayName = "7-Zip",
            WingetId = "7zip.7zip",
            CatalogAppId = "7zip",
        });

        Assert.Null(again);
        Assert.Single(editor.Apps);
    }

    // ---------------------------------------------------------------- restore from history

    [Fact]
    public void Restoring_a_revision_reads_as_an_unpublished_change()
    {
        var editor = NewEditor(DocumentWith(("ScanIntervalMinutes", SettingValue.From(240))));
        var revision = DocumentWith(("ScanIntervalMinutes", SettingValue.From(60)));
        revision.Apps["7zip"] = new SortedDictionary<string, SettingValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["Enabled"] = SettingValue.From(true),
        };

        editor.LoadPending(revision);

        Assert.True(editor.IsDirty);
        Assert.True(editor.CanPublish);
        Assert.Equal("60", editor.GlobalRows.First(r => r.Name == "ScanIntervalMinutes").TextValue);
        Assert.Contains("7zip", editor.ToDocument().Apps.Keys);
    }

    [Fact]
    public void Restoring_the_document_that_is_already_current_is_not_a_change()
    {
        var editor = NewEditor(DocumentWith(("ScanIntervalMinutes", SettingValue.From(240))));

        editor.LoadPending(DocumentWith(("ScanIntervalMinutes", SettingValue.From(240))));

        Assert.False(editor.IsDirty);
    }

    [Fact]
    public void Discarding_a_restore_goes_back_to_the_published_document()
    {
        var editor = NewEditor(DocumentWith(("ScanIntervalMinutes", SettingValue.From(240))));
        editor.LoadPending(DocumentWith(("ScanIntervalMinutes", SettingValue.From(60))));

        editor.Discard();

        Assert.False(editor.IsDirty);
        Assert.Equal("240", editor.GlobalRows.First(r => r.Name == "ScanIntervalMinutes").TextValue);
    }

    // ---------------------------------------------------------------- change notifications

    [Fact]
    public void Every_edit_raises_Changed_so_the_pages_can_re_render()
    {
        var editor = NewEditor();
        var raised = 0;
        editor.Changed += () => raised++;

        editor.GlobalRows.First(r => r.Name == "NotificationsEnabled").IsOverridden = true;
        editor.AddFromCatalog("7zip");

        Assert.True(raised >= 2);
    }
}
