using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Arkimentum.AppMonitor.Ipc;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Native;
using Arkimentum.AppMonitor.Tray.Infrastructure;
using Arkimentum.AppMonitor.Tray.Resources;
using Arkimentum.AppMonitor.Tray.ViewModels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Toolkit.Uwp.Notifications;
using Windows.UI.Notifications;

namespace Arkimentum.AppMonitor.Tray.Services;

/// <summary>
/// Owns the "Close apps to update X" prompts - at most one per update key, each a toast with the dialog's old choices
/// as buttons - and performs the session-bound process closing the service asks for. A prompt nobody answers within
/// <see cref="CloseAppsPrompt.AnswerTimeout"/> is taken as "Not now" and removed; the rules are in
/// <see cref="CloseAppsPrompt"/>.
/// </summary>
public sealed class CloseAppsCoordinator : IHostedService, ICloseAppsLauncher
{
    private static readonly TimeSpan GracefulWait = TimeSpan.FromSeconds(30);

    /// <summary>Windows drops the "your apps are being closed now" toast after this, should nothing replace it.</summary>
    private static readonly TimeSpan ClosingToastLifetime = TimeSpan.FromMinutes(10);

    private readonly ILogger<CloseAppsCoordinator> _log;
    private readonly IpcClientService _ipc;
    private readonly AgentStateStore _store;
    private readonly IWindowService _windows;
    private readonly AppIconProvider _icons;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, OpenPrompt> _prompts = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();

    /// <summary>One prompt on screen, waiting for its answer.</summary>
    private sealed class OpenPrompt(PendingUpdate update, CloseAppsPromptContent content, DispatcherTimer timer)
    {
        public PendingUpdate Update { get; set; } = update;

        public CloseAppsPromptContent Content { get; } = content;

        /// <summary>Runs out after <see cref="CloseAppsPrompt.AnswerTimeout"/>: no answer is "Not now".</summary>
        public DispatcherTimer Timer { get; } = timer;

        /// <summary>The toast as shown, so exactly this one is removed - never a newer toast with the same tag.</summary>
        public ToastNotification? Toast { get; set; }
    }

    public CloseAppsCoordinator(
        ILogger<CloseAppsCoordinator> log,
        IpcClientService ipc,
        AgentStateStore store,
        IWindowService windows,
        AppIconProvider icons,
        Dispatcher dispatcher)
    {
        _log = log;
        _ipc = ipc;
        _store = store;
        _windows = windows;
        _icons = icons;
        _dispatcher = dispatcher;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ipc.MessageReceived += OnMessage;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _ipc.MessageReceived -= OnMessage;
        _cts.Cancel();
        // The agent is going away, not the user declining: take the prompts down without sending an answer. The
        // service asks the next agent again (it re-sends outstanding prompts when an agent connects).
        _dispatcher.Invoke(() =>
        {
            foreach (var key in _prompts.Keys.ToList()) Withdraw(key, "the agent is stopping");
        });
        _cts.Dispose();
        return Task.CompletedTask;
    }

    private void OnMessage(IpcMessage message)
    {
        switch (message)
        {
            case PromptCloseMessage prompt:
                Show(prompt.Update, userAsked: false);
                break;
            case CloseProcessesMessage close:
                _ = CloseProcessesAsync(close);
                break;
            case StateMessage:
                PruneFinishedPrompts();
                break;
        }
    }

    /// <summary>
    /// Shows the prompt for an update the tray already holds. Called from the update card's "Close apps and update"
    /// button and from an older toast's "Close apps" button, which is how a user whose prompt went away gets it back
    /// without waiting for the service to ask again. The service's own <see cref="PromptCloseMessage"/> goes through
    /// <see cref="Show"/> directly.
    /// </summary>
    public void ShowFor(PendingUpdate update) => Show(update, userAsked: true);

