using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Threading;
using Arkimentum.AppMonitor.Ipc;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Tray.Infrastructure;
using Arkimentum.AppMonitor.Tray.Resources;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Toolkit.Uwp.Notifications;
using Windows.UI.Notifications;

namespace Arkimentum.AppMonitor.Tray.Services;

/// <summary>
/// Windows toast notifications and their activation. Activation is handled through
/// <see cref="ToastNotificationManagerCompat.OnActivated"/>, so a toast clicked after the agent restarted is
/// still routed to the right action.
/// </summary>
public sealed class NotificationService : IHostedService
{
    /// <summary>The group of every toast of the agent; the close-apps prompt (<see cref="CloseAppsCoordinator"/>) uses it too.</summary>
    internal const string ToastGroup = "ArkimentumAppMonitor";

    /// <summary>
    /// A scan that finds several updates sends one <see cref="NotifyMessage"/> per update within a few hundred
    /// milliseconds. In Quiet mode they are collected for this long and shown as a single summary toast; a lone
    /// update is still shown as its own toast, just this much later.
    /// </summary>
    private static readonly TimeSpan CoalesceWindow = TimeSpan.FromSeconds(2);

    /// <summary>How many application names the summary toast lists before it falls back to "and N more".</summary>
    private const int SummaryNamesShown = 4;

    private readonly ILogger<NotificationService> _log;
    private readonly IpcClientService _ipc;
    private readonly AgentStateStore _store;
    private readonly IWindowService _windows;
    private readonly CloseAppsCoordinator _closeApps;
    private readonly AppIconProvider _icons;
    private readonly Dispatcher _dispatcher;

    private bool _activationHooked;
    private DispatcherTimer? _coalesceTimer;
    private readonly List<NotifyMessage> _pendingAvailable = [];

    /// <summary>
    /// Updates whose "Installing" toast is still on screen. It stays up (reminder scenario) until the install ends, and is
    /// removed then unless a newer toast for the same update (installed, failed) has already replaced it.
    /// </summary>
    private readonly Dictionary<string, InstallToast> _installToasts = new(StringComparer.Ordinal);

    /// <summary>Windows drops the "Installing" toast after this even if the agent never saw the install end (it was restarted).</summary>
    private static readonly TimeSpan InstallToastLifetime = TimeSpan.FromHours(2);

    /// <summary>
    /// How often an "Installing" toast is refreshed without news from the service, so "2 min so far" keeps moving. The
    /// text changes once a minute at most; an unchanged text is not sent again.
    /// </summary>
    private static readonly TimeSpan InstallToastRefresh = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The data-binding names of the "Installing" toast's progress bar. The toast is shown with these placeholders and
    /// their first values, and then updated in place (<see cref="ToastNotifierCompat.Update(NotificationData, string, string)"/>
    /// with the toast's tag and group): no new toast, no sound, no banner, and the Hide button stays.
    /// </summary>
    private const string BindProgressValue = "progressValue";
    private const string BindProgressValueString = "progressValueString";
    private const string BindProgressStatus = "progressStatus";

    /// <summary>One "Installing" toast on screen: the sequence number of its last data, what that data said, and whether it is still there.</summary>
    private sealed class InstallToast
    {
        /// <summary>Windows ignores data whose sequence number is not higher than the toast's current one.</summary>
        public uint Sequence { get; set; } = 1;

        public string? Shown { get; set; }

        /// <summary>The user hid it (or Windows dropped it): it is never brought back, only removed when the install ends.</summary>
        public bool Gone { get; set; }
    }

    private readonly InstallProgressClamp _toastClamp = new();
    private DispatcherTimer? _installToastClock;
    private ToastNotifierCompat? _notifier;

