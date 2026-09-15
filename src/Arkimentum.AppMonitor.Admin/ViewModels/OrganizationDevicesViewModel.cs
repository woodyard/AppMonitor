using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Arkimentum.AppMonitor.Admin.Infrastructure;
using Arkimentum.AppMonitor.Admin.Resources;
using Arkimentum.AppMonitor.Admin.Services;
using Arkimentum.AppMonitor.Cloud;
using Arkimentum.AppMonitor.UI;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Admin.ViewModels;

/// <summary>One device in the master list.</summary>
public sealed class DeviceRowViewModel
{
    public DeviceRowViewModel(DeviceSummary summary)
    {
        Summary = summary;
        DeviceName = summary.DeviceName;
        User = string.IsNullOrWhiteSpace(summary.LastLogonUser) ? Strings.None : summary.LastLogonUser!;
        Os = string.IsNullOrWhiteSpace(summary.OsVersion) ? Strings.None : summary.OsVersion!;
        AgentVersion = string.IsNullOrWhiteSpace(summary.AgentVersion) ? Strings.None : summary.AgentVersion!;
        LastSeen = TimeFormat.Relative(summary.LastSeenUtc);
        LastSeenTooltip = TimeFormat.Absolute(summary.LastSeenUtc);
        IsStale = TimeFormat.IsStale(summary.LastSeenUtc);
        Counts = Strings.PendingAndFailed(summary.PendingUpdateCount, summary.FailedUpdateCount);
        HasFailures = summary.FailedUpdateCount > 0;
        PrerequisitesUnhealthy = !summary.PrerequisitesHealthy;
        SummaryLine = $"{User} · {Os}";
    }

    public DeviceSummary Summary { get; }
    public Guid DeviceId => Summary.DeviceId;
    public string DeviceName { get; }
    public string User { get; }
    public string Os { get; }
    public string AgentVersion { get; }
    public string LastSeen { get; }
    public string LastSeenTooltip { get; }
    public bool IsStale { get; }
    public string StaleBadge => Strings.BadgeStale;
    public string Counts { get; }
    public bool HasFailures { get; }
    public bool PrerequisitesUnhealthy { get; }
    public string PrerequisiteBadge => Strings.BadgePrerequisites;
    public string SummaryLine { get; }
}

/// <summary>One installed application in the detail pane.</summary>
public sealed class ReportedAppRowViewModel
{
    public ReportedAppRowViewModel(ReportedApp app)
    {
        DisplayName = app.DisplayName;
        Publisher = string.IsNullOrWhiteSpace(app.Publisher) ? Strings.None : app.Publisher!;
        WingetId = string.IsNullOrWhiteSpace(app.WingetId) ? Strings.None : app.WingetId!;
        Version = string.IsNullOrWhiteSpace(app.Version) ? Strings.None : app.Version!;
        Available = string.IsNullOrWhiteSpace(app.AvailableVersion) ? Strings.None : app.AvailableVersion!;
        HasUpdate = !string.IsNullOrWhiteSpace(app.AvailableVersion);
        IsMonitored = !string.IsNullOrWhiteSpace(app.MonitoredAppId);
        Context = app.Context.ToString();
        _searchText = $"{DisplayName} {Publisher} {WingetId}";
    }

    private readonly string _searchText;

    public string DisplayName { get; }
    public string Publisher { get; }
    public string WingetId { get; }
    public string Version { get; }
    public string Available { get; }
    public bool HasUpdate { get; }
    public bool IsMonitored { get; }
    public string Context { get; }

    public bool Matches(string? filter) =>
        string.IsNullOrWhiteSpace(filter) || _searchText.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>One tracked update in the detail pane.</summary>
public sealed class ReportedUpdateRowViewModel
{
    public ReportedUpdateRowViewModel(ReportedUpdate update)
    {
        DisplayName = string.IsNullOrWhiteSpace(update.DisplayName) ? update.AppId : update.DisplayName!;
        AppId = update.AppId;
        Versions = $"{update.InstalledVersion ?? Strings.None} → {update.AvailableVersion ?? Strings.None}";
        State = update.State.ToString();
        Mandatory = update.Mandatory ? Strings.Yes : Strings.No;
        IsMandatory = update.Mandatory;
        Deadline = TimeFormat.Absolute(update.DeadlineUtc);
        Context = update.Context.ToString();
        LastError = update.LastError;
        HasError = !string.IsNullOrWhiteSpace(update.LastError);
    }

