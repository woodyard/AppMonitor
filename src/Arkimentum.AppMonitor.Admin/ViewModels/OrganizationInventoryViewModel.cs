using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Arkimentum.AppMonitor.Admin.Infrastructure;
using Arkimentum.AppMonitor.Admin.Resources;
using Arkimentum.AppMonitor.Admin.Services;
using Arkimentum.AppMonitor.Cloud;
using Arkimentum.AppMonitor.Configuration;
using Arkimentum.AppMonitor.UI;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Admin.ViewModels;

/// <summary>One application as the organization's devices report it.</summary>
public sealed class InventoryRowViewModel : ObservableObject
{
    private readonly Action _changed;
    private bool _isSelected;

    public InventoryRowViewModel(OrganizationInventoryItem item, Action changed)
    {
        Item = item;
        _changed = changed;
        DisplayName = item.DisplayName;
        Publisher = string.IsNullOrWhiteSpace(item.Publisher) ? Strings.None : item.Publisher!;
        WingetId = string.IsNullOrWhiteSpace(item.WingetId) ? Strings.NoWingetPackage : item.WingetId!;
        HasWingetId = !string.IsNullOrWhiteSpace(item.WingetId);
        DeviceCount = Strings.InventoryDeviceCount(item.DeviceCount);
        UpdatesText = Strings.InventoryUpdatesAvailable(item.DevicesWithUpdateAvailable);
        HasUpdates = item.DevicesWithUpdateAvailable > 0;
        Versions = Strings.InventoryVersions(item.Versions
            .OrderByDescending(v => v.DeviceCount)
            .Take(4)
            .Select(v => $"{v.Version} ({v.DeviceCount})"));
        IsMonitored = !string.IsNullOrWhiteSpace(item.MonitoredAppId);
        IsInCatalog = !string.IsNullOrWhiteSpace(item.CatalogAppId);
        LastSeen = TimeFormat.Relative(item.LastSeenUtc);
        Context = item.PredominantContext.ToString();
    }

    public OrganizationInventoryItem Item { get; }
    public string DisplayName { get; }
    public string Publisher { get; }
    public string WingetId { get; }
    public bool HasWingetId { get; }
    public string DeviceCount { get; }
    public string UpdatesText { get; }
    public bool HasUpdates { get; }
    public string Versions { get; }
    public bool IsMonitored { get; }
    public bool IsInCatalog { get; }
    public string LastSeen { get; }
    public string Context { get; }
    public string MonitoredBadge => Strings.BadgeMonitored;
    public string CatalogBadge => Strings.BadgeInCatalog;

    /// <summary>Only unmonitored applications with a usable identity can be ticked.</summary>
    public bool CanSelect => !IsMonitored && (IsInCatalog || HasWingetId);

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!CanSelect) return;
            if (SetProperty(ref _isSelected, value)) _changed();
        }
    }
}

/// <summary>
/// The Inventory page: every application the organization's devices have reported, and a way to put the
/// interesting ones into the organization configuration.
///
/// <para>
/// Adding follows exactly the rule the local Discover dialog follows: an application the catalog recognises is
/// added by catalog id with nothing but <c>Enabled = 1</c>, so the shipped identity, source and detection data
/// apply; anything else becomes a plain winget application with <c>DisplayName</c>, <c>Source</c> and
/// <c>WingetId</c>. The difference is only where it lands — the organization document, not this machine's
/// registry — and it still has to be published.
/// </para>
/// </summary>
public sealed partial class OrganizationInventoryViewModel : ObservableObject
{
    [GeneratedRegex(@"^[A-Za-z0-9._-]+$")]
    private static partial Regex AppIdPattern { get; }

    private readonly ILogger<OrganizationInventoryViewModel> _log;
    private readonly CloudSession _session;
    private readonly OrganizationConfigViewModel _configuration;

    private readonly RelayCommand _refreshCommand;
    private readonly RelayCommand _addCommand;
    private readonly List<InventoryRowViewModel> _all = [];

    private string _filter = string.Empty;
    private bool _onlyUnmonitored;
    private bool _isBusy;
    private string? _error;
    private string? _notice;
    private Guid? _loadedOrganization;

    public OrganizationInventoryViewModel(ILogger<OrganizationInventoryViewModel> log, CloudSession session,
        OrganizationConfigViewModel configuration)
    {
        _log = log;
        _session = session;
        _configuration = configuration;

        _refreshCommand = new RelayCommand(Reload, () => !_isBusy && _session.OrganizationId is not null);
        _addCommand = new RelayCommand(AddSelected, () => !_isBusy && SelectedCount > 0);

        _session.Changed += OnSessionChanged;
    }

    public string Title => Strings.InventoryTitle;

    public string Subtitle => Strings.InventorySubtitle;

    public ObservableCollection<InventoryRowViewModel> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    public bool IsSignedIn => _session.OrganizationId is not null;

