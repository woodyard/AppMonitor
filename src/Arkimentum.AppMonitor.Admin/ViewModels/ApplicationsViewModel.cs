using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Arkimentum.AppMonitor.Admin.Resources;
using Arkimentum.AppMonitor.Admin.Services;
using Arkimentum.AppMonitor.UI;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Admin.ViewModels;

/// <summary>
/// The Applications page: a filtered master list of configured applications (preference layer plus the read-only
/// policy layer) and a generated detail editor for the selected one.
/// </summary>
public sealed partial class ApplicationsViewModel : ObservableObject
{
    [GeneratedRegex(@"^[A-Za-z0-9._-]+$")]
    private static partial Regex AppIdPattern { get; }

    private readonly ILogger<ApplicationsViewModel> _log;
    private readonly ConfigurationEditor _editor;
    private readonly CatalogService _catalog;
    private readonly IDialogService _dialogs;

    private readonly RelayCommand _duplicateCommand;
    private readonly RelayCommand _removeCommand;

    private readonly IAppDiscoveryService _discovery;

    private AppEditorViewModel? _selected;
    private string _filter = string.Empty;
    private string? _notice;

    public ApplicationsViewModel(ILogger<ApplicationsViewModel> log, ConfigurationEditor editor, CatalogService catalog,
        IDialogService dialogs, IAppDiscoveryService discovery, SettingsPageViewModel settingsPage)
    {
        _log = log;
        _editor = editor;
        _catalog = catalog;
        _dialogs = dialogs;
        _discovery = discovery;
        BrowseCommand = settingsPage.BrowseCommand;

        AddFromCatalogCommand = new RelayCommand(AddFromCatalog);
        DiscoverCommand = new RelayCommand(Discover);
        AddCustomCommand = new RelayCommand(AddCustom);
        _duplicateCommand = new RelayCommand(Duplicate, () => _selected is { IsEditable: true });
        _removeCommand = new RelayCommand(Remove, () => _selected is { IsEditable: true });

        _editor.Apps.CollectionChanged += OnAppsChanged;
        _editor.Reloaded += OnReloaded;
        RefreshList();
    }

    public ConfigurationEditor Editor => _editor;

    public string Title => Strings.ApplicationsTitle;

    public ObservableCollection<AppEditorViewModel> VisibleApps { get; } = [];

    public bool HasApps => _editor.Apps.Count > 0;

    public bool HasSelection => _selected is not null;

    public AppEditorViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            OnPropertyChanged(nameof(HasSelection));
            _duplicateCommand.RaiseCanExecuteChanged();
            _removeCommand.RaiseCanExecuteChanged();
        }
    }

    public string Filter
    {
        get => _filter;
        set
        {
            if (!SetProperty(ref _filter, value ?? string.Empty)) return;
            RefreshList();
        }
    }

    public ICommand AddFromCatalogCommand { get; }

    public ICommand DiscoverCommand { get; }

    /// <summary>Result line of the last bulk add, shown under the toolbar.</summary>
    public string? Notice
    {
        get => _notice;
        private set
        {
            if (SetProperty(ref _notice, value)) OnPropertyChanged(nameof(HasNotice));
        }
    }

    public bool HasNotice => !string.IsNullOrEmpty(_notice);

    public ICommand AddCustomCommand { get; }

    public ICommand DuplicateCommand => _duplicateCommand;

    public ICommand RemoveCommand => _removeCommand;

    /// <summary>Shared with the Settings page so Path rows behave identically on both.</summary>
    public ICommand BrowseCommand { get; }

    // ---------------------------------------------------------------- list

    private void OnAppsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshList();

    private void OnReloaded()
    {
        var previous = _selected?.AppId;
        RefreshList();
        Selected = previous is null ? VisibleApps.FirstOrDefault()
            : _editor.Apps.FirstOrDefault(a => a.AppId.Equals(previous, StringComparison.OrdinalIgnoreCase)) ?? VisibleApps.FirstOrDefault();
    }

    private void RefreshList()
    {
        VisibleApps.Clear();
        foreach (var app in _editor.Apps.Where(a => a.Matches(_filter))) VisibleApps.Add(app);
        if (_selected is not null && !VisibleApps.Contains(_selected)) Selected = VisibleApps.FirstOrDefault();
        _selected ??= VisibleApps.FirstOrDefault();
        OnPropertyChanged(nameof(HasApps), nameof(Selected), nameof(HasSelection));
    }

    // ---------------------------------------------------------------- commands

    private void AddFromCatalog()
    {
        var available = _catalog.Entries.Where(e => !_editor.Exists(e.AppId)).ToList();
        var dialog = new AddFromCatalogViewModel(available, _catalog.Path is not null);
        if (!_dialogs.ShowDialog(dialog)) return;

        AppEditorViewModel? last = null;
        foreach (var entry in dialog.Selected)
        {
            last = _editor.AddFromCatalog(entry.AppId);
            _log.LogInformation("Added catalog application {AppId} to the pending configuration.", entry.AppId);
        }
        if (last is not null) Selected = last;
    }

    private void Discover()
    {
        Notice = null;
        var dialog = new DiscoverViewModel(_discovery);
        dialog.Start();
        if (!_dialogs.ShowDialog(dialog)) return;

        AppEditorViewModel? first = null;
        int added = 0, skipped = 0;
        foreach (var app in dialog.Selected)
        {
            var created = _editor.AddDiscovered(app);
            if (created is null) { skipped++; continue; }
            added++;
            first ??= created;
        }
        _log.LogInformation("Discovery added {Added} application(s); {Skipped} were already configured or unusable.", added, skipped);
        if (first is not null) Selected = first;
        Notice = Strings.DiscoverAdded(added, skipped);
    }

    private void AddCustom()
    {
        var input = new TextInputViewModel(Strings.AddCustomTitle, Strings.AddCustomPrompt, string.Empty, Strings.ButtonAdd, ValidateAppId);
        if (!_dialogs.ShowDialog(input)) return;
        var app = _editor.AddCustom(input.Text.Trim());
        _log.LogInformation("Added custom application {AppId} to the pending configuration.", app.AppId);
        Selected = app;
    }

    private void Duplicate()
    {
        if (_selected is null) return;
        var input = new TextInputViewModel(Strings.DuplicateTitle, Strings.AddCustomPrompt, _selected.AppId + "-copy",
            Strings.ButtonAdd, ValidateAppId);
        if (!_dialogs.ShowDialog(input)) return;
        var app = _editor.Duplicate(_selected, input.Text.Trim());
        _log.LogInformation("Duplicated application {Source} as {AppId}.", _selected.AppId, app.AppId);
        Selected = app;
    }

    private void Remove()
    {
        if (_selected is null) return;
        if (!_dialogs.Confirm(Strings.ConfirmRemoveTitle, Strings.ConfirmRemoveBody(_selected.AppId), Strings.Remove, Strings.Cancel)) return;
        var removed = _selected;
        _editor.Remove(removed);
        Selected = VisibleApps.FirstOrDefault();
    }

    private string? ValidateAppId(string text)
    {
        var value = text.Trim();
        if (value.Length == 0) return Strings.AddCustomEmpty;
        if (!AppIdPattern.IsMatch(value)) return Strings.AddCustomInvalid;
        if (_editor.Exists(value)) return Strings.AddCustomExists;
        return null;
    }
}
