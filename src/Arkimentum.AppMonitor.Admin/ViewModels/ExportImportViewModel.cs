using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Input;
using Arkimentum.AppMonitor.Admin.Infrastructure;
using Arkimentum.AppMonitor.Admin.Resources;
using Arkimentum.AppMonitor.Admin.Services;
using Arkimentum.AppMonitor.Configuration;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.UI;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Admin.ViewModels;

/// <summary>
/// The Export &amp; import page. Exports always describe the <em>saved</em> preference layer, never the editor's
/// unsaved state, so what leaves the machine is exactly what the service reads.
/// </summary>
public sealed class ExportImportViewModel : ObservableObject
{
    private readonly ILogger<ExportImportViewModel> _log;
    private readonly SettingsStoreService _store;
    private readonly ConfigurationEditor _editor;
    private readonly IDialogService _dialogs;
    private readonly RelayCommand _importCommand;

    private SettingsExportFormat _format = SettingsExportFormat.Json;
    private bool _policyTarget;
    private bool _replaceApps = true;
    private string _description = string.Empty;
    private string _preview = string.Empty;
    private string? _exportNotice;

    private SettingsDocument? _incoming;
    private string? _incomingPath;
    private bool _merge;
    private string? _importNotice;
    private bool _importNoticeIsError;

    public ExportImportViewModel(ILogger<ExportImportViewModel> log, SettingsStoreService store,
        ConfigurationEditor editor, IDialogService dialogs)
    {
        _log = log;
        _store = store;
        _editor = editor;
        _dialogs = dialogs;

        SaveAsCommand = new RelayCommand(SaveAs);
        CopyCommand = new RelayCommand(Copy);
        OpenProfileCommand = new RelayCommand(OpenProfile);
        _importCommand = new RelayCommand(Import, () => _incoming is not null && Problems.Count == 0);

        _editor.Reloaded += OnReloaded;
        _editor.Changed += () => OnPropertyChanged(nameof(ShowDirtyWarning));
        RefreshPreview();
    }

    public string Title => Strings.ExportImportTitle;

    // ---------------------------------------------------------------- export

    public bool ShowDirtyWarning => _editor.IsDirty;

    public bool IsPolicyTarget
    {
        get => _policyTarget;
        set
        {
            if (SetProperty(ref _policyTarget, value)) RefreshPreview();
        }
    }

    public bool ReplaceApps
    {
        get => _replaceApps;
        set
        {
            if (SetProperty(ref _replaceApps, value)) RefreshPreview();
        }
    }

    public string Description
    {
        get => _description;
        set
        {
            if (SetProperty(ref _description, value ?? string.Empty)) RefreshPreview();
        }
    }

    public bool IsJson
    {
        get => _format == SettingsExportFormat.Json;
        set { if (value) SetFormat(SettingsExportFormat.Json); }
    }

    public bool IsReg
    {
        get => _format == SettingsExportFormat.RegFile;
        set { if (value) SetFormat(SettingsExportFormat.RegFile); }
    }

    public bool IsPowerShell
    {
        get => _format == SettingsExportFormat.PowerShell;
        set { if (value) SetFormat(SettingsExportFormat.PowerShell); }
    }

    public string FormatCaption => _format switch
    {
        SettingsExportFormat.RegFile => Strings.FormatRegCaption,
        SettingsExportFormat.PowerShell => Strings.FormatPs1Caption,
        _ => Strings.FormatJsonCaption,
    };

    public string Preview
    {
        get => _preview;
        private set => SetProperty(ref _preview, value);
    }

    public string? ExportNotice
    {
        get => _exportNotice;
        private set
        {
            if (SetProperty(ref _exportNotice, value)) OnPropertyChanged(nameof(HasExportNotice));
        }
    }

    public bool HasExportNotice => !string.IsNullOrEmpty(_exportNotice);

    public ICommand SaveAsCommand { get; }

    public ICommand CopyCommand { get; }

