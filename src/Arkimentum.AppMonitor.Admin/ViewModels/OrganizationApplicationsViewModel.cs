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
/// The Organization applications page: the same master/detail editor as the local Applications page, over the
/// organization document.
///
/// <para>
/// Two things are deliberately missing compared with the local page. "Discover installed…" would list what is
/// installed on <em>this</em> machine, which says nothing about a fleet — the Inventory page is the organization's
/// answer to it. "Test detection" would likewise check this machine only, so the detail pane has no test button
/// and the organization editor is built without a detection tester at all.
/// </para>
/// </summary>
public sealed partial class OrganizationApplicationsViewModel : ObservableObject
{
    [GeneratedRegex(@"^[A-Za-z0-9._-]+$")]
    private static partial Regex AppIdPattern { get; }

    private readonly ILogger<OrganizationApplicationsViewModel> _log;
    private readonly CatalogService _catalog;
    private readonly IDialogService _dialogs;

    private readonly RelayCommand _duplicateCommand;
    private readonly RelayCommand _removeCommand;

    private AppEditorViewModel? _selected;
    private string _filter = string.Empty;

    public OrganizationApplicationsViewModel(
        ILogger<OrganizationApplicationsViewModel> log,
        OrganizationConfigViewModel configuration,
        OrganizationSettingsViewModel settingsPage,
        CatalogService catalog,
        IDialogService dialogs)
    {
        _log = log;
        _catalog = catalog;
        _dialogs = dialogs;
        Configuration = configuration;
        BrowseCommand = settingsPage.BrowseCommand;

        AddFromCatalogCommand = new RelayCommand(AddFromCatalog, () => Configuration.HasDocument);
        AddCustomCommand = new RelayCommand(AddCustom, () => Configuration.HasDocument);
        _duplicateCommand = new RelayCommand(Duplicate, () => _selected is { IsEditable: true });
        _removeCommand = new RelayCommand(Remove, () => _selected is { IsEditable: true });

        Editor.Apps.CollectionChanged += OnAppsChanged;
        Editor.Reloaded += OnReloaded;
        RefreshList();
    }

    public OrganizationConfigViewModel Configuration { get; }

    public ConfigurationEditor Editor => Configuration.Editor;

    public string Title => Strings.OrganizationApplicationsTitle;

    public string Subtitle => Strings.OrganizationApplicationsSubtitle;

    public ObservableCollection<AppEditorViewModel> VisibleApps { get; } = [];

    public bool HasApps => Editor.Apps.Count > 0;

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
            if (SetProperty(ref _filter, value ?? string.Empty)) RefreshList();
        }
    }

    public ICommand AddFromCatalogCommand { get; }

    public ICommand AddCustomCommand { get; }

    public ICommand DuplicateCommand => _duplicateCommand;

    public ICommand RemoveCommand => _removeCommand;

    /// <summary>Shared with the Organization settings page so Path rows behave identically on both.</summary>
    public ICommand BrowseCommand { get; }

    // ---------------------------------------------------------------- list

    private void OnAppsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshList();

    private void OnReloaded()
    {
        var previous = _selected?.AppId;
        RefreshList();
        Selected = previous is null
            ? VisibleApps.FirstOrDefault()
            : Editor.Apps.FirstOrDefault(a => a.AppId.Equals(previous, StringComparison.OrdinalIgnoreCase)) ?? VisibleApps.FirstOrDefault();
        (AddFromCatalogCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (AddCustomCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void RefreshList()
    {
        VisibleApps.Clear();
        foreach (var app in Editor.Apps.Where(a => a.Matches(_filter))) VisibleApps.Add(app);
        if (_selected is not null && !VisibleApps.Contains(_selected)) Selected = VisibleApps.FirstOrDefault();
        _selected ??= VisibleApps.FirstOrDefault();
        OnPropertyChanged(nameof(HasApps), nameof(Selected), nameof(HasSelection));
    }

    // ---------------------------------------------------------------- commands

    private void AddFromCatalog()
    {
        var available = _catalog.Entries.Where(e => !Editor.Exists(e.AppId)).ToList();
        var dialog = new AddFromCatalogViewModel(available, _catalog.Path is not null);
        if (!_dialogs.ShowDialog(dialog)) return;

        AppEditorViewModel? last = null;
        foreach (var entry in dialog.Selected)
        {
            last = Editor.AddFromCatalog(entry.AppId);
            _log.LogInformation("Added catalog application {AppId} to the organization configuration.", entry.AppId);
        }
        if (last is not null) Selected = last;
    }

    private void AddCustom()
    {
        var input = new TextInputViewModel(Strings.AddCustomTitle, Strings.AddCustomPrompt, string.Empty, Strings.ButtonAdd, ValidateAppId);
        if (!_dialogs.ShowDialog(input)) return;
        var app = Editor.AddCustom(input.Text.Trim());
        _log.LogInformation("Added custom application {AppId} to the organization configuration.", app.AppId);
        Selected = app;
    }

    private void Duplicate()
    {
        if (_selected is null) return;
        var input = new TextInputViewModel(Strings.DuplicateTitle, Strings.AddCustomPrompt, _selected.AppId + "-copy",
            Strings.ButtonAdd, ValidateAppId);
        if (!_dialogs.ShowDialog(input)) return;
        var app = Editor.Duplicate(_selected, input.Text.Trim());
        _log.LogInformation("Duplicated organization application {Source} as {AppId}.", _selected.AppId, app.AppId);
        Selected = app;
    }

    private void Remove()
    {
        if (_selected is null) return;
        if (!_dialogs.Confirm(Strings.ConfirmRemoveTitle, Strings.ConfirmRemoveBody(_selected.AppId), Strings.Remove, Strings.Cancel)) return;
        Editor.Remove(_selected);
        Selected = VisibleApps.FirstOrDefault();
    }

    private string? ValidateAppId(string text)
    {
        var value = text.Trim();
        if (value.Length == 0) return Strings.AddCustomEmpty;
        if (!AppIdPattern.IsMatch(value)) return Strings.AddCustomInvalid;
        if (Editor.Exists(value)) return Strings.AddCustomExists;
        return null;
    }
}