    /// <summary>
    /// Shows the prompt, or leaves the one already on screen alone when the service merely repeats it and it would say
    /// the same thing. A user asking for it pops it up again (and restarts its half minute); so does a prompt whose
    /// text changed - a forced close newly scheduled, a process that started in another session.
    /// </summary>
    private void Show(PendingUpdate? update, bool userAsked)
    {
        if (update is null) { _log.LogWarning("Ignoring a request to show the close-apps prompt for a null update"); return; }
        var key = update.Key;
        var now = DateTimeOffset.UtcNow;

        CloseAppsPromptContent content;
        try
        {
            content = CloseAppsPrompt.Compose(update, AppInfo.SessionId, ProcessDisplay.Friendly, now);
        }
        catch (Exception ex)
        {
            // Never leave the service without an answer: an unanswered prompt parks the update in WaitingForClose.
            _log.LogError(ex, "Could not compose the close-apps prompt for {Key} ({App}); blocking detail: {Detail}",
                key, update.DisplayName, Describe(update));
            NotNow(update, CloseAppsPromptEnd.NotShown);
            return;
        }

        if (_prompts.TryGetValue(key, out var open))
        {
            open.Update = update;
            if (!userAsked && open.Content.Signature == content.Signature)
            {
                _log.LogDebug("The close-apps prompt for {App} is already on screen and unchanged", update.DisplayName);
                return;
            }
            Withdraw(key, userAsked ? "the user asked for it again" : "what it says changed");
        }

        var timer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = CloseAppsPrompt.AnswerTimeout };
        var prompt = new OpenPrompt(update, content, timer);
        timer.Tick += (_, _) =>
        {
            // Only this prompt's own timer may end it; a newer prompt for the same key has a timer of its own.
            if (_prompts.TryGetValue(key, out var current) && ReferenceEquals(current, prompt)) Resolve(key, CloseAppsPromptEnd.Timeout);
            else timer.Stop();
        };
        _prompts[key] = prompt;

