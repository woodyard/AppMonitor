namespace Arkimentum.AppMonitor.Tray.Services;

/// <summary>The set of actions a toast (or one of its buttons) can ask the agent to perform.</summary>
public static class ToastAction
{
    public const string ArgumentAction = "action";
    public const string ArgumentKey = "key";
    public const string ArgumentMinutes = "minutes";

    /// <summary>Open the main window.</summary>
    public const string Details = "details";

    /// <summary>Send InstallNow for the update in the "key" argument.</summary>
    public const string Install = "install";

    /// <summary>Send Defer for the update in the "key" argument, by "minutes".</summary>
    public const string Defer = "defer";

    /// <summary>Open the close-apps dialog for the update in the "key" argument.</summary>
    public const string CloseApps = "closeapps";
}
