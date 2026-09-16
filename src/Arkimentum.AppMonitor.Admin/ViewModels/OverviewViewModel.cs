using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Arkimentum.AppMonitor.Admin.Infrastructure;
using Arkimentum.AppMonitor.Admin.Resources;
using Arkimentum.AppMonitor.Admin.Services;
using Arkimentum.AppMonitor.Cloud;
using Arkimentum.AppMonitor.Configuration;
using Arkimentum.AppMonitor.Ipc;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Prerequisites;
using Arkimentum.AppMonitor.UI;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Admin.ViewModels;

/// <summary>One row of the pending-updates table.</summary>
public sealed class PendingUpdateRowViewModel
{
    public PendingUpdateRowViewModel(PendingUpdate update)
    {
        DisplayName = string.IsNullOrWhiteSpace(update.DisplayName) ? update.AppId : update.DisplayName;
        AppId = update.AppId;
        Versions = $"{update.InstalledVersion ?? Strings.None} → {update.AvailableVersion ?? Strings.None}";
        State = update.State.ToString();
        Mandatory = update.Mandatory ? Strings.Yes : Strings.No;
        Deadline = TimeFormat.Absolute(update.DeadlineUtc);
        Deferrals = update.MaxDeferrals > 0 ? $"{update.DeferralCount}/{update.MaxDeferrals}" : update.DeferralCount.ToString();
        Context = update.Context.ToString();
        IsMandatory = update.Mandatory;
    }

    public string DisplayName { get; }
    public string AppId { get; }
    public string Versions { get; }
    public string State { get; }
    public string Mandatory { get; }
    public string Deadline { get; }
    public string Deferrals { get; }
    public string Context { get; }
    public bool IsMandatory { get; }
}

/// <summary>
/// The Overview page: what the service is doing, whether the agent pipe answers, what is pending, and a summary
/// of the saved configuration. Every card degrades gracefully — neither the service nor the pipe need exist.
/// </summary>
public sealed class OverviewViewModel : ObservableObject
{
    private readonly ILogger<OverviewViewModel> _log;
    private readonly ServiceControlService _service;
    private readonly AdminIpcService _ipc;
    private readonly SettingsStoreService _store;
    private readonly CatalogService _catalog;
    private readonly ConfigurationEditor _editor;
    private readonly IDialogService _dialogs;
    private readonly ILoggerFactory _loggers;
    private readonly CloudStatusReader _cloudStatus;

    private readonly RelayCommand _startCommand;
    private readonly RelayCommand _stopCommand;
    private readonly RelayCommand _restartCommand;
    private readonly RelayCommand _scanCommand;
    private readonly RelayCommand _repairCommand;
    private readonly RelayCommand _updateAgentCommand;

    private ServiceSnapshot _snapshot = new();
    private AgentSettings _effective = new();
    private CloudStatus? _cloud;
    private string? _notice;
    private bool _noticeIsError;
    private bool _repairRequested;
    private bool _agentUpdateRequested;

    public OverviewViewModel(
        ILogger<OverviewViewModel> log,
        ILoggerFactory loggers,
        ServiceControlService service,
        AdminIpcService ipc,
        SettingsStoreService store,
        CatalogService catalog,
        ConfigurationEditor editor,
        IDialogService dialogs,
        CloudStatusReader cloudStatus)
    {
        _log = log;
        _loggers = loggers;
        _service = service;
        _ipc = ipc;
        _store = store;
        _catalog = catalog;
        _editor = editor;
        _dialogs = dialogs;
        _cloudStatus = cloudStatus;

        _startCommand = new RelayCommand(() => Control(_service.Start), () => _service.IsAvailable && _snapshot.CanStart);
        _stopCommand = new RelayCommand(() => Control(_service.Stop), () => _service.IsAvailable && _snapshot.CanStop);
        _restartCommand = new RelayCommand(() => Control(_service.Restart), () => _service.IsAvailable && _snapshot.IsInstalled);
        _scanCommand = new RelayCommand(RequestScan, () => _ipc.IsConnected);
        _repairCommand = new RelayCommand(RequestRepair, () => _ipc.IsConnected && !RepairInProgress);
        _updateAgentCommand = new RelayCommand(RequestAgentUpdate, () => _ipc.IsConnected && !AgentUpdateInProgress);
        OpenLogFolderCommand = new RelayCommand(() => _dialogs.OpenFolder(_effective.LogDirectory));
        OpenStateFolderCommand = new RelayCommand(() => _dialogs.OpenFolder(_effective.StateDirectory));
        RefreshCommand = new RelayCommand(Refresh);

        _ipc.StateReceived += _ => RefreshAgent();
        _ipc.ConnectionChanged += _ => RefreshAgent();
        _ipc.AckReceived += OnAck;
        _editor.Reloaded += Refresh;

        Refresh();
    }

