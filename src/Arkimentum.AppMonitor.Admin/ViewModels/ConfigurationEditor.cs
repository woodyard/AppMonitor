using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using Arkimentum.AppMonitor.Admin.Resources;
using Arkimentum.AppMonitor.Admin.Services;
using Arkimentum.AppMonitor.Configuration;
using Arkimentum.AppMonitor.Inventory;
using Arkimentum.AppMonitor.UI;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Admin.ViewModels;

/// <summary>
/// The one dirty document behind both the Settings page and the Applications page: every global value and every
/// configured application, generated from <see cref="SettingsSchema"/>, layered over the saved document and the
/// read-only policy document.
///
/// <para>
/// Where those two documents come from is <see cref="IConfigurationStore"/>'s business. The console builds two
/// instances of this class: one over <see cref="RegistryConfigurationStore"/> for this machine's registry, one over
/// <see cref="OrganizationConfigurationStore"/> for the organization document fetched from the cloud. Nothing below
/// knows which it is — the organization store simply has no policy layer, so no row is ever locked there.
/// </para>
/// </summary>
public sealed partial class ConfigurationEditor : ObservableObject
{
    /// <summary>AppIds are registry key names; the schema only accepts these characters.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9._-]+$")]
    private static partial Regex AppIdPattern { get; }

