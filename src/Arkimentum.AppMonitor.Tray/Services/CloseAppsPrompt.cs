using System.Linq;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Tray.Infrastructure;
using Arkimentum.AppMonitor.Tray.Resources;

namespace Arkimentum.AppMonitor.Tray.Services;

/// <summary>What the user answered to a close-apps prompt.</summary>
public enum CloseAppsChoice
{
    /// <summary>Close the blocking applications (killing what ignores the request) and install.</summary>
    CloseAndUpdate,
    /// <summary>Defer the update by one of its deferral options.</summary>
    Defer,
    /// <summary>Not now: the update waits; a non-mandatory one is dismissed.</summary>
    NotNow,
}

/// <summary>How a close-apps prompt ended. Only <see cref="Button"/> is the user choosing; everything else is "Not now".</summary>
public enum CloseAppsPromptEnd
{
    /// <summary>One of the toast's buttons.</summary>
    Button,
    /// <summary>The user closed the toast with its X, or swiped it away.</summary>
    ToastClosed,
    /// <summary>The user clicked the toast itself, which opens the main window instead of answering.</summary>
    OpenedWindow,
    /// <summary>Nothing happened within <see cref="CloseAppsPrompt.AnswerTimeout"/>.</summary>
    Timeout,
    /// <summary>Windows would not show the toast (notifications for the agent are off, or showing it threw).</summary>
    NotShown,
}

/// <summary>
/// The text and the choices of one close-apps toast. <see cref="Signature"/> lets a repeated prompt for the same
/// update leave a toast alone that would say exactly the same thing, instead of popping it up again.
/// </summary>
public sealed record CloseAppsPromptContent(
    string Title,
    string Body,
    string? Detail,
    bool ShowNotNow,
    IReadOnlyList<int> DeferralMinutes,
    DateTimeOffset? ForceCloseAtUtc,
    string Signature)
{
    /// <summary>
    /// A single deferral option is a button of its own ("Defer 1 hour"); several become a selection box next to one
    /// "Defer" button - the toast's version of the dialog's drop-down, and the only way to offer every option within
    /// the five buttons a toast can carry.
    /// </summary>
    public bool DeferAsSelection => DeferralMinutes.Count > 1;
}

