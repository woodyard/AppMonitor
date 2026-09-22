using Arkimentum.AppMonitor.Api.Contracts;
using Arkimentum.AppMonitor.Web.Catalog;

namespace Arkimentum.AppMonitor.Web.Editing;

/// <summary>
/// One configured application: the rows of <see cref="SettingsSchema.App"/> over the values the organization
/// document holds for that AppId, plus the summary the master list shows.
///
/// <para>
/// Ported from <c>src/Arkimentum.AppMonitor.Admin/ViewModels/AppEditorViewModel.cs</c> without the WPF plumbing and
/// without the local "test detection" action, which says nothing about a fleet. Two rules survive verbatim, because
/// they are what makes the editor readable:
/// </para>
/// <list type="bullet">
///   <item>Only the fields of the effective source are shown - winget ids for winget, URLs and installer arguments
///   for the vendor web site.</item>
///   <item>A row that is not set shows what the agent would use instead: behaviour falls back to the global
///   <c>Default*</c> setting, identity and detection to the catalog entry, everything else to the built-in default.</item>
/// </list>
/// </summary>
public sealed class AppEditor
{
    /// <summary>winget-only fields; hidden when the effective source is the vendor web site.</summary>
    private static readonly HashSet<string> WingetFields =
        new(StringComparer.OrdinalIgnoreCase) { "WingetId", "WingetSource", "WingetExtraArgs", "WingetReplaceOnMismatch" };