    public string Title => Strings.OverviewTitle;

    // ---------------------------------------------------------------- service card

    public bool ServiceInstalled => _snapshot.IsInstalled;

    public string ServiceState => _snapshot.IsInstalled ? _snapshot.State : Strings.ServiceNotInstalled;

    public string ServiceStartType => string.IsNullOrEmpty(_snapshot.StartType) ? Strings.None : _snapshot.StartType;

    public string ServiceAccount => string.IsNullOrEmpty(_snapshot.Account) ? Strings.None : _snapshot.Account;

    public string ServiceVersion => _snapshot.Version ?? Strings.None;

    public string ServiceExecutable => _snapshot.ImagePath ?? Strings.None;

    public bool ServiceRunning => _snapshot.State == "Running";

    public bool ShowServiceHint => !_snapshot.IsInstalled || !_service.IsAvailable;

    public string ServiceHint => !_service.IsAvailable ? Strings.ServiceControlDisabledHint : Strings.ServiceNotInstalledHint;

    public ICommand StartCommand => _startCommand;

    public ICommand StopCommand => _stopCommand;

    public ICommand RestartCommand => _restartCommand;

    public ICommand ScanNowCommand => _scanCommand;

    public ICommand OpenLogFolderCommand { get; }

    public ICommand OpenStateFolderCommand { get; }

    public ICommand RefreshCommand { get; }

    public string? Notice
    {
        get => _notice;
        private set
        {
            if (SetProperty(ref _notice, value)) OnPropertyChanged(nameof(HasNotice));
        }
    }

    public bool NoticeIsError
    {
        get => _noticeIsError;
        private set => SetProperty(ref _noticeIsError, value);
    }

    public bool HasNotice => !string.IsNullOrEmpty(_notice);

    // ---------------------------------------------------------------- agent card

    public bool IsConnected => _ipc.IsConnected;

    public string ConnectionText => _ipc.IsConnected ? Strings.AgentConnected : Strings.AgentDisconnected;

    public bool ShowConnectionHint => !_ipc.IsConnected;

    public string LastScanText => TimeFormat.Absolute(_ipc.LastState?.LastScanUtc);

    public string NextScanText => TimeFormat.Absolute(_ipc.LastState?.NextScanUtc);

    /// <summary>
    /// The whole configured set, not just what is installed here: this is the administrator's view of the policy.
    /// The service's <c>MonitoredAppCount</c> counts only the applications that apply to the connected user's device
    /// (that is what the tray shows), so the console reads <c>ConfiguredAppCount</c> - falling back to the old
    /// meaning of <c>MonitoredAppCount</c> when the service predates it.
    /// </summary>
    public string MonitoredAppsText => _ipc.LastState is { } state
        ? (state.Settings.ConfiguredAppCount > 0 ? state.Settings.ConfiguredAppCount : state.Settings.MonitoredAppCount).ToString()
        : _effective.Apps.Count.ToString();

    public string ReportedServiceVersion => _ipc.LastState?.ServiceVersion ?? Strings.None;

    public ObservableCollection<PendingUpdateRowViewModel> PendingUpdates { get; } = [];

    public bool HasPendingUpdates => PendingUpdates.Count > 0;

    // ---------------------------------------------------------------- prerequisites card

    private PrerequisiteStatus? Prerequisites => _ipc.LastState?.Settings.Prerequisites;

    /// <summary>False until a running service has reported a check; the card then explains why it is empty.</summary>
    public bool HasPrerequisites => Prerequisites is not null;

    public string PrerequisiteSummary => Prerequisites?.Summary ?? Strings.PrerequisitesUnknown;

    public bool PrerequisitesHealthy => Prerequisites?.IsHealthy == true;

    public string PrerequisiteBadge => PrerequisitesHealthy ? Strings.PrerequisitesHealthy : Strings.PrerequisitesUnhealthy;

    public string WingetVersionText => Prerequisites?.WingetVersion ?? Strings.None;

