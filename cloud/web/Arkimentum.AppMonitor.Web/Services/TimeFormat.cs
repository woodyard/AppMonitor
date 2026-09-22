using System.Globalization;

namespace Arkimentum.AppMonitor.Web.Services;

/// <summary>
/// How timestamps read in this console: an absolute local time when precision matters, a relative one ("2 hours
/// ago") when recency is the point. Mirrors <c>Arkimentum.AppMonitor.UI.TimeFormat</c> so the two consoles do not
/// describe the same fleet differently.
/// </summary>
public static class TimeFormat
{
    /// <summary>The local wall-clock time, e.g. "22 Sep 2026 14:05".</summary>
    public static string Absolute(DateTimeOffset? value) =>
        value is { } v ? v.ToLocalTime().ToString("d MMM yyyy HH:mm", CultureInfo.CurrentCulture) : "None";

    /// <summary>How long ago, in the coarsest unit that still says something.</summary>
    public static string Relative(DateTimeOffset? value)
    {
        if (value is not { } v) return "Never";
        var delta = DateTimeOffset.UtcNow - v.ToUniversalTime();
        if (delta < TimeSpan.Zero) return "In the future";
        if (delta < TimeSpan.FromMinutes(1)) return "Just now";
        if (delta < TimeSpan.FromHours(1)) return Plural((int)delta.TotalMinutes, "minute");
        if (delta < TimeSpan.FromDays(1)) return Plural((int)delta.TotalHours, "hour");
        if (delta < TimeSpan.FromDays(60)) return Plural((int)delta.TotalDays, "day");
        return Plural((int)(delta.TotalDays / 30), "month");
    }

    /// <summary>Absolute and relative together, for a detail line: "22 Sep 2026 14:05 (2 hours ago)".</summary>
    public static string Both(DateTimeOffset? value) =>
        value is null ? "Never" : $"{Absolute(value)} ({Relative(value)})";

    private static string Plural(int count, string unit) => $"{count} {unit}{(count == 1 ? "" : "s")} ago";
}
