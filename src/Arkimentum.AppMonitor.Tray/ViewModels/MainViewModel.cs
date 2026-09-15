using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Windows.Input;
using System.Windows.Threading;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Tray.Infrastructure;
using Arkimentum.AppMonitor.Tray.Resources;
using Arkimentum.AppMonitor.Tray.Services;
using Arkimentum.AppMonitor.UI;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Tray.ViewModels;

/// <summary>The main window: the update list, the status line and the details footer.</summary>
public sealed class MainViewModel : ObservableObject, IUpdateActions
{
    private const int MaxMonitoredAppsShown = 12;

    private readonly ILogger<MainViewModel> _log;
    private readonly AgentStateStore _store;
    private readonly IpcClientService _ipc;
    private readonly IWindowService _windows;
    private readonly RelayCommand _checkNowCommand;
    private readonly RelayCommand _updateAllCommand;
    private readonly DispatcherTimer _clock;

    public MainViewModel(ILogger<MainViewModel> log, AgentStateStore store, IpcClientService ipc, IWindowService windows,
        AgentUpdateViewModel agentUpdate)
    {
        _log = log;
        _store = store;
        _ipc = ipc;
        _windows = windows;
        AgentUpdate = agentUpdate;

        _checkNowCommand = new RelayCommand(CheckNow, () => _store.IsConnected && !_store.ScanInProgress);
        _updateAllCommand = new RelayCommand(UpdateAll,
            () => _store.IsConnected && !_store.UpdateAllPending && _store.InstallableUpdates.Count > 0);
        OpenLogFolderCommand = new RelayCommand(() => _windows.OpenLogFolder());
        AboutCommand = new RelayCommand(() => _windows.ShowAbout());

        _store.Changed += Refresh;

        // Relative times ("in 2 hours", "Deferred until …") stay honest without a message from the service.
        _clock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
        _clock.Tick += (_, _) => Refresh();
        _clock.Start();

        Refresh();
    }

    public ObservableCollection<UpdateViewModel> Updates { get; } = [];

    /// <summary>The agent's own update state and the two actions in the details footer; shared with the About dialog.</summary>
    public AgentUpdateViewModel AgentUpdate { get; }

    public ICommand CheckNowCommand => _checkNowCommand;

    /// <summary>Queues every update the user could start by hand; the button is only shown while there are some.</summary>
    public ICommand UpdateAllCommand => _updateAllCommand;

    public bool ShowUpdateAll => _store.IsConnected && _store.InstallableUpdates.Count > 0;

    public string UpdateAllText => Strings.UpdateAllCount(_store.InstallableUpdates.Count);

    public ICommand OpenLogFolderCommand { get; }

    public ICommand AboutCommand { get; }

    public string Title => Strings.MainWindowTitle;

    public string Subtitle => Strings.MainHeaderSubtitle;

    // ---------------------------------------------------------------- status

    public bool IsConnected => _store.IsConnected;

    public bool ShowDisconnectedBanner => !_store.IsConnected;

    public bool IsScanning => _store.ScanInProgress;

    public string StatusLine
    {
        get
        {
            if (!_store.IsConnected) return Strings.StatusDisconnected;
            if (_store.ScanInProgress) return Strings.Checking;

            var parts = new List<string>(2);
            parts.Add(_store.LastScanUtc is { } last
                ? Strings.LastChecked(TimeFormat.Absolute(last))
                : Strings.NeverChecked);
            if (_store.NextScanUtc is { } next) parts.Add(Strings.NextCheck(TimeFormat.Absolute(next)));
            return string.Join(Strings.StatusSeparator, parts);
        }
    }

    // ---------------------------------------------------------------- progress banner

    /// <summary>Keys that took part in the current round of installs; the round ends when nothing is in progress.</summary>
    private readonly HashSet<string> _roundKeys = new(StringComparer.Ordinal);

    /// <summary>True while the service is queueing, waiting for a close or installing something.</summary>
    public bool ShowProgressBanner { get; private set; }

    /// <summary>One line such as "Installing 7-Zip… (1 of 6 done, 4 queued)".</summary>
    public string ProgressText { get; private set; } = string.Empty;