    private void SetFormat(SettingsExportFormat format)
    {
        if (_format == format) return;
        _format = format;
        OnPropertyChanged(nameof(IsJson), nameof(IsReg), nameof(IsPowerShell), nameof(FormatCaption));
        RefreshPreview();
    }

    private SettingsExportOptions Options => new()
    {
        TargetLayer = _policyTarget ? SettingsLayer.Policy : SettingsLayer.Preference,
        ReplaceApps = _replaceApps,
        Description = string.IsNullOrWhiteSpace(_description) ? null : _description.Trim(),
    };

    private void RefreshPreview()
    {
        try
        {
            Preview = SettingsExporter.Export(Decorate(_store.Read(SettingsLayer.Preference)), _format, Options);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Generating the export preview failed.");
            Preview = ex.Message;
        }
    }

    /// <summary>Stamps the profile with who exported it and when, so a file found later can be traced.</summary>
    private SettingsDocument Decorate(SettingsDocument document)
    {
        document.Description = string.IsNullOrWhiteSpace(_description) ? null : _description.Trim();
        document.ExportedUtc = DateTimeOffset.UtcNow;
        document.ExportedBy = AdminAppInfo.UserName;
        document.ExportedFrom = Environment.MachineName;
        document.ProductVersion = AdminAppInfo.Version;
        return document;
    }

    private void SaveAs()
    {
        var extension = SettingsExporter.FileExtension(_format);
        var filter = _format switch
        {
            SettingsExportFormat.RegFile => "Registration entries (*.reg)|*.reg",
            SettingsExportFormat.PowerShell => "PowerShell script (*.ps1)|*.ps1",
            _ => "JSON profile (*.json)|*.json",
        };
        var path = _dialogs.SaveFile("Arkimentum.AppMonitor" + extension, filter + "|All files (*.*)|*.*", extension.TrimStart('.'));
        if (path is null) return;
        try
        {
            File.WriteAllText(path, Preview);
            _log.LogInformation("Exported the preference layer to {Path} ({Format}).", path, _format);
            ExportNotice = Strings.ExportedNotice(path);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Writing the export to {Path} failed.", path);
            _dialogs.ShowMessage(Strings.ProductName, Strings.HeadlessExportFailed(ex.Message), DialogTone.Critical);
        }
    }

    private void Copy()
    {
        _dialogs.CopyToClipboard(Preview);
        _log.LogInformation("Copied the {Format} export to the clipboard.", _format);
        ExportNotice = Strings.CopiedNotice;
    }

    // ---------------------------------------------------------------- import

    public string? ImportedPath => _incomingPath;

    public bool HasProfile => _incoming is not null;

    public ObservableCollection<string> Problems { get; } = [];

    public bool HasProblems => Problems.Count > 0;

    public bool IsMerge
    {
        get => _merge;
        set
        {
            if (SetProperty(ref _merge, value)) RefreshSummary();
        }
    }

    public bool IsReplace
    {
        get => !_merge;
        set
        {
            if (value) IsMerge = false;
        }
    }

    public ObservableCollection<ChangeSummaryViewModel> Summary { get; } = [];

    public bool HasSummary => Summary.Count > 0;

    public string? ImportNotice
    {
        get => _importNotice;
        private set
        {
            if (SetProperty(ref _importNotice, value)) OnPropertyChanged(nameof(HasImportNotice));
        }
    }

    public bool ImportNoticeIsError
    {
        get => _importNoticeIsError;
        private set => SetProperty(ref _importNoticeIsError, value);
    }

    public bool HasImportNotice => !string.IsNullOrEmpty(_importNotice);

    public ICommand OpenProfileCommand { get; }

    public ICommand ImportCommand => _importCommand;

    private void OpenProfile()
    {
        var path = _dialogs.OpenFile("JSON profile (*.json)|*.json|All files (*.*)|*.*");
        if (path is null) return;
        try
        {
            _incoming = SettingsDocument.FromJson(File.ReadAllText(path));
            _incomingPath = path;
            ImportNotice = Strings.ImportedFrom(path);
            ImportNoticeIsError = false;
            _log.LogInformation("Loaded the settings profile {Path}.", path);
        }
        catch (Exception ex)
        {
            _incoming = null;
            _incomingPath = null;
            ImportNotice = Strings.ReadFailed(ex.Message);
            ImportNoticeIsError = true;
            _log.LogWarning(ex, "Reading the settings profile {Path} failed.", path);
        }
        RefreshSummary();
    }

