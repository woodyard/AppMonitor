using System.IO;

namespace Arkimentum.AppMonitor.Admin.Infrastructure;

/// <summary>
/// The admin console's command line.
/// <list type="bullet">
/// <item><c>(none)</c> — open the window.</item>
/// <item><c>--export &lt;file&gt; [--policy] [--no-replace-apps]</c> — headless export of the preference layer.</item>
/// <item><c>--import &lt;file.json&gt; [--merge]</c> — headless import into the preference layer.</item>
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

    public IReadOnlyList<string> Unknown { get; init; } = [];

    /// <summary>True when the process must do its work without showing the main window.</summary>
    public bool IsHeadless => ExportPath is not null || ImportPath is not null;

    /// <summary>The original arguments, used when the process relaunches itself elevated.</summary>
    public IReadOnlyList<string> RawArguments { get; init; } = [];

    public static CommandLineOptions Parse(IReadOnlyList<string> args)
    {
        string? export = null, import = null;
        bool policy = false, noReplace = false, merge = false, userConfig = false;
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
