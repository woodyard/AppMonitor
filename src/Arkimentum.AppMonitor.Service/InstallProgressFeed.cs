using System.Globalization;
using Arkimentum.AppMonitor.Install;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Service.State;

namespace Arkimentum.AppMonitor.Service;

/// <summary>
/// The progress of the installs that are running, by update key, as the trays get to see it. The coordinator offers
/// every snapshot (from its own <see cref="InstallProgressTracker"/> for a machine-wide install, from the tray's
/// <c>UserInstallProgressMessage</c> for a per-user one) and the feed says whether it is worth a state broadcast now:
/// a new phase always, a moved byte count at most once per <see cref="ByteInterval"/> (two seconds; the counts arrive
/// about once a second). It also writes the one log line per phase change. Nothing here is saved: the progress lives
/// in memory and is laid over the tracked update when the state message is built, so a progress tick never touches
/// state.json and never takes the policy lock. Thread-safe.
/// </summary>
public sealed class InstallProgressFeed(TimeSpan? byteInterval = null)
{
    public static readonly TimeSpan DefaultByteInterval = TimeSpan.FromSeconds(2);

    public TimeSpan ByteInterval { get; } = byteInterval ?? DefaultByteInterval;

    private sealed class Slot
    {
        public InstallProgress Latest = new(InstallPhase.Starting);
        public InstallProgress? Sent;
        public DateTimeOffset SentUtc;
        public DateTimeOffset? DownloadStartedUtc;
        public bool DownloadLogged;
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>An install of <paramref name="key"/> starts: whatever an earlier one left behind is forgotten.</summary>
    public void Begin(string key)
    {
        lock (_lock) _slots[key] = new Slot();
    }

    /// <summary>The install of <paramref name="key"/> has ended; later snapshots for it are ignored until the next <see cref="Begin"/>.</summary>
    public void End(string key)
    {
        lock (_lock) _slots.Remove(key);
    }

    /// <summary>The latest snapshot of a running install, or null when none is running for <paramref name="key"/>.</summary>
    public InstallProgress? Latest(string key)
    {
        lock (_lock) return _slots.TryGetValue(key, out var slot) ? slot.Latest : null;
    }

    /// <summary>
    /// Takes a new snapshot of the install of <paramref name="key"/>. <c>Broadcast</c> says whether the trays should
    /// get a state message now; <c>LogStep</c> is the text of the log line for it ("downloading (825 MB)"), or null.
    /// A snapshot for an install that is not running (never begun, or ended) is ignored.
    /// </summary>
    public (bool Broadcast, string? LogStep) Offer(string key, InstallProgress progress, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (!_slots.TryGetValue(key, out var slot)) return (false, null);
            var previous = slot.Latest;
            slot.Latest = progress;
            var phaseChanged = previous.Phase != progress.Phase;

            var log = phaseChanged ? StepFor(slot, previous, progress, now) : null;
            // The size is often known a moment after the download has begun (the HEAD request): say it then, once.
            if (!phaseChanged && progress.Phase == InstallPhase.Downloading && !slot.DownloadLogged && progress.DownloadTotalBytes is { } total)
            {
                slot.DownloadLogged = true;
                log = $"downloading ({FormatBytes(total)})";
            }

            bool broadcast;
            if (slot.Sent is not { } sent) broadcast = true;
            else if (sent.Phase != progress.Phase) broadcast = true;
            else if (sent == progress) broadcast = false;
            // The size arriving is a one-off worth showing at once; byte counts wait for the interval.
            else if (sent.DownloadTotalBytes != progress.DownloadTotalBytes) broadcast = true;
            else broadcast = now - slot.SentUtc >= ByteInterval;

            if (broadcast) { slot.Sent = progress; slot.SentUtc = now; }
            return (broadcast, log);
        }
    }

    private static string? StepFor(Slot slot, InstallProgress previous, InstallProgress next, DateTimeOffset now)
    {
        // What the download came to, said once it is over (the size, if known, and how long it took).
        string Download()
        {
            var downloaded = Math.Max(previous.DownloadedBytes ?? 0, next.DownloadedBytes ?? 0);
            var bytes = downloaded > 0 ? downloaded : next.DownloadTotalBytes ?? previous.DownloadTotalBytes;
            var parts = new List<string>();
            if (bytes is { } b) parts.Add(FormatBytes(b));
            if (slot.DownloadStartedUtc is { } started) parts.Add("in " + InstallHistory.FormatDuration(now - started));
            return parts.Count == 0 ? string.Empty : $" (downloaded {string.Join(' ', parts)})";
        }

        var afterDownload = previous.Phase == InstallPhase.Downloading;
        var step = next.Phase switch
        {
            InstallPhase.Downloading => StartDownload(slot, next, now),
            InstallPhase.Verifying => "verifying the download" + (afterDownload ? Download() : string.Empty),
            InstallPhase.Installing => "installing" + (afterDownload ? Download() : string.Empty),
            InstallPhase.Checking => "checking the installed version",
            _ => null,
        };
        if (!afterDownload || next.Phase == InstallPhase.Downloading) return step;
        slot.DownloadStartedUtc = null;
        return step;
    }

    private static string? StartDownload(Slot slot, InstallProgress next, DateTimeOffset now)
    {
        slot.DownloadStartedUtc = now;
        slot.DownloadLogged = next.DownloadTotalBytes is not null;
        return next.DownloadTotalBytes is { } total ? $"downloading ({FormatBytes(total)})" : "downloading";
    }

    /// <summary>"825 MB", "1.2 GB", "640 KB": sizes in the log, in binary units like the web provider's download messages.</summary>
    public static string FormatBytes(long bytes)
    {
        const double Kb = 1024, Mb = Kb * 1024, Gb = Mb * 1024;
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < (long)Mb => string.Create(CultureInfo.InvariantCulture, $"{Math.Round(bytes / Kb)} KB"),
            < (long)Gb => string.Create(CultureInfo.InvariantCulture, $"{Math.Round(bytes / Mb)} MB"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / Gb:0.0} GB"),
        };
    }

    /// <summary>Lays the running install's progress over a copy of its tracked update (only while it is installing).</summary>
    public void Apply(PendingUpdate copy)
    {
        if (copy.State != UpdateState.Installing || Latest(copy.Key) is not { } p) return;
        copy.InstallPhase = p.Phase;
        copy.DownloadedBytes = p.DownloadedBytes;
        copy.DownloadTotalBytes = p.DownloadTotalBytes;
    }
}
