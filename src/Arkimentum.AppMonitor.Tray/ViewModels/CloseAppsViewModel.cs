using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using System.Windows.Threading;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Tray.Infrastructure;
using Arkimentum.AppMonitor.Tray.Resources;
using Arkimentum.AppMonitor.UI;

namespace Arkimentum.AppMonitor.Tray.ViewModels;

/// <summary>One blocking application, named the way the user knows it.</summary>
public sealed class BlockingProcessViewModel
{
    public BlockingProcessViewModel(string processName)
    {
        ProcessName = processName;
        FriendlyName = ProcessDisplay.Friendly(processName);
        ShowProcessName = !string.Equals(FriendlyName, processName, StringComparison.OrdinalIgnoreCase);
    }

    public string ProcessName { get; }

    public string FriendlyName { get; }

    /// <summary>False when the friendly name is just the process name, so it is not printed twice.</summary>
    public bool ShowProcessName { get; }
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
        SetProcesses(names);

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

    /// <summary>Narrows the list to the processes that are still running after a close attempt.</summary>
    public void SetProcesses(IReadOnlyList<string> names)
    {
        if (Processes.Select(p => p.ProcessName).SequenceEqual(names, StringComparer.OrdinalIgnoreCase)) return;
        Processes.Clear();
        foreach (var name in names) Processes.Add(new BlockingProcessViewModel(name));
        OnPropertyChanged(nameof(IntroText));
    }

    public void Dispose() => _countdown.Stop();
}
