using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using System.Windows.Threading;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Native;
using Arkimentum.AppMonitor.Tray.Infrastructure;
using Arkimentum.AppMonitor.Tray.Resources;
using Arkimentum.AppMonitor.UI;

namespace Arkimentum.AppMonitor.Tray.ViewModels;

/// <summary>One blocking application, named the way the user knows it.</summary>
public sealed class BlockingProcessViewModel
{
    /// <param name="processName">The bare process name, as the service reports it.</param>
    /// <param name="details">
    /// What the service (running as LocalSystem) could read about the running instances of this process; empty when
    /// the service is older than 1.2 or could not read anything. Instances that run elevated or in another session
    /// are the ones this agent cannot close itself, and the dialog has to say so or the user retries forever.
    /// </param>
    public BlockingProcessViewModel(string processName, IEnumerable<BlockingProcessInfo>? details = null)
    {
        ProcessName = processName;
        FriendlyName = ProcessDisplay.Friendly(processName);
        ShowProcessName = !string.Equals(FriendlyName, processName, StringComparison.OrdinalIgnoreCase);

        var mine = (details ?? [])
            .Where(d => string.Equals(ProcessHelper.Normalize(d.ProcessName), ProcessHelper.Normalize(processName), StringComparison.OrdinalIgnoreCase))
            .ToList();
        IsElevated = mine.Any(d => d.Elevated == true);
        var otherSession = mine.Where(d => d.SessionId >= 0 && d.SessionId != AppInfo.SessionId).ToList();
        IsInAnotherSession = otherSession.Count > 0;
        OtherSessionUser = otherSession.Select(d => d.UserName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));

        var markers = new List<string>();
        if (IsElevated) markers.Add(Strings.CloseAppsElevated);
        if (IsInAnotherSession)
            markers.Add(OtherSessionUser is null ? Strings.CloseAppsOtherSession : Strings.CloseAppsOtherSessionAs(OtherSessionUser));
        Qualifier = markers.Count == 0 ? string.Empty : Strings.CloseAppsQualifier(string.Join(", ", markers));
    }

    public string ProcessName { get; }

    public string FriendlyName { get; }

    /// <summary>False when the friendly name is just the process name, so it is not printed twice.</summary>
    public bool ShowProcessName { get; }

    /// <summary>At least one instance runs elevated; only the service can end it.</summary>
    public bool IsElevated { get; }

    /// <summary>At least one instance runs in a session this agent does not own.</summary>
    public bool IsInAnotherSession { get; }

    /// <summary>Who owns the instance in the other session, when the service could read it.</summary>
    public string? OtherSessionUser { get; }

    /// <summary>"— elevated, another session (CONTOSO\bob)", or empty when this agent can close it itself.</summary>
    public string Qualifier { get; }

    public bool HasQualifier => Qualifier.Length > 0;

    /// <summary>True when only the service can close this one.</summary>
    public bool NeedsService => IsElevated || IsInAnotherSession;
}

/// <summary>What the close-apps dialog's buttons do. Implemented by <see cref="Services.CloseAppsCoordinator"/>.</summary>
public interface ICloseAppsActions
{
    void CloseAndUpdate(CloseAppsViewModel dialog);

    void Defer(CloseAppsViewModel dialog, int minutes);

    void NotNow(CloseAppsViewModel dialog);
}

/// <summary>The "Close apps to update X" dialog.</summary>
public sealed class CloseAppsViewModel : ObservableObject, IDisposable
{
    private readonly ICloseAppsActions _actions;
    private readonly RelayCommand _closeAndUpdateCommand;
    private readonly RelayCommand _notNowCommand;
    private readonly DispatcherTimer _countdown;

    private PendingUpdate _update;
    private string? _statusMessage;
    private bool _isBusy;
    private string? _detailsSignature;

    public CloseAppsViewModel(PendingUpdate update, ICloseAppsActions actions)
    {
        _update = update;
        _actions = actions;

        _closeAndUpdateCommand = new RelayCommand(() => _actions.CloseAndUpdate(this), () => !_isBusy);
        _notNowCommand = new RelayCommand(() => _actions.NotNow(this), () => !_isBusy);

        Processes = [];
        DeferralOptions = [];
        _countdown = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(1) };
        _countdown.Tick += (_, _) => OnPropertyChanged(nameof(CountdownText), nameof(ShowCountdown));