    /// <summary>
    /// Recomputes the progress line. "Done" counts the updates of this round the service no longer reports (an
    /// installed update disappears from the state message), so the scoreboard works for one install and for a batch.
    /// </summary>
    private void RefreshProgress()
    {
        // The agent replacing itself owns the banner: the service stops and starts this tray, so nothing else can
        // be running at the same time (a request is refused while an application install is in flight).
        if (AgentUpdate.InProgress && AgentUpdate.IsEnabled)
        {
            _roundKeys.Clear();
            ShowProgressBanner = true;
            ProgressText = AgentUpdate.LatestVersion is { } version
                ? Strings.AgentUpdateProgress(version)
                : Strings.AgentUpdateProgressUnknown;
            return;
        }

        var inProgress = _store.UpdatesInProgress;
        if (inProgress.Count == 0)
        {
            _roundKeys.Clear();
            ShowProgressBanner = false;
            ProgressText = string.Empty;
            return;
        }

        foreach (var u in inProgress) _roundKeys.Add(u.Key);

        var installing = _store.CurrentInstall;
        var head = installing is not null
            ? Strings.ProgressInstalling(installing.DisplayName)
            : inProgress.Any(u => u.State == UpdateState.WaitingForClose)
                ? Strings.ProgressWaitingForClose
                : Strings.ProgressPreparing;

        var total = _roundKeys.Count;
        var done = _roundKeys.Count(k => _store.Find(k) is null);
        var queued = inProgress.Count(u => u.State != UpdateState.Installing);

        ShowProgressBanner = true;
        ProgressText = total > 1 ? $"{head} {Strings.ProgressCounts(done, total, queued)}" : head;
    }

    public bool ShowEmptyState => Updates.Count == 0;

    public string EmptySubtitle =>
        _store.LastScanUtc is { } last ? Strings.EmptySubtitle(TimeFormat.Absolute(last)) : Strings.EmptySubtitleNoScan;

    public string EmptyGlyph => Strings.EmptyGlyph;

    // ---------------------------------------------------------------- details footer

    public string ScanIntervalText => _store.Settings.ScanIntervalMinutes > 0
        ? Strings.EveryDuration(TimeFormat.Duration(_store.Settings.ScanIntervalMinutes))
        : Strings.DetailsNone;

    public string NotificationIntervalText => _store.Settings.NotificationIntervalMinutes > 0
        ? Strings.EveryDuration(TimeFormat.Duration(_store.Settings.NotificationIntervalMinutes))
        : Strings.DetailsNone;

    public string MonitoredAppsText
    {
        get
        {
            var apps = _store.Settings.MonitoredApps;
            if (apps.Count == 0)
                return _store.Settings.MonitoredAppCount > 0
                    ? _store.Settings.MonitoredAppCount.ToString()
                    : Strings.DetailsNone;

            var shown = apps.Take(MaxMonitoredAppsShown).ToList();
            var text = new StringBuilder(string.Join(", ", shown));
            if (apps.Count > shown.Count) text.Append(", ").Append(Strings.AndMore(apps.Count - shown.Count));
            return text.ToString();
        }
    }

    public string SourcesText
    {
        get
        {
            var sources = new List<string>(2);
            if (_store.Settings.WingetEnabled) sources.Add(Strings.BadgeWinget);
            if (_store.Settings.WebSourcesEnabled) sources.Add(Strings.BadgeWeb);
            return sources.Count == 0 ? Strings.DetailsNone : string.Join(", ", sources);
        }
    }

    public string NotificationsText =>
        _store.Settings.NotificationsEnabled ? Strings.DetailsEnabled : Strings.DetailsDisabled;

    public string LogDirectory =>
        string.IsNullOrWhiteSpace(_store.Settings.LogDirectory) ? AppInfo.LogDirectory : _store.Settings.LogDirectory;

    public string ServiceVersion => string.IsNullOrWhiteSpace(_store.ServiceVersion) ? Strings.DetailsNone : _store.ServiceVersion!;

