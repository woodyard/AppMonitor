using System.Collections.Generic;
using System.Globalization;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Tray.Resources;

namespace Arkimentum.AppMonitor.Tray.Services;

/// <summary>
/// Where an install this agent runs itself (a user-context install) is, before the service has heard of it: the
/// tray's own <c>InstallProgressTracker</c> reading, and when the tracker was started.
/// </summary>
public sealed record LocalInstallProgress(InstallPhase Phase, long? DownloadedBytes, long? DownloadTotalBytes, DateTimeOffset StartedUtc);

/// <summary>
/// What the "Installing" toast and the update card show under their progress bar: one status line and the bar's
/// value (0..1), or no value for an indeterminate bar. <see cref="Status"/> is null when nothing is known about the
/// install (a service older than the progress fields): the caller then shows exactly what it showed before.
/// </summary>
public readonly record struct InstallProgressView(string? Status, double? Value)
{
    public bool IsIndeterminate => Value is null;

    /// <summary>The bar's value as a percentage, e.g. "38%"; empty while indeterminate.</summary>
    public string PercentText => Value is { } v ? string.Create(CultureInfo.InvariantCulture, $"{(int)Math.Floor(v * 100)}%") : string.Empty;

    public static InstallProgressView Unknown => new(null, null);
}

/// <summary>
/// Turns what is known about a running install (<see cref="PendingUpdate.InstallPhase"/>, the byte counts, when it
/// started and how long it usually takes) into the line under the progress bar: "Downloading · 312 of 825 MB",
/// "Verifying the download", "Installing · 2 min so far · usually about 4 min", "Finishing up". The bar is
/// determinate only while a download is visibly under way (0 &lt; downloaded &lt; total): Delivery Optimization keeps
/// the file at 0 bytes for a while and then grows it in large steps, and the vendor's installer reports nothing at
/// all. Pure, so the toast and the card read the same and the rules can be tested.
/// </summary>
public static class InstallProgressText
{
    private const double BytesPerMegabyte = 1024d * 1024d;
    private const double BytesPerGigabyte = BytesPerMegabyte * 1024d;

    /// <summary>"usually about …" is only said for installs that take at least this long; shorter ones need no estimate.</summary>
    public const int MinimumExpectedSeconds = 60;

    /// <summary>Beyond this many times the usual duration the estimate is dropped rather than look wrong.</summary>
    public const double ExpectedOverrunFactor = 3;

    /// <summary>The status line and bar for one update; <see cref="InstallProgressView.Unknown"/> unless it is installing.</summary>
    public static InstallProgressView Format(PendingUpdate update, DateTimeOffset nowUtc, LocalInstallProgress? local = null)
    {
        if (local is not null)
            return Format(local.Phase, local.DownloadedBytes, local.DownloadTotalBytes, local.StartedUtc, update.ExpectedInstallSeconds, nowUtc);
        if (update.State != UpdateState.Installing) return InstallProgressView.Unknown;
        return Format(update.InstallPhase, update.DownloadedBytes, update.DownloadTotalBytes, update.InstallStartedUtc, update.ExpectedInstallSeconds, nowUtc);
    }

    /// <summary>
    /// The same from the bare facts. Without a phase and a start time (an older service) nothing is known; with a start
    /// time but no phase, the install is described as running.
    /// </summary>
    public static InstallProgressView Format(
        InstallPhase? phase, long? downloadedBytes, long? totalBytes, DateTimeOffset? startedUtc, int? expectedSeconds, DateTimeOffset nowUtc)
    {
        if (phase is null && startedUtc is null) return InstallProgressView.Unknown;

        switch (phase ?? InstallPhase.Installing)
        {
            case InstallPhase.Starting:
                return new InstallProgressView(Strings.InstallPhaseStarting, null);

            case InstallPhase.Downloading:
                return FormatDownload(downloadedBytes, totalBytes);

            case InstallPhase.Verifying:
                return new InstallProgressView(Strings.InstallPhaseVerifying, null);

            case InstallPhase.Checking:
                return new InstallProgressView(Strings.InstallPhaseChecking, null);

            default:
                var parts = new List<string>(3) { Strings.InstallPhaseInstalling };
                TimeSpan? elapsed = startedUtc is { } started ? Max(nowUtc - started, TimeSpan.Zero) : null;
                if (elapsed is { } e) parts.Add(Strings.InstallElapsed(Duration(e)));
                if (UsualDuration(expectedSeconds, elapsed) is { } usual) parts.Add(Strings.InstallUsually(usual));
                return new InstallProgressView(string.Join(Strings.StatusSeparator, parts), null);
        }
    }

    /// <summary>
    /// "Usually takes about 4 min" for an update that is queued, when this device has timed earlier installs of it; null
    /// otherwise (and for installs too quick to be worth mentioning).
    /// </summary>
    public static string? QueuedHint(PendingUpdate update) =>
        update.State == UpdateState.Scheduled && UsualDuration(update.ExpectedInstallSeconds, elapsed: null) is { } usual
            ? Strings.InstallUsuallyTakes(usual)
            : null;