        Apply(update);
    }

    public PendingUpdate Model => _update;

    public string Key => _update.Key;

    public ObservableCollection<BlockingProcessViewModel> Processes { get; }

    public ObservableCollection<DeferOptionViewModel> DeferralOptions { get; }

    public ICommand CloseAndUpdateCommand => _closeAndUpdateCommand;

    public ICommand NotNowCommand => _notNowCommand;

    public string Title => Strings.CloseAppsTitle(
        string.IsNullOrWhiteSpace(_update.DisplayName) ? _update.AppId : _update.DisplayName);

    public string VersionText =>
        $"{_update.InstalledVersion ?? Strings.UnknownVersion}{Strings.VersionArrow}{_update.AvailableVersion ?? Strings.UnknownVersion}";

    public string IntroText => Processes.Count == 1 ? Strings.CloseAppsIntroSingle : Strings.CloseAppsIntro;

    public string SaveHint => Strings.CloseAppsSaveHint;

    /// <summary>
    /// True when at least one blocking process runs elevated or in another session. The dialog then explains that this
    /// agent cannot close those and that the service will: without it the user only sees an app that refuses to close.
    /// </summary>
    public bool ShowServiceCloseHint => Processes.Any(p => p.NeedsService);

    public string ServiceCloseHint => Strings.CloseAppsServiceCloses;

    /// <summary>True while a forced close is scheduled: the dialog stays on top and counts down.</summary>
    public bool ForceClosePending => _update.ForceCloseAtUtc is not null;

    public bool ShowCountdown => ForceClosePending;

    public string CountdownText
    {
        get
        {
            if (_update.ForceCloseAtUtc is not { } at) return string.Empty;
            var remaining = at - DateTimeOffset.UtcNow;
            return remaining <= TimeSpan.Zero
                ? Strings.CloseAppsCountdownElapsed
                : Strings.CloseAppsCountdown(TimeFormat.Countdown(remaining));
        }
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        set
        {
            if (SetProperty(ref _statusMessage, value)) OnPropertyChanged(nameof(HasStatus));
        }
    }

    public bool HasStatus => !string.IsNullOrWhiteSpace(_statusMessage);

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            _closeAndUpdateCommand.RaiseCanExecuteChanged();
            _notNowCommand.RaiseCanExecuteChanged();
        }
    }

    public bool ShowDefer => DeferralOptions.Count > 0;

    /// <summary>"Not now" disappears once a mandatory update is past its deadline.</summary>
    public bool ShowNotNow => !_update.IsPastDeadline(DateTimeOffset.UtcNow);

    /// <summary>Raised when the dialog should close itself.</summary>
    public event Action? CloseRequested;

    public void RequestClose() => CloseRequested?.Invoke();

    /// <summary>Applies a newer <see cref="PromptCloseMessage"/> for the same update to this dialog.</summary>
    public void Apply(PendingUpdate update)
    {
        _update = update;

        var names = update.BlockingProcesses.Count > 0 ? update.BlockingProcesses : update.ProcessNames;
        SetProcesses(names, update.BlockingDetails);

        var wanted = update.CanDefer(DateTimeOffset.UtcNow)
            ? update.DeferralOptionsMinutes.Where(m => m > 0).Distinct().ToList()
            : [];
        if (!DeferralOptions.Select(o => o.Minutes).SequenceEqual(wanted))
        {
            DeferralOptions.Clear();
            foreach (var minutes in wanted)
                DeferralOptions.Add(new DeferOptionViewModel(minutes, m => _actions.Defer(this, m)));
        }

        if (ForceClosePending) _countdown.Start();
        else _countdown.Stop();

        OnPropertyChanged(
            nameof(Title), nameof(VersionText), nameof(IntroText), nameof(ForceClosePending), nameof(ShowCountdown),
            nameof(CountdownText), nameof(ShowDefer), nameof(ShowNotNow));
    }

    /// <summary>
    /// Narrows the list to the processes that are still running after a close attempt. <paramref name="details"/> is
    /// what the service could read about them; pass it whenever it is at hand so the markers stay accurate.
    /// </summary>
    public void SetProcesses(IReadOnlyList<string> names, IReadOnlyList<BlockingProcessInfo>? details = null)
    {
        details ??= _update.BlockingDetails;
        // Rebuilding is not free (the friendly name walks the process list), so skip it while nothing moved - the
        // signature covers the markers too, or an instance appearing in another session would go unnoticed.
        var signature = string.Join("|", details.Select(d => $"{d.ProcessName}:{d.ProcessId}:{d.SessionId}:{d.Elevated}"));
        if (signature == _detailsSignature && Processes.Select(p => p.ProcessName).SequenceEqual(names, StringComparer.OrdinalIgnoreCase)) return;
        _detailsSignature = signature;
        Processes.Clear();
        foreach (var name in names) Processes.Add(new BlockingProcessViewModel(name, details));
        OnPropertyChanged(nameof(IntroText), nameof(ShowServiceCloseHint));
    }

    public void Dispose() => _countdown.Stop();
}
