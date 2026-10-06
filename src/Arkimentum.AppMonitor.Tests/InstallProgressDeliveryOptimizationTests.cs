using System.Management;
using System.Runtime.InteropServices;
using Arkimentum.AppMonitor.Install;
using Arkimentum.AppMonitor.Models;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// The download's byte counts from Delivery Optimization's job list (MSFT_DeliveryOptimizationFile): DO fills the file
/// in winget's folder only at the end, so its job is where the progress is. Measured 2026-10-06 with a VS Code
/// download: SourceURL = the URL winget printed, FileSize known from the start, TotalBytesDownloaded 0 -> 30 -> 108 ->
/// 187.8 -> 193.8 MB, Status 0 while downloading and 1 when complete.
/// </summary>
public sealed class InstallProgressDeliveryOptimizationTests : IDisposable
{
    private const string VsCodeUrl = "https://vscode.download.prss.microsoft.com/dbazure/download/insider/abc/VSCodeSetup-x64-1.127.0-insider.exe";
    private const long VsCodeSize = 203_257_376;
    private const string Winget = DeliveryOptimizationJob.WingetCaller;
    private static readonly TimeSpan FastPolling = TimeSpan.FromMilliseconds(25);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "AppMonitorTests", "WinGetDo-" + Guid.NewGuid().ToString("N"));
    private readonly List<InstallProgress> _seen = [];

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>A Delivery Optimization that lists what the test puts in, and counts how often it is asked.</summary>
    private sealed class FakeDo
    {
        private readonly object _lock = new();
        private readonly List<DeliveryOptimizationJob> _jobs = [];
        public int Calls;
        public bool Fail;

        public void Set(DeliveryOptimizationJob job)
        {
            lock (_lock)
            {
                _jobs.RemoveAll(j => j.FileId == job.FileId);
                _jobs.Add(job);
            }
        }

        public IReadOnlyList<DeliveryOptimizationJob> List(CancellationToken _)
        {
            Interlocked.Increment(ref Calls);
            if (Fail) throw new ManagementException("Invalid namespace");
            lock (_lock) return [.. _jobs];
        }
    }

    private static DeliveryOptimizationJob Job(string id, string? url, long? size, long bytes, uint status = 0, string? caller = Winget) =>
        new(id, url, size, bytes, status, caller);

    private void OnChange(InstallProgress p) { lock (_seen) _seen.Add(p); }

    private List<InstallProgress> Seen() { lock (_seen) return [.. _seen]; }

    private InstallProgressTracker Tracker(FakeDo fake, long? headSize = null, string? wingetId = null) =>
        new(OnChange, contentLength: (_, _) => Task.FromResult(headSize), wingetTempRoots: [_root], pollInterval: FastPolling,
            wingetId: wingetId, version: wingetId is null ? null : "1.0", deliveryOptimizationJobs: fake.List);

    private static async Task<bool> WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    [Fact]
    public async Task The_job_with_the_printed_url_supplies_size_and_bytes_and_completion_means_everything_is_in()
    {
        var fake = new FakeDo();
        fake.Set(Job("wu1", "http://dl.delivery.mp.microsoft.com/kb.cab", 30_062_765, 2_799_789, 2, "WU Client Download"));
        using var tracker = Tracker(fake, headSize: 999); // the DO size wins over the HEAD answer

        tracker.Report($"Downloading {VsCodeUrl}");
        fake.Set(Job("vs", VsCodeUrl.ToUpperInvariant(), VsCodeSize, 0));
        Assert.True(await WaitFor(() => tracker.Current.DownloadTotalBytes == VsCodeSize));
        Assert.Equal(0, tracker.Current.DownloadedBytes);

        fake.Set(Job("vs", VsCodeUrl, VsCodeSize, 108_000_000));
        Assert.True(await WaitFor(() => tracker.Current.DownloadedBytes == 108_000_000));
        fake.Set(Job("vs", VsCodeUrl, VsCodeSize, 193_800_000, status: 1));
        Assert.True(await WaitFor(() => tracker.Current.DownloadedBytes == VsCodeSize));

        Assert.Equal(new InstallProgress(InstallPhase.Downloading, VsCodeSize, VsCodeSize), tracker.Current);
        Assert.DoesNotContain(Seen(), p => p.DownloadTotalBytes == 999 && p.DownloadedBytes is not null);
    }

    [Fact]
    public async Task Byte_counts_never_go_backwards_for_one_job()
    {
        var fake = new FakeDo();
        using var tracker = Tracker(fake);
        tracker.Report($"Downloading {VsCodeUrl}");
        fake.Set(Job("vs", VsCodeUrl, VsCodeSize, 108_000_000));
        Assert.True(await WaitFor(() => tracker.Current.DownloadedBytes == 108_000_000));

        fake.Set(Job("vs", VsCodeUrl, VsCodeSize, 30_000_000)); // DO revising its count after a lost piece
        await Task.Delay(150);
        Assert.Equal(108_000_000, tracker.Current.DownloadedBytes);
        Assert.All(Seen().Select(p => p.DownloadedBytes ?? 0).Zip(Seen().Skip(1).Select(p => p.DownloadedBytes ?? 0)),
            pair => Assert.True(pair.Second >= pair.First || pair.Second == 0));
    }

    [Fact]
    public async Task A_redirected_download_is_found_by_the_size_the_url_announced()
    {
        const long postmanSize = 152_000_000;
        var fake = new FakeDo();
        using var tracker = Tracker(fake, headSize: postmanSize);
        tracker.Report("Downloading https://dl.pstmn.io/download/version/12.31.2/windows_64");
        Assert.True(await WaitFor(() => tracker.Current.DownloadTotalBytes == postmanSize));

        fake.Set(Job("other", "https://cdn.example.com/other.msi", 5_000_000, 1_000)); // another winget download
        fake.Set(Job("pm", "https://dl-cdn.pstmn.io/download/12.31.2/Postman-win64-Setup.exe", postmanSize, 40_000_000));

        Assert.True(await WaitFor(() => tracker.Current.DownloadedBytes == 40_000_000));
        Assert.Equal(postmanSize, tracker.Current.DownloadTotalBytes);
    }

    [Fact]
    public async Task Without_url_or_size_only_a_single_new_winget_job_is_taken_and_older_ones_are_not()
    {
        var fake = new FakeDo();
        fake.Set(Job("stale", "https://old.example.com/a.exe", 9_000_000, 4_000_000)); // an aborted earlier download
        using var tracker = Tracker(fake);
        tracker.Report("Downloading https://redirect.example.com/latest");

        await Task.Delay(120);
        Assert.Null(tracker.Current.DownloadedBytes); // the stale job is never this download

        fake.Set(Job("fresh", "https://final.example.com/b.exe", 70_000_000, 1_234));
        Assert.True(await WaitFor(() => tracker.Current.DownloadedBytes == 1_234));
        Assert.Equal(70_000_000, tracker.Current.DownloadTotalBytes);

        // Matched once, it sticks to that job.
        fake.Set(Job("later", "https://redirect.example.com/latest", 1, 1, status: 1));
        fake.Set(Job("fresh", "https://final.example.com/b.exe", 70_000_000, 50_000_000));
        Assert.True(await WaitFor(() => tracker.Current.DownloadedBytes == 50_000_000));
    }

    [Fact]
    public async Task Two_new_winget_jobs_that_cannot_be_told_apart_match_nothing()
    {
        var fake = new FakeDo();
        using var tracker = Tracker(fake);
        tracker.Report("Downloading https://redirect.example.com/latest");
        fake.Set(Job("a", "https://x.example.com/a.exe", 1_000, 10));
        fake.Set(Job("b", "https://y.example.com/b.exe", 2_000, 20));

        await Task.Delay(150);
        Assert.Equal(new InstallProgress(InstallPhase.Downloading), tracker.Current);
    }

    [Fact]
    public async Task When_do_cannot_be_read_the_tracker_stops_asking_and_uses_the_download_folder()
    {
        var fake = new FakeDo { Fail = true };
        var folder = Path.Combine(_root, "Microsoft.PowerShell.1.0");
        Directory.CreateDirectory(folder);
        using (var fs = File.Create(Path.Combine(folder, "DO1.tmp"))) fs.SetLength(4_096);
        using var tracker = Tracker(fake, wingetId: "Microsoft.PowerShell");

        tracker.Report("Downloading https://github.com/PowerShell/PowerShell/releases/download/v7.6.0/PowerShell-7.6.0-win-x64.msi");

        Assert.True(await WaitFor(() => tracker.Current.DownloadedBytes == 4_096));
        await Task.Delay(150);
        Assert.Equal(InstallProgressTracker.MaxDeliveryOptimizationFailures, fake.Calls);
    }

    [Fact]
    public async Task Do_is_only_asked_while_downloading()
    {
        var fake = new FakeDo();
        using var tracker = Tracker(fake);
        await Task.Delay(100);
        Assert.Equal(0, fake.Calls); // starting: no URL yet, nothing to ask about

        tracker.Report($"Downloading {VsCodeUrl}");
        Assert.True(await WaitFor(() => fake.Calls >= 3));
        tracker.Report("Successfully verified installer hash");
        await Task.Delay(60);
        var calls = fake.Calls;
        await Task.Delay(150);
        Assert.Equal(calls, fake.Calls);
    }

    [Fact]
    public async Task In_another_language_the_job_of_the_url_line_proves_the_download_and_its_end()
    {
        var fake = new FakeDo();
        using var tracker = Tracker(fake);
        tracker.Report($"Wird heruntergeladen {VsCodeUrl}");
        Assert.Equal(InstallPhase.Starting, tracker.Current.Phase);

        fake.Set(Job("vs", VsCodeUrl, VsCodeSize, 30_000_000));
        Assert.True(await WaitFor(() => tracker.Current.Phase == InstallPhase.Downloading));
        Assert.Equal(30_000_000, tracker.Current.DownloadedBytes);

        fake.Set(Job("vs", VsCodeUrl, VsCodeSize, VsCodeSize, status: 1));
        Assert.True(await WaitFor(() => tracker.Current.Phase == InstallPhase.Installing));
    }

    [Fact]
    public void Matching_prefers_the_exact_url_and_never_takes_a_finished_older_job()
    {
        var baseline = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "old-done", "old-running" };
        DeliveryOptimizationJob[] jobs =
        [
            Job("old-done", VsCodeUrl, VsCodeSize, VsCodeSize, status: 1),
            Job("noise", "https://cdn.example.com/x", VsCodeSize, 5),
        ];

        // A finished job of the same URL from an earlier run is not this download; the size rule then applies.
        Assert.Equal("noise", InstallProgressTracker.MatchJob(jobs, VsCodeUrl, baseline, VsCodeSize)?.FileId);
        Assert.NotEqual("old-done", InstallProgressTracker.MatchJob(jobs, VsCodeUrl, baseline, null)?.FileId);

        // An unfinished older job of the same URL is taken (DO may resume it), a new one first.
        DeliveryOptimizationJob[] resumed = [Job("old-running", VsCodeUrl, VsCodeSize, 10)];
        Assert.Equal("old-running", InstallProgressTracker.MatchJob(resumed, VsCodeUrl, baseline, null)?.FileId);
        DeliveryOptimizationJob[] both = [Job("old-running", VsCodeUrl, VsCodeSize, 10), Job("new", VsCodeUrl.ToUpperInvariant(), VsCodeSize, 1)];
        Assert.Equal("new", InstallProgressTracker.MatchJob(both, VsCodeUrl, baseline, null)?.FileId);

        // Someone else's job never counts by size or as the only new one.
        DeliveryOptimizationJob[] windowsUpdate = [Job("wu", "http://wu.example.com/kb.cab", VsCodeSize, 1, caller: "WU Client Download")];
        Assert.Null(InstallProgressTracker.MatchJob(windowsUpdate, VsCodeUrl, baseline, VsCodeSize));
        Assert.Null(InstallProgressTracker.MatchJob(windowsUpdate, VsCodeUrl, new HashSet<string>(), null));
        // Without a baseline there is no "new": nothing is guessed.
        Assert.Null(InstallProgressTracker.MatchJob([Job("w", "https://a.example.com/a", 1_000, 1)], VsCodeUrl, null, null));
    }

    [Fact]
    public void A_job_is_complete_by_its_status_or_its_bytes()
    {
        Assert.True(Job("a", null, 10, 3, status: 1).IsComplete);
        Assert.True(Job("a", null, 10, 10, status: 0).IsComplete);
        Assert.False(Job("a", null, 10, 3, status: 2).IsComplete);
        Assert.False(Job("a", null, null, 3).IsComplete);
        Assert.True(Job("a", null, 1, 0, caller: "windows package manager ").IsWinget);
    }

    [Fact]
    public void The_real_query_either_lists_jobs_or_fails_with_a_wmi_error()
    {
        try
        {
            var jobs = DeliveryOptimizationJobs.Query(CancellationToken.None);
            Assert.All(jobs, j => Assert.True(j.TotalBytesDownloaded >= 0));
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            // Delivery Optimization disabled or its CIM class missing (a build agent): the tracker then falls back.
        }
    }
}
