using System.Globalization;
using Arkimentum.AppMonitor.Tray.Resources;

namespace Arkimentum.AppMonitor.Tray.Infrastructure;

/// <summary>
/// Local-time and duration formatting. Absolute where it matters (deadlines), relative where it helps (next scan).
/// </summary>
public static class TimeFormat
{
    /// <summary>"1 hour", "4 hours", "1 day", "90 minutes" — used for deferral options and intervals.</summary>
    public static string Duration(int minutes)
    {
        if (minutes <= 0) return Strings.DurationNow;
        if (minutes % 1440 == 0)
        {
            var days = minutes / 1440;
            return days == 1 ? "1 day" : $"{days} days";
        }
        if (minutes % 60 == 0)
        {
            var hours = minutes / 60;
            return hours == 1 ? "1 hour" : $"{hours} hours";
        }
        if (minutes > 60)
        {
            var hours = minutes / 60;
            var rest = minutes % 60;
            var h = hours == 1 ? "1 hour" : $"{hours} hours";
            return $"{h} {rest} min";
        }
        return minutes == 1 ? "1 minute" : $"{minutes} minutes";
    }

    /// <summary>Clock time in the user's locale, e.g. "14:02".</summary>
    public static string Clock(DateTimeOffset utc) => utc.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

    /// <summary>
    /// Absolute point in time, short but unambiguous: "14:02" today, "Fri 18:00" within a week, "12 Oct 18:00" beyond.
    /// </summary>
    public static string Absolute(DateTimeOffset utc)
    {
        var local = utc.ToLocalTime();
        var now = DateTimeOffset.Now;
        if (local.Date == now.Date) return local.ToString("t", CultureInfo.CurrentCulture);
        var delta = local.Date - now.Date;
        if (delta > TimeSpan.Zero && delta < TimeSpan.FromDays(7))
            return local.ToString("ddd HH:mm", CultureInfo.CurrentCulture);
        return local.ToString("d MMM HH:mm", CultureInfo.CurrentCulture);
    }

    /// <summary>"in 2 hours" / "5 minutes ago" / "now".</summary>
    public static string Relative(DateTimeOffset utc, DateTimeOffset? nowUtc = null)
    {
        var now = nowUtc ?? DateTimeOffset.UtcNow;
        var delta = utc - now;
        var abs = delta.Duration();
        if (abs < TimeSpan.FromSeconds(45)) return Strings.DurationNow;
        var text = Duration((int)Math.Round(abs.TotalMinutes));
        return delta > TimeSpan.Zero ? Strings.InDuration(text) : Strings.DurationAgo(text);
    }

    /// <summary>Absolute plus a relative hint: "18:02 (in 2 hours)".</summary>
    public static string AbsoluteWithRelative(DateTimeOffset utc, DateTimeOffset? nowUtc = null) =>
        $"{Absolute(utc)} ({Relative(utc, nowUtc)})";

    /// <summary>Countdown as "12:34" (mm:ss) or "1:02:33" (h:mm:ss).</summary>
    public static string Countdown(TimeSpan remaining)
    {
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        return remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours}:{remaining.Minutes:00}:{remaining.Seconds:00}"
            : $"{remaining.Minutes:00}:{remaining.Seconds:00}";
    }
}
