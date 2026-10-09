using Arkimentum.AppMonitor.Install;
using Arkimentum.AppMonitor.Models;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Providers;

/// <summary>
/// Installer logs: every winget run that installs or uninstalls gets <c>--log &lt;file&gt;</c> in
/// <see cref="ProviderOptions.InstallerLogDirectory"/> (see <see cref="InstallerLogs"/> for the names and the retention),
/// so the time an installer takes and why it failed can be read afterwards (Cloudflare's MSI took 8m51s, Snagit more
/// than 14 minutes, and nothing showed why). A failed install names the log in its message, which reaches the cloud.
/// </summary>
public sealed partial class WingetProvider
{
    /// <summary>The log files the winget runs of the current install were given (per install call, see <see cref="BeginInstallerLogs"/>).</summary>
    private readonly AsyncLocal<List<string>?> _installLogs = new();

    /// <summary>Every log path this provider instance handed out, so two runs in the same second never share a file.</summary>
    private readonly HashSet<string> _reservedLogPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Prunes the installer log folder (see <see cref="InstallerLogs.Prune"/>) and returns the list the winget runs of
    /// this install record their log files in; null when installer logs are off. The caller stores it in
    /// <see cref="_installLogs"/>, so it travels with the install's async flow.
    /// </summary>
    private List<string>? BeginInstallerLogs()
    {
        var directory = _options.InstallerLogDirectory;
        if (string.IsNullOrWhiteSpace(directory)) return null;
        InstallerLogs.Prune(directory, DateTime.UtcNow, _logger);
        return [];
    }

    /// <summary>
    /// A new installer log file for one winget run (<paramref name="step"/> tells the runs of one install apart), or null
    /// when installer logs are off, when the configured extra or global arguments already pass <c>--log</c>/<c>-o</c>, or
    /// when the folder cannot be created.
    /// </summary>
    private string? NewInstallerLog(AppPolicy app, string wingetId, string? step, string? extraArgs)
    {
        var directory = _options.InstallerLogDirectory;
        if (string.IsNullOrWhiteSpace(directory)) return null;
        if (HasArgument(extraArgs, "--log", "-o") || HasArgument(_options.WingetGlobalArgs, "--log", "-o")) return null;
        var path = InstallerLogs.NewPath(directory, app.AppId, wingetId, step, DateTime.Now, _reservedLogPaths, _logger);
        if (path is not null && _installLogs.Value is { } logs)
            lock (logs) logs.Add(path);
        return path;
    }

    /// <summary>The <c>--log</c> argument for <paramref name="path"/>, or nothing.</summary>
    private static string LogArgument(string? path) => path is null ? string.Empty : " --log " + Quote(path);

    /// <summary>Logs where the installer log of a winget run other than the upgrade goes (the upgrade names it in its own line).</summary>
    private void LogInstallerLogPath(AppPolicy app, string wingetId, string? path)
    {
        if (path is not null)
            _logger.LogInformation("{AppId}: the installer log of this winget run for '{WingetId}' goes to {Path}.", app.AppId, wingetId, path);
    }

    /// <summary>
    /// A failed install's result with " Installer log: &lt;path&gt;" appended: the log of the last winget run of the install
    /// that wrote one (a run whose installer takes no log switch leaves no file or an empty one). Successes and installs
    /// without a written log are returned unchanged. Pure apart from the file check, so the rule is testable.
    /// </summary>
    internal static InstallResult WithInstallerLog(InstallResult result, IReadOnlyList<string>? logs)
    {
        if (result.Success || logs is null || logs.Count == 0) return result;
        string? path;
        lock (logs) path = logs.LastOrDefault(InstallerLogs.HasContent);
        if (path is null || (result.Message?.Contains(path, StringComparison.OrdinalIgnoreCase) ?? false)) return result;
        var message = string.IsNullOrWhiteSpace(result.Message) ? $"Installer log: {path}" : $"{result.Message.TrimEnd()} Installer log: {path}";
        return result with { Message = message };
    }

    /// <summary>
    /// Whether a winget argument string passes one of <paramref name="names"/> (as a word of its own, or as
    /// <c>name=value</c>), case-insensitively. Pure, so the rule is testable.
    /// </summary>
    internal static bool HasArgument(string? arguments, params string[] names)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return false;
        foreach (var token in arguments.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var t = token.Trim('"');
            foreach (var name in names)
            {
                if (string.Equals(t, name, StringComparison.OrdinalIgnoreCase) || t.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }
}
