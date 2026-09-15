using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Arkimentum.AppMonitor.Admin.Resources;
using Arkimentum.AppMonitor.Admin.Services;
using Arkimentum.AppMonitor.Configuration;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.UI;

namespace Arkimentum.AppMonitor.Admin.ViewModels;

/// <summary>
/// One configured application: the master/detail right-hand editor, generated from <see cref="SettingsSchema.App"/>,
/// plus the summary the master list shows.
/// </summary>
public sealed class AppEditorViewModel : ObservableObject
{
    /// <summary>winget-only fields; hidden when the effective source is the vendor web site.</summary>
    private static readonly HashSet<string> WingetFields =
        new(StringComparer.OrdinalIgnoreCase) { "WingetId", "WingetSource", "WingetExtraArgs" };

    /// <summary>Web-only fields; hidden when the effective source is winget.</summary>
    private static readonly HashSet<string> WebFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "VersionUrl", "VersionRegex", "DownloadUrl", "InstallerType", "InstallerArgs",
        "UserDownloadUrl", "UserInstallerArgs", "Sha256Url", "Sha256",
    };

    private readonly Func<string, string> _globalValue;
    private readonly IDetectionTester? _tester;
    private readonly RelayCommand _testCommand;

    private AppPolicy? _catalogEntry;
    private readonly string? _organizationName;
    private bool _showAdvanced;
    private bool _isTesting;
    private string? _detectionInstalled;
    private string? _detectionAvailable;
    private string? _detectionMessage;
    private bool _detectionFailed;

    public AppEditorViewModel(
        string appId,
        IReadOnlyDictionary<string, SettingValue> preference,
        IReadOnlyDictionary<string, SettingValue>? policy,
        AppPolicy? catalogEntry,
        Func<string, string> globalValue,
        IDetectionTester? tester,
        string? overriddenBadge = null,
        IReadOnlyDictionary<string, SettingValue>? organization = null,
        string? organizationName = null)
    {
        AppId = appId;
        IsPolicyOnly = policy is not null && preference.Count == 0;
        IsOrganizationOnly = policy is null && organization is not null && preference.Count == 0;
        _organizationName = organizationName;
        _catalogEntry = catalogEntry;
        _globalValue = globalValue;
        _tester = tester;

        Rows = SettingsSchema.App
            .Select(def => new SettingRowViewModel(def,
                preference.TryGetValue(def.Name, out var p) ? p : null,
                policy is not null && policy.TryGetValue(def.Name, out var q) ? q : null,
                overriddenBadge,
                organization is not null && organization.TryGetValue(def.Name, out var o) ? o : null,
                organizationName))
            .ToList();

        Groups = Rows
            .GroupBy(r => r.Category)
            .Select(g => new SettingGroupViewModel(g.Key, [.. g]))
            .ToList();

        foreach (var row in Rows) row.ValueChanged += OnRowChanged;

        _testCommand = new RelayCommand(RunDetectionTest, () => _tester is not null && !_isTesting);
        RefreshInherited();
        RefreshVisibility();
    }

    /// <summary>Raised whenever an editable value changes, so the owning document can re-evaluate dirtiness.</summary>
    public event Action? Changed;

    public string AppId { get; }

    /// <summary>The application exists only in the policy layer: shown, badged and read-only.</summary>
    public bool IsPolicyOnly { get; }

    /// <summary>The application exists only in the organization configuration: shown, badged and read-only.</summary>
    public bool IsOrganizationOnly { get; }

    public bool IsEditable => !IsPolicyOnly && !IsOrganizationOnly;

    public bool IsReadOnly => !IsEditable;

    /// <summary>Badge text for a read-only application: which layer owns it.</summary>
    public string ReadOnlyBadge => IsPolicyOnly ? Strings.BadgePolicy : Strings.BadgeOrganization;

    /// <summary>Why the application cannot be edited here, and where to edit it instead.</summary>
    public string ReadOnlyHint => IsPolicyOnly ? Strings.PolicyAppHint : Strings.OrganizationAppHint(_organizationName);

    public IReadOnlyList<SettingRowViewModel> Rows { get; }

    public IReadOnlyList<SettingGroupViewModel> Groups { get; }

    public ICommand TestDetectionCommand => _testCommand;

    // ---------------------------------------------------------------- master-list summary

    public string DisplayName
    {
        get
        {
            var configured = Row("DisplayName");
            if (configured.IsOverridden && !string.IsNullOrWhiteSpace(configured.TextValue)) return configured.TextValue.Trim();
            if (!string.IsNullOrWhiteSpace(_catalogEntry?.DisplayName)) return _catalogEntry!.DisplayName;
            return AppId;
        }
    }

    public string SourceText => EffectiveText("Source", _catalogEntry?.Source == UpdateSource.Web ? "web" : "winget");

    public string ContextText => EffectiveText("Context", _catalogEntry?.Context.ToString().ToLowerInvariant() ?? "auto");

    public bool IsEnabled => Row("Enabled") is { IsOverridden: true } row ? row.BoolValue : _catalogEntry?.Enabled ?? true;

    public string EnabledText => IsEnabled ? Strings.AppEnabled : Strings.AppDisabled;

    public bool IsMandatory =>
        Row("Mandatory") is { IsOverridden: true } row ? row.BoolValue : _globalValue("DefaultMandatory") is "1" or "true";

    public string MandatoryText
    {
        get
        {
            if (!IsMandatory) return Strings.No;
            var hours = EffectiveText("DeadlineHours", _globalValue("DefaultDeadlineHours"));
            return int.TryParse(hours, out var h) && h > 0 ? $"{Strings.Yes} · {h} h" : Strings.Yes;
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

    public bool ShowAdvanced
    {
        get => _showAdvanced;
        set
        {
            if (!SetProperty(ref _showAdvanced, value)) return;
            foreach (var row in Rows) row.ShowAdvanced = value;
        }
    }

    public SettingRowViewModel Row(string name) =>
        Rows.First(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The values to write for this application (empty when nothing is overridden).</summary>
    public SortedDictionary<string, SettingValue> ToValues()
    {
        var values = new SortedDictionary<string, SettingValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Rows)
        {
            if (row.ToValue() is { } value) values[row.Name] = value;
        }
        return values;
    }

    public IEnumerable<string> Problems => Rows.Where(r => r.HasError).Select(r => r.Error!);

    public void SetCatalogEntry(AppPolicy? entry)
    {
        _catalogEntry = entry;
        RefreshInherited();
    }

    /// <summary>Re-reads the inherited hints (catalog for identity/source/detection, globals for behaviour).</summary>
    public void RefreshInherited()
    {
        foreach (var row in Rows)
        {
            var (text, label) = Inherited(row);
            row.SetInherited(text, label);
        }
        RefreshDerived();
    }

    private (string Text, string Label) Inherited(SettingRowViewModel row)
    {
        // Behaviour is policy, not catalog data: the agent falls back to the global Default* value.
        var global = row.Name switch
        {
            "Mandatory" => "DefaultMandatory",
            "DeadlineHours" => "DefaultDeadlineHours",
            "MaxDeferrals" => "DefaultMaxDeferrals",
            "DeferralOptions" => "DefaultDeferralOptions",
            "AutoInstall" => "DefaultAutoInstall",
            "CloseGracePeriodMinutes" => "DefaultCloseGracePeriodMinutes",
            "ForceCloseAtDeadline" => "DefaultForceCloseAtDeadline",
            "NotificationIntervalMinutes" => "NotificationIntervalMinutes",
            _ => null,
        };
        if (global is not null) return (_globalValue(global), Strings.InheritedFromGlobal);

        var catalog = CatalogText(row.Name);
        return catalog is not null
            ? (catalog, Strings.InheritedFromCatalog)
            : (DefaultText(row), Strings.InheritedFromBuiltIn);
    }

    private static string DefaultText(SettingRowViewModel row) => row.Definition.Default switch
    {
        null => string.Empty,
        bool b => b ? "1" : "0",
        var v => v.ToString() ?? string.Empty,
    };

    private string? CatalogText(string name)
    {
        if (_catalogEntry is not { } c) return null;
        return name switch
        {
            "Enabled" => c.Enabled ? "1" : "0",
            "DisplayName" => c.DisplayName,
            "Source" => c.Source == UpdateSource.Web ? "web" : "winget",
            "Context" => c.Context.ToString().ToLowerInvariant(),
            "WingetId" => c.WingetId,
            "WingetSource" => c.WingetSourceName,
            "WingetExtraArgs" => c.WingetExtraArgs,
            "VersionUrl" => c.VersionUrl,
            "VersionRegex" => c.VersionRegex,
            "DownloadUrl" => c.DownloadUrl,
            "InstallerType" => c.InstallerType.ToString().ToLowerInvariant(),
            "InstallerArgs" => c.InstallerArgs,
            "UserDownloadUrl" => c.UserDownloadUrl,
            "UserInstallerArgs" => c.UserInstallerArgs,
            "Sha256Url" => c.Sha256Url,
            "Sha256" => c.Sha256,
            "DetectDisplayNameRegex" => c.DetectDisplayNameRegex,
            "DetectPublisherRegex" => c.DetectPublisherRegex,
            "DetectFilePath" => c.DetectFilePath,
            "MinimumVersion" => c.MinimumVersion,
            "ProcessNames" => c.ProcessNames.Count > 0 ? string.Join(", ", c.ProcessNames) : null,
            _ => null,
        } ?? string.Empty;
    }

    private string EffectiveText(string name, string? fallback)
    {
        var row = Row(name);
        if (row.IsLocked) return row.Format(row.LockValue);
        if (row.IsOverridden && !string.IsNullOrWhiteSpace(row.TextValue)) return row.TextValue.Trim();
        return fallback ?? string.Empty;
    }

    /// <summary>Only the fields that belong to the selected source are shown.</summary>
    private void RefreshVisibility()
    {
        var web = SourceText.Equals("web", StringComparison.OrdinalIgnoreCase);
        foreach (var row in Rows)
        {
            if (WingetFields.Contains(row.Name)) row.IsVisible = !web;
            else if (WebFields.Contains(row.Name)) row.IsVisible = web;
        }
    }

    private void OnRowChanged(SettingRowViewModel row)
    {
        RefreshVisibility();
        RefreshDerived();
        Changed?.Invoke();
    }

    private void RefreshDerived() => OnPropertyChanged(
        nameof(DisplayName), nameof(SourceText), nameof(ContextText), nameof(IsEnabled),
        nameof(EnabledText), nameof(IsMandatory), nameof(MandatoryText), nameof(SummaryLine));

    // ---------------------------------------------------------------- detection test

    public bool IsTesting
    {
        get => _isTesting;
        private set
        {
            if (SetProperty(ref _isTesting, value)) _testCommand.RaiseCanExecuteChanged();
        }
    }

    public string? DetectionInstalledVersion
    {
        get => _detectionInstalled;
        private set => SetProperty(ref _detectionInstalled, value);
    }

    public string? DetectionAvailableVersion
    {
        get => _detectionAvailable;
        private set => SetProperty(ref _detectionAvailable, value);
    }

    public string? DetectionMessage
    {
        get => _detectionMessage;
        private set
        {
            if (SetProperty(ref _detectionMessage, value)) OnPropertyChanged(nameof(HasDetectionResult));
        }
    }

    public bool DetectionFailed
    {
        get => _detectionFailed;
        private set => SetProperty(ref _detectionFailed, value);
    }

    public bool HasDetectionResult => !string.IsNullOrEmpty(_detectionMessage);

    /// <summary>True when the test had to fall back to the values in the editor because the app is not saved yet.</summary>
    public bool DetectionUsedEditorValues { get; private set; }

    private async void RunDetectionTest()
    {
        if (_tester is null) return;
        IsTesting = true;
        DetectionMessage = null;
        DetectionInstalledVersion = null;
        DetectionAvailableVersion = null;
        DetectionFailed = false;
        try
        {
            var outcome = await _tester.TestAsync(AppId, BuildFallbackPolicy).ConfigureAwait(true);
            DetectionUsedEditorValues = outcome.UsedFallbackPolicy;
            DetectionInstalledVersion = string.IsNullOrWhiteSpace(outcome.InstalledVersion) ? Strings.None : outcome.InstalledVersion;
            DetectionAvailableVersion = string.IsNullOrWhiteSpace(outcome.AvailableVersion) ? Strings.None : outcome.AvailableVersion;
            DetectionFailed = outcome.Error is not null;
            DetectionMessage = outcome.Error is not null
                ? Strings.DetectionFailed(outcome.Error)
                : !outcome.IsInstalled ? Strings.DetectionNotInstalled
                : outcome.UpdateAvailable ? Strings.DetectionUpdateAvailable + Strings.DetectionContextSuffix(outcome.Context)
                : Strings.DetectionUpToDate + Strings.DetectionContextSuffix(outcome.Context);
            OnPropertyChanged(nameof(DetectionUsedEditorValues));
        }
        catch (Exception ex)
        {
            DetectionFailed = true;
            DetectionMessage = Strings.DetectionFailed($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsTesting = false;
        }
    }

    /// <summary>
    /// The policy to test with when the application has not been saved to the registry yet: the editor's own values,
    /// filled in from the catalog entry exactly as <see cref="AppPolicy.MergeDefaultsFrom"/> does for the agent.
    /// </summary>
    private AppPolicy BuildFallbackPolicy()
    {
        string? Text(string name)
        {
            var value = EffectiveText(name, null);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        bool Flag(string name, bool fallback)
        {
            var row = Row(name);
            return row.IsLocked ? row.LockValue!.AsBool() ?? fallback : row.IsOverridden ? row.BoolValue : fallback;
        }

        var policy = new AppPolicy
        {
            AppId = AppId,
            DisplayName = DisplayName,
            Enabled = Flag("Enabled", true),
            Source = SourceText.Equals("web", StringComparison.OrdinalIgnoreCase) ? UpdateSource.Web : UpdateSource.Winget,
            Context = ContextText.ToLowerInvariant() switch
            {
                "system" => InstallContext.System,
                "user" => InstallContext.User,
                _ => InstallContext.Auto,
            },
            WingetId = Text("WingetId"),
            WingetSourceName = Text("WingetSource") ?? "winget",
            WingetExtraArgs = Text("WingetExtraArgs"),
            VersionUrl = Text("VersionUrl"),
            VersionRegex = Text("VersionRegex"),
            DownloadUrl = Text("DownloadUrl"),
            InstallerArgs = Text("InstallerArgs"),
            InstallerType = Text("InstallerType")?.ToLowerInvariant() switch
            {
                "msi" => InstallerType.Msi,
                "msix" => InstallerType.Msix,
                _ => InstallerType.Exe,
            },
            Sha256Url = Text("Sha256Url"),
            Sha256 = Text("Sha256"),
            UserDownloadUrl = Text("UserDownloadUrl"),
            UserInstallerArgs = Text("UserInstallerArgs"),
            DetectDisplayNameRegex = Text("DetectDisplayNameRegex"),
            DetectPublisherRegex = Text("DetectPublisherRegex"),
            DetectFilePath = Text("DetectFilePath"),
            MinimumVersion = Text("MinimumVersion"),
            ProcessNames = SettingRowViewModel.Lines(EffectiveText("ProcessNames", string.Empty)),
        };
        return _catalogEntry is null ? policy : policy.MergeDefaultsFrom(_catalogEntry);
    }
}
