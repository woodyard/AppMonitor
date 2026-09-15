using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Arkimentum.AppMonitor.Admin.Resources;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.UI;

namespace Arkimentum.AppMonitor.Admin.ViewModels;

/// <summary>One selectable catalog entry in the "Add from catalog" dialog.</summary>
public sealed class CatalogEntryViewModel : ObservableObject
{
    private bool _isSelected;

    public CatalogEntryViewModel(AppPolicy entry, Action changed)
    {
        Entry = entry;
        Changed = changed;
    }

    private Action Changed { get; }

    public AppPolicy Entry { get; }

    public string AppId => Entry.AppId;

    public string DisplayName => string.IsNullOrWhiteSpace(Entry.DisplayName) ? Entry.AppId : Entry.DisplayName;

    public string SourceText => Entry.Source == UpdateSource.Web ? "web" : "winget";

    public string Detail => Entry.Source == UpdateSource.Web
        ? Entry.VersionUrl ?? string.Empty
        : Entry.WingetId ?? string.Empty;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value)) Changed();
        }
    }

    public bool Matches(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        var f = filter.Trim();
        return DisplayName.Contains(f, StringComparison.OrdinalIgnoreCase) ||
               AppId.Contains(f, StringComparison.OrdinalIgnoreCase) ||
               Detail.Contains(f, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Picks catalog entries that are not configured yet. Adding one writes a single value — <c>Enabled = 1</c> — so
/// the catalog stays in charge of the identity, source and detection data.
/// </summary>
public sealed class AddFromCatalogViewModel : DialogViewModel
{
    private readonly List<CatalogEntryViewModel> _all;
    private readonly RelayCommand _acceptCommand;
    private string _filter = string.Empty;

    public AddFromCatalogViewModel(IEnumerable<AppPolicy> available, bool catalogFound)
    {
        CatalogFound = catalogFound;
        _all = available
            .OrderBy(e => string.IsNullOrWhiteSpace(e.DisplayName) ? e.AppId : e.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(e => new CatalogEntryViewModel(e, OnSelectionChanged))
            .ToList();
        Entries = [.. _all];
        _acceptCommand = new RelayCommand(() => Close(true), () => SelectedCount > 0);
        CancelCommand = new RelayCommand(() => Close(false));
    }

    public string Title => Strings.CatalogDialogTitle;

    public string Subtitle => Strings.CatalogDialogSubtitle;

    public bool CatalogFound { get; }

    public string EmptyText => CatalogFound ? Strings.CatalogEmpty : Strings.CatalogNotFound;

    public ObservableCollection<CatalogEntryViewModel> Entries { get; }

    public bool IsEmpty => _all.Count == 0;

    public string Filter
    {
        get => _filter;
        set
        {
            if (!SetProperty(ref _filter, value ?? string.Empty)) return;
            Entries.Clear();
            foreach (var entry in _all.Where(e => e.Matches(_filter))) Entries.Add(entry);
        }
    }

    public int SelectedCount => _all.Count(e => e.IsSelected);

    public string SelectedText => Strings.CatalogSelected(SelectedCount);

    public IReadOnlyList<AppPolicy> Selected => _all.Where(e => e.IsSelected).Select(e => e.Entry).ToList();

    public string AcceptText => Strings.ButtonAdd;

    public string CancelText => Strings.Cancel;

    public ICommand AcceptCommand => _acceptCommand;

    public ICommand CancelCommand { get; }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount), nameof(SelectedText));
        _acceptCommand.RaiseCanExecuteChanged();
    }
}