    public string WingetPathText => Prerequisites?.WingetPath ?? Strings.None;

    public string MinimumVersionText => Prerequisites?.MinimumVersion ?? Strings.None;

    public string AppInstallerText => Prerequisites?.AppInstallerProvisioned switch
    {
        true => Strings.Yes,
        false => Strings.No,
        _ => Strings.Unknown,
    };

    public string AutoInstallText => Prerequisites?.AutoInstallEnabled == true ? Strings.Enabled : Strings.Disabled;

    public string PrerequisiteCheckedText => TimeFormat.Absolute(Prerequisites?.CheckedUtc);

    public string? PrerequisiteLastAction => Prerequisites?.LastAction;

    public bool HasPrerequisiteLastAction => !string.IsNullOrWhiteSpace(Prerequisites?.LastAction);

    public string? PrerequisiteLastError => Prerequisites?.LastError;

    public bool HasPrerequisiteLastError => !string.IsNullOrWhiteSpace(Prerequisites?.LastError);

    /// <summary>True from the moment the repair is acknowledged until a state snapshot says it has finished.</summary>
    public bool RepairInProgress => _repairRequested || Prerequisites?.RepairInProgress == true;

    public ICommand RepairPrerequisitesCommand => _repairCommand;

    /// <summary>
    /// Asks the service to replace the agent with a newer release now. The service owns the update (it runs as
    /// SYSTEM) and refuses when <c>AgentAutoUpdate</c> is off or an application install is running; its answer
    /// lands in the notice line.
    /// </summary>
    public ICommand UpdateAgentCommand => _updateAgentCommand;

    /// <summary>True from the moment the request is sent until the service answers it.</summary>
    public bool AgentUpdateInProgress => _agentUpdateRequested || _ipc.LastState?.AgentUpdate?.InProgress == true;

    // ---------------------------------------------------------------- configuration card

    public string ConfiguredAppsText { get; private set; } = "0";

    public string LockedAppsText { get; private set; } = "0";

    public string LogLevelText { get; private set; } = string.Empty;

    public string ScanIntervalText { get; private set; } = string.Empty;

    public string ConfigurationKey => _store.PreferencePath;

    public ObservableCollection<string> ConfigurationProblems { get; } = [];

    public bool HasConfigurationProblems => ConfigurationProblems.Count > 0;

    // ---------------------------------------------------------------- organization card

    /// <summary>
    /// What the service last managed to do with the cloud, from <c>&lt;StateDirectory&gt;\cloud-status.json</c>.
    /// The console never writes that file; when it is absent this machine is simply not enrolled.
    /// </summary>
    public bool IsCloudConnected => _cloud is not null;

    public string CloudOrganizationText => Value(_cloud?.OrganizationName);

    public string CloudServerText => Value(_cloud?.ServerUrl);

    public string CloudDeviceIdText => _cloud?.DeviceId?.ToString() ?? Strings.None;

    public string CloudEnrolledText => TimeFormat.Absolute(_cloud?.EnrolledUtc);

    public string CloudConfigText => _cloud?.LastConfigVersion is { Length: > 0 } version
        ? $"{version} · {TimeFormat.Relative(_cloud.LastConfigUtc)}"
        : TimeFormat.Relative(_cloud?.LastConfigUtc);

    public string CloudReportText => TimeFormat.Relative(_cloud?.LastReportUtc);

    public string? CloudError => _cloud?.LastError;

    public bool HasCloudError => !string.IsNullOrWhiteSpace(_cloud?.LastError);

    private static string Value(string? text) => string.IsNullOrWhiteSpace(text) ? Strings.None : text!;

    // ---------------------------------------------------------------- refresh

    public void Refresh()
    {
        _snapshot = _service.Read();
        _effective = _store.ReadEffective(_loggers, _catalog);
        _cloud = _cloudStatus.Read(_effective.StateDirectory);

        var saved = _editor.SavedDocument;
        var policy = _editor.PolicyDocument;
        ConfiguredAppsText = saved.Apps.Count.ToString();
        LockedAppsText = policy.Apps.Count.ToString();
        LogLevelText = _effective.LogLevel;
        ScanIntervalText = TimeFormat.Duration(_effective.ScanIntervalMinutes);

        ConfigurationProblems.Clear();
        foreach (var problem in saved.Validate()) ConfigurationProblems.Add(problem);

        RefreshAgent();
        OnPropertyChanged(
            nameof(ServiceInstalled), nameof(ServiceState), nameof(ServiceStartType), nameof(ServiceAccount),
            nameof(ServiceVersion), nameof(ServiceExecutable), nameof(ServiceRunning), nameof(ShowServiceHint),
            nameof(ServiceHint), nameof(ConfiguredAppsText), nameof(LockedAppsText), nameof(LogLevelText),
            nameof(ScanIntervalText), nameof(ConfigurationKey), nameof(HasConfigurationProblems),
            nameof(IsCloudConnected), nameof(CloudOrganizationText), nameof(CloudServerText), nameof(CloudDeviceIdText),
            nameof(CloudEnrolledText), nameof(CloudConfigText), nameof(CloudReportText), nameof(CloudError),
            nameof(HasCloudError));
        RaiseCanExecuteChanged();
    }