    private static InstallProgressView FormatDownload(long? downloaded, long? total)
    {
        var have = downloaded is > 0 ? downloaded.Value : 0;
        var size = total is > 0 ? total.Value : 0;

        if (size > 0 && have > 0 && have < size)
            return new InstallProgressView($"{Strings.InstallPhaseDownloading}{Strings.StatusSeparator}{SizePair(have, size)}", (double)have / size);
        // The size is announced but nothing is on disk yet (or the file is complete and being moved): no bar to fill.
        if (size > 0)
            return new InstallProgressView($"{Strings.InstallPhaseDownloading}{Strings.StatusSeparator}{Size(size)}", null);
        // No size from the server: the count alone, without a bar.
        if (have > 0)
            return new InstallProgressView($"{Strings.InstallPhaseDownloading}{Strings.StatusSeparator}{Strings.InstallDownloadedSoFar(Size(have))}", null);
        return new InstallProgressView(Strings.InstallPhaseDownloading, null);
    }

    private static string? UsualDuration(int? expectedSeconds, TimeSpan? elapsed)
    {
        if (expectedSeconds is not { } seconds || seconds < MinimumExpectedSeconds) return null;
        var expected = TimeSpan.FromSeconds(seconds);
        if (elapsed is { } e && e >= expected * ExpectedOverrunFactor) return null;
        // Rounded to the nearest minute: "about 4 min" for 3 min 40 s.
        return Duration(TimeSpan.FromMinutes(Math.Round(expected.TotalMinutes, MidpointRounding.AwayFromZero)));
    }

    // ---------------------------------------------------------------- units

    /// <summary>"less than a minute", "1 min", "4 min", "1 h", "1 h 5 min" (whole minutes, rounded down).</summary>
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.FromMinutes(1)) return Strings.DurationUnderAMinute;
        var minutes = (int)Math.Floor(span.TotalMinutes);
        if (minutes < 60) return string.Create(CultureInfo.InvariantCulture, $"{minutes} min");
        var hours = minutes / 60;
        var rest = minutes % 60;
        return rest == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours} h")
            : string.Create(CultureInfo.InvariantCulture, $"{hours} h {rest} min");
    }

    /// <summary>"825 MB", or "1.2 GB" from 1 GB up (one decimal); anything above zero shows as at least "1 MB".</summary>
    public static string Size(long bytes) =>
        bytes >= BytesPerGigabyte ? $"{Gigabytes(bytes)} GB" : $"{Megabytes(bytes)} MB";

    /// <summary>"312 of 825 MB", "1.1 of 3.4 GB", or "312 MB of 1.2 GB" when the two need different units.</summary>
    public static string SizePair(long downloaded, long total)
    {
        if (total < BytesPerGigabyte) return Strings.InstallSizeOf(Megabytes(downloaded), $"{Megabytes(total)} MB");
        if (downloaded >= BytesPerGigabyte) return Strings.InstallSizeOf(Gigabytes(downloaded), $"{Gigabytes(total)} GB");
        return Strings.InstallSizeOf($"{Megabytes(downloaded)} MB", $"{Gigabytes(total)} GB");
    }

    private static string Megabytes(long bytes)
    {
        var mb = Math.Round(bytes / BytesPerMegabyte, MidpointRounding.AwayFromZero);
        if (bytes > 0 && mb < 1) mb = 1;
        return mb.ToString("0", CultureInfo.InvariantCulture);
    }

    // Rounded down, so "1.0 of 1.0 GB" never shows while bytes are still missing.
    private static string Gigabytes(long bytes) =>
        (Math.Floor(bytes / BytesPerGigabyte * 10) / 10).ToString("0.0", CultureInfo.InvariantCulture);

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}

/// <summary>
/// Keeps a determinate bar from going backwards. Within one phase of one install the downloaded byte count only
/// grows, but snapshots can arrive out of order (a state broadcast next to a local reading) or a size measurement
/// can briefly dip; the bar then holds its highest value. A new phase or a different total is a new bar.
/// </summary>
public sealed class InstallProgressClamp
{
    private readonly Dictionary<string, (InstallPhase? Phase, long? Total, double Value)> _last = new(StringComparer.Ordinal);

    /// <summary>The value to draw for <paramref name="key"/>: the higher of this one and the last within the same bar.</summary>
    public double? Apply(string key, InstallPhase? phase, long? total, double? value)
    {
        if (value is not { } v) return null;
        v = Math.Clamp(v, 0, 1);
        if (_last.TryGetValue(key, out var last) && last.Phase == phase && last.Total == total && last.Value > v) return last.Value;
        _last[key] = (phase, total, v);
        return v;
    }

    /// <summary>The view with its bar value clamped.</summary>
    public InstallProgressView Apply(string key, InstallPhase? phase, long? total, InstallProgressView view) =>
        view with { Value = Apply(key, phase, total, view.Value) };

    /// <summary>Forgets an install that ended, so its next attempt starts from an empty bar.</summary>
    public void Forget(string key) => _last.Remove(key);
}