/// <summary>
/// The rules of the close-apps prompt, which the tray shows as a toast: what it says, which answers it offers, and
/// what happens when the user gives none. Kept free of WPF and of the toast library so the rules are testable; the
/// <see cref="CloseAppsCoordinator"/> does the showing and the sending.
/// </summary>
public static class CloseAppsPrompt
{
    /// <summary>
    /// How long the tray waits for an answer before it takes the prompt as "Not now" and removes the toast. Measured
    /// by the tray, not left to Windows: a toast's own expiry differs per scenario and per Windows build, and a
    /// reminder toast never expires on screen at all.
    /// </summary>
    public static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Windows drops the prompt toast this long after it was shown even if the agent never got to remove it (it
    /// crashed or was stopped). Only a backstop; <see cref="AnswerTimeout"/> is what normally ends the prompt.
    /// </summary>
    public static readonly TimeSpan ToastExpiry = AnswerTimeout + TimeSpan.FromMinutes(1);

    /// <summary>A toast's selection box holds at most five items, so a longer list of deferral options is cut there.</summary>
    public const int MaxDeferralChoices = 5;

    /// <summary>
    /// Every way out of the prompt other than a button means "Not now", exactly as the dialog's X did: the service has
    /// to hear an answer, or the update sits in WaitingForClose until the next notification interval.
    /// </summary>
    public static CloseAppsChoice ChoiceFor(CloseAppsPromptEnd end, CloseAppsChoice? clicked) =>
        end == CloseAppsPromptEnd.Button && clicked is { } choice ? choice : CloseAppsChoice.NotNow;

    /// <summary>
    /// What "Not now" tells the service: a dismissal for an optional update; nothing for a mandatory one, which cannot
    /// be dismissed and simply waits until the service prompts again at the notification interval. That is also why a
    /// "Not now" (clicked or timed out) can never cancel a forced close: one is only ever scheduled for a mandatory
    /// update past its deadline, and even a dismissal leaves it in place then (PolicyEngine.Dismiss).
    /// </summary>
    public static bool NotNowSendsDismissal(PendingUpdate update) => !update.Mandatory;

    /// <summary>
    /// After an unanswered prompt the toast goes away, but a forced close is still on its way: the user is left a
    /// silent notice in the notification centre saying when, until it happens. A user who closed the toast or opened
    /// the window has read it, and one whose toasts Windows refuses cannot be shown a notice either.
    /// </summary>
    public static bool LeavesForcedCloseNotice(PendingUpdate update, CloseAppsPromptEnd end, DateTimeOffset now) =>
        end == CloseAppsPromptEnd.Timeout && update.ForceCloseAtUtc is { } at && at > now;

    /// <summary>
    /// True when the prompt for an update has become moot while it was on screen, so it is taken down without
    /// answering: the update is gone, installing or installed, or the user deferred it from the main window. A stale
    /// "deferred" state from before the prompt cannot fool this: the service only prompts once a deferral has run out.
    /// </summary>
    public static bool IsWithdrawn(PendingUpdate? latest, DateTimeOffset now) =>
        latest is null
        || latest.State is UpdateState.Installing or UpdateState.Installed
        || (latest.State == UpdateState.Deferred && latest.IsDeferred(now));

    /// <summary>
    /// Composes the toast for <paramref name="update"/>. <paramref name="friendly"/> names a process the way the user
    /// knows it (the window title, in the tray); <paramref name="sessionId"/> is the agent's own session, which is
    /// what decides whether a process counts as "another session".
    /// </summary>
    public static CloseAppsPromptContent Compose(PendingUpdate update, int sessionId, Func<string, string> friendly, DateTimeOffset now)
    {
        var name = string.IsNullOrWhiteSpace(update.DisplayName) ? update.AppId : update.DisplayName;
        var title = Strings.CloseAppsTitle(name);

        // The same markers the dialog had: a process that runs elevated or in another session is one this agent cannot
        // close itself, and without saying so the user only sees an application that refuses to close.
        var needsService = false;
        var items = new List<string>();
        foreach (var process in BlockingProcessSummary.NamesFor(update))
        {
            var summary = BlockingProcessSummary.For(process, update.BlockingDetails, sessionId);
            needsService |= summary.NeedsService;
            var markers = new List<string>();
            if (summary.IsElevated) markers.Add(Strings.CloseAppsElevated);
            if (summary.IsInAnotherSession)
                markers.Add(summary.OtherSessionUser is null ? Strings.CloseAppsOtherSession : Strings.CloseAppsOtherSessionAs(summary.OtherSessionUser));
            var label = Friendly(friendly, process);
            items.Add(markers.Count == 0 ? label : Strings.CloseAppsProcessWithMarkers(label, string.Join(", ", markers)));
        }
        var body = Strings.CloseAppsToastBody(items.Count == 0 ? name : string.Join(", ", items));

        // The forced close goes first: a toast cuts long text short, and that is the line the user must not miss.
        var details = new List<string>();
        if (update.ForceCloseAtUtc is { } at)
            details.Add(at > now ? Strings.CloseAppsForcedCloseAt(TimeFormat.Clock(at)) : Strings.CloseAppsCountdownElapsed);
        if (needsService) details.Add(Strings.CloseAppsServiceCloses);
        var detail = details.Count == 0 ? null : string.Join(" ", details);

        // "Not now" disappears once a mandatory update is past its deadline, as it did in the dialog.
        var showNotNow = !update.IsPastDeadline(now);
        IReadOnlyList<int> deferrals = update.CanDefer(now)
            ? [.. (update.DeferralOptionsMinutes ?? []).Where(m => m > 0).Distinct().Take(MaxDeferralChoices)]
            : [];

        var signature = string.Join("\u001f", title, body, detail ?? string.Empty, showNotNow, string.Join(",", deferrals),
            update.ForceCloseAtUtc?.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
        return new CloseAppsPromptContent(title, body, detail, showNotNow, deferrals, update.ForceCloseAtUtc, signature);
    }

    /// <summary>The friendly name never stops a prompt from showing; the bare process name is always good enough.</summary>
    private static string Friendly(Func<string, string> friendly, string process)
    {
        try
        {
            var label = friendly(process);
            return string.IsNullOrWhiteSpace(label) ? process : label;
        }
        catch
        {
            return process;
        }
    }
}
