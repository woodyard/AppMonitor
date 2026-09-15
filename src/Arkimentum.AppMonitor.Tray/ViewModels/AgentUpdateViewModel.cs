using System.Windows.Input;
using Arkimentum.AppMonitor.Ipc;
using Arkimentum.AppMonitor.Tray.Infrastructure;
using Arkimentum.AppMonitor.Tray.Resources;
using Arkimentum.AppMonitor.Tray.Services;
using Arkimentum.AppMonitor.UI;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Tray.ViewModels;

/// <summary>
/// The agent's own update state: one status line ("1.1.3 · up to date (checked 16:22)") and the two actions behind
/// it. Shared by the details footer and the About dialog so both show exactly the same thing. Nothing is updated
/// here - the request goes to the service, which downloads, verifies and installs the release as SYSTEM.
/// </summary>
public sealed class AgentUpdateViewModel : ObservableObject
{
    private readonly ILogger<AgentUpdateViewModel> _log;
    private readonly AgentStateStore _store;
    private readonly IpcClientService _ipc;
    private readonly RelayCommand _checkCommand;
    private readonly RelayCommand _updateCommand;

    public AgentUpdateViewModel(ILogger<AgentUpdateViewModel> log, AgentStateStore store, IpcClientService ipc)
    {
        _log = log;
        _store = store;
        _ipc = ipc;

        _checkCommand = new RelayCommand(() => Request(checkOnly: true), CanRequest);
        _updateCommand = new RelayCommand(() => Request(checkOnly: false), () => CanRequest() && UpdateAvailable);

        _store.Changed += Refresh;
    }

    private AgentUpdateStatus? Status => _store.AgentUpdate;

    /// <summary>False against a service that does not report its self-update state; the UI then hides the row's extras.</summary>
    public bool IsSupported => Status is not null;

    /// <summary>The effective AgentAutoUpdate: false means an administrator owns the binaries and requests are refused.</summary>
    public bool IsEnabled => Status?.Enabled == true;

    /// <summary>The agent is being replaced right now: the service downloads, installs and restarts the tray.</summary>
    public bool InProgress => Status?.InProgress == true;

    /// <summary>InProgress, or a request of our own that the service has not answered yet: both buttons stay disabled.</summary>
    public bool Busy => InProgress || _store.AgentUpdateRequested;

    public bool UpdateAvailable => Status is { UpdateAvailable: true };

    /// <summary>The version the feed offers, for the "Update now" wording and the progress banner.</summary>
    public string? LatestVersion => Status?.LatestVersion;

    /// <summary>The value of the "Agent version" row: the running version plus what the last check concluded.</summary>
    public string VersionText
    {
        get
        {
            var version = AppInfo.Version;
            return IsSupported ? Strings.AgentVersionWithStatus(version, StatusText) : version;
        }
    }

    /// <summary>The status half of the version row, in the order that matters most to the user.</summary>
    private string StatusText
    {
        get
        {
            if (!IsEnabled) return Strings.AgentUpdateDisabled;
            if (Busy) return Strings.AgentUpdateChecking;
            if (UpdateAvailable) return Strings.AgentUpdateAvailable(LatestVersion ?? Strings.UnknownVersion);
            if (!string.IsNullOrWhiteSpace(Status?.LastError)) return Strings.AgentUpdateCheckFailed;
            return Status?.LastCheckUtc is { } last ? Strings.AgentUpToDate(TimeFormat.Absolute(last)) : Strings.AgentNotCheckedYet;
        }
    }

    /// <summary>Both buttons are hidden against an older service and while updates are disabled by policy.</summary>
    public bool ShowActions => IsSupported && IsEnabled;

    public bool ShowUpdateNow => ShowActions && UpdateAvailable;

    public ICommand CheckCommand => _checkCommand;

    public ICommand UpdateNowCommand => _updateCommand;

    /// <summary>The service's answer to the last request, shown under the row until the next one.</summary>
    public string? Notice => _store.AgentUpdateNotice;

    public bool HasNotice => !string.IsNullOrWhiteSpace(Notice);

    private bool CanRequest() => _store.IsConnected && IsSupported && IsEnabled && !Busy;

    private async void Request(bool checkOnly)
    {
        _log.LogInformation("User asked the agent to {Kind}", checkOnly ? "check for a newer release" : "update itself");
        var messageId = await _ipc.RequestAgentUpdateAsync(checkOnly).ConfigureAwait(true);
        if (messageId is null)
        {
            _log.LogWarning("The agent update request was not sent: the service pipe is not connected");
            return;
        }
        _store.TrackAgentUpdateRequest(messageId);
    }

    /// <summary>Re-reads everything from the store; called for every state message and every acknowledgement.</summary>
    public void Refresh()
    {
        _checkCommand.RaiseCanExecuteChanged();
        _updateCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(
            nameof(IsSupported), nameof(IsEnabled), nameof(InProgress), nameof(Busy), nameof(UpdateAvailable), nameof(LatestVersion),
            nameof(VersionText), nameof(ShowActions), nameof(ShowUpdateNow), nameof(Notice), nameof(HasNotice));
    }
}
