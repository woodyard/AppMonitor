using System.Text.RegularExpressions;
using Arkimentum.AppMonitor.Api.Contracts;
using Arkimentum.AppMonitor.Web.Catalog;

namespace Arkimentum.AppMonitor.Web.Editing;

/// <summary>
/// The one pending organization configuration behind both the Settings page and the Applications page: every global
/// value and every configured application, generated from <see cref="SettingsSchema"/> over the document the server
/// returned.
///
/// <para>
/// Dirtiness is a JSON comparison against the baseline the editor was loaded with, so a value typed back to what it
/// was is not a change. <see cref="LoadPending"/> puts another document in without moving that baseline, which is
/// what "restore this version" means: the restored document becomes the pending change and publishing it appends a
/// new revision rather than rewinding the history.
/// </para>
///
/// <para>
/// Ported from <c>src/Arkimentum.AppMonitor.Admin/ViewModels/ConfigurationEditor.cs</c>. No Blazor dependency: the
/// pages subscribe to <see cref="Changed"/> and re-render.
/// </para>
/// </summary>
public sealed partial class ConfigDocumentEditor
{
    /// <summary>AppIds become registry key names on the device; the schema only accepts these characters.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9._-]+$")]
    public static partial Regex AppIdPattern { get; }

    [GeneratedRegex(@"\s*\((?:x64|x86|64-bit|32-bit|user|machine|current user|all users)[^)]*\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ArchitectureSuffix { get; }

    [GeneratedRegex(@"\s+v?\d+(?:\.\d+)+[\w.\-]*\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionSuffix { get; }

    private readonly Func<string, CatalogEntry?> _catalog;
    private readonly List<AppEditor> _apps = [];

    private SettingsDocument _loaded = new();
    private string _baselineJson;
    private bool _suspend;

    public ConfigDocumentEditor(Func<string, CatalogEntry?> catalog)
    {
        _catalog = catalog;
        _baselineJson = Baseline(_loaded);
        Build(_loaded);
        Recompute();
    }

    /// <summary>Raised whenever anything the pages show has changed (a load, or an edit).</summary>
    public event Action? Changed;

    public IReadOnlyList<SettingRow> GlobalRows { get; private set; } = [];

    public IReadOnlyList<SettingGroup> GlobalGroups { get; private set; } = [];

    public IReadOnlyList<AppEditor> Apps => _apps;

    public bool IsDirty { get; private set; }

    /// <summary>Row-level problems (range, choice, list syntax) and, when there are none, the document's own.</summary>
    public IReadOnlyList<string> Problems { get; private set; } = [];

    public bool HasProblems => Problems.Count > 0;

    public bool CanPublish => IsDirty && !HasProblems;

    // ---------------------------------------------------------------- load

    /// <summary>Loads the document the server returned and makes it the baseline. Also used by Discard.</summary>
    public void Load(SettingsDocument document)
    {
        _loaded = document;
        _baselineJson = Baseline(document);
        Build(document);
        Recompute();
    }

    /// <summary>
    /// Puts another document into the editor without moving the baseline, so it reads as a pending change that still
    /// has to be published. Used by "Restore this version".
    /// </summary>
    public void LoadPending(SettingsDocument document)
    {
        Build(document);
        Recompute();
    }

    /// <summary>Throws away the pending edits and goes back to the document that was loaded.</summary>
    public void Discard() => Load(_loaded);

    /// <summary>
    /// The loaded document reduced to what <see cref="ToDocument"/> can produce, so dirtiness compares like with
    /// like: a document that arrived from the server also carries provenance fields (<c>exportedUtc</c>, …) that the
    /// editor neither shows nor rewrites, and comparing those would make a freshly loaded document look dirty.
    /// </summary>
    private static string Baseline(SettingsDocument document)
    {
        var reduced = new SettingsDocument { Description = document.Description };
        foreach (var (name, value) in document.Global) reduced.Global[name] = value;
        foreach (var (appId, values) in document.Apps) reduced.Apps[appId] = values;
        return reduced.ToJson();
    }

    /// <summary>The document the editor currently describes - what Publish sends.</summary>
    public SettingsDocument ToDocument()
    {
        // The description travels with the document: the editor does not show it, so it must not silently drop it.
        var document = new SettingsDocument { Description = _loaded.Description };
        foreach (var row in GlobalRows)
        {
            if (row.ToValue() is { } value) document.Global[row.Name] = value;
        }
        foreach (var app in _apps) document.Apps[app.AppId] = app.ToValues();
        return document;
    }

    private void Build(SettingsDocument values)
    {
        _suspend = true;
        try
        {
            foreach (var row in GlobalRows) row.ValueChanged -= OnGlobalChanged;
            GlobalRows = SettingsSchema.Global
                .Select(def => new SettingRow(def, values.Global.TryGetValue(def.Name, out var v) ? v : null))
                .ToList();
            foreach (var row in GlobalRows)
            {
                row.SetInherited(row.DefaultText(), SettingRow.InheritedFromBuiltIn);
                row.ValueChanged += OnGlobalChanged;
            }
            GlobalGroups = GlobalRows.GroupBy(r => r.Category).Select(g => new SettingGroup(g.Key, [.. g])).ToList();

            foreach (var app in _apps) app.Changed -= OnAppChanged;
            _apps.Clear();
            // Ordered by the name the list shows, not by AppId: a catalog id ("vscode") and a winget id
            // ("Microsoft.VisualStudioCode") sort nowhere near each other, which reads as an unsorted list.
            _apps.AddRange(values.Apps
                .Select(pair => CreateApp(pair.Key, pair.Value))
                .OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(a => a.AppId, StringComparer.OrdinalIgnoreCase));
        }
        finally
        {
            _suspend = false;
        }
    }

    private AppEditor CreateApp(string appId, IReadOnlyDictionary<string, SettingValue> values)
    {
        var app = new AppEditor(appId, values, _catalog(appId), GlobalValue);
        app.Changed += OnAppChanged;
        return app;
    }

    // ---------------------------------------------------------------- applications

    public bool Exists(string appId) => _apps.Any(a => a.AppId.Equals(appId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Adds a catalog-backed application: only <c>Enabled</c> is written, so the shipped identity, source and
    /// detection data stay authoritative and follow the catalog when it is updated.
    /// </summary>
    public AppEditor AddFromCatalog(string appId) => AddValues(appId, new SortedDictionary<string, SettingValue>(StringComparer.OrdinalIgnoreCase)
    {
        ["Enabled"] = SettingValue.From(true),
    });

    /// <summary>Adds an application with exactly these values.</summary>
    public AppEditor AddValues(string appId, SortedDictionary<string, SettingValue> values) => Insert(CreateApp(appId, values));

    /// <summary>A hand-made winget application: enabled, source winget, nothing else.</summary>
    public AppEditor AddCustom(string appId) => AddValues(appId, new SortedDictionary<string, SettingValue>(StringComparer.OrdinalIgnoreCase)
    {
        ["Enabled"] = SettingValue.From(true),
        ["Source"] = SettingValue.From("winget"),
    });

    /// <summary>
    /// Adds an application the fleet inventory reported. A catalog match is added by catalog id with nothing but
    /// <c>Enabled</c>; anything else becomes a plain winget application with a cleaned display name, the source and
    /// the winget id. Context is deliberately left unset, so the agent resolves it from where the app is installed.
    /// Returns null when the AppId is unusable or already in the document.
    /// </summary>
    public AppEditor? AddFromInventory(OrganizationInventoryItem item)
    {
        var appId = SuggestAppId(item);
        if (appId is null || Exists(appId)) return null;

        var values = new SortedDictionary<string, SettingValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["Enabled"] = SettingValue.From(true),
        };
        if (item.CatalogAppId is null)
        {
            values["DisplayName"] = SettingValue.From(CleanDisplayName(item.DisplayName));
            values["Source"] = SettingValue.From("winget");
            values["WingetId"] = SettingValue.From(item.WingetId!);
        }
        return AddValues(appId, values);
    }

    /// <summary>The catalog id when the catalog knows the application, otherwise the winget id verbatim.</summary>
    public static string? SuggestAppId(OrganizationInventoryItem item)
    {
        var id = item.CatalogAppId ?? item.WingetId;
        return string.IsNullOrWhiteSpace(id) || !AppIdPattern.IsMatch(id) ? null : id;
    }

    /// <summary>Trailing version and architecture noise ("7-Zip 26.02 (x64 edition)" becomes "7-Zip").</summary>
    public static string CleanDisplayName(string name)
    {
        var text = name.Trim();
        for (var i = 0; i < 3; i++)
        {
            var next = ArchitectureSuffix.Replace(text, string.Empty);
            next = VersionSuffix.Replace(next, string.Empty).Trim();
            if (next == text) break;
            text = next;
        }
        return text.Length >= 2 ? text : name.Trim();
    }

    public AppEditor Duplicate(AppEditor source, string newAppId) => AddValues(newAppId, source.ToValues());

    public void Remove(AppEditor app)
    {
        app.Changed -= OnAppChanged;
        _apps.Remove(app);
        Recompute();
    }

    private AppEditor Insert(AppEditor app)
    {
        var index = 0;
        while (index < _apps.Count &&
               string.Compare(_apps[index].DisplayName, app.DisplayName, StringComparison.CurrentCultureIgnoreCase) < 0)
        {
            index++;
        }
        _apps.Insert(index, app);
        Recompute();
        return app;
    }

    // ---------------------------------------------------------------- global value lookup

    /// <summary>
    /// The effective text of a global setting as the editor currently has it, so per-application "inherits from the
    /// global setting" hints follow an unpublished change to the global value.
    /// </summary>
    public string GlobalValue(string name)
    {
        var row = GlobalRows.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (row is null) return string.Empty;
        return row.IsOverridden ? row.TextValue.Trim() : row.DefaultText();
    }

    // ---------------------------------------------------------------- change tracking

    private void OnGlobalChanged(SettingRow row)
    {
        foreach (var app in _apps) app.RefreshInherited();
        Recompute();
    }

    private void OnAppChanged() => Recompute();

    private void Recompute()
    {
        if (_suspend) return;
        var document = ToDocument();
        IsDirty = !string.Equals(document.ToJson(), _baselineJson, StringComparison.Ordinal);

        var problems = new List<string>();
        problems.AddRange(GlobalRows.Where(r => r.HasError).Select(r => r.Error!));
        foreach (var app in _apps) problems.AddRange(app.Problems.Select(p => $"{app.AppId}: {p}"));
        if (problems.Count == 0) problems.AddRange(document.Validate());
        Problems = problems;

        Changed?.Invoke();
    }
}
