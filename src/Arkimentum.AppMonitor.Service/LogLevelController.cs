using Arkimentum.AppMonitor.Logging;
using Arkimentum.AppMonitor.Models;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Service;

/// <summary>
/// Keeps the process log level on the effective <c>LogLevel</c> setting (policy > organization > preference > default).
/// Program.cs starts the switch from the registry-only bootstrap read; the first <see cref="SettingsProvider.Reload"/>
/// (service start) adds the cached organization configuration, and every later reload that changes the configuration
/// (policy tick, scan, cloud sync) applies again - no restart.
/// </summary>
public sealed class LogLevelController(LogLevelSwitch levelSwitch, ILogger<LogLevelController> logger)
{
    public LogLevelSwitch Switch => levelSwitch;

    /// <summary>Follows every configuration change the provider announces.</summary>
    public void Attach(SettingsProvider settings) => settings.Changed += s => Apply(s);

    /// <summary>Sets the switch from <paramref name="settings"/>; true when the level actually changed.</summary>
    public bool Apply(AgentSettings settings)
    {
        settings.ValueSources.TryGetValue(nameof(AgentSettings.LogLevel), out var source);
        return levelSwitch.Apply(FileLoggerExtensions.ParseLevel(settings.LogLevel), logger, DescribeSource(source));
    }

    /// <summary>Plain-language name of the configuration layer recorded in <see cref="AgentSettings.ValueSources"/>.</summary>
    public static string DescribeSource(string? source) => source switch
    {
        "Policy" or "PolicyAppList" => "policy",
        "Cloud" => "organization",
        "Preference" or "PreferenceAppList" => "local preference",
        null or "" or "Default" => "default",
        _ => source,
    };
}
