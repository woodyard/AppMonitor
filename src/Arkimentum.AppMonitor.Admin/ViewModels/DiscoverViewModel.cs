using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Windows.Input;
using Arkimentum.AppMonitor.Admin.Resources;
using Arkimentum.AppMonitor.Admin.Services;
using Arkimentum.AppMonitor.Inventory;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.UI;

namespace Arkimentum.AppMonitor.Admin.ViewModels;

/// <summary>One installed application in the discovery dialog.</summary>
public sealed class DiscoveredAppViewModel : ObservableObject
{
    private readonly Action _changed;
    private bool _isSelected;

    public DiscoveredAppViewModel(DiscoveredApp app, Action changed)
    {
        App = app;
        _changed = changed;
    }

    public DiscoveredApp App { get; }

    public string DisplayName => App.DisplayName;

    public string Version => string.IsNullOrWhiteSpace(App.Version) ? Strings.None : App.Version!;

    public string Publisher => string.IsNullOrWhiteSpace(App.Publisher) ? string.Empty : App.Publisher!;

    public bool HasPublisher => !string.IsNullOrWhiteSpace(App.Publisher);

    public string WingetText => App.WingetIdTruncated ? Strings.WingetIdTruncated
        : string.IsNullOrWhiteSpace(App.WingetId) ? Strings.NoWingetPackage
        : App.WingetId!;

    public bool HasWingetId => !App.WingetIdTruncated && !string.IsNullOrWhiteSpace(App.WingetId);

    public string ContextText => App.Context switch
    {
        InstallContext.System => "System",
        InstallContext.User => "User",
        _ => "Auto",
    };

    public bool IsInCatalog => App.CatalogAppId is not null;

    public string CatalogBadge => Strings.BadgeInCatalog;

    public bool HasUpdate => !string.IsNullOrWhiteSpace(App.AvailableVersion);

    public string UpdateBadge => Strings.BadgeUpdateAvailable(App.AvailableVersion ?? string.Empty);

    public bool IsConfigured => App.IsConfigured;

    public string ConfiguredText => Strings.AlreadyMonitored(App.ConfiguredAppId ?? string.Empty);

    /// <summary>Only rows with a usable identity can be ticked; configured and truncated ones are read-only.</summary>
    public bool CanSelect => App.CanQuickAdd;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!CanSelect) return;
            if (SetProperty(ref _isSelected, value)) _changed();
        }
    }

    public bool Matches(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        var f = filter.Trim();
        return DisplayName.Contains(f, StringComparison.OrdinalIgnoreCase) ||
               Publisher.Contains(f, StringComparison.OrdinalIgnoreCase) ||
               (App.WingetId?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (App.CatalogAppId?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false);
    }
}

/// <summary>
/// The "Discover installed…" dialog: runs <see cref="InstalledAppDiscovery"/> in the background, shows a progress
/// line while it works (winget takes a few seconds), then a filterable, tickable list in the order Core delivered.
/// </summary>
public sealed class DiscoverViewModel : DialogViewModel
{
    private readonly IAppDiscoveryService _discovery;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<DiscoveredAppViewModel> _all = [];
    private readonly RelayCommand _acceptCommand;

    private bool _isRunning = true;
    private string _progress = Strings.DiscoverStarting;
    private string? _error;
    private string _filter = string.Empty;
    private bool _onlyAddable = true;

    public DiscoverViewModel(IAppDiscoveryService discovery)
    {
        _discovery = discovery;
        _acceptCommand = new RelayCommand(() => Close(true), () => !_isRunning && SelectedCount > 0);
        CancelCommand = new RelayCommand(Cancel);
        SelectAllCommand = new RelayCommand(() => SetAll(true), () => !_isRunning && Items.Any(i => i.CanSelect));
        SelectNoneCommand = new RelayCommand(() => SetAll(false), () => !_isRunning && Items.Any(i => i.IsSelected));
    }

    public string Title => Strings.DiscoverTitle;

    public string Subtitle => Strings.DiscoverSubtitle;

    public ObservableCollection<DiscoveredAppViewModel> Items { get; } = [];

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetProperty(ref _isRunning, value)) return;
            OnPropertyChanged(nameof(ShowList));
            RaiseCanExecuteChanged();
        }
    }

    public string Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public string? Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);

    public bool ShowList => !IsRunning && !HasError;

    public bool IsEmpty => _all.Count == 0;

    public string EmptyText => _all.Count == 0 ? Strings.DiscoverEmpty : Strings.DiscoverNoMatches;

    public bool ShowEmpty => Items.Count == 0;

    public string Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value ?? string.Empty)) RefreshList();
        }
    }

    public bool OnlyAddable
    {
        get => _onlyAddable;
        set
        {
            if (SetProperty(ref _onlyAddable, value)) RefreshList();
        }
    }

    public string OnlyAddableText => Strings.DiscoverOnlyAddable;

    public int SelectedCount => _all.Count(i => i.IsSelected);

    public string SelectedText => Strings.CatalogSelected(SelectedCount);

    public IReadOnlyList<DiscoveredApp> Selected => _all.Where(i => i.IsSelected).Select(i => i.App).ToList();

    public string AcceptText => Strings.ButtonAddSelected;

    public string CancelText => Strings.Cancel;

    public string SelectAllText => Strings.DiscoverSelectAll;

    public string SelectNoneText => Strings.DiscoverSelectNone;

    public ICommand AcceptCommand => _acceptCommand;

    public ICommand CancelCommand { get; }

    public ICommand SelectAllCommand { get; }

    public ICommand SelectNoneCommand { get; }

    /// <summary>
    /// Kicks the discovery off. Called before the window is shown; the continuation runs on the UI thread as soon
    /// as the dialog starts pumping messages.
    /// </summary>
    public async void Start()
    {
        try
        {
            var progress = new Progress<string>(text => Progress = text);
            var found = await _discovery.DiscoverAsync(progress, _cts.Token).ConfigureAwait(true);
            _all.Clear();
            _all.AddRange(found.Select(a => new DiscoveredAppViewModel(a, OnSelectionChanged)));
            RefreshList();
            OnPropertyChanged(nameof(IsEmpty), nameof(EmptyText));
        }
        catch (OperationCanceledException)
        {
            Error = Strings.DiscoverCancelled;
        }
        catch (Exception ex)
        {
            Error = Strings.DiscoverFailed($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
        }
    }

    private void Cancel()
    {
        if (!_cts.IsCancellationRequested) _cts.Cancel();
        Close(false);
    }

    private void SetAll(bool selected)
    {
        // Only what the filter currently shows, so "Select all" never ticks something out of sight.
        foreach (var item in Items.Where(i => i.CanSelect)) item.IsSelected = selected;
    }

    private void RefreshList()
    {
        Items.Clear();
        foreach (var item in _all.Where(i => (!_onlyAddable || i.CanSelect) && i.Matches(_filter))) Items.Add(item);
        OnPropertyChanged(nameof(ShowEmpty), nameof(EmptyText));
        RaiseCanExecuteChanged();
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount), nameof(SelectedText));
        RaiseCanExecuteChanged();
    }

    private void RaiseCanExecuteChanged()
    {
        _acceptCommand.RaiseCanExecuteChanged();
        (SelectAllCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SelectNoneCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}
