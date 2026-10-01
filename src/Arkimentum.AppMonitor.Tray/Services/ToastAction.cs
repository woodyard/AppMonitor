namespace Arkimentum.AppMonitor.Tray.Services;

/// <summary>The set of actions a toast (or one of its buttons) can ask the agent to perform.</summary>
public static class ToastAction
{
    public const string ArgumentAction = "action";
    public const string ArgumentKey = "key";
    public const string ArgumentMinutes = "minutes";

    /// <summary>Id of the close-apps prompt's selection box of deferral options; its value is the minutes.</summary>
    public const string InputDeferMinutes = "deferMinutes";

    /// <summary>Open the main window.</summary>
    public const string Details = "details";

    /// <summary>Send InstallNow for the update in the "key" argument.</summary>
    public const string Install = "install";

    /// <summary>Send Defer for the update in the "key" argument, by "minutes".</summary>
    public const string Defer = "defer";

    /// <summary>Show the close-apps prompt for the update in the "key" argument.</summary>
    public const string CloseApps = "closeapps";

    // ---- the close-apps prompt's own answers; handled by CloseAppsCoordinator, which owns the prompt

    /// <summary>"Close apps and update" on the close-apps prompt (or on the notice left after it timed out).</summary>
    public const string PromptCloseAndUpdate = "prompt-close";

    /// <summary>A deferral on the close-apps prompt: "minutes", or the selection box <see cref="InputDeferMinutes"/>.</summary>
    public const string PromptDefer = "prompt-defer";

    /// <summary>"Not now" on the close-apps prompt.</summary>
    public const string PromptNotNow = "prompt-notnow";

    /// <summary>The body of the close-apps prompt: opens the main window, and counts as "Not now".</summary>
    public const string PromptOpen = "prompt-open";
}
