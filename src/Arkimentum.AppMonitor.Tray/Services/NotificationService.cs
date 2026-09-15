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

namespace Arkimentum.AppMonitor.Tray.Services;

/// <summary>
/// Windows toast notifications and their activation. Activation is handled through
/// <see cref="ToastNotificationManagerCompat.OnActivated"/>, so a toast clicked after the agent restarted is
/// still routed to the right action.
/// </summary>
public sealed class NotificationService : IHostedService
{
    private const string ToastGroup = "ArkimentumAppMonitor";

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
    private readonly Dispatcher _dispatcher;

    private bool _activationHooked;
    private DispatcherTimer? _coalesceTimer;
    private readonly List<NotifyMessage> _pendingAvailable = [];

    public NotificationService(
        ILogger<NotificationService> log,
        IpcClientService ipc,
        AgentStateStore store,
        IWindowService windows,
        CloseAppsCoordinator closeApps,
        Dispatcher dispatcher)
    {
        _log = log;
        _ipc = ipc;
        _store = store;
        _windows = windows;
        _closeApps = closeApps;
        _dispatcher = dispatcher;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ipc.MessageReceived += OnMessage;
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
        _dispatcher.Invoke(() =>
        {
            _coalesceTimer?.Stop();
            _coalesceTimer = null;
            _pendingAvailable.Clear();
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

            AddButtons(builder, notify, update);

            if (notify.Kind == NotificationKind.CloseApplications && update?.ForceCloseAtUtc is not null)
                builder.SetToastScenario(ToastScenario.Reminder);

            var tag = TagFor(update?.Key);
            builder.Show(toast =>
            {
                toast.Tag = tag;
                toast.Group = ToastGroup;
            });

            _log.LogInformation("Showed {Kind} toast for {App}", notify.Kind, update?.DisplayName ?? "-");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to show a {Kind} toast", notify.Kind);
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

    /// <summary>A toast tag is limited to 64 characters, so the update key is hashed into one.</summary>
    private static string TagFor(string? key)
    {
        if (string.IsNullOrEmpty(key)) return "general";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return System.Convert.ToHexString(hash, 0, 12);
    }

    // ---------------------------------------------------------------- activation

    private void OnToastActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        var argument = e.Argument ?? string.Empty;
        _log.LogInformation("Toast activated: {Argument}", argument);
        if (_dispatcher.CheckAccess()) Handle(argument);
        else _dispatcher.BeginInvoke(() => Handle(argument));
    }

    private void Handle(string argument)
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
