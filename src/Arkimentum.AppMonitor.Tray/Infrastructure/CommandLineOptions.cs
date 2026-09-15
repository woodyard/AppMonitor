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