    private void RefreshAgent()
    {
        PendingUpdates.Clear();
        foreach (var update in _ipc.LastState?.Updates ?? [])
        {
            PendingUpdates.Add(new PendingUpdateRowViewModel(update));
        }
        // A snapshot that no longer reports a running repair is what ends the "repair requested" state.
        if (_repairRequested && Prerequisites is { RepairInProgress: false }) _repairRequested = false;

        OnPropertyChanged(
            nameof(IsConnected), nameof(ConnectionText), nameof(ShowConnectionHint), nameof(LastScanText),
            nameof(NextScanText), nameof(MonitoredAppsText), nameof(ReportedServiceVersion), nameof(HasPendingUpdates),
            nameof(HasPrerequisites), nameof(PrerequisiteSummary), nameof(PrerequisitesHealthy), nameof(PrerequisiteBadge),
            nameof(WingetVersionText), nameof(WingetPathText), nameof(MinimumVersionText), nameof(AppInstallerText),
            nameof(AutoInstallText), nameof(PrerequisiteCheckedText), nameof(PrerequisiteLastAction),
            nameof(HasPrerequisiteLastAction), nameof(PrerequisiteLastError), nameof(HasPrerequisiteLastError),
            nameof(RepairInProgress), nameof(AgentUpdateInProgress));
        RaiseCanExecuteChanged();
    }

    private void OnAck(AckMessage ack)
    {
        // The agent update is answered when the check is done, which also ends the "requested" state.
        if (_agentUpdateRequested)
        {
            _agentUpdateRequested = false;
            OnPropertyChanged(nameof(AgentUpdateInProgress));
            RaiseCanExecuteChanged();
        }
        if (string.IsNullOrWhiteSpace(ack.Message)) return;
        Notice = ack.Message;
        NoticeIsError = !ack.Ok;
        _log.LogInformation("Service acknowledged: {Message}", ack.Message);
    }

    private void RaiseCanExecuteChanged()
    {
        _startCommand.RaiseCanExecuteChanged();
        _stopCommand.RaiseCanExecuteChanged();
        _restartCommand.RaiseCanExecuteChanged();
        _scanCommand.RaiseCanExecuteChanged();
        _repairCommand.RaiseCanExecuteChanged();
        _updateAgentCommand.RaiseCanExecuteChanged();
    }

    private async void RequestAgentUpdate()
    {
        var sent = await _ipc.RequestAgentUpdateAsync().ConfigureAwait(true);
        _agentUpdateRequested = sent;
        if (!sent)
        {
            Notice = Strings.AgentUpdateRequestFailed;
            NoticeIsError = true;
        }
        OnPropertyChanged(nameof(AgentUpdateInProgress));
        RaiseCanExecuteChanged();
    }

    private async void RequestRepair()
    {
        var sent = await _ipc.RequestPrerequisiteRepairAsync().ConfigureAwait(true);
        _repairRequested = sent;
        if (!sent)
        {
            Notice = Strings.RepairRequestFailed;
            NoticeIsError = true;
        }
        OnPropertyChanged(nameof(RepairInProgress));
        RaiseCanExecuteChanged();
    }

    private void Control(Func<string?> action)
    {
        var error = action();
        Notice = error;
        NoticeIsError = error is not null;
        Refresh();
    }

    private async void RequestScan()
    {
        var sent = await _ipc.RequestScanAsync().ConfigureAwait(true);
        Notice = sent ? Strings.ScanRequested : Strings.ScanRequestFailed;
        NoticeIsError = !sent;
        _log.LogInformation("Scan request {Result}.", sent ? "accepted" : "refused");
    }
}
