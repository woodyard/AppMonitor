using System.Collections.ObjectModel;
using System.Windows.Input;
using Arkimentum.AppMonitor.Admin.Infrastructure;
using Arkimentum.AppMonitor.Admin.Resources;
using Arkimentum.AppMonitor.Admin.Services;
using Arkimentum.AppMonitor.Cloud;
using Arkimentum.AppMonitor.UI;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Admin.ViewModels;

/// <summary>One organization-wide event: which device, when, what happened.</summary>
public sealed class ActivityRowViewModel
{
    public ActivityRowViewModel(OrganizationEvent reported)
    {
        DeviceName = string.IsNullOrWhiteSpace(reported.DeviceName) ? reported.DeviceId.ToString() : reported.DeviceName!;
        When = TimeFormat.Relative(reported.OccurredUtc);
        WhenTooltip = TimeFormat.Absolute(reported.OccurredUtc);
        Kind = reported.Kind.ToString();
        AppId = string.IsNullOrWhiteSpace(reported.AppId) ? Strings.None : reported.AppId!;
        Message = string.IsNullOrWhiteSpace(reported.Message) ? Versions(reported) : reported.Message!;
        IsFailure = reported.Kind == ReportedEventKind.InstallFailed;
    }

    private static string Versions(OrganizationEvent reported) =>
        reported.FromVersion is null && reported.ToVersion is null
            ? Strings.None
            : $"{reported.FromVersion ?? Strings.None} → {reported.ToVersion ?? Strings.None}";

    public string DeviceName { get; }
    public string When { get; }
    public string WhenTooltip { get; }
    public string Kind { get; }
    public string AppId { get; }
    public string Message { get; }
    public bool IsFailure { get; }
}

/// <summary>
/// The Activity page: the organization's event feed, newest first — what actually happened on the fleet, as
/// opposed to what is configured. It is deliberately plain: a paged table, no filtering beyond the paging the API
/// already offers, because the per-device story lives on the Devices page.
/// </summary>
public sealed class OrganizationActivityViewModel : ObservableObject
{
    public const int PageSize = 50;

    private readonly ILogger<OrganizationActivityViewModel> _log;
    private readonly CloudSession _session;

    private readonly RelayCommand _refreshCommand;
    private readonly RelayCommand _previousCommand;
    private readonly RelayCommand _nextCommand;

    private int _page = 1;
    private int _total;
    private bool _isBusy;
    private string? _error;
    private Guid? _loadedOrganization;

    public OrganizationActivityViewModel(ILogger<OrganizationActivityViewModel> log, CloudSession session)
    {
        _log = log;
        _session = session;

        _refreshCommand = new RelayCommand(() => Load(1), () => !_isBusy && _session.OrganizationId is not null);
        _previousCommand = new RelayCommand(() => Load(_page - 1), () => !_isBusy && _page > 1);
        _nextCommand = new RelayCommand(() => Load(_page + 1), () => !_isBusy && _page < PageCount);

        _session.Changed += OnSessionChanged;
    }

    public string Title => Strings.ActivityTitle;

    public string Subtitle => Strings.ActivitySubtitle;

    public ObservableCollection<ActivityRowViewModel> Events { get; } = [];

    public bool HasEvents => Events.Count > 0;

    public bool IsSignedIn => _session.OrganizationId is not null;

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

    public int PageCount => Math.Max(1, (int)Math.Ceiling(_total / (double)PageSize));

    public string PageText => Strings.ActivityPageOf(_page, PageCount, _total);

    public ICommand RefreshCommand => _refreshCommand;

    public ICommand PreviousPageCommand => _previousCommand;

    public ICommand NextPageCommand => _nextCommand;

    // ---------------------------------------------------------------- loading

    public void EnsureLoaded()
    {
        if (_session.OrganizationId is not { } id) return;
        if (_loadedOrganization == id && Events.Count > 0) return;
        Load(1);
    }

    private async void Load(int page)
    {
        if (_session.OrganizationId is not { } id) return;
        IsBusy = true;
        Error = null;
        try
        {
            var result = await _session.GetEventsAsync(null, Math.Max(1, page), PageSize, CancellationToken.None).ConfigureAwait(true);
            _page = Math.Max(1, page);
            _total = result.Total;
            _loadedOrganization = id;

            Events.Clear();
            foreach (var reported in result.Items) Events.Add(new ActivityRowViewModel(reported));
            _log.LogInformation("Listed {Count} of {Total} organization event(s) for {Id} (page {Page}).",
                result.Items.Count, result.Total, id, _page);
        }
        catch (Exception ex)
        {
            Error = CloudSession.Describe(ex);
            _log.LogWarning(ex, "Listing the organization's events failed.");
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasEvents), nameof(PageText), nameof(PageCount), nameof(IsSignedIn));
            RaiseCanExecuteChanged();
        }
    }

    private void OnSessionChanged()
    {
        if (_session.OrganizationId == _loadedOrganization) return;
        _loadedOrganization = null;
        _page = 1;
        _total = 0;
        Events.Clear();
        Error = null;
        OnPropertyChanged(nameof(HasEvents), nameof(PageText), nameof(IsSignedIn));
        RaiseCanExecuteChanged();
    }

    private void RaiseCanExecuteChanged()
    {
        _refreshCommand.RaiseCanExecuteChanged();
        _previousCommand.RaiseCanExecuteChanged();
        _nextCommand.RaiseCanExecuteChanged();
    }
}