    public NotificationService(
        ILogger<NotificationService> log,
        IpcClientService ipc,
        AgentStateStore store,
        IWindowService windows,
        CloseAppsCoordinator closeApps,
        AppIconProvider icons,
        Dispatcher dispatcher)
    {
        _log = log;
        _ipc = ipc;
        _store = store;
        _windows = windows;
        _closeApps = closeApps;
        _icons = icons;
        _dispatcher = dispatcher;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ipc.MessageReceived += OnMessage;
        _store.Changed += OnStoreChanged;
        try
        {
            // Subscribing registers the COM activator (HKCU\Software\Classes\CLSID\{guid}\LocalServer32).
            ToastNotificationManagerCompat.OnActivated += OnToastActivated;
            _activationHooked = true;
            // The library stops there, so the AUMID that ties the activator to this process is registered here.
            ToastRegistration.EnsureRegistered(AgentSettings.ToastAppUserModelId, AgentSettings.ProductName, _log);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not register the toast activation handler; toasts will not be interactive");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _ipc.MessageReceived -= OnMessage;
        _store.Changed -= OnStoreChanged;
        _dispatcher.Invoke(() =>
        {
            _coalesceTimer?.Stop();
            _coalesceTimer = null;
            _pendingAvailable.Clear();
            _installToastClock?.Stop();
        });
        if (_activationHooked)
        {
            try { ToastNotificationManagerCompat.OnActivated -= OnToastActivated; } catch { }
        }
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- showing

    private void OnMessage(IpcMessage message)
    {
        if (message is NotifyMessage notify) Receive(notify);
    }

    /// <summary>
    /// Entry point for one notification from the service. In Quiet mode "update available" messages are held back for
    /// <see cref="CoalesceWindow"/> so that a scan which found several updates produces one summary toast instead of a
    /// burst; everything that needs the user (deadline, close prompt, failure) is shown immediately.
    /// </summary>
    public void Receive(NotifyMessage notify)
    {
        if (notify.Kind != NotificationKind.UpdateAvailable || _store.Settings.NotificationMode != NotificationMode.Quiet)
        {
            Show(notify);
            return;
        }

        _pendingAvailable.RemoveAll(m => m.Update?.Key is { } k && k == notify.Update?.Key);
        _pendingAvailable.Add(notify);

        _coalesceTimer ??= new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = CoalesceWindow };
        _coalesceTimer.Tick -= OnCoalesceElapsed;
        _coalesceTimer.Tick += OnCoalesceElapsed;
        // Restart the window so updates still arriving join this batch.
        _coalesceTimer.Stop();
        _coalesceTimer.Start();
    }

    private void OnCoalesceElapsed(object? sender, EventArgs e)
    {
        _coalesceTimer?.Stop();
        var batch = _pendingAvailable.ToList();
        _pendingAvailable.Clear();
        if (batch.Count == 0) return;
        if (batch.Count == 1) { Show(batch[0]); return; }
        ShowSummary(batch);
    }

    /// <summary>One toast for a whole batch: the count, the application names and a Details button opening the window.</summary>
    private void ShowSummary(IReadOnlyList<NotifyMessage> batch)
    {
        if (!_store.Settings.NotificationsEnabled)
        {
            _log.LogInformation("Suppressed a summary notification for {Count} updates: notifications are disabled", batch.Count);
            return;
        }

        var names = batch.Select(m => m.Update?.DisplayName).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).ToList();
        try
        {
            var builder = new ToastContentBuilder()
                .AddArgument(ToastAction.ArgumentAction, ToastAction.Details);
            builder.AddText(Strings.ToastSummaryTitle(batch.Count));
            if (names.Count > 0) builder.AddText(Strings.ToastSummaryBody(names, SummaryNamesShown));
            builder.AddAttributionText(Strings.ProductName);
            AddDetails(builder);

            builder.Show(toast =>
            {
                toast.Tag = SummaryTag;
                toast.Group = ToastGroup;
            });
            _log.LogInformation("Showed a summary toast for {Count} updates: {Apps}", batch.Count, string.Join(", ", names));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to show the summary toast for {Count} updates", batch.Count);
        }
    }

