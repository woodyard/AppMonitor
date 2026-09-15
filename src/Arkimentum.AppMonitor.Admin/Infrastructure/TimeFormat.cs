using System.Globalization;
using Arkimentum.AppMonitor.Admin.Resources;

namespace Arkimentum.AppMonitor.Admin.Infrastructure;

/// <summary>Dates and durations as the console shows them: local time, short, unambiguous.</summary>
public static class TimeFormat
{
    public static string Absolute(DateTimeOffset? value) =>
        value is null ? Strings.None : value.Value.ToLocalTime().ToString("dd MMM yyyy HH:mm", CultureInfo.CurrentCulture);

    /// <summary>
    /// "3 minutes ago", "2 days ago" — what a fleet list needs to read at a glance. The absolute time stays
    /// available as the tooltip, because "2 days ago" is never enough when something is actually wrong.
    /// </summary>
    public static string Relative(DateTimeOffset? value)
    {
        if (value is null) return Strings.None;
        var span = DateTimeOffset.UtcNow - value.Value.ToUniversalTime();
        if (span < TimeSpan.Zero) return Strings.RelativeJustNow;
        if (span < TimeSpan.FromMinutes(1)) return Strings.RelativeJustNow;
        if (span < TimeSpan.FromHours(1)) return Strings.RelativeAgo(Strings.MinutesText((int)span.TotalMinutes));
        if (span < TimeSpan.FromDays(1))
        {
            var hours = (int)span.TotalHours;
            return Strings.RelativeAgo(hours == 1 ? "1 hour" : $"{hours} hours");
        }
        var days = (int)span.TotalDays;
        if (days < 30) return Strings.RelativeAgo(days == 1 ? "1 day" : $"{days} days");
        var months = days / 30;
        return Strings.RelativeAgo(months == 1 ? "1 month" : $"{months} months");
    }

    /// <summary>A device is stale when the server has not heard from it for more than a week.</summary>
    public static bool IsStale(DateTimeOffset? lastSeenUtc, int days = 7) =>
        lastSeenUtc is null || DateTimeOffset.UtcNow - lastSeenUtc.Value.ToUniversalTime() > TimeSpan.FromDays(days);

    public static string Duration(int minutes)
    {
        if (minutes <= 0) return Strings.None;
        if (minutes < 60) return Strings.MinutesText(minutes);
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
        return $"{minutes / 60} h {minutes % 60} min";
    }
}
