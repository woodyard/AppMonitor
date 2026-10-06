using Arkimentum.AppMonitor.Install;

namespace Arkimentum.AppMonitor.Tray.Services;

/// <summary>
/// Decides which progress snapshots of a user-context install go out (to the service and into the window). A new
/// phase goes out at once; a byte count that moved at most once per <see cref="Interval"/>. A snapshot held back is
/// not lost: <see cref="TakeDue"/> hands out the latest one once the interval has passed, so a download that stalls
/// right after a held reading still shows where it stopped. Not thread-safe; the executor uses it on the UI thread.
/// </summary>
public sealed class InstallProgressThrottle
{
    private InstallProgress? _lastSent;
    private DateTimeOffset _lastSentUtc;
    private InstallProgress? _held;

    public InstallProgressThrottle(TimeSpan interval) => Interval = interval;

    public TimeSpan Interval { get; }

    /// <summary>True when <paramref name="progress"/> should go out now; otherwise it is held (or dropped as a repeat).</summary>
    public bool Offer(InstallProgress progress, DateTimeOffset nowUtc)
    {
        if (_lastSent is null || _lastSent.Phase != progress.Phase || nowUtc - _lastSentUtc >= Interval)
        {
            if (_lastSent == progress) { _held = null; return false; }
            MarkSent(progress, nowUtc);
            return true;
        }
        _held = _lastSent == progress ? null : progress;
        return false;
    }

    /// <summary>How long until the held snapshot is due; null when nothing is held.</summary>
    public TimeSpan? DueIn(DateTimeOffset nowUtc)
    {
        if (_held is null) return null;
        var due = _lastSentUtc + Interval - nowUtc;
        return due > TimeSpan.Zero ? due : TimeSpan.Zero;
    }

    /// <summary>The held snapshot once it is due (and marks it sent); null when there is none or it is not due yet.</summary>
    public InstallProgress? TakeDue(DateTimeOffset nowUtc)
    {
        if (_held is not { } held || nowUtc - _lastSentUtc < Interval) return null;
        MarkSent(held, nowUtc);
        return held;
    }

    private void MarkSent(InstallProgress progress, DateTimeOffset nowUtc)
    {
        _lastSent = progress;
        _lastSentUtc = nowUtc;
        _held = null;
    }
}
