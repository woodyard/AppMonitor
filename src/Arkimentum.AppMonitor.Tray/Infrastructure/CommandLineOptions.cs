namespace Arkimentum.AppMonitor.Tray.Infrastructure;

/// <summary>Parsed command line. Unknown switches are ignored: the service owns how the agent is launched.</summary>
public sealed record CommandLineOptions
{
    /// <summary>Open the main window at start.</summary>
    public bool Show { get; init; }

    /// <summary>Start to the tray only. The default.</summary>
    public bool Minimized { get; init; } = true;

    /// <summary>Developer mode: adds an Exit item to the tray menu and logs at Debug.</summary>
    public bool Debug { get; init; }

    /// <summary>True when the process was started by the notification platform to deliver a toast activation.</summary>
    public bool ToastActivated { get; init; }

    /// <summary>
    /// Whether a later instance, started with <paramref name="args"/> while an agent already runs in the session, makes
    /// that agent open its main window. Only when it says so: <c>--show</c>, or a toast activation, which is the user
    /// clicking a notification. A plain start does not: at every logon the agent is started twice, by the service and by
    /// the Run value the installer writes as its fallback, and the later of the two used to put the window on the
    /// screen of a user who had asked for nothing. The window opens from the tray icon.
    /// </summary>
    public static bool OpensWindowOfRunningAgent(IReadOnlyList<string> args)
    {
        var options = Parse(args);
        return options.Show || options.ToastActivated;
    }

    public static CommandLineOptions Parse(IReadOnlyList<string> args)
    {
        var show = false;
        var minimized = false;
        var debug = false;
        var toast = false;

        foreach (var raw in args)
        {
            var arg = raw.Trim();
            if (arg.Length == 0) continue;
            var normalized = arg.TrimStart('-', '/').ToLowerInvariant();
            switch (normalized)
            {
                case "show":
                    show = true;
                    break;
                case "minimized":
                case "min":
                    minimized = true;
                    break;
                case "debug":
                    debug = true;
                    break;
                // The notification platform appends this when it launches the process for a toast activation.
                case "toastactivated":
                    toast = true;
                    break;
            }
        }

        return new CommandLineOptions
        {
            Show = show && !toast,
            Minimized = !show || minimized || toast,
            Debug = debug,
            ToastActivated = toast,
        };
    }
}