    public string DisplayName { get; }
    public string AppId { get; }
    public string Versions { get; }
    public string State { get; }
    public string Mandatory { get; }
    public bool IsMandatory { get; }
    public string Deadline { get; }
    public string Context { get; }
    public string? LastError { get; }
    public bool HasError { get; }
}

/// <summary>One reported event in the detail pane.</summary>
public sealed class ReportedEventRowViewModel
{
    public ReportedEventRowViewModel(ReportedEvent reported)
    {
        When = TimeFormat.Relative(reported.OccurredUtc);
        WhenTooltip = TimeFormat.Absolute(reported.OccurredUtc);
        Kind = reported.Kind.ToString();
        AppId = string.IsNullOrWhiteSpace(reported.AppId) ? Strings.None : reported.AppId!;
        Message = string.IsNullOrWhiteSpace(reported.Message)
            ? Versions(reported)
            : reported.Message!;
        IsFailure = reported.Kind == ReportedEventKind.InstallFailed;
    }

    private static string Versions(ReportedEvent reported) =>
        reported.FromVersion is null && reported.ToVersion is null
            ? Strings.None
            : $"{reported.FromVersion ?? Strings.None} → {reported.ToVersion ?? Strings.None}";

    public string When { get; }
    public string WhenTooltip { get; }
    public string Kind { get; }
    public string AppId { get; }
    public string Message { get; }
    public bool IsFailure { get; }
}

/// <summary>One queued command in the detail pane.</summary>
public sealed class DeviceCommandRowViewModel
{
    public DeviceCommandRowViewModel(DeviceCommand command)
    {
        Kind = command.Kind.ToString();
        Issued = TimeFormat.Relative(command.IssuedUtc);
        IssuedBy = string.IsNullOrWhiteSpace(command.IssuedBy) ? Strings.None : command.IssuedBy!;
    }

    public string Kind { get; }
    public string Issued { get; }
    public string IssuedBy { get; }
}

/// <summary>
/// The Devices page: a searched, paged master list of the organization's devices with a detail pane for the
/// selected one, and the four commands an administrator can queue for it plus deletion.
///
/// <para>
/// Commands are queued, never executed here: the device picks them up on its next sync and acknowledges them in
/// its next report, which is why the buttons say what they queue rather than promising an immediate result.
/// </para>
/// </summary>
public sealed class OrganizationDevicesViewModel : ObservableObject
{
    public const int PageSize = 25;

    private readonly ILogger<OrganizationDevicesViewModel> _log;
    private readonly CloudSession _session;
    private readonly IDialogService _dialogs;

    private readonly RelayCommand _refreshCommand;
    private readonly RelayCommand _previousCommand;
    private readonly RelayCommand _nextCommand;
    private readonly RelayCommand _scanCommand;
    private readonly RelayCommand _reportCommand;
    private readonly RelayCommand _repairCommand;
    private readonly RelayCommand _updateAgentCommand;
    private readonly RelayCommand _deleteCommand;

    private readonly List<ReportedAppRowViewModel> _allInstalled = [];

    private string _search = string.Empty;
    private string _installedFilter = string.Empty;
    private int _page = 1;
    private int _total;
    private bool _isBusy;
    private string? _error;
    private string? _notice;
    private DeviceRowViewModel? _selected;
    private DeviceDetail? _detail;
    private Guid? _loadedOrganization;

    public OrganizationDevicesViewModel(ILogger<OrganizationDevicesViewModel> log, CloudSession session, IDialogService dialogs)
    {
        _log = log;
        _session = session;
        _dialogs = dialogs;

        _refreshCommand = new RelayCommand(() => Load(1), () => !_isBusy && _session.OrganizationId is not null);
        _previousCommand = new RelayCommand(() => Load(_page - 1), () => !_isBusy && _page > 1);
        _nextCommand = new RelayCommand(() => Load(_page + 1), () => !_isBusy && _page < PageCount);
        _scanCommand = new RelayCommand(() => Command(DeviceCommandKind.ScanNow, Strings.ButtonScanNowDevice), CanCommand);
        _reportCommand = new RelayCommand(() => Command(DeviceCommandKind.ReportNow, Strings.ButtonReportNow), CanCommand);
        _repairCommand = new RelayCommand(() => Command(DeviceCommandKind.RepairPrerequisites, Strings.ButtonRepairDevice), CanCommand);
        _updateAgentCommand = new RelayCommand(() => Command(DeviceCommandKind.UpdateAgent, Strings.ButtonUpdateAgent), CanCommand);
        _deleteCommand = new RelayCommand(Delete, CanCommand);

        _session.Changed += OnSessionChanged;
    }

    public string Title => Strings.DevicesTitle;

    public string Subtitle => Strings.DevicesSubtitle;

    public ObservableCollection<DeviceRowViewModel> Devices { get; } = [];

    public bool HasDevices => Devices.Count > 0;

    public bool IsSignedIn => _session.OrganizationId is not null;

    public string Search
    {
        get => _search;
        set
        {
            if (SetProperty(ref _search, value ?? string.Empty)) Load(1);
        }
    }

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

    public int PageCount => Math.Max(1, (int)Math.Ceiling(_total / (double)PageSize));