        if (!TryShowToast(prompt))
        {
            Resolve(key, CloseAppsPromptEnd.NotShown);
            return;
        }
        timer.Start();
        _log.LogInformation("Showing the close-apps prompt for {App} (blocking: {Processes}{Force}); no answer within {Seconds:F0} s counts as Not now",
            update.DisplayName, Describe(update),
            content.ForceCloseAtUtc is { } at ? $"; forced close at {TimeFormat.Clock(at)}" : string.Empty,
            CloseAppsPrompt.AnswerTimeout.TotalSeconds);
    }

    /// <summary>
    /// Shows the prompt's toast. The reminder scenario keeps it on screen until it is answered or this agent removes it,
    /// so the half minute is spent in front of the user rather than in the notification centre.
    /// </summary>
    private bool TryShowToast(OpenPrompt prompt)
    {
        var update = prompt.Update;
        var content = prompt.Content;
        var key = update.Key;

        try
        {
            // Toasts switched off for the agent (in Settings or by policy) are dropped silently by Windows; say so in
            // the log, because the user is then never asked and every prompt ends as "Not now".
            var setting = ToastNotificationManagerCompat.CreateToastNotifier().Setting;
            if (setting != NotificationSetting.Enabled)
            {
                _log.LogWarning("Cannot show the close-apps prompt for {App}: Windows reports the agent's notifications as {Setting}",
                    update.DisplayName, setting);
                return false;
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not read the notification setting; showing the close-apps prompt anyway");
        }

        try
        {
            var builder = new ToastContentBuilder()
                .AddArgument(ToastAction.ArgumentAction, ToastAction.PromptOpen)
                .AddArgument(ToastAction.ArgumentKey, key)
                .AddText(content.Title)
                .AddText(content.Body);
            if (content.Detail is { } detail) builder.AddText(detail);
            NotificationService.AddAppLogo(builder, update, _icons, _log);

            builder.AddButton(new ToastButton()
                .SetContent(Strings.ToastButtonCloseAndUpdate)
                .AddArgument(ToastAction.ArgumentAction, ToastAction.PromptCloseAndUpdate));

            if (content.DeferAsSelection)
            {
                builder.AddComboBox(ToastAction.InputDeferMinutes,
                    content.DeferralMinutes[0].ToString(CultureInfo.InvariantCulture),
                    [.. content.DeferralMinutes.Select(m => (m.ToString(CultureInfo.InvariantCulture), TimeFormat.Duration(m)))]);
                builder.AddButton(new ToastButton()
                    .SetContent(Strings.Defer)
                    .AddArgument(ToastAction.ArgumentAction, ToastAction.PromptDefer));
            }
            else if (content.DeferralMinutes.Count == 1)
            {
                var minutes = content.DeferralMinutes[0];
                builder.AddButton(new ToastButton()
                    .SetContent(Strings.ToastButtonDefer(TimeFormat.Duration(minutes)))
                    .AddArgument(ToastAction.ArgumentAction, ToastAction.PromptDefer)
                    .AddArgument(ToastAction.ArgumentMinutes, minutes));
            }

            if (content.ShowNotNow)
                builder.AddButton(new ToastButton()
                    .SetContent(Strings.CloseAppsNotNow)
                    .AddArgument(ToastAction.ArgumentAction, ToastAction.PromptNotNow));

            builder.SetToastScenario(ToastScenario.Reminder);

            builder.Show(toast =>
            {
                toast.Tag = NotificationService.TagFor(key);
                toast.Group = NotificationService.ToastGroup;
                // A backstop only: the agent removes the toast itself when the half minute is up.
                toast.ExpirationTime = DateTimeOffset.Now + CloseAppsPrompt.ToastExpiry;
                toast.Dismissed += (sender, e) => OnToastDismissed(key, sender, e.Reason);
                toast.Failed += (sender, e) => OnToastFailed(key, sender, e.ErrorCode);
                prompt.Toast = toast;
            });
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not show the close-apps prompt for {Key} ({App}); blocking detail: {Detail}",
                key, update.DisplayName, Describe(update));
            return false;
        }
    }

    /// <summary>
    /// The X on the toast (or a swipe) is an answer like the dialog's X was: "Not now". Removals by this agent
    /// (<see cref="ToastDismissalReason.ApplicationHidden"/>) and Windows' own expiry are not the user saying anything.
    /// </summary>
    private void OnToastDismissed(string key, ToastNotification toast, ToastDismissalReason reason)
    {
        if (reason != ToastDismissalReason.UserCanceled) return;
        _dispatcher.BeginInvoke(() =>
        {
            if (_prompts.TryGetValue(key, out var prompt) && ReferenceEquals(prompt.Toast, toast))
                Resolve(key, CloseAppsPromptEnd.ToastClosed);
        });
    }

    private void OnToastFailed(string key, ToastNotification toast, Exception? error)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (!_prompts.TryGetValue(key, out var prompt) || !ReferenceEquals(prompt.Toast, toast)) return;
            _log.LogWarning("Windows failed to show the close-apps prompt for {App}: {Error}", prompt.Update.DisplayName, error?.Message ?? "no detail");
            Resolve(key, CloseAppsPromptEnd.NotShown);
        });
    }

    /// <summary>
    /// A click on one of the prompt's toasts, routed here by <see cref="NotificationService"/>. <paramref name="minutes"/>
    /// is the deferral the button carries, or the one picked in the selection box; 0 when neither is there.
    /// </summary>
    public void OnToastAction(string action, string key, int minutes)
    {
        switch (action)
        {
            case ToastAction.PromptCloseAndUpdate:
                Resolve(key, CloseAppsPromptEnd.Button, CloseAppsChoice.CloseAndUpdate);
                break;
            case ToastAction.PromptDefer:
                Resolve(key, CloseAppsPromptEnd.Button, CloseAppsChoice.Defer, minutes);
                break;
            case ToastAction.PromptNotNow:
                Resolve(key, CloseAppsPromptEnd.Button, CloseAppsChoice.NotNow);
                break;
            case ToastAction.PromptOpen:
                _windows.ShowMain();
                Resolve(key, CloseAppsPromptEnd.OpenedWindow);
                break;
            default:
                _log.LogWarning("Unknown close-apps prompt action {Action} for {Key}", action, key);
                break;
        }
    }

    /// <summary>
    /// Ends the prompt for <paramref name="key"/> with exactly one answer. The toast is removed, the timer stopped, and
    /// the answer carried out - every way that is not a button being "Not now" (<see cref="CloseAppsPrompt.ChoiceFor"/>).
    /// </summary>
    private void Resolve(string key, CloseAppsPromptEnd end, CloseAppsChoice? clicked = null, int minutes = 0)
    {
        var now = DateTimeOffset.UtcNow;
        if (!_prompts.Remove(key, out var prompt))
        {
            // Nothing is waiting: the prompt already timed out, or this is a toast that outlived an agent restart, or
            // the notice left behind for a forced close. An explicit "close" or "defer" still means what it says; any
            // other way out has nothing left to answer.
            if (end == CloseAppsPromptEnd.Button && clicked is CloseAppsChoice.CloseAndUpdate or CloseAppsChoice.Defer
                && _store.Find(key) is { } late && !CloseAppsPrompt.IsWithdrawn(late, now))
            {
                _log.LogInformation("Close-apps choice {Choice} for {App} arrived with no prompt waiting; carrying it out", clicked, late.DisplayName);
                Carry(late, clicked.Value, end, minutes, late.DeferralOptionsMinutes);
            }
            else
            {
                _log.LogInformation("Ignoring {End} for {Key}: no close-apps prompt is waiting for it", end, key);
            }
            return;
        }

        prompt.Timer.Stop();
        Hide(prompt);
        var update = _store.Find(key) ?? prompt.Update;
        var choice = CloseAppsPrompt.ChoiceFor(end, clicked);
        Carry(update, choice, end, minutes, prompt.Content.DeferralMinutes);

        if (CloseAppsPrompt.LeavesForcedCloseNotice(update, end, now)) ShowForcedCloseNotice(update);
    }

    private void Carry(PendingUpdate update, CloseAppsChoice choice, CloseAppsPromptEnd end, int minutes, IReadOnlyList<int> offered)
    {
        switch (choice)
        {
            case CloseAppsChoice.CloseAndUpdate:
                _ = CloseAndUpdateAsync(update);
                break;
            case CloseAppsChoice.Defer:
                // The selection box always has a value, but a toast from an older agent may carry neither it nor minutes.
                if (minutes <= 0) minutes = offered.FirstOrDefault();
                if (minutes <= 0)
                {
                    _log.LogWarning("A deferral for {App} came without a length; taking it as Not now", update.DisplayName);
                    NotNow(update, end);
                    break;
                }
                _log.LogInformation("User deferred {App} by {Minutes} minutes from the close-apps prompt", update.DisplayName, minutes);
                _ = _ipc.DeferAsync(update.Key, minutes);
                break;
            default:
                NotNow(update, end);
                break;
        }
    }

    /// <summary>
    /// "Not now" - pressed, or any other way the prompt ended without a choice. The log says which, so a timeout is
    /// never mistaken for a click; what is sent is the same either way.
    /// </summary>
    private void NotNow(PendingUpdate update, CloseAppsPromptEnd end)
    {
        switch (end)
        {
            case CloseAppsPromptEnd.Button:
                _log.LogInformation("User chose Not now for {App}", update.DisplayName);
                break;
            case CloseAppsPromptEnd.Timeout:
                _log.LogInformation("No answer to the close-apps prompt for {App} within {Seconds:F0} s; taking it as Not now",
                    update.DisplayName, CloseAppsPrompt.AnswerTimeout.TotalSeconds);
                break;
            case CloseAppsPromptEnd.ToastClosed:
                _log.LogInformation("User closed the close-apps prompt for {App}; taking it as Not now", update.DisplayName);
                break;
            case CloseAppsPromptEnd.OpenedWindow:
                _log.LogInformation("User opened the main window from the close-apps prompt for {App}; taking it as Not now", update.DisplayName);
                break;
            default:
                _log.LogWarning("The close-apps prompt for {App} could not be shown; taking it as Not now", update.DisplayName);
                break;
        }

        // Mandatory updates cannot be dismissed; the prompt simply goes away until the service asks again at the
        // notification interval. A scheduled forced close is not touched either way.
        if (CloseAppsPrompt.NotNowSendsDismissal(update)) _ = _ipc.DismissAsync(update.Key);
        else _log.LogDebug("{App} is mandatory: nothing to dismiss; the service asks again at the notification interval", update.DisplayName);
    }

    /// <summary>
    /// What is left on screen after an unanswered prompt with a forced close on its way: a silent entry in the
    /// notification centre saying when, with the button to do it now. It expires when the close happens.
    /// </summary>
    private void ShowForcedCloseNotice(PendingUpdate update)
    {
        if (update.ForceCloseAtUtc is not { } at) return;
        try
        {
            var builder = new ToastContentBuilder()
                .AddArgument(ToastAction.ArgumentAction, ToastAction.Details)
                .AddArgument(ToastAction.ArgumentKey, update.Key)
                .AddText(Strings.CloseAppsTitle(string.IsNullOrWhiteSpace(update.DisplayName) ? update.AppId : update.DisplayName))
                .AddText($"{Strings.CloseAppsForcedCloseAt(TimeFormat.Clock(at))} {Strings.CloseAppsSaveHint}");
            NotificationService.AddAppLogo(builder, update, _icons, _log);
            builder.AddButton(new ToastButton()
                .SetContent(Strings.ToastButtonCloseAndUpdate)
                .AddArgument(ToastAction.ArgumentAction, ToastAction.PromptCloseAndUpdate));
            builder.Show(toast =>
            {
                toast.Tag = NotificationService.TagFor(update.Key);
                toast.Group = NotificationService.ToastGroup;
                // Straight to the notification centre: the prompt has just been on screen for half a minute.
                toast.SuppressPopup = true;
                toast.ExpirationTime = at.ToLocalTime();
            });
            _log.LogInformation("Left a notice for {App}: its apps are closed automatically at {At}", update.DisplayName, TimeFormat.Clock(at));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not leave the forced-close notice for {App}", update.DisplayName);
        }
    }

    /// <summary>The service is closing the apps for a forced close: the moment they vanish is announced, not silent.</summary>
    private void ShowClosingNow(string updateKey)
    {
        var update = _store.Find(updateKey);
        try
        {
            var builder = new ToastContentBuilder()
                .AddArgument(ToastAction.ArgumentAction, ToastAction.Details)
                .AddArgument(ToastAction.ArgumentKey, updateKey)
                .AddText(Strings.CloseAppsTitle(update is null ? updateKey : string.IsNullOrWhiteSpace(update.DisplayName) ? update.AppId : update.DisplayName))
                .AddText(Strings.CloseAppsCountdownElapsed);
            NotificationService.AddAppLogo(builder, update, _icons, _log);
            builder.Show(toast =>
            {
                toast.Tag = NotificationService.TagFor(updateKey);
                toast.Group = NotificationService.ToastGroup;
                toast.ExpirationTime = DateTimeOffset.Now + ClosingToastLifetime;
            });
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not announce the forced close of {Key}", updateKey);
        }
    }

    /// <summary>Takes a prompt down without answering it: its update moved on, it is being replaced, or the agent stops.</summary>
    private void Withdraw(string key, string reason)
    {
        if (!_prompts.Remove(key, out var prompt)) return;
        prompt.Timer.Stop();
        Hide(prompt);
        _log.LogInformation("Took down the close-apps prompt for {App}: {Reason}", prompt.Update.DisplayName, reason);
    }

    /// <summary>
    /// Removes exactly the toast this prompt showed. By tag would be wrong: the update's next toast ("Installing")
    /// shares the tag and may already have replaced it.
    /// </summary>
    private void Hide(OpenPrompt prompt)
    {
        if (prompt.Toast is not { } toast) return;
        prompt.Toast = null;
        try { ToastNotificationManagerCompat.CreateToastNotifier().Hide(toast); }
        catch (Exception ex) { _log.LogDebug(ex, "Removing the close-apps prompt for {Key} failed (it is probably gone already)", prompt.Update.Key); }
    }

    /// <summary>Takes down prompts whose update has moved on (installing, installed, gone, or deferred meanwhile).</summary>
    private void PruneFinishedPrompts()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var key in _prompts.Keys.ToList())
        {
            var latest = _store.Find(key);
            // The update moved on by itself, or the user answered elsewhere; the prompt is not declined, so send nothing.
            if (CloseAppsPrompt.IsWithdrawn(latest, now)) Withdraw(key, "the update moved on");
            else _prompts[key].Update = latest!;
        }
    }

    /// <summary>What the service said is blocking this update, for a log line. Never throws.</summary>
    private static string Describe(PendingUpdate update)
    {
        try
        {
            var names = string.Join(", ", BlockingProcessSummary.NamesFor(update));
            var detail = update.BlockingDetails is { Count: > 0 } d ? BlockingProcessInfo.Describe(d) : "none";
            return $"{(names.Length == 0 ? "none" : names)} [{detail}]";
        }
        catch (Exception ex) { return $"unreadable ({ex.GetType().Name})"; }
    }

    // ---------------------------------------------------------------- service-driven closing

    private async Task CloseProcessesAsync(CloseProcessesMessage message)
    {
        var wait = TimeSpan.FromSeconds(Math.Max(1, message.GracefulWaitSeconds));
        // A prompt is on screen for this update: the user is being asked nicely, so never kill from here.
        var prompting = _prompts.ContainsKey(message.UpdateKey);
        var force = message.Force && !prompting;
        if (message.Force && !force)
            _log.LogInformation("Downgrading a forced close of {Key} to graceful: the user has the close-apps prompt on screen", message.UpdateKey);
        // Deadline enforcement: the apps are about to vanish, so the user is told now, whatever answer they gave before.
        // A prompt still on screen already says so.
        if (message.Force && !prompting) ShowClosingNow(message.UpdateKey);

        _log.LogInformation("Closing {Processes} in session {Session} (force={Force}, wait={Wait}s)",
            string.Join(", ", message.ProcessNames), AppInfo.SessionId, force, wait.TotalSeconds);

        IReadOnlyList<string> stillRunning;
        try
        {
            stillRunning = await Task.Run(
                () => ProcessHelper.CloseAsync(message.ProcessNames, AppInfo.SessionId, wait, force, _cts.Token),
                _cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _log.LogError(ex, "Closing processes for {Key} failed", message.UpdateKey);
            stillRunning = ProcessHelper.GetRunning(message.ProcessNames, AppInfo.SessionId);
        }

        _log.LogInformation("Close attempt for {Key} finished; still running: {StillRunning}",
            message.UpdateKey, stillRunning.Count == 0 ? "none" : string.Join(", ", stillRunning));

        await _ipc.SendAsync(new ProcessesClosedMessage
        {
            UpdateKey = message.UpdateKey,
            StillRunning = stillRunning.ToList(),
            Declined = false,
        }).ConfigureAwait(true);
    }

    // ---------------------------------------------------------------- "Close apps and update"

    private async Task CloseAndUpdateAsync(PendingUpdate update)
    {
        var names = BlockingProcessSummary.NamesFor(update).ToList();
        _log.LogInformation("User chose Close apps and update for {App}: {Processes}",
            update.DisplayName, string.Join(", ", names));

        IReadOnlyList<string> stillRunning;
        try
        {
            // Ask nicely first: a window that has unsaved work gets its chance to say so.
            stillRunning = await Task.Run(
                () => ProcessHelper.CloseAsync(names, AppInfo.SessionId, GracefulWait, force: false, _cts.Token),
                _cts.Token).ConfigureAwait(true);

            if (stillRunning.Count > 0)
            {
                // Then mean what the button says. A console process (pwsh in Windows Terminal, for instance) has no
                // main window to close, so WM_CLOSE alone never ends it and the prompt used to come straight back.
                _log.LogInformation("{Count} application(s) ignored the close request for {Key}; terminating them: {StillRunning}",
                    stillRunning.Count, update.Key, string.Join(", ", stillRunning));
                stillRunning = await Task.Run(
                    () => ProcessHelper.CloseAsync(stillRunning, AppInfo.SessionId, TimeSpan.Zero, force: true, _cts.Token),
                    _cts.Token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _log.LogError(ex, "Closing applications for {Key} failed", update.Key);
            stillRunning = ProcessHelper.GetRunning(names, AppInfo.SessionId);
        }

        // Anything left runs elevated or in another user's session: this agent has no rights over either, so it hands
        // the job to the service, which runs as LocalSystem. Never ask the user again for the impossible.
        if (stillRunning.Count > 0)
            _log.LogWarning("{Count} application(s) are out of this agent's reach for {Key}; asking the service to close them: {StillRunning}",
                stillRunning.Count, update.Key, string.Join(", ", stillRunning));

        await _ipc.InstallNowAsync(update.Key, closeBlockingProcesses: true).ConfigureAwait(true);
    }
}