    [GeneratedRegex(@"\s*\((?:x64|x86|64-bit|32-bit|user|machine|current user|all users)[^)]*\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ArchitectureSuffix { get; }

    [GeneratedRegex(@"\s+v?\d+(?:\.\d+)+[\w.\-]*\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionSuffix { get; }

    private readonly ILogger<ConfigurationEditor> _log;
    private readonly IConfigurationStore _store;
    private readonly CatalogService _catalog;
    private readonly IDetectionTester? _tester;

    private SettingsDocument _saved = new();
    private SettingsDocument _policy = new();
    private SettingsDocument _organization = new();
    private string? _organizationName;
    private string _savedJson = string.Empty;
    private bool _showAdvanced;
    private bool _isDirty;
    private bool _suspend;

    public ConfigurationEditor(ILogger<ConfigurationEditor> log, IConfigurationStore store, CatalogService catalog, IDetectionTester? tester)
    {
        _log = log;
        _store = store;
        _catalog = catalog;
        _tester = tester;
        Reload();
    }

    /// <summary>Raised whenever anything the pages show has changed (a reload, or an edit).</summary>
    public event Action? Changed;

    /// <summary>Raised after a successful reload, so pages can re-bind their selection.</summary>
    public event Action? Reloaded;

    public IReadOnlyList<SettingRowViewModel> GlobalRows { get; private set; } = [];

    public IReadOnlyList<SettingGroupViewModel> GlobalGroups { get; private set; } = [];

    public ObservableCollection<AppEditorViewModel> Apps { get; } = [];

    public SettingsDocument PolicyDocument => _policy;

    public SettingsDocument SavedDocument => _saved;

    /// <summary>The badge an overridden row carries: "Preference" for the machine, "Organization" for the cloud.</summary>
    public string OverriddenBadge => _store.OverriddenBadge;

    public bool ShowAdvanced
    {
        get => _showAdvanced;
        set
        {
            if (!SetProperty(ref _showAdvanced, value)) return;
            foreach (var row in GlobalRows) row.ShowAdvanced = value;
            foreach (var app in Apps) app.ShowAdvanced = value;
        }
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set => SetProperty(ref _isDirty, value);
    }

    public string DirtyText => IsDirty ? Strings.UnsavedChanges : Strings.NoChanges;

    /// <summary>Editor-level problems (range, choice, list syntax) plus whatever the document itself rejects.</summary>
    public IReadOnlyList<string> Problems { get; private set; } = [];

    public bool HasProblems => Problems.Count > 0;

    public string ProblemSummary => Strings.ValidationSummary(Problems.Count);

    public bool CanApply => IsDirty && !HasProblems;

    // ---------------------------------------------------------------- load / save

    /// <summary>Re-reads both layers and rebuilds every row. Also used by Discard.</summary>
    public void Reload()
    {
        _saved = _store.ReadSaved();
        _policy = _store.ReadPolicy();
        _organization = _store.ReadOrganization();
        _organizationName = _store.OrganizationName;
        _savedJson = Baseline(_saved);
        Build(_saved);
        Recompute();
        Reloaded?.Invoke();
    }

    /// <summary>
    /// Puts another document into the editor without moving the baseline, so it reads as unsaved changes that still
    /// have to be published. Used by "Restore this version": the restored document becomes the pending edit, and
    /// publishing it appends a new revision on top of the current one rather than rewinding the history.
    /// </summary>
    public void LoadPending(SettingsDocument document)
    {
        _policy = _store.ReadPolicy();
        _organization = _store.ReadOrganization();
        _organizationName = _store.OrganizationName;
        Build(document);
        Recompute();
        Reloaded?.Invoke();
    }

    private void Build(SettingsDocument values)
    {
        _suspend = true;
        try
        {
            foreach (var row in GlobalRows) row.ValueChanged -= OnGlobalChanged;
            GlobalRows = SettingsSchema.Global
                .Select(def => new SettingRowViewModel(def,
                    values.Global.TryGetValue(def.Name, out var p) ? p : null,
                    _policy.Global.TryGetValue(def.Name, out var q) ? q : null,
                    _store.OverriddenBadge,
                    _organization.Global.TryGetValue(def.Name, out var o) ? o : null,
                    _organizationName))
                .ToList();
            foreach (var row in GlobalRows)
            {
                row.ShowAdvanced = _showAdvanced;
                row.SetInherited(DefaultText(row), Strings.InheritedFromBuiltIn);
                row.ValueChanged += OnGlobalChanged;
            }
            GlobalGroups = GlobalRows.GroupBy(r => r.Category).Select(g => new SettingGroupViewModel(g.Key, [.. g])).ToList();

            foreach (var app in Apps) app.Changed -= OnAppChanged;
            Apps.Clear();
            var ids = values.Apps.Keys.Concat(_policy.Apps.Keys).Concat(_organization.Apps.Keys).Distinct(StringComparer.OrdinalIgnoreCase);
            // Ordered by the name the list shows, not by AppId: a catalog id ("vscode") and a winget id
            // ("Microsoft.VisualStudioCode") sort nowhere near each other, which read as an unsorted list.
            var apps = ids.Select(id => CreateApp(id,
                    values.Apps.TryGetValue(id, out var pref) ? pref : new SortedDictionary<string, SettingValue>(StringComparer.OrdinalIgnoreCase),
                    _policy.Apps.TryGetValue(id, out var pol) ? pol : null,
                    _organization.Apps.TryGetValue(id, out var org) ? org : null))
                .OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(a => a.AppId, StringComparer.OrdinalIgnoreCase);
            foreach (var app in apps) Apps.Add(app);

            OnPropertyChanged(nameof(GlobalRows), nameof(GlobalGroups));
        }
        finally
        {
            _suspend = false;
        }
    }

    /// <summary>Writes the whole document to the store, then reloads so the baseline matches again.</summary>
    public void Apply()
    {
        var document = ToDocument();
        _store.Write(document);
        _log.LogInformation("Applied {Global} global value(s) and {Apps} application(s).", document.Global.Count, document.Apps.Count);
        Reload();
    }

    public void Discard()
    {
        _log.LogInformation("Discarded the unsaved configuration changes.");
        Reload();
    }

    /// <summary>
    /// The saved document reduced to what <see cref="ToDocument"/> can produce, so dirtiness compares like with
    /// like. The registry layers carry nothing else, but a document that arrived as JSON from the cloud also has
    /// provenance fields (<c>description</c>, <c>exportedUtc</c>, …) that the editor neither shows nor rewrites —
    /// comparing those would make a freshly loaded organization document look dirty the moment it appeared.
    /// </summary>
    private static string Baseline(SettingsDocument saved)
    {
        var document = new SettingsDocument { Description = saved.Description };
        foreach (var (name, value) in saved.Global) document.Global[name] = value;
        foreach (var (appId, values) in saved.Apps) document.Apps[appId] = values;
        return document.ToJson();
    }

    /// <summary>The document the editor currently describes.</summary>
    public SettingsDocument ToDocument()
    {
        // The description travels with the document: the editor does not show it, so it must not silently drop it.
        var document = new SettingsDocument { Description = _saved.Description };
        foreach (var row in GlobalRows)
        {
            if (row.ToValue() is { } value) document.Global[row.Name] = value;
        }
        foreach (var app in Apps)
        {
            if (!app.IsEditable) continue;   // policy-only and organization-only apps are never written locally
            document.Apps[app.AppId] = app.ToValues();
        }
        return document;
    }

    // ---------------------------------------------------------------- applications

    public bool Exists(string appId) => Apps.Any(a => a.AppId.Equals(appId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds a catalog-backed application: only <c>Enabled</c> is written, so catalog identity applies.</summary>
    public AppEditorViewModel AddFromCatalog(string appId)
    {
        var values = new SortedDictionary<string, SettingValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["Enabled"] = SettingValue.From(true),
        };
        return Insert(CreateApp(appId, values, null));
    }

    /// <summary>
    /// Adds an application with exactly these values. The organization Inventory page builds them the same way the
    /// Discover dialog does, but from what the fleet reported rather than from what is installed here.
    /// </summary>
    public AppEditorViewModel AddValues(string appId, SortedDictionary<string, SettingValue> values) =>
        Insert(CreateApp(appId, values, null));

    /// <summary>
    /// Adds an application the discovery found. A catalog match is added by catalog id with nothing but
    /// <c>Enabled</c>, so the shipped identity/source/detection data applies; anything else becomes a plain winget
    /// application. Returns null when the AppId is unusable or already in the document.
    /// </summary>
    public AppEditorViewModel? AddDiscovered(DiscoveredApp app)
    {
        var appId = app.SuggestedAppId;
        if (string.IsNullOrWhiteSpace(appId) || !AppIdPattern.IsMatch(appId) || Exists(appId)) return null;

        var values = new SortedDictionary<string, SettingValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["Enabled"] = SettingValue.From(true),
        };
        if (app.CatalogAppId is null)
        {
            values["DisplayName"] = SettingValue.From(CleanDisplayName(app.DisplayName));
            values["Source"] = SettingValue.From("winget");
            values["WingetId"] = SettingValue.From(app.WingetId!);
        }
        // Context is deliberately left unset: "auto" lets the agent resolve it from where the app is installed.
        _log.LogInformation("Added discovered application {AppId} ({Source}) to the pending configuration.",
            appId, app.CatalogAppId is null ? "winget" : "catalog");
        return Insert(CreateApp(appId, values, null));
    }

    /// <summary>Trailing version and architecture noise ("7-Zip 26.02 (x64 edition)" becomes "7-Zip").</summary>
    internal static string CleanDisplayName(string name)
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

    public AppEditorViewModel AddCustom(string appId)
    {
        var values = new SortedDictionary<string, SettingValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["Enabled"] = SettingValue.From(true),
            ["Source"] = SettingValue.From("winget"),
        };
        return Insert(CreateApp(appId, values, null));
    }

    public AppEditorViewModel Duplicate(AppEditorViewModel source, string newAppId) =>
        Insert(CreateApp(newAppId, source.ToValues(), null));

    public void Remove(AppEditorViewModel app)
    {
        app.Changed -= OnAppChanged;
        Apps.Remove(app);
        _log.LogInformation("Removed application {AppId} from the pending configuration.", app.AppId);
        Recompute();
    }

    private AppEditorViewModel Insert(AppEditorViewModel app)
    {
        var index = 0;
        while (index < Apps.Count &&
               string.Compare(Apps[index].DisplayName, app.DisplayName, StringComparison.CurrentCultureIgnoreCase) < 0)
        {
            index++;
        }
        Apps.Insert(index, app);
        Recompute();
        return app;
    }

    private AppEditorViewModel CreateApp(string appId, IReadOnlyDictionary<string, SettingValue> preference,
        IReadOnlyDictionary<string, SettingValue>? policy, IReadOnlyDictionary<string, SettingValue>? organization = null)
    {
        var app = new AppEditorViewModel(appId, preference, policy, _catalog.Find(appId), GlobalValue, _tester, _store.OverriddenBadge,
            organization, _organizationName)
        {
            ShowAdvanced = _showAdvanced,
        };
        app.Changed += OnAppChanged;
        return app;
    }

    // ---------------------------------------------------------------- global value lookup

    /// <summary>
    /// The effective text of a global setting as the editor currently has it, so per-app "Global default" hints
    /// follow an unsaved change to the global value.
    /// </summary>
    public string GlobalValue(string name)
    {
        var row = GlobalRows.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (row is null) return string.Empty;
        if (row.IsLockedByPolicy) return row.Format(row.Policy);
        return row.IsOverridden ? row.TextValue.Trim() : DefaultText(row);
    }

    private static string DefaultText(SettingRowViewModel row) => row.Definition.Default switch
    {
        null => string.Empty,
        bool b => b ? "1" : "0",
        var v => v.ToString() ?? string.Empty,
    };

    // ---------------------------------------------------------------- change tracking

    private void OnGlobalChanged(SettingRowViewModel row)
    {
        foreach (var app in Apps) app.RefreshInherited();
        Recompute();
    }

    private void OnAppChanged() => Recompute();

    private void Recompute()
    {
        if (_suspend) return;
        var document = ToDocument();
        IsDirty = !string.Equals(document.ToJson(), _savedJson, StringComparison.Ordinal);

        var problems = new List<string>();
        problems.AddRange(GlobalRows.Where(r => r.HasError).Select(r => r.Error!));
        foreach (var app in Apps) problems.AddRange(app.Problems.Select(p => $"{app.AppId}: {p}"));
        if (problems.Count == 0) problems.AddRange(document.Validate());
        Problems = problems;

        OnPropertyChanged(nameof(Problems), nameof(HasProblems), nameof(ProblemSummary), nameof(CanApply), nameof(DirtyText));
        Changed?.Invoke();
    }
}