    public void Show(NotifyMessage notify)
    {
        var update = notify.Update;
        if (!ShouldShow(notify))
        {
            _log.LogInformation("Suppressed {Kind} notification for {App}: notifications are disabled",
                notify.Kind, update?.DisplayName ?? "-");
            return;
        }

        try
        {
            var builder = new ToastContentBuilder()
                .AddArgument(ToastAction.ArgumentAction, ToastAction.Details);
            if (update is not null) builder.AddArgument(ToastAction.ArgumentKey, update.Key);

            builder.AddText(string.IsNullOrWhiteSpace(notify.Title) ? Strings.ProductName : notify.Title);
            if (!string.IsNullOrWhiteSpace(notify.Body)) builder.AddText(notify.Body);
            builder.AddAttributionText(Strings.ProductName);
            AddAppLogo(builder, update);

            AddButtons(builder, notify, update);

            if (notify.Kind == NotificationKind.CloseApplications && update?.ForceCloseAtUtc is not null)
                builder.SetToastScenario(ToastScenario.Reminder);

            // "Installing" stays on screen for as long as the install runs: the reminder scenario keeps a toast up until
            // the user acts on it, and its only button is Hide. It is silent (the reminder sound would be wrong for
            // progress). Its bar and the line under it are bound data: the phase, the download and the time so far
            // (InstallProgressText), updated in place while the toast is up; indeterminate unless a download visibly runs.
            var installing = notify.Kind == NotificationKind.Installing && update is not null;
            InstallToastContent? content = null;
            if (installing)
            {
                // The title is given explicitly: the library turns a field left null into a data-binding placeholder,
                // and without bound data Windows shows the placeholder's name ("progressBarTitle_0") on the toast. The
                // other three are bound, and the toast is shown with their values.
                builder.AddVisualChild(new AdaptiveProgressBar
                {
                    Title = string.Empty,
                    Value = new BindableProgressBarValue(BindProgressValue),
                    ValueStringOverride = new BindableString(BindProgressValueString),
                    Status = new BindableString(BindProgressStatus),
                });
                builder.SetToastScenario(ToastScenario.Reminder);
                builder.AddAudio(new ToastAudio { Silent = true });
                // The message usually comes just before the state broadcast that marks the update as installing.
                content = InstallToastContentFor(_store.Find(update!.Key) is { State: UpdateState.Installing } known ? known : update);
            }

            var tag = TagFor(update?.Key);
            builder.Show(toast =>
            {
                toast.Tag = tag;
                toast.Group = ToastGroup;
                if (installing)
                {
                    toast.ExpirationTime = DateTimeOffset.Now + InstallToastLifetime;
                    toast.Data = content!.ToData(1);
                }
            });

            // Every toast of an update shares its tag, so this one replaced whatever that update showed before.
            if (update is not null)
            {
                if (installing) _installToasts[update.Key] = new InstallToast { Shown = content!.Signature };
                else ForgetInstallToast(update.Key);
                UpdateInstallToastClock();
            }

            _log.LogInformation("Showed {Kind} toast for {App}", notify.Kind, update?.DisplayName ?? "-");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to show a {Kind} toast", notify.Kind);
        }
    }

    /// <summary>
    /// Takes an "Installing" toast down once its install is no longer running. A success without an "installed" toast
    /// (the default) would otherwise leave it up until it expires. A result toast that already replaced it has dropped
    /// the key in <see cref="Show"/>, so this never removes the result.
    /// </summary>
    private void OnStoreChanged()
    {
        // Icons are resolved ahead of the toasts that will want them; cached per application, so this is cheap.
        foreach (var update in _store.Updates) _icons.Prefetch(AppIconRequest.For(update));

        if (_installToasts.Count == 0) return;
        foreach (var key in _installToasts.Keys.ToList())
        {
            if (_store.Find(key) is { State: UpdateState.Installing or UpdateState.Scheduled }) continue;
            ForgetInstallToast(key);
            try
            {
                ToastNotificationManagerCompat.History.Remove(TagFor(key), ToastGroup);
                _log.LogInformation("Removed the Installing toast for {Key}: the install is no longer running", key);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Could not remove the Installing toast for {Key}", key);
            }
        }
        RefreshInstallToasts();
        UpdateInstallToastClock();
    }

