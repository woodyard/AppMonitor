using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Providers;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Install;

/// <summary>A snapshot of a running install, as <see cref="InstallProgressTracker"/> reports it.</summary>
/// <param name="Phase">The step the install is in.</param>
/// <param name="DownloadedBytes">Bytes of the installer on disk so far; null when the download cannot be seen.</param>
/// <param name="DownloadTotalBytes">The installer's announced size; null when the server did not say.</param>
public sealed record InstallProgress(InstallPhase Phase, long? DownloadedBytes = null, long? DownloadTotalBytes = null);

/// <summary>
/// Turns winget's install output into an <see cref="InstallProgress"/>. winget draws no progress bar when its output is
/// redirected, but it prints one line per step ("Found X [id] Version v", "Downloading &lt;url&gt;", "Successfully
/// verified installer hash", "Starting package install...", "Successfully installed").
/// <para>
/// The download goes through Delivery Optimization, whose job (<see cref="DeliveryOptimizationJob"/>, queried once a
/// second while downloading) carries the file size and the bytes so far: the job with the very URL of the "Downloading"
/// line, else (a redirecting URL: DO records the final one) winget's unfinished job of the size the URL announced
/// (HTTP HEAD), else the only unfinished winget job that appeared after the line. Once matched it sticks to that job.
/// Without DO (disabled, or its CIM class missing; three failed queries end the querying) the tracker falls back to the
/// installer file in winget's download folder against the HEAD size: <c>%TEMP%\WinGet\&lt;id&gt;.&lt;version&gt;\</c>
/// for a user's (packaged) winget, <c>C:\Windows\Temp\WinGet\defaultState\&lt;id&gt;.&lt;version&gt;\</c> for SYSTEM's
/// (unpackaged) one. DO fills that file only at the very end, so through DO it mostly shows 0 and then everything.
/// </para>
/// <para>
/// When the service has set SYSTEM's winget to download with WinINet (<c>WingetDownloader</c> = <c>wininet</c>, passed in
/// as <c>deliveryOptimization: false</c>) there is no DO job at all: DO is not asked (so no unrelated job - another
/// winget's, the Store's - can ever be taken for this download), and the download folder is the source. winget then
/// writes the installer progressively into a file named by its SHA-256 and renames it to the installer's name after the
/// hash check, so there a hash-named file is the download in progress, not a finished one. The byte counts are a
/// best-effort hint, never a promise.
/// </para>
/// Feed it every output line through <see cref="Report"/>; it raises <c>onChange</c> on each phase change and, while
/// downloading, whenever the byte count moves. Thread-safe; dispose it when the install ends.
/// <para>
/// The phase lines are English. On a Windows whose winget speaks another language none of them match; the tracker then
/// stays at <see cref="InstallPhase.Starting"/> unless a DO job is matched to a URL line (or the download folder of the
/// known package id shows a file that grows): then <see cref="InstallPhase.Downloading"/>, and once that job is complete
/// (or the file is renamed to its hash or has grown to the announced size) <see cref="InstallPhase.Installing"/>. It
/// never claims a step it has no evidence for.
/// The web provider's own messages ("Downloading...", "Verifying download...", "Running setup.exe...") map too.
/// </para>
/// </summary>
public sealed class InstallProgressTracker : IProgress<string>, IDisposable
{
    /// <summary>How long the built-in Content-Length lookup may take before the total is left unknown.</summary>
    public static readonly TimeSpan ContentLengthTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>How long the "Downloading" line waits for the list of DO jobs that existed before it.</summary>
    private static readonly TimeSpan BaselineTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Consecutive failed DO queries after which this tracker stops asking (DO disabled, CIM class missing).</summary>
    public const int MaxDeliveryOptimizationFailures = 3;

    /// <summary>A Delivery Optimization source that lists nothing: the download folder and HEAD only (tests, diagnostics).</summary>
    public static readonly Func<CancellationToken, IReadOnlyList<DeliveryOptimizationJob>> NoDeliveryOptimization = _ => [];