    private void RefreshSummary()
    {
        Problems.Clear();
        Summary.Clear();
        if (_incoming is not null)
        {
            foreach (var problem in _incoming.Validate()) Problems.Add(problem);
            if (Problems.Count == 0) BuildSummary(_incoming);
        }
        OnPropertyChanged(nameof(HasProfile), nameof(HasProblems), nameof(HasSummary), nameof(ImportedPath),
            nameof(IsMerge), nameof(IsReplace), nameof(SummaryEmptyText));
        _importCommand.RaiseCanExecuteChanged();
    }

    public string SummaryEmptyText => _incoming is null ? Strings.ImportNoFile : Strings.ImportNoChanges;

    private void BuildSummary(SettingsDocument incoming)
    {
        var current = _store.Read(SettingsLayer.Preference);

        var globalsAdded = incoming.Global.Keys.Where(k => !current.Global.ContainsKey(k)).ToList();
        var globalsChanged = incoming.Global
            .Where(kv => current.Global.TryGetValue(kv.Key, out var old) && !old.Equals(kv.Value))
            .Select(kv => $"{kv.Key}: {current.Global[kv.Key]} → {kv.Value}").ToList();
        var globalsRemoved = _merge ? new List<string>() : current.Global.Keys.Where(k => !incoming.Global.ContainsKey(k)).ToList();

        var appsAdded = incoming.Apps.Keys.Where(k => !current.Apps.ContainsKey(k)).ToList();
        var appsChanged = incoming.Apps
            .Where(kv => current.Apps.TryGetValue(kv.Key, out var old) && !SameValues(old, kv.Value))
            .Select(kv => kv.Key).ToList();
        var appsRemoved = _merge ? new List<string>() : current.Apps.Keys.Where(k => !incoming.Apps.ContainsKey(k)).ToList();

        Add(Strings.SummaryGlobalsAdded, globalsAdded);
        Add(Strings.SummaryGlobalsChanged, globalsChanged);
        Add(Strings.SummaryGlobalsRemoved, globalsRemoved);
        Add(Strings.SummaryAppsAdded, appsAdded);
        Add(Strings.SummaryAppsChanged, appsChanged);
        Add(Strings.SummaryAppsRemoved, appsRemoved);
    }

    private void Add(string title, IReadOnlyList<string> items)
    {
        if (items.Count == 0) return;
        Summary.Add(new ChangeSummaryViewModel(title, items));
    }

    private static bool SameValues(IDictionary<string, SettingValue> a, IDictionary<string, SettingValue> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var other) && kv.Value.Equals(other));

    private void Import()
    {
        if (_incoming is null) return;
        var mode = _merge ? SettingsWriteMode.Merge : SettingsWriteMode.Replace;
        try
        {
            _store.Write(_incoming, SettingsLayer.Preference, mode);
            _log.LogInformation("Imported {Path} into the preference layer ({Mode}).", _incomingPath, mode);
            _editor.Reload();
            ImportNotice = Strings.ImportedNotice;
            ImportNoticeIsError = false;
            RefreshSummary();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Importing {Path} failed.", _incomingPath);
            ImportNotice = Strings.HeadlessImportFailed(ex.Message);
            ImportNoticeIsError = true;
        }
    }

    private void OnReloaded()
    {
        RefreshPreview();
        RefreshSummary();
        OnPropertyChanged(nameof(ShowDirtyWarning));
    }
}

/// <summary>One "globals added" / "applications removed" block of the import summary.</summary>
public sealed class ChangeSummaryViewModel
{
    public ChangeSummaryViewModel(string title, IReadOnlyList<string> items)
    {
        Title = $"{title} ({items.Count})";
        Items = items;
    }

    public string Title { get; }

    public IReadOnlyList<string> Items { get; }
}