    // ---------------------------------------------------------------- the "Installing" toast's progress

    /// <summary>The bound values of an "Installing" toast, and a signature to tell whether they changed.</summary>
    private sealed record InstallToastContent(string Value, string ValueString, string Status)
    {
        public string Signature => $"{Value}|{ValueString}|{Status}";

        public NotificationData ToData(uint sequence) => new(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [BindProgressValue] = Value,
                [BindProgressValueString] = ValueString,
                [BindProgressStatus] = Status,
            },
            sequence);
    }

    /// <summary>
    /// What the toast says about an install: the agent's own reading of a user-context install it runs, otherwise the
    /// service's. With nothing known (an older service) it is today's plain "In progress" with an indeterminate bar.
    /// </summary>
    private InstallToastContent InstallToastContentFor(PendingUpdate update)
    {
        var local = _store.GetLocalProgress(update.Key);
        var view = InstallProgressText.Format(update, DateTimeOffset.UtcNow, local);
        view = _toastClamp.Apply(update.Key, local?.Phase ?? update.InstallPhase, local?.DownloadTotalBytes ?? update.DownloadTotalBytes, view);
        return new InstallToastContent(
            view.Value is { } v ? v.ToString("0.###", CultureInfo.InvariantCulture) : "indeterminate",
            view.PercentText,
            view.Status ?? Strings.ToastInstallingStatus);
    }

    /// <summary>
    /// Brings every "Installing" toast still on screen up to date, in place. Only changed text is sent; a toast the user
    /// hid answers "not found" and is left alone from then on (an update never shows it again).
    /// </summary>
    private void RefreshInstallToasts()
    {
        foreach (var (key, toast) in _installToasts)
        {
            if (toast.Gone || _store.Find(key) is not { State: UpdateState.Installing } update) continue;
            var content = InstallToastContentFor(update);
            if (content.Signature == toast.Shown) continue;
            try
            {
                _notifier ??= ToastNotificationManagerCompat.CreateToastNotifier();
                var result = _notifier.Update(content.ToData(++toast.Sequence), TagFor(key), ToastGroup);
                if (result == NotificationUpdateResult.NotificationNotFound)
                {
                    toast.Gone = true;
                    _log.LogDebug("The Installing toast for {Key} is no longer on screen; not updating it", key);
                }
                else
                {
                    toast.Shown = content.Signature;
                    _log.LogDebug("Updated the Installing toast for {Key}: {Status} ({Result})", key, content.Status, result);
                }
            }
            catch (Exception ex)
            {
                toast.Gone = true;
                _log.LogDebug(ex, "Could not update the Installing toast for {Key}", key);
            }
        }
    }

    private void ForgetInstallToast(string key)
    {
        _installToasts.Remove(key);
        _toastClamp.Forget(key);
    }

    /// <summary>Ticks while an "Installing" toast is on screen, so its elapsed time moves; stopped otherwise.</summary>
    private void UpdateInstallToastClock()
    {
        var needed = _installToasts.Values.Any(t => !t.Gone);
        if (!needed)
        {
            _installToastClock?.Stop();
            return;
        }
        if (_installToastClock is null)
        {
            _installToastClock = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = InstallToastRefresh };
            _installToastClock.Tick += (_, _) =>
            {
                RefreshInstallToasts();
                UpdateInstallToastClock();
            };
        }
        if (!_installToastClock.IsEnabled) _installToastClock.Start();
    }

    /// <summary>
    /// A plain toast answering something the user just did from the tray menu (whose window is usually closed). Shown
    /// even when notifications are switched off, because the user asked for it; a newer answer replaces an older one.
    /// </summary>
    public void ShowFeedback(string title, string? body)
    {
        try
        {
            var builder = new ToastContentBuilder()
                .AddArgument(ToastAction.ArgumentAction, ToastAction.Details)
                .AddText(title);
            if (!string.IsNullOrWhiteSpace(body)) builder.AddText(body);
            builder.AddAttributionText(Strings.ProductName);
            builder.Show(toast =>
            {
                toast.Tag = FeedbackTag;
                toast.Group = ToastGroup;
            });
            _log.LogInformation("Showed feedback toast: {Title} - {Body}", title, body ?? "-");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to show a feedback toast");
        }
    }

    private const string FeedbackTag = "feedback";

    /// <summary>How long a toast waits for an icon that is not cached yet; the next toast of that application has it.</summary>
    private static readonly TimeSpan AppLogoWait = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Puts the application's own icon on a toast about one update, when the icon cache has a PNG for it. Without one the
    /// toast keeps the agent's logo; an icon never stops a toast from showing.
    /// </summary>
    private void AddAppLogo(ToastContentBuilder builder, PendingUpdate? update) => AddAppLogo(builder, update, _icons, _log);

    /// <summary>The same for a toast built elsewhere (the close-apps prompt).</summary>
    internal static void AddAppLogo(ToastContentBuilder builder, PendingUpdate? update, AppIconProvider icons, ILogger log)
    {
        if (update is null) return;
        try
        {
            if (icons.ToastLogoPath(AppIconRequest.For(update), AppLogoWait) is { } png)
                builder.AddAppLogoOverride(new Uri(png), ToastGenericAppLogoCrop.Default);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "No app logo on the toast for {App}", update.DisplayName);
        }
    }

    private bool ShouldShow(NotifyMessage notify)
    {
        if (_store.Settings.NotificationsEnabled) return true;
        // A pending forced close must reach the user even when notifications are switched off.
        return notify.Kind == NotificationKind.CloseApplications && notify.Update?.ForceCloseAtUtc is not null;
    }

    private static void AddButtons(ToastContentBuilder builder, NotifyMessage notify, PendingUpdate? update)
    {
        var now = DateTimeOffset.UtcNow;
        switch (notify.Kind)
        {
            case NotificationKind.UpdateAvailable:
                AddInstall(builder);
                AddDefer(builder, update, now);
                AddDetails(builder);
                break;

            case NotificationKind.DeadlineApproaching:
                AddInstall(builder);
                AddDetails(builder);
                break;

            case NotificationKind.CloseApplications:
                builder.AddButton(new ToastButton()
                    .SetContent(Strings.ToastButtonCloseAndUpdate)
                    .AddArgument(ToastAction.ArgumentAction, ToastAction.CloseApps));
                AddDefer(builder, update, now);
                AddDetails(builder);
                break;

            case NotificationKind.Installing:
                // Hides the toast only; the install carries on, and its result still gets a toast of its own.
                builder.AddButton(new ToastButtonDismiss(Strings.ToastButtonHide));
                break;

            default:
                AddDetails(builder);
                break;
        }
    }

    private static void AddInstall(ToastContentBuilder builder) =>
        builder.AddButton(new ToastButton()
            .SetContent(Strings.ToastButtonInstallNow)
            .AddArgument(ToastAction.ArgumentAction, ToastAction.Install));

    private static void AddDetails(ToastContentBuilder builder) =>
        builder.AddButton(new ToastButton()
            .SetContent(Strings.ToastButtonDetails)
            .AddArgument(ToastAction.ArgumentAction, ToastAction.Details));

    private static void AddDefer(ToastContentBuilder builder, PendingUpdate? update, DateTimeOffset now)
    {
        if (update is null || !update.CanDefer(now)) return;
        var minutes = update.DeferralOptionsMinutes.FirstOrDefault();
        if (minutes <= 0) return;
        builder.AddButton(new ToastButton()
            .SetContent(Strings.ToastButtonDefer(TimeFormat.Duration(minutes)))
            .AddArgument(ToastAction.ArgumentAction, ToastAction.Defer)
            .AddArgument(ToastAction.ArgumentMinutes, minutes));
    }

    /// <summary>Tag of the summary toast, so a newer summary replaces the previous one instead of stacking.</summary>
    private const string SummaryTag = "summary";

    /// <summary>
    /// A toast tag is limited to 64 characters, so the update key is hashed into one. Every toast of one update shares
    /// it, the close-apps prompt included, so a newer one replaces the older instead of stacking.
    /// </summary>
    internal static string TagFor(string? key)
    {
        if (string.IsNullOrEmpty(key)) return "general";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return System.Convert.ToHexString(hash, 0, 12);
    }

    // ---------------------------------------------------------------- activation

    private void OnToastActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        var argument = e.Argument ?? string.Empty;
        // The close-apps prompt's selection box of deferral options; read here, because UserInput is only valid now.
        string? selectedMinutes = null;
        try
        {
            if (e.UserInput is { } input && input.TryGetValue(ToastAction.InputDeferMinutes, out var value)) selectedMinutes = value as string;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not read the toast's user input");
        }
        _log.LogInformation("Toast activated: {Argument}{Input}", argument, selectedMinutes is null ? string.Empty : $" ({ToastAction.InputDeferMinutes}={selectedMinutes})");
        if (_dispatcher.CheckAccess()) Handle(argument, selectedMinutes);
        else _dispatcher.BeginInvoke(() => Handle(argument, selectedMinutes));
    }

    private void Handle(string argument, string? selectedMinutes = null)
    {
        try
        {
            var args = ToastArguments.Parse(argument);
            var action = args.Contains(ToastAction.ArgumentAction)
                ? args.Get(ToastAction.ArgumentAction)
                : ToastAction.Details;
            var key = args.Contains(ToastAction.ArgumentKey) ? args.Get(ToastAction.ArgumentKey) : null;

            switch (action)
            {
                // The close-apps prompt belongs to the coordinator: it holds the prompt's timer and sends the one answer.
                case ToastAction.PromptCloseAndUpdate or ToastAction.PromptDefer or ToastAction.PromptNotNow or ToastAction.PromptOpen
                    when key is not null:
                    var promptMinutes = args.Contains(ToastAction.ArgumentMinutes)
                        ? int.Parse(args.Get(ToastAction.ArgumentMinutes), CultureInfo.InvariantCulture)
                        : int.TryParse(selectedMinutes, NumberStyles.Integer, CultureInfo.InvariantCulture, out var picked) ? picked : 0;
                    _closeApps.OnToastAction(action, key, promptMinutes);
                    break;

                case ToastAction.Install when key is not null:
                    _log.LogInformation("User chose Install now from a toast for {Key}", key);
                    _ = _ipc.InstallNowAsync(key);
                    break;

                case ToastAction.Defer when key is not null:
                    var minutes = args.Contains(ToastAction.ArgumentMinutes)
                        ? int.Parse(args.Get(ToastAction.ArgumentMinutes), CultureInfo.InvariantCulture)
                        : 0;
                    if (minutes > 0)
                    {
                        _log.LogInformation("User deferred {Key} by {Minutes} minutes from a toast", key, minutes);
                        _ = _ipc.DeferAsync(key, minutes);
                    }
                    break;

                case ToastAction.CloseApps when key is not null:
                    // An older toast's "Close apps" button: it opened the dialog, and now shows the prompt instead.
                    _log.LogInformation("User chose Close apps from a toast for {Key}", key);
                    var update = _store.Find(key);
                    if (update is not null) _closeApps.ShowFor(update);
                    else _windows.ShowMain();
                    break;

                default:
                    _windows.ShowMain();
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not handle toast activation {Argument}", argument);
            _windows.ShowMain();
        }
    }
}