    /// <summary>Web-only fields; hidden when the effective source is winget.</summary>
    private static readonly HashSet<string> WebFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "VersionUrl", "VersionRegex", "DownloadUrl", "InstallerType", "InstallerArgs",
        "UserDownloadUrl", "UserInstallerArgs", "Sha256Url", "Sha256",
    };

    /// <summary>Per-app setting name -> the global setting it inherits from when it is not set.</summary>
    private static readonly Dictionary<string, string> GlobalFallbacks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Mandatory"] = "DefaultMandatory",
        ["DeadlineHours"] = "DefaultDeadlineHours",
        ["MaxDeferrals"] = "DefaultMaxDeferrals",
        ["DeferralOptions"] = "DefaultDeferralOptions",
        ["AutoInstall"] = "DefaultAutoInstall",
        ["CloseGracePeriodMinutes"] = "DefaultCloseGracePeriodMinutes",
        ["ForceCloseAtDeadline"] = "DefaultForceCloseAtDeadline",
        ["NotificationIntervalMinutes"] = "NotificationIntervalMinutes",
        ["NotificationMode"] = "NotificationMode",
    };

    private readonly Func<string, string> _globalValue;

    public AppEditor(string appId, IReadOnlyDictionary<string, SettingValue> values, CatalogEntry? catalogEntry,
        Func<string, string> globalValue)
    {
        AppId = appId;
        CatalogEntry = catalogEntry;
        _globalValue = globalValue;

        Rows = SettingsSchema.App
            .Select(def => new SettingRow(def, values.TryGetValue(def.Name, out var v) ? v : null))
            .ToList();
        Groups = Rows.GroupBy(r => r.Category).Select(g => new SettingGroup(g.Key, [.. g])).ToList();
        foreach (var row in Rows) row.ValueChanged += OnRowChanged;

        RefreshInherited();
        RefreshVisibility();
    }

    /// <summary>Raised whenever a value changes, so the owning document can re-evaluate dirtiness.</summary>
    public event Action? Changed;

    public string AppId { get; }

    /// <summary>The catalog entry this AppId matches, when there is one. Null = a custom application.</summary>
    public CatalogEntry? CatalogEntry { get; }

    public bool IsFromCatalog => CatalogEntry is not null;

    public IReadOnlyList<SettingRow> Rows { get; }

    public IReadOnlyList<SettingGroup> Groups { get; }

    public SettingRow Row(string name) => Rows.First(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    // ---------------------------------------------------------------- master-list summary

    public string DisplayName
    {
        get
        {
            var configured = Row("DisplayName");
            if (configured.IsOverridden && !string.IsNullOrWhiteSpace(configured.TextValue)) return configured.TextValue.Trim();
            if (!string.IsNullOrWhiteSpace(CatalogEntry?.DisplayName)) return CatalogEntry!.DisplayName!.Trim();
            return AppId;
        }
    }

    public string SourceText => EffectiveText("Source", NormalisedCatalogSource ?? "winget");

    public string ContextText => EffectiveText("Context", CatalogEntry?.Context?.ToLowerInvariant() ?? "auto");

    public bool IsWeb => SourceText.Equals("web", StringComparison.OrdinalIgnoreCase);

    public bool IsEnabled => Row("Enabled") is { IsOverridden: true } row
        ? row.BoolValue
        : CatalogEntry?.Enabled ?? true;

    public bool IsMandatory => Row("Mandatory") is { IsOverridden: true } row
        ? row.BoolValue
        : _globalValue("DefaultMandatory") is "1" or "true" or "True";

    /// <summary>"Yes · 24 h" / "No", as the master list shows the enforcement.</summary>
    public string MandatoryText
    {
        get
        {
            if (!IsMandatory) return "No";
            var hours = EffectiveText("DeadlineHours", _globalValue("DefaultDeadlineHours"));
            return int.TryParse(hours, out var h) && h > 0 ? $"Yes · {h} h" : "Yes";
        }
    }

    public string SummaryLine => $"{AppId} · {SourceText} · {ContextText}";

    public bool Matches(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        var f = filter.Trim();
        return DisplayName.Contains(f, StringComparison.OrdinalIgnoreCase) ||
               AppId.Contains(f, StringComparison.OrdinalIgnoreCase) ||
               SourceText.Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- editing

    /// <summary>The values to write for this application (empty when nothing is set).</summary>
    public SortedDictionary<string, SettingValue> ToValues()
    {
        var values = new SortedDictionary<string, SettingValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Rows)
        {
            if (row.ToValue() is { } value) values[row.Name] = value;
        }
        return values;
    }

    /// <summary>
    /// Row-level problems plus the one rule that spans rows: a vendor web site source needs its URLs. The agent's
    /// configuration reader drops a "web" application that has no version URL or download URL, and the version regex
    /// is what turns the version page into a version, so publishing without them silently stops the monitoring.
    /// </summary>
    public IEnumerable<string> Problems
    {
        get
        {
            foreach (var row in Rows.Where(r => r.HasError)) yield return row.Error!;
            if (IsWeb && MissingWebFields().ToList() is { Count: > 0 } missing)
                yield return $"the vendor web site source needs {Join(missing)}; fill them in or set the update source back to winget. The agent ignores the application until then.";
        }
    }

    /// <summary>Titles of the required web-source fields that neither this document nor the catalog supplies.</summary>
    private IEnumerable<string> MissingWebFields() =>
        RequiredWebFields.Select(Row).Where(row => string.IsNullOrWhiteSpace(EffectiveText(row.Name, CatalogText(row.Name)))).Select(row => row.Title);

    private static readonly string[] RequiredWebFields = ["VersionUrl", "VersionRegex", "DownloadUrl"];

    private static string Join(IReadOnlyList<string> titles) => titles.Count switch
    {
        1 => $"a {titles[0]}",
        2 => $"a {titles[0]} and a {titles[1]}",
        _ => $"a {string.Join(", a ", titles.Take(titles.Count - 1))} and a {titles[^1]}",
    };

    /// <summary>Re-reads the inherited hints (globals for behaviour, catalog for identity, source and detection).</summary>
    public void RefreshInherited()
    {
        foreach (var row in Rows)
        {
            var (text, label) = Inherited(row);
            row.SetInherited(text, label);
        }
    }

    private (string Text, string Label) Inherited(SettingRow row)
    {
        // Behaviour is policy, not catalog data: the agent falls back to the global Default* value.
        if (GlobalFallbacks.TryGetValue(row.Name, out var global)) return (_globalValue(global), SettingRow.InheritedFromGlobal);

        var catalog = CatalogText(row.Name);
        return catalog is not null ? (catalog, SettingRow.InheritedFromCatalog) : (row.DefaultText(), SettingRow.InheritedFromBuiltIn);
    }

    private string? NormalisedCatalogSource => CatalogEntry?.Source is { } s && s.Equals("web", StringComparison.OrdinalIgnoreCase)
        ? "web"
        : CatalogEntry is null ? null : "winget";

    private string? CatalogText(string name)
    {
        if (CatalogEntry is not { } c) return null;
        return name switch
        {
            "Enabled" => (c.Enabled ?? true) ? "1" : "0",
            "DisplayName" => c.DisplayName,
            "Source" => NormalisedCatalogSource,
            "Context" => c.Context?.ToLowerInvariant() ?? "auto",
            "WingetId" => c.WingetId,
            "WingetSource" => c.WingetSourceName,
            "WingetExtraArgs" => c.WingetExtraArgs,
            "VersionUrl" => c.VersionUrl,
            "VersionRegex" => c.VersionRegex,
            "DownloadUrl" => c.DownloadUrl,
            "InstallerType" => c.InstallerType?.ToLowerInvariant() ?? "exe",
            "InstallerArgs" => c.InstallerArgs,
            "UserDownloadUrl" => c.UserDownloadUrl,
            "UserInstallerArgs" => c.UserInstallerArgs,
            "Sha256Url" => c.Sha256Url,
            "Sha256" => c.Sha256,
            "DetectDisplayNameRegex" => c.DetectDisplayNameRegex,
            "DetectPublisherRegex" => c.DetectPublisherRegex,
            "DetectFilePath" => c.DetectFilePath,
            "MinimumVersion" => c.MinimumVersion,
            "ProcessNames" => c.ProcessNames is { Count: > 0 } p ? string.Join(", ", p) : null,
            _ => null,
        } ?? string.Empty;
    }

    private string EffectiveText(string name, string? fallback)
    {
        var row = Row(name);
        if (row.IsOverridden && !string.IsNullOrWhiteSpace(row.TextValue)) return row.TextValue.Trim();
        return fallback ?? string.Empty;
    }

    /// <summary>Only the fields that belong to the effective source are shown.</summary>
    private void RefreshVisibility()
    {
        var web = IsWeb;
        foreach (var row in Rows)
        {
            if (WingetFields.Contains(row.Name)) row.IsVisible = !web;
            else if (WebFields.Contains(row.Name)) row.IsVisible = web;
        }
    }

    private void OnRowChanged(SettingRow row)
    {
        RefreshVisibility();
        Changed?.Invoke();
    }
}