    private static readonly Regex FoundLine = new(@"^Found\s.+\[(?<id>[^\[\]\s]+)\](?:\s+Version\s+(?<version>\S+))?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex DownloadingLine = new(@"^Downloading\s+(?<url>https?://\S+)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    /// <summary>A short line that ends in a URL: winget's download line in another language ("Wird heruntergeladen https://...").</summary>
    private static readonly Regex AnyUrlLine = new(@"^\S[^:/]{0,60}\s(?<url>https?://\S+)\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex HashName = new("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant);

    private readonly Action<InstallProgress> _onChange;
    private readonly ILogger? _logger;
    private readonly Func<string, CancellationToken, Task<long?>> _contentLength;
    private readonly Func<CancellationToken, IReadOnlyList<DeliveryOptimizationJob>> _doJobs;
    private readonly IReadOnlyList<string> _roots;
    /// <summary>False when winget is known to download without DO (WinINet): DO is never asked, the folder is the source.</summary>
    private readonly bool _deliveryOptimization;
    private readonly Timer _timer;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();

    // ---- guarded by _gate
    private InstallProgress _current = new(InstallPhase.Starting);
    private string? _wingetId;
    private string? _version;
    private string? _url;
    /// <summary>True once any English phase line was recognised: the folder then only supplies byte counts, never phases.</summary>
    private bool _linesUnderstood;
    /// <summary>The largest file the last poll saw, to tell a growing download from a leftover one.</summary>
    private long? _lastSeenBytes;
    /// <summary>The folder showed the installer under its hash name during this download (WinINet: the download in progress).</summary>
    private bool _sawHashName;
    private bool _disposed;
    /// <summary>The size the URL announced (HEAD); the DO job's own size is preferred.</summary>
    private long? _headTotal;
    private bool _doDisabled;
    private int _doFailures;
    /// <summary>The DO jobs that existed before the "Downloading" line; null when unknown.</summary>
    private HashSet<string>? _doBaseline;
    /// <summary>The DO job matched to this download; it is followed until the next "Downloading" line.</summary>
    private string? _doFileId;
    private bool _doMatched;
    private long _doBytes;
    private long? _doTotal;

    private long _sequence;
    private long _delivered;
    private int _polling;

    /// <param name="onChange">Called with every new snapshot (from the caller's thread or a timer thread).</param>
    /// <param name="logger">Optional diagnostics.</param>
    /// <param name="contentLength">Resolves an installer URL's size (HTTP HEAD); null uses the built-in client. Tests pass a fake.</param>
    /// <param name="wingetTempRoots">The <c>WinGet</c> temp folders to watch; null = the current process's and the system's.</param>
    /// <param name="pollInterval">How often the download folder is measured; null = 1 second.</param>
    /// <param name="wingetId">
    /// The package id being installed, so the download folder can be found before (or without) winget's "Found" line,
    /// which also sets it. Null: only that line can.
    /// </param>
    /// <param name="version">The version being installed (the folder is <c>&lt;id&gt;.&lt;version&gt;</c>); null = any version of the id.</param>
    /// <param name="deliveryOptimizationJobs">
    /// Lists Delivery Optimization's download jobs; null = the WMI query (<see cref="DeliveryOptimizationJobs.Query"/>).
    /// May throw; tests pass a fake, <see cref="NoDeliveryOptimization"/> switches it off.
    /// </param>
    /// <param name="deliveryOptimization">
    /// False when winget is known to download with WinINet (the service set <c>network.downloader</c> to <c>wininet</c> in
    /// SYSTEM's winget settings): DO is never queried and a hash-named file in the download folder counts as a download
    /// still running. True (default): winget's own choice, which is DO.
    /// </param>
    public InstallProgressTracker(
        Action<InstallProgress> onChange,
        ILogger? logger = null,
        Func<string, CancellationToken, Task<long?>>? contentLength = null,
        IReadOnlyList<string>? wingetTempRoots = null,
        TimeSpan? pollInterval = null,
        string? wingetId = null,
        string? version = null,
        Func<CancellationToken, IReadOnlyList<DeliveryOptimizationJob>>? deliveryOptimizationJobs = null,
        bool deliveryOptimization = true)
    {
        ArgumentNullException.ThrowIfNull(onChange);
        _onChange = onChange;
        _logger = logger;
        _deliveryOptimization = deliveryOptimization;
        _doDisabled = !deliveryOptimization;
        _doJobs = deliveryOptimizationJobs ?? DeliveryOptimizationJobs.Query;
        _contentLength = contentLength ?? CreateContentLengthResolver(null);
        _roots = wingetTempRoots ?? DefaultWingetTempRoots();
        _wingetId = string.IsNullOrWhiteSpace(wingetId) ? null : wingetId.Trim();
        _version = string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        var interval = pollInterval is { } p && p > TimeSpan.Zero ? p : DefaultPollInterval;
        _timer = new Timer(_ => Poll(), null, interval, interval);
    }

    /// <summary>The latest snapshot; <see cref="InstallPhase.Starting"/> until the first recognised line.</summary>
    public InstallProgress Current { get { lock (_gate) return _current; } }

    /// <summary>One line of winget's output (spinner and progress artefacts are ignored).</summary>
    public void Report(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        foreach (var segment in WingetOutputParser.CleanLines(line)) ReportOne(segment.Trim());
    }

    /// <summary>The installer has exited and the agent is reading the version back.</summary>
    public void MarkChecking() => SetPhase(InstallPhase.Checking, understood: true);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _timer.Dispose();
        // Not disposed: a size lookup still in flight reads its token; a CancellationTokenSource without a timer holds nothing.
        _cts.Cancel();
    }

    private void ReportOne(string line)
    {
        if (line.Length == 0) return;

        var found = FoundLine.Match(line);
        if (found.Success)
        {
            lock (_gate)
            {
                // The take-over and the fallbacks run several winget commands; the latest "Found" names the folder.
                _wingetId = found.Groups["id"].Value;
                if (found.Groups["version"].Success) _version = found.Groups["version"].Value;
                _lastSeenBytes = null;
                _sawHashName = false;
            }
            return;
        }

        var downloading = DownloadingLine.Match(line);
        if (downloading.Success)
        {
            StartDownload(downloading.Groups["url"].Value, setPhase: true);
            return;
        }

        if (line.StartsWith("Successfully verified installer hash", StringComparison.OrdinalIgnoreCase))
            SetPhase(InstallPhase.Verifying, understood: true);
        else if (line.StartsWith("Starting package install", StringComparison.OrdinalIgnoreCase))
            SetPhase(InstallPhase.Installing, understood: true);
        else if (line.StartsWith("Successfully installed", StringComparison.OrdinalIgnoreCase))
            SetPhase(InstallPhase.Checking, understood: true);
        // The web provider's own messages (WebProvider, InstallerRunner): stable English text written by this agent.
        else if (line.StartsWith("Downloading...", StringComparison.Ordinal))
            SetPhase(InstallPhase.Downloading, understood: true, resetBytes: true);
        else if (line.StartsWith("Verifying download", StringComparison.Ordinal))
            SetPhase(InstallPhase.Verifying, understood: true);
        else if (line.StartsWith("Running ", StringComparison.Ordinal) && line.EndsWith("...", StringComparison.Ordinal))
            SetPhase(InstallPhase.Installing, understood: true);
        else if (AnyUrlLine.Match(line) is { Success: true } other)
        {
            // Probably the download line in another language: worth asking for the size, not a reason to claim a phase.
            bool fresh;
            lock (_gate) fresh = !_linesUnderstood && _url is null && _current.Phase == InstallPhase.Starting;
            if (fresh) StartDownload(other.Groups["url"].Value, setPhase: false);
        }
    }

    private void StartDownload(string url, bool setPhase)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _url = url;
            _lastSeenBytes = null;
            _sawHashName = false;
            _headTotal = null;
            _doBaseline = null;
            _doFileId = null;
            _doMatched = false;
            _doBytes = 0;
            _doTotal = null;
        }
        // Before the phase changes, so the first poll of the download already knows which DO jobs are older than it.
        var baseline = TakeBaseline();
        lock (_gate) { if (string.Equals(_url, url, StringComparison.Ordinal)) _doBaseline = baseline; }
        if (setPhase) SetPhase(InstallPhase.Downloading, understood: true, resetBytes: true);
        _ = ResolveTotalAsync(url);
    }

    /// <summary>The ids of the DO jobs that exist right now, or null when DO cannot say (quickly).</summary>
    private HashSet<string>? TakeBaseline()
    {
        lock (_gate) { if (_doDisabled) return null; }
        try
        {
            var query = Task.Run(() => _doJobs(_cts.Token));
            if (!query.Wait(BaselineTimeout)) return null;
            var ids = query.Result.Select(j => j.FileId).OfType<string>().Where(id => id.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            lock (_gate) _doFailures = 0;
            return ids;
        }
        catch (Exception ex)
        {
            DeliveryOptimizationFailed(ex);
            return null;
        }
    }

    private void DeliveryOptimizationFailed(Exception ex)
    {
        lock (_gate)
        {
            if (_doDisabled) return;
            if (++_doFailures < MaxDeliveryOptimizationFailures) return;
            _doDisabled = true;
        }
        _logger?.LogDebug("Delivery Optimization's download jobs cannot be read ({Error}); the download is measured in winget's folder instead",
            (ex as AggregateException)?.InnerException?.Message ?? ex.Message);
    }

    /// <summary>
    /// This download's DO job, or null when DO lists none that can be told to be it (or cannot be asked). Not under the
    /// lock: the query takes some milliseconds.
    /// </summary>
    private DeliveryOptimizationJob? FindJob(string url)
    {
        IReadOnlyList<DeliveryOptimizationJob> jobs;
        try
        {
            jobs = _doJobs(_cts.Token);
            lock (_gate) _doFailures = 0;
        }
        catch (Exception ex)
        {
            DeliveryOptimizationFailed(ex);
            return null;
        }

        lock (_gate)
        {
            if (!string.Equals(_url, url, StringComparison.Ordinal)) return null;
            if (_doMatched && _doFileId is { } id)
                return jobs.FirstOrDefault(j => string.Equals(j.FileId, id, StringComparison.OrdinalIgnoreCase));
            var match = MatchJob(jobs, url, _doBaseline, _headTotal);
            if (match is null) return null;
            _doMatched = true;
            _doFileId = match.FileId;
            _logger?.LogDebug("Download of {Url} matched to Delivery Optimization job {FileId} ({Source})", url, match.FileId, match.SourceUrl);
            return match;
        }
    }

    /// <summary>
    /// Which of DO's jobs is the download of <paramref name="url"/>: the job with that very URL (one that did not exist
    /// before the download began, else an unfinished one); else winget's unfinished job whose size is the one the URL
    /// announced (a redirecting URL, which DO records as the final one); else the only unfinished winget job that is not
    /// among <paramref name="baseline"/>, the jobs that existed before. Null when none can be told to be it.
    /// </summary>
    internal static DeliveryOptimizationJob? MatchJob(IReadOnlyList<DeliveryOptimizationJob> jobs, string url, IReadOnlySet<string>? baseline, long? headTotal)
    {
        bool Stale(DeliveryOptimizationJob j) => baseline is not null && j.FileId is not null && baseline.Contains(j.FileId);

        var byUrl = jobs.Where(j => string.Equals(j.SourceUrl?.Trim(), url, StringComparison.OrdinalIgnoreCase)).ToList();
        if ((byUrl.FirstOrDefault(j => !Stale(j)) ?? byUrl.FirstOrDefault(j => !j.IsComplete)) is { } exact) return exact;

        var winget = jobs.Where(j => j.IsWinget && !j.IsComplete).ToList();
        if (headTotal is { } size && size > 0)
        {
            var bySize = winget.Where(j => j.FileSize == size).ToList();
            if ((bySize.FirstOrDefault(j => !Stale(j)) ?? (bySize.Count == 1 ? bySize[0] : null)) is { } sized) return sized;
        }

        if (baseline is null) return null;
        var fresh = winget.Where(j => !Stale(j)).ToList();
        return fresh.Count == 1 ? fresh[0] : null;
    }

    private async Task ResolveTotalAsync(string url)
    {
        long? total;
        var token = _cts.Token;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(ContentLengthTimeout);
            total = await _contentLength(url, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug("Could not read the size of {Url}: {Error}", url, ex.Message);
            return;
        }
        if (total is not { } size || size <= 0) return;

        (InstallProgress Progress, long Sequence) snapshot;
        lock (_gate)
        {
            if (_disposed || !string.Equals(_url, url, StringComparison.Ordinal)) return;
            _headTotal = size;
            if (_doTotal is not null) return; // the DO job's own size is the better answer
            var downloaded = _current.DownloadedBytes;
            if (downloaded is { } d && d > size) return; // the server's answer does not describe this file
            if (_current.DownloadTotalBytes == size) return;
            snapshot = Commit(_current with { DownloadTotalBytes = size });
        }
        Raise(snapshot);
    }

    private void SetPhase(InstallPhase phase, bool understood, bool resetBytes = false)
    {
        (InstallProgress Progress, long Sequence) snapshot;
        lock (_gate)
        {
            if (_disposed) return;
            if (understood) _linesUnderstood = true;
            if (_current.Phase == phase && !resetBytes) return;
            var next = resetBytes ? new InstallProgress(phase) : _current with { Phase = phase };
            if (next == _current) return;
            snapshot = Commit(next);
        }
        Raise(snapshot);
    }

    /// <summary>Measures the download folder; runs on the timer, never twice at once.</summary>
    private void Poll()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;
        try { PollOnce(); }
        catch (Exception ex) { _logger?.LogDebug(ex, "Measuring the winget download folder failed"); }
        finally { Interlocked.Exchange(ref _polling, 0); }
    }

    private void PollOnce()
    {
        string? id, version, url;
        bool askDo;
        lock (_gate)
        {
            if (_disposed) return;
            // Only a download is measured; once the installer runs the folder says nothing more.
            if (_current.Phase is not (InstallPhase.Starting or InstallPhase.Downloading)) return;
            id = _wingetId;
            version = _version;
            url = _url;
            // DO only while downloading, or before that for a URL line winget printed in another language.
            askDo = !_doDisabled && url is not null && (_current.Phase == InstallPhase.Downloading || !_linesUnderstood);
        }

        var job = askDo ? FindJob(url!) : null;
        var measured = id is null ? null : MeasureDownload(_roots, id, version);
        if (job is null && measured is null) return;

        (InstallProgress Progress, long Sequence)? snapshot = null;
        lock (_gate)
        {
            if (_disposed || _current.Phase is not (InstallPhase.Starting or InstallPhase.Downloading)) return;
            if (job is not null && string.Equals(_url, url, StringComparison.Ordinal))
            {
                if (FromJob(job) is { } fromJob) snapshot = Commit(fromJob);
            }
            else if (!_doMatched && measured is { } m && FromFolder(m) is { } fromFolder) snapshot = Commit(fromFolder);
            // A matched job missing from one listing keeps its last numbers: the folder would only say 0.
        }
        if (snapshot is { } changed) Raise(changed);
    }

    /// <summary>The snapshot DO's job gives (under the lock), or null when nothing changed.</summary>
    private InstallProgress? FromJob(DeliveryOptimizationJob job)
    {
        if (job.FileSize is { } size && size > 0) _doTotal = size;
        var total = _doTotal ?? _headTotal;
        var bytes = job.IsComplete && total is { } all ? all : job.TotalBytesDownloaded;
        if (total is { } cap && bytes > cap) bytes = cap;
        _doBytes = Math.Max(_doBytes, Math.Max(0, bytes)); // never backwards for one job

        var phase = _current.Phase;
        if (!_linesUnderstood)
        {
            // Another language: a job for the URL winget printed proves the download, its completion the end of it.
            // One step per poll: from Starting only to Downloading, even when the job is complete already.
            if (phase == InstallPhase.Starting) phase = InstallPhase.Downloading;
            else if (phase == InstallPhase.Downloading && job.IsComplete) phase = InstallPhase.Installing;
        }
        if (phase == InstallPhase.Starting) return null;
        var next = new InstallProgress(phase, _doBytes, total);
        return next == _current ? null : next;
    }

    /// <summary>The snapshot winget's download folder gives (under the lock), or null when nothing changed or nothing is proven.</summary>
    private InstallProgress? FromFolder(Measured measured)
    {
        var grew = _lastSeenBytes is { } before && measured.Bytes > before;
        _lastSeenBytes = measured.Bytes;
        var hashNamedBefore = _sawHashName;
        if (measured.RenamedToHash) _sawHashName = true;
        var total = _current.DownloadTotalBytes;
        if (total is { } t && measured.Bytes > t) total = null; // a size that cannot be this file's

        var phase = _current.Phase;
        if (!_linesUnderstood)
        {
            // No phase line understood (winget in another language): only what the folder proves. Through DO the
            // hash-named file appears whole at the end of the download; with WinINet it is the download in progress,
            // and its rename to the installer's name (after the hash check) is what ends it.
            var finished = _deliveryOptimization ? measured.RenamedToHash : hashNamedBefore && !measured.RenamedToHash;
            if (phase == InstallPhase.Starting && grew) phase = InstallPhase.Downloading;
            else if (phase == InstallPhase.Downloading && (finished || (total is { } tt && measured.Bytes >= tt)))
                phase = InstallPhase.Installing;
        }

        if (phase == InstallPhase.Starting) return null; // a leftover file, or nothing happening yet: no claim
        var next = new InstallProgress(phase, measured.Bytes, total);
        return next == _current ? null : next;
    }

    /// <summary>The download as the folder shows it: the largest file, and whether it already carries its hash as its name.</summary>
    internal readonly record struct Measured(long Bytes, bool RenamedToHash);

    /// <summary>
    /// The largest file in <c>&lt;root&gt;\&lt;id&gt;.&lt;version&gt;</c> of any of <paramref name="roots"/>, or, when
    /// the version is unknown or has no folder, in the newest <c>&lt;id&gt;.&lt;digit&gt;...</c> folder (the digit keeps
    /// <c>Mozilla.Firefox</c> from matching <c>Mozilla.Firefox.MSIX.157.0</c>). Each root is searched itself (a user's
    /// packaged winget: <c>%TEMP%\WinGet\&lt;id&gt;.&lt;version&gt;</c>) and in its <c>*State</c> subfolders (an
    /// unpackaged winget, SYSTEM's: <c>C:\Windows\Temp\WinGet\defaultState\&lt;id&gt;.&lt;version&gt;</c>). Among several
    /// matching folders the most recently written wins (the download in progress over a leftover). Null when there is no
    /// such folder. Files may be renamed or removed while they are read; such errors only skip the file.
    /// </summary>
    internal static Measured? MeasureDownload(IReadOnlyList<string> roots, string wingetId, string? version)
    {
        DirectoryInfo? exactFolder = null;
        DirectoryInfo? prefixFolder = null;
        var prefix = wingetId + ".";
        foreach (var searchRoot in SearchRoots(roots))
        {
            try
            {
                if (version is not null)
                {
                    var exact = new DirectoryInfo(Path.Combine(searchRoot, $"{wingetId}.{version}"));
                    if (exact.Exists && (exactFolder is null || exact.LastWriteTimeUtc > exactFolder.LastWriteTimeUtc)) exactFolder = exact;
                }
                if (exactFolder is not null) continue; // an exact folder anywhere beats a guess by prefix
                foreach (var dir in new DirectoryInfo(searchRoot).EnumerateDirectories(prefix + "*"))
                {
                    if (dir.Name.Length <= prefix.Length || !char.IsAsciiDigit(dir.Name[prefix.Length])) continue;
                    if (prefixFolder is null || dir.LastWriteTimeUtc > prefixFolder.LastWriteTimeUtc) prefixFolder = dir;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        var folder = exactFolder ?? prefixFolder;
        if (folder is null) return null;

        long largest = 0;
        var hashed = false;
        try
        {
            foreach (var file in folder.EnumerateFiles())
            {
                try
                {
                    var length = file.Length;
                    if (length < largest) continue;
                    largest = length;
                    hashed = HashName.IsMatch(file.Name);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
        return new Measured(largest, hashed);
    }

    /// <summary>
    /// The existing folders that may hold winget's <c>&lt;id&gt;.&lt;version&gt;</c> download folders: every root, and its
    /// <c>*State</c> subfolders (<c>defaultState</c>: where an unpackaged winget - SYSTEM's - keeps its temp files).
    /// </summary>
    internal static IEnumerable<string> SearchRoots(IReadOnlyList<string> roots)
    {
        foreach (var root in roots)
        {
            List<string> states;
            try
            {
                if (!Directory.Exists(root)) continue;
                states = Directory.EnumerateDirectories(root, "*State").ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                states = [];
            }
            yield return root;
            foreach (var state in states) yield return state;
        }
    }

    /// <summary>Makes <paramref name="next"/> the current snapshot and numbers it; called under <see cref="_gate"/>.</summary>
    private (InstallProgress Progress, long Sequence) Commit(InstallProgress next)
    {
        _current = next;
        return (next, ++_sequence);
    }

    /// <summary>
    /// Delivers a snapshot unless a newer one (numbered under the lock, so in the order the state changed) has been
    /// delivered already; a throwing handler never reaches the install.
    /// </summary>
    private void Raise((InstallProgress Progress, long Sequence) change)
    {
        var (snapshot, sequence) = change;
        while (true)
        {
            var delivered = Interlocked.Read(ref _delivered);
            if (sequence <= delivered) return;
            if (Interlocked.CompareExchange(ref _delivered, sequence, delivered) == delivered) break;
        }
        lock (_gate) { if (_disposed) return; }
        try { _onChange(snapshot); }
        catch (Exception ex) { _logger?.LogDebug(ex, "An install progress handler threw"); }
    }

    /// <summary>
    /// The <c>WinGet</c> folders under the temp directories winget may use: this process's own (the user's
    /// <c>%TEMP%</c>, or SYSTEM's <c>C:\Windows\SystemTemp</c> on newer Windows), <c>C:\Windows\Temp</c> (where
    /// SYSTEM's winget was seen to download, in <c>WinGet\defaultState</c>) and <c>C:\Windows\SystemTemp</c>. The
    /// <c>*State</c> subfolders are searched by <see cref="MeasureDownload"/> itself.
    /// </summary>
    public static IReadOnlyList<string> DefaultWingetTempRoots()
    {
        var roots = new List<string>();
        void Add(Func<string?> dir)
        {
            try
            {
                if (dir() is { Length: > 0 } d) roots.Add(Path.Combine(d, "WinGet"));
            }
            catch { /* an unreadable environment only loses one candidate */ }
        }
        Add(Path.GetTempPath);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (windows.Length > 0)
        {
            Add(() => Path.Combine(windows, "Temp"));
            Add(() => Path.Combine(windows, "SystemTemp"));
        }
        return roots.Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The built-in size lookup: an HTTP HEAD (redirects followed), and when that gives no length a one-byte ranged
    /// GET whose Content-Range carries the size. Through <paramref name="proxyUrl"/> when one is configured, else the
    /// system proxy. Any failure, or a server that does not say, yields null; it never throws.
    /// </summary>
    public static Func<string, CancellationToken, Task<long?>> CreateContentLengthResolver(string? proxyUrl, HttpMessageHandler? handler = null) =>
        async (url, ct) =>
        {
            try
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)) return null;
                using var http = new HttpClient(handler ?? CreateHandler(proxyUrl), disposeHandler: handler is null) { Timeout = ContentLengthTimeout };
                http.DefaultRequestHeaders.UserAgent.ParseAdd(WebProvider.UserAgent);

                using (var head = new HttpRequestMessage(HttpMethod.Head, uri))
                using (var response = await http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    if (response.IsSuccessStatusCode && response.Content.Headers.ContentLength is { } length && length > 0) return length;
                }

                using var ranged = new HttpRequestMessage(HttpMethod.Get, uri);
                ranged.Headers.Range = new RangeHeaderValue(0, 0);
                using var partial = await http.SendAsync(ranged, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (partial.StatusCode == HttpStatusCode.PartialContent && partial.Content.Headers.ContentRange?.Length is { } full && full > 0) return full;
                return null;
            }
            catch
            {
                return null;
            }
        };

    private static SocketsHttpHandler CreateHandler(string? proxyUrl)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            ConnectTimeout = ContentLengthTimeout,
            UseCookies = false,
        };
        if (!string.IsNullOrWhiteSpace(proxyUrl))
        {
            handler.Proxy = new WebProxy(proxyUrl) { UseDefaultCredentials = true };
            handler.UseProxy = true;
        }
        return handler;
    }
}
