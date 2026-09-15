using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Tray.Infrastructure;
using Arkimentum.AppMonitor.Tray.Resources;
using Arkimentum.AppMonitor.UI;

namespace Arkimentum.AppMonitor.Tray.ViewModels;

/// <summary>How prominent an update's status line should be.</summary>
public enum StatusSeverity
{
    Normal,
    Attention,
    Error,
    Success,
}

/// <summary>What a card's buttons do. Implemented by <see cref="MainViewModel"/>.</summary>
public interface IUpdateActions
{
    void Install(PendingUpdate update);

    void Defer(PendingUpdate update, int minutes);

    void Dismiss(PendingUpdate update);
}

/// <summary>One update card.</summary>
public sealed class UpdateViewModel : ObservableObject
{
    private readonly IUpdateActions _actions;
    private readonly RelayCommand _installCommand;
    private readonly RelayCommand _dismissCommand;

    private PendingUpdate _update;
    private string? _localStatus;
    private bool _isConnected;

    public UpdateViewModel(PendingUpdate update, IUpdateActions actions, bool isConnected, string? localStatus)
    {
        _update = update;
        _actions = actions;
        _isConnected = isConnected;
        _localStatus = localStatus;

        _installCommand = new RelayCommand(() => _actions.Install(_update), () => IsInstallEnabled);
        _dismissCommand = new RelayCommand(() => _actions.Dismiss(_update), () => _isConnected);
        DeferralOptions = [];
        RebuildDeferralOptions();
    }

    public PendingUpdate Model => _update;

    public string Key => _update.Key;

    public ICommand InstallCommand => _installCommand;

    public ICommand DismissCommand => _dismissCommand;

    public ObservableCollection<DeferOptionViewModel> DeferralOptions { get; }

    // ---------------------------------------------------------------- identity

    public string DisplayName => string.IsNullOrWhiteSpace(_update.DisplayName) ? _update.AppId : _update.DisplayName;

    public string VersionText =>
        $"{_update.InstalledVersion ?? Strings.UnknownVersion}{Strings.VersionArrow}{_update.AvailableVersion ?? Strings.UnknownVersion}";

    public string SourceBadge => _update.Source == UpdateSource.Winget ? Strings.BadgeWinget : Strings.BadgeWeb;

    public string ContextBadge => _update.Context == InstallContext.User ? Strings.BadgeUser : Strings.BadgeSystem;

    // ---------------------------------------------------------------- status

    public string StatusText
    {
        get
        {
            if (_localStatus is { Length: > 0 }) return _localStatus;
            return _update.State switch
            {
                UpdateState.Available => Strings.StateAvailable,
                UpdateState.Deferred => Strings.StateDeferred(
                    _update.DeferredUntilUtc is { } until ? TimeFormat.Absolute(until) : Strings.DetailsNone),
                UpdateState.Scheduled => Strings.StateScheduled,
                UpdateState.WaitingForClose => Strings.StateWaitingForClose(BlockingProcessText),
                UpdateState.Installing => Strings.StateInstalling,
                UpdateState.Installed => Strings.StateInstalled(
                    _update.InstalledAtUtc is { } at ? TimeFormat.Absolute(at) : string.Empty).TrimEnd(),
                UpdateState.Failed => Strings.StateFailed(_update.LastError),
                _ => Strings.StateAvailable,
            };
        }
    }

    public StatusSeverity Severity => _update.State switch
    {
        UpdateState.Failed => StatusSeverity.Error,
        UpdateState.Installed => StatusSeverity.Success,
        UpdateState.WaitingForClose => StatusSeverity.Attention,
        _ => _update.IsPastDeadline(DateTimeOffset.UtcNow) ? StatusSeverity.Attention : StatusSeverity.Normal,
    };

    public string DeadlineText =>
        _update.Mandatory && _update.DeadlineUtc is { } deadline ? Strings.RequiredBy(TimeFormat.Absolute(deadline)) : string.Empty;

    public bool HasDeadline => DeadlineText.Length > 0;

    private string BlockingProcessText =>
        _update.BlockingProcesses.Count > 0
            ? string.Join(", ", _update.BlockingProcesses)
            : string.Join(", ", _update.ProcessNames);

    // ---------------------------------------------------------------- buttons

    public bool ShowInstall => _update.State is not (UpdateState.Installing or UpdateState.Installed) && _localStatus is null;

    public bool IsInstallEnabled => ShowInstall && _isConnected && _update.State != UpdateState.Scheduled;

    public bool ShowDefer => DeferralOptions.Count > 0;

    public bool ShowNoMoreDeferrals =>
        !ShowDefer &&
        _update.MaxDeferrals > 0 &&
        _update.DeferralCount >= _update.MaxDeferrals &&
        _update.State is not (UpdateState.Installing or UpdateState.Installed);

    public string DeferralsUsedText =>
        _update.MaxDeferrals > 0 ? Strings.DeferralsUsed(_update.DeferralCount, _update.MaxDeferrals) : string.Empty;

    public bool ShowRemindMeLater =>
        !_update.Mandatory &&
        _update.State is UpdateState.Available or UpdateState.Deferred or UpdateState.WaitingForClose or UpdateState.Failed &&
        _localStatus is null;

    public bool ShowAnyAction => ShowInstall || ShowDefer || ShowRemindMeLater || ShowNoMoreDeferrals;

    // ---------------------------------------------------------------- refresh

    public void Update(PendingUpdate update, bool isConnected, string? localStatus)
    {
        _update = update;
        _isConnected = isConnected;
        _localStatus = localStatus;
        RebuildDeferralOptions();
        RaiseAll();
    }

    private void RebuildDeferralOptions()
    {
        var wanted = _update.CanDefer(DateTimeOffset.UtcNow) && _localStatus is null
            ? _update.DeferralOptionsMinutes.Where(m => m > 0).Distinct().ToList()
            : [];

        if (DeferralOptions.Select(o => o.Minutes).SequenceEqual(wanted)) return;
        DeferralOptions.Clear();
        foreach (var minutes in wanted)
            DeferralOptions.Add(new DeferOptionViewModel(minutes, m => _actions.Defer(_update, m)));
    }

    private void RaiseAll()
    {
        _installCommand.RaiseCanExecuteChanged();
        _dismissCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(
            nameof(DisplayName), nameof(VersionText), nameof(SourceBadge), nameof(ContextBadge),
            nameof(StatusText), nameof(Severity), nameof(DeadlineText), nameof(HasDeadline),
            nameof(ShowInstall), nameof(IsInstallEnabled), nameof(ShowDefer), nameof(ShowNoMoreDeferrals),
            nameof(DeferralsUsedText), nameof(ShowRemindMeLater), nameof(ShowAnyAction));
    }
}