    public string Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value ?? string.Empty)) RefreshList();
        }
    }

    public bool OnlyUnmonitored
    {
        get => _onlyUnmonitored;
        set
        {
            if (SetProperty(ref _onlyUnmonitored, value)) RefreshList();
        }
    }

    public string OnlyUnmonitoredText => Strings.InventoryOnlyUnmonitored;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) RaiseCanExecuteChanged();
        }
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

    public string? Notice
    {
        get => _notice;
        private set
        {
            if (SetProperty(ref _notice, value)) OnPropertyChanged(nameof(HasNotice));
        }
    }

    public bool HasNotice => !string.IsNullOrEmpty(_notice);

    public int SelectedCount => _all.Count(i => i.IsSelected);

    public string SelectedText => Strings.CatalogSelected(SelectedCount);

    public ICommand RefreshCommand => _refreshCommand;

    public ICommand AddToOrganizationCommand => _addCommand;

    // ---------------------------------------------------------------- loading

    public void EnsureLoaded()
    {
        if (_session.OrganizationId is not { } id) return;
        // Start loading the organization document now, so it is normally in place by the time something is added.
        _ = _configuration.EnsureLoadedAsync();
        if (_loadedOrganization == id && _all.Count > 0) return;
        Reload();
    }

    private async void Reload()
    {
        if (_session.OrganizationId is not { } id) return;
        IsBusy = true;
        Error = null;
        try
        {
            // The whole inventory is fetched once and narrowed here, so typing in the filter box works keystroke by
            // keystroke without a round trip; the server-side search and unmonitored switches are deliberately unused.
            var items = await _session.GetInventoryAsync(null, false, CancellationToken.None).ConfigureAwait(true);
            _all.Clear();
            _all.AddRange(items
                .OrderBy(i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(i => i.WingetId, StringComparer.OrdinalIgnoreCase)
                .Select(i => new InventoryRowViewModel(i, OnSelectionChanged)));
            _loadedOrganization = id;
            _log.LogInformation("Organization inventory: {Count} application(s) (filter '{Filter}', onlyUnmonitored={Only}).",
                _all.Count, _filter, _onlyUnmonitored);
            RefreshList();
        }
        catch (Exception ex)
        {
            Error = CloudSession.Describe(ex);
            _log.LogWarning(ex, "Loading the organization inventory failed.");
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(IsSignedIn));
            RaiseCanExecuteChanged();
        }
    }

    private void RefreshList()
    {
        Items.Clear();
        foreach (var item in _all)
        {
            if (_onlyUnmonitored && item.IsMonitored) continue;
            if (Matches(item.Item, _filter)) Items.Add(item);
        }
        OnPropertyChanged(nameof(HasItems), nameof(SelectedCount), nameof(SelectedText));
    }

    /// <summary>The same fields the server searches, so a filter typed here finds what its search would find.</summary>
    private static bool Matches(OrganizationInventoryItem item, string filter)
    {
        var needle = filter.Trim();
        if (needle.Length == 0) return true;
        return Has(item.DisplayName) || Has(item.WingetId) || Has(item.Publisher) || Has(item.CatalogAppId);
        bool Has(string? value) => value is not null && value.Contains(needle, StringComparison.CurrentCultureIgnoreCase);
    }

    // ---------------------------------------------------------------- adding

    /// <summary>
    /// Puts the ticked applications into the organization configuration editor. Nothing is sent to the server
    /// here — the administrator reviews the result on the Organization applications page and publishes it.
    /// </summary>
    private async void AddSelected()
    {
        // Adding into an editor that has not loaded the server document yet looks fine here and is lost the moment the
        // document arrives, because the load rebuilds every row. So wait for it, and refuse when it is not there.
        IsBusy = true;
        try { await _configuration.EnsureLoadedAsync().ConfigureAwait(true); }
        finally { IsBusy = false; }
        if (!_configuration.HasDocument)
        {
            Error = Strings.InventoryConfigurationNotLoaded;
            _log.LogWarning("Nothing was added from the inventory: the organization configuration is not loaded.");
            return;
        }

        var editor = _configuration.Editor;
        int added = 0, skipped = 0;
        foreach (var row in _all.Where(i => i.IsSelected).ToList())
        {
            var appId = SuggestAppId(row.Item);
            if (appId is null || editor.Exists(appId)) { skipped++; continue; }

            var values = new SortedDictionary<string, SettingValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["Enabled"] = SettingValue.From(true),
            };
            if (row.Item.CatalogAppId is null)
            {
                values["DisplayName"] = SettingValue.From(ConfigurationEditor.CleanDisplayName(row.Item.DisplayName));
                values["Source"] = SettingValue.From("winget");
                values["WingetId"] = SettingValue.From(row.Item.WingetId!);
            }
            editor.AddValues(appId, values);
            added++;
            row.IsSelected = false;
            _log.LogInformation("Added {AppId} ({Source}) to the organization configuration from the inventory.",
                appId, row.Item.CatalogAppId is null ? "winget" : "catalog");
        }

        Notice = Strings.InventoryAdded(added, skipped);
        OnPropertyChanged(nameof(SelectedCount), nameof(SelectedText));
        RaiseCanExecuteChanged();
    }

    /// <summary>
    /// The catalog id when the catalog knows the application, otherwise the winget id verbatim — exactly
    /// <see cref="Inventory.DiscoveredApp.SuggestedAppId"/>, so an application added here gets the same AppId it
    /// would get from the local Discover dialog.
    /// </summary>
    private static string? SuggestAppId(OrganizationInventoryItem item)
    {
        var id = item.CatalogAppId ?? item.WingetId;
        return string.IsNullOrWhiteSpace(id) || !AppIdPattern.IsMatch(id) ? null : id;
    }

    // ---------------------------------------------------------------- plumbing

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount), nameof(SelectedText));
        RaiseCanExecuteChanged();
    }

    private void OnSessionChanged()
    {
        if (_session.OrganizationId == _loadedOrganization) return;
        _loadedOrganization = null;
        _all.Clear();
        Items.Clear();
        Notice = null;
        Error = null;
        OnPropertyChanged(nameof(HasItems), nameof(IsSignedIn), nameof(SelectedCount), nameof(SelectedText));
        RaiseCanExecuteChanged();
    }

    private void RaiseCanExecuteChanged()
    {
        _refreshCommand.RaiseCanExecuteChanged();
        _addCommand.RaiseCanExecuteChanged();
    }
}