    /// <summary>
    /// Who manages this device: the organization's name once enrolled, "connecting" while the cloud connection is
    /// configured but the device has not enrolled yet, otherwise stand-alone. A 1.1.1 service sends none of the
    /// organization fields, which reads as stand-alone.
    /// </summary>
    public string OrganizationText
    {
        get
        {
            var settings = _store.Settings;
            if (!string.IsNullOrWhiteSpace(settings.OrganizationName)) return settings.OrganizationName!;
            if (settings.CloudConfigured) return Strings.OrganizationEnrolling;
            return Strings.OrganizationStandalone;
        }
    }

    // ---------------------------------------------------------------- lifetime

    /// <summary>Called when the window becomes visible: ask the service for a fresh snapshot.</summary>
    public void OnWindowShown()
    {
        _log.LogInformation("Main window shown");
        _ = _ipc.RequestStateAsync();
    }

    private void CheckNow()
    {
        _log.LogInformation("User requested a scan");
        _ = _ipc.RequestScanAsync();
    }

    /// <summary>
    /// "Update all": one install request per installable update, the same as pressing Install now on each card.
    /// The service queues them and runs the installs one after another; the button greys out at once (the store
    /// remembers the requested keys) instead of staying live until the state message with "scheduled" comes back.
    /// </summary>
    private void UpdateAll()
    {
        var updates = _store.InstallableUpdates;
        if (updates.Count == 0) return;
        _log.LogInformation("User chose Update all: {Count} update(s): {Apps}", updates.Count,
            string.Join(", ", updates.Select(u => u.DisplayName)));
        var keys = updates.Select(u => u.Key).ToList();
        _store.BeginUpdateAll(keys);
        _ = _ipc.InstallAllAsync(keys);
    }

    /// <summary>Rebuilds the card list in place: cards are matched by <see cref="PendingUpdate.Key"/> so the UI stays stable.</summary>
    private void Refresh()
    {
        var connected = _store.IsConnected;
        var incoming = _store.Updates;
        var byKey = Updates.ToDictionary(vm => vm.Key, StringComparer.Ordinal);

        for (var index = 0; index < incoming.Count; index++)
        {
            var model = incoming[index];
            var local = _store.GetLocalStatus(model.Key);
            if (byKey.TryGetValue(model.Key, out var existing))
            {
                existing.Update(model, connected, local);
                var current = Updates.IndexOf(existing);
                if (current != index) Updates.Move(current, index);
            }
            else
            {
                Updates.Insert(index, new UpdateViewModel(model, this, connected, local));
            }
        }

        for (var index = Updates.Count - 1; index >= incoming.Count; index--) Updates.RemoveAt(index);

        RefreshProgress();

        _checkNowCommand.RaiseCanExecuteChanged();
        _updateAllCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(
            nameof(ShowUpdateAll), nameof(UpdateAllText), nameof(ShowProgressBanner), nameof(ProgressText),
            nameof(IsConnected), nameof(ShowDisconnectedBanner), nameof(IsScanning), nameof(StatusLine),
            nameof(ShowEmptyState), nameof(EmptySubtitle), nameof(ScanIntervalText), nameof(NotificationIntervalText),
            nameof(MonitoredAppsText), nameof(SourcesText), nameof(NotificationsText), nameof(LogDirectory),
            nameof(ServiceVersion), nameof(OrganizationText));
    }

    // ---------------------------------------------------------------- IUpdateActions

    public void Install(PendingUpdate update)
    {
        _log.LogInformation("User chose Install now for {App} ({Key})", update.DisplayName, update.Key);
        _ = _ipc.InstallNowAsync(update.Key);
    }

    public void Defer(PendingUpdate update, int minutes)
    {
        if (!update.CanDefer(DateTimeOffset.UtcNow))
        {
            _log.LogWarning("Ignoring a deferral for {Key}: it can no longer be deferred", update.Key);
            return;
        }
        _log.LogInformation("User deferred {App} by {Minutes} minutes", update.DisplayName, minutes);
        _ = _ipc.DeferAsync(update.Key, minutes);
    }

    public void Dismiss(PendingUpdate update)
    {
        _log.LogInformation("User chose Remind me later for {App}", update.DisplayName);
        _ = _ipc.DismissAsync(update.Key);
    }
}
