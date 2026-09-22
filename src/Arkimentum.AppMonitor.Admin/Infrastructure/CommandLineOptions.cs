using System.IO;

namespace Arkimentum.AppMonitor.Admin.Infrastructure;

/// <summary>
/// The admin console's command line.
/// <list type="bullet">
/// <item><c>(none)</c> — open the window on the organization pages.</item>
/// <item><c>--local</c> — <b>deprecated</b>: also show the "This machine" pages. Kept for the transition to
/// central management; it is the only interactive switch that still needs elevation.</item>
/// <item><c>--export &lt;file&gt; [--policy] [--no-replace-apps]</c> — <b>deprecated</b>: headless export of the
/// preference layer.</item>
/// <item><c>--import &lt;file.json&gt; [--merge]</c> — <b>deprecated</b>: headless import into the preference layer.</item>
/// <item><c>--user-config</c> — testing only: HKCU instead of HKLM, no service control.</item>
/// </list>
/// Unknown switches are reported so a typo does not silently open the window instead of exporting.
/// </summary>
public sealed record CommandLineOptions
{
    public string? ExportPath { get; init; }
    public string? ImportPath { get; init; }

    /// <summary>--policy: the exported artefact targets the Policies key.</summary>
    public bool Policy { get; init; }

    /// <summary>--no-replace-apps: the exported artefact leaves unknown application entries alone.</summary>
    public bool NoReplaceApps { get; init; }

    /// <summary>--merge: import only the values in the profile instead of replacing the layer.</summary>
    public bool Merge { get; init; }

    /// <summary>--user-config: read and write HKCU, keep logs in %LOCALAPPDATA%, disable service control.</summary>
    public bool UserConfig { get; init; }

    /// <summary>
    /// --local (deprecated): show the per-machine pages — Overview, Settings, Applications, Export &amp; import —
    /// after the organization pages. Every setting is meant to be managed centrally; without this switch the
    /// console is the organization console only, and touches nothing on the machine it runs on.
    /// </summary>
    public bool Local { get; init; }

    public IReadOnlyList<string> Unknown { get; init; } = [];

    /// <summary>True when the process must do its work without showing the main window.</summary>
    public bool IsHeadless => ExportPath is not null || ImportPath is not null;

    /// <summary>
    /// True when this run reads or writes this machine's own configuration layer: the deprecated local pages, or a
    /// headless export/import. Everything else is organization work in the cloud and leaves the machine alone.
    /// </summary>
    public bool ManagesThisMachine => Local || IsHeadless;

    /// <summary>
    /// True when the process has to be an administrator to do what it was asked to do. Organization mode changes
    /// nothing here, so it runs as a standard user and never raises a UAC prompt; <c>--user-config</c> stays
    /// unprivileged by definition, because it works on HKCU.
    /// </summary>
    public bool RequiresElevation => ManagesThisMachine && !UserConfig;

    /// <summary>The original arguments, used when the process relaunches itself elevated.</summary>
    public IReadOnlyList<string> RawArguments { get; init; } = [];

    public static CommandLineOptions Parse(IReadOnlyList<string> args)
    {
        string? export = null, import = null;
        bool policy = false, noReplace = false, merge = false, userConfig = false, local = false;
        var unknown = new List<string>();

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i].Trim();
            if (arg.Length == 0) continue;
            switch (arg.TrimStart('-', '/').ToLowerInvariant())
            {
                case "export":
                    export = Next(args, ref i);
                    break;
                case "import":
                    import = Next(args, ref i);
                    break;
                case "policy":
                    policy = true;
                    break;
                case "no-replace-apps":
                case "noreplaceapps":
                    noReplace = true;
                    break;
                case "merge":
                    merge = true;
                    break;
                case "user-config":
                case "userconfig":
                    userConfig = true;
                    break;
                case "local":
                    local = true;
                    break;
                default:
                    unknown.Add(arg);
                    break;
            }
        }

        return new CommandLineOptions
        {
            ExportPath = Normalise(export),
            ImportPath = Normalise(import),
            Policy = policy,
            NoReplaceApps = noReplace,
            Merge = merge,
            UserConfig = userConfig,
            Local = local,
            Unknown = unknown,
            RawArguments = [.. args],
        };
    }

    private static string? Next(IReadOnlyList<string> args, ref int i) =>
        i + 1 < args.Count && !args[i + 1].StartsWith('-') ? args[++i] : null;

    private static string? Normalise(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path.Trim().Trim('"')); }
        catch { return path.Trim(); }
    }
}