    public string PageText => Strings.DevicesPageOf(_page, PageCount, _total);

    public ICommand RefreshCommand => _refreshCommand;

    public ICommand PreviousPageCommand => _previousCommand;

    public ICommand NextPageCommand => _nextCommand;

    // ---------------------------------------------------------------- selection and detail

    public DeviceRowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            OnPropertyChanged(nameof(HasSelection));
            RaiseCanExecuteChanged();
            if (value is not null) LoadDetail(value.DeviceId);
            else ClearDetail();
        }
    }

    public bool HasSelection => _selected is not null;

    public string DeviceName => _detail?.Summary.DeviceName ?? _selected?.DeviceName ?? string.Empty;

    public string UserText => Text(_detail?.Summary.LastLogonUser);

    public string OsText => Text(_detail?.Summary.OsVersion);

    public string AgentVersionText => Text(_detail?.Summary.AgentVersion);

    public string LastSeenText => _detail is null ? Strings.None
        : $"{TimeFormat.Relative(_detail.Summary.LastSeenUtc)} ({TimeFormat.Absolute(_detail.Summary.LastSeenUtc)})";

    public string EnrolledText => _detail is null ? Strings.None : TimeFormat.Absolute(_detail.Summary.EnrolledUtc);

    public string ConfigVersionText => Text(_detail?.Summary.ConfigVersionApplied);

    public string DeviceIdText => _detail?.Summary.DeviceId.ToString() ?? Strings.None;

    public string MachineGuidText => Text(_detail?.MachineGuid);

    public bool PrerequisitesHealthy => _detail?.Summary.PrerequisitesHealthy ?? true;

    public string PrerequisiteSummary => _detail?.Prerequisites?.Summary ?? Strings.PrerequisitesUnknown;

    public ObservableCollection<ReportedAppRowViewModel> InstalledApps { get; } = [];

    public bool HasInstalledApps => InstalledApps.Count > 0;

    public string InstalledFilter
    {
        get => _installedFilter;
        set
        {
            if (SetProperty(ref _installedFilter, value ?? string.Empty)) RefreshInstalled();
        }
    }

    public ObservableCollection<ReportedUpdateRowViewModel> Updates { get; } = [];

    public bool HasUpdates => Updates.Count > 0;

    public ObservableCollection<ReportedEventRowViewModel> Events { get; } = [];

    public bool HasEvents => Events.Count > 0;

    public ObservableCollection<DeviceCommandRowViewModel> PendingCommands { get; } = [];

    public bool HasPendingCommands => PendingCommands.Count > 0;

    public ICommand ScanNowCommand => _scanCommand;

    public ICommand ReportNowCommand => _reportCommand;

    public ICommand RepairPrerequisitesCommand => _repairCommand;

    public ICommand UpdateAgentCommand => _updateAgentCommand;

    public ICommand DeleteDeviceCommand => _deleteCommand;

    // ---------------------------------------------------------------- loading

    /// <summary>Loads the first page when the page is shown for an organization it has not listed yet.</summary>
    public void EnsureLoaded()
    {
        if (_session.OrganizationId is not { } id) return;
        if (_loadedOrganization == id && Devices.Count > 0) return;
        Load(1);
    }

    private async void Load(int page)
    {
        if (_session.OrganizationId is not { } id) return;
        IsBusy = true;
        Error = null;
        try
        {
            var result = await _session.GetDevicesAsync(_search, Math.Max(1, page), PageSize, CancellationToken.None).ConfigureAwait(true);
            _page = Math.Max(1, page);
            _total = result.Total;
            _loadedOrganization = id;

            var previous = _selected?.DeviceId;
            Devices.Clear();
            foreach (var device in result.Items) Devices.Add(new DeviceRowViewModel(device));
            _log.LogInformation("Listed {Count} of {Total} device(s) for organization {Id} (page {Page}).",
                result.Items.Count, result.Total, id, _page);

            var restore = previous is { } wanted ? Devices.FirstOrDefault(d => d.DeviceId == wanted) : null;
            Selected = restore ?? Devices.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Error = CloudSession.Describe(ex);
            _log.LogWarning(ex, "Listing the organization's devices failed.");
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasDevices), nameof(PageText), nameof(PageCount), nameof(IsSignedIn));
            RaiseCanExecuteChanged();
        }
    }

    private async void LoadDetail(Guid deviceId)
    {
        IsBusy = true;
        Error = null;
        try
        {
            _detail = await _session.GetDeviceAsync(deviceId, CancellationToken.None).ConfigureAwait(true);
            FillDetail();
        }
        catch (Exception ex)
        {
            Error = CloudSession.Describe(ex);
            _log.LogWarning(ex, "Loading device {DeviceId} failed.", deviceId);
            ClearDetail();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void FillDetail()
    {
        _allInstalled.Clear();
        if (_detail is not null)
        {
            _allInstalled.AddRange(_detail.InstalledApps
                .OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Select(a => new ReportedAppRowViewModel(a)));
        }
        RefreshInstalled();

        Updates.Clear();
        foreach (var update in _detail?.Updates ?? []) Updates.Add(new ReportedUpdateRowViewModel(update));

        Events.Clear();
        foreach (var reported in (_detail?.RecentEvents ?? []).OrderByDescending(e => e.OccurredUtc))
            Events.Add(new ReportedEventRowViewModel(reported));

        PendingCommands.Clear();
        foreach (var command in _detail?.PendingCommands ?? []) PendingCommands.Add(new DeviceCommandRowViewModel(command));

        RefreshDetailProperties();
    }

    private void ClearDetail()
    {
        _detail = null;
        _allInstalled.Clear();
        InstalledApps.Clear();
        Updates.Clear();
        Events.Clear();
        PendingCommands.Clear();
        RefreshDetailProperties();
    }

    private void RefreshInstalled()
    {
        InstalledApps.Clear();
        foreach (var app in _allInstalled.Where(a => a.Matches(_installedFilter))) InstalledApps.Add(app);
        OnPropertyChanged(nameof(HasInstalledApps));
    }

    private void RefreshDetailProperties() => OnPropertyChanged(
        nameof(DeviceName), nameof(UserText), nameof(OsText), nameof(AgentVersionText), nameof(LastSeenText),
        nameof(EnrolledText), nameof(ConfigVersionText), nameof(DeviceIdText), nameof(MachineGuidText),
        nameof(PrerequisitesHealthy), nameof(PrerequisiteSummary), nameof(HasInstalledApps), nameof(HasUpdates),
        nameof(HasEvents), nameof(HasPendingCommands));

    // ---------------------------------------------------------------- commands

    private bool CanCommand() => !_isBusy && _selected is not null && _session.OrganizationId is not null;

    private async void Command(DeviceCommandKind kind, string label)
    {
        if (_selected is not { } device) return;
        IsBusy = true;
        Error = null;
        Notice = null;
        try
        {
            await _session.SendCommandAsync(device.DeviceId, kind, null, CancellationToken.None).ConfigureAwait(true);
            Notice = Strings.CommandQueued(label);
            _log.LogInformation("Queued {Kind} for device {DeviceId} ({Name}).", kind, device.DeviceId, device.DeviceName);
            LoadDetail(device.DeviceId);
        }
        catch (Exception ex)
        {
            Error = CloudSession.Describe(ex);
            _log.LogWarning(ex, "Queueing {Kind} for device {DeviceId} failed.", kind, device.DeviceId);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async void Delete()
    {
        if (_selected is not { } device) return;
        if (!_dialogs.Confirm(Strings.ConfirmDeleteDeviceTitle, Strings.ConfirmDeleteDeviceBody(device.DeviceName),
                Strings.ButtonDelete, Strings.Cancel)) return;

        IsBusy = true;
        Error = null;
        Notice = null;
        try
        {
            await _session.DeleteDeviceAsync(device.DeviceId, CancellationToken.None).ConfigureAwait(true);
            _log.LogInformation("Deleted device {DeviceId} ({Name}) from the organization.", device.DeviceId, device.DeviceName);
            Notice = Strings.DeviceDeleted(device.DeviceName);
            _selected = null;
            ClearDetail();
            Load(_page);
        }
        catch (Exception ex)
        {
            Error = CloudSession.Describe(ex);
            _log.LogWarning(ex, "Deleting device {DeviceId} failed.", device.DeviceId);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---------------------------------------------------------------- plumbing

    private static string Text(string? value) => string.IsNullOrWhiteSpace(value) ? Strings.None : value!;

    private void OnSessionChanged()
    {
        if (_session.OrganizationId == _loadedOrganization) return;
        _loadedOrganization = null;
        _page = 1;
        _total = 0;
        Devices.Clear();
        _selected = null;
        ClearDetail();
        Notice = null;
        Error = null;
        OnPropertyChanged(nameof(HasDevices), nameof(PageText), nameof(IsSignedIn), nameof(Selected), nameof(HasSelection));
        RaiseCanExecuteChanged();
    }

    private void RaiseCanExecuteChanged()
    {
        _refreshCommand.RaiseCanExecuteChanged();
        _previousCommand.RaiseCanExecuteChanged();
        _nextCommand.RaiseCanExecuteChanged();
        _scanCommand.RaiseCanExecuteChanged();
        _reportCommand.RaiseCanExecuteChanged();
        _repairCommand.RaiseCanExecuteChanged();
        _updateAgentCommand.RaiseCanExecuteChanged();
        _deleteCommand.RaiseCanExecuteChanged();
    }
}
