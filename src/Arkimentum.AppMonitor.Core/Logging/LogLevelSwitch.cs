using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Logging;

/// <summary>
/// The minimum log level of a process, changeable while it runs. The file logger and the logger factory's global filter
/// (<see cref="FileLoggerExtensions.AddLevelSwitch"/>) both consult it on every call, so a new <c>LogLevel</c> from any
/// configuration layer takes effect at the next reload without a restart.
/// </summary>
public sealed class LogLevelSwitch
{
    private readonly object _gate = new();
    private int _level;

    public LogLevelSwitch(LogLevel initial = LogLevel.Information) => _level = (int)initial;

    public LogLevel MinimumLevel
    {
        get => (LogLevel)Volatile.Read(ref _level);
        set => Volatile.Write(ref _level, (int)value);
    }

    /// <summary>
    /// True when the level was fixed on the command line (the tray's <c>--debug</c>): <see cref="Apply"/> then leaves it alone.
    /// </summary>
    public bool Pinned { get; init; }

    public bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= MinimumLevel;

    /// <summary>
    /// Moves to <paramref name="level"/> and writes one Information line naming the change and where the level came from.
    /// Returns false when nothing changed (same level, or <see cref="Pinned"/>). The line is written while the more verbose
    /// of the two levels is active, so it is not filtered out by the change it announces.
    /// </summary>
    public bool Apply(LogLevel level, ILogger? logger, string source)
    {
        if (Pinned) return false;
        lock (_gate)
        {
            var previous = MinimumLevel;
            if (previous == level) return false;
            if (level > previous)
            {
                Announce(previous);
                MinimumLevel = level;
            }
            else
            {
                MinimumLevel = level;
                Announce(previous);
            }
            return true;
        }

        void Announce(LogLevel previous) =>
            logger?.LogInformation("Log level changed from {Previous} to {Level} (source: {Source})", previous, level, source);
    }
}
