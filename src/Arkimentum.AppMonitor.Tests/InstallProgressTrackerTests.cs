using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Arkimentum.AppMonitor.Install;
using Arkimentum.AppMonitor.Models;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// The install progress tracker: winget's step lines as they arrive on this machine (Acrobat Reader, 2026-10-05), the
/// download folder Delivery Optimization writes into, and the graceful fallbacks for localized output and missing sizes.
/// </summary>
public sealed class InstallProgressTrackerTests : IDisposable
{
    private const string AcrobatId = "Adobe.Acrobat.Reader.64-bit";
    private const string AcrobatVersion = "26.002.21996";
    private const string AcrobatUrl = "https://ardownload2.adobe.com/pub/adobe/acrobat/win/AcrobatDC/2600221996/AcroRdrDCx642600221996_MUI.exe";

    /// <summary>The real output of <c>winget upgrade</c> for Acrobat Reader with stdout redirected (CRLF).</summary>
    private static readonly string[] AcrobatLines =
    [
        $"Found Adobe Acrobat Reader (64-bit) [{AcrobatId}] Version {AcrobatVersion}",
        "This application is licensed to you by its owner.",
        "Microsoft is not responsible for, nor does it grant any licenses to, third-party packages.",
        $"Downloading {AcrobatUrl}",
        "Successfully verified installer hash",
        "Starting package install...",
        "Successfully installed",
    ];

    /// <summary>The existing folder/HEAD tests must not see this machine's real Delivery Optimization jobs.</summary>
    private static readonly Func<CancellationToken, IReadOnlyList<DeliveryOptimizationJob>> NoDo = InstallProgressTracker.NoDeliveryOptimization;

    private static readonly TimeSpan NoPolling = TimeSpan.FromHours(1);
    private static readonly TimeSpan FastPolling = TimeSpan.FromMilliseconds(25);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "AppMonitorTests", "WinGet-" + Guid.NewGuid().ToString("N"));
    private readonly List<InstallProgress> _seen = [];

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void OnChange(InstallProgress p) { lock (_seen) _seen.Add(p); }

    private List<InstallProgress> Seen() { lock (_seen) return [.. _seen]; }

    private static Func<string, CancellationToken, Task<long?>> Size(long? size, List<string>? asked = null) => (url, _) =>
    {
        if (asked is not null) lock (asked) asked.Add(url);
        return Task.FromResult(size);
    };

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

    private string PackageFolder(string id = AcrobatId, string version = AcrobatVersion)
    {
        var dir = Path.Combine(_root, $"{id}.{version}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Grow(string path, long length)
    {
        using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        fs.SetLength(length);
    }

    [Fact]
    public void The_real_winget_lines_walk_through_every_phase_and_the_url_is_asked_for_its_size()
    {
        var asked = new List<string>();
        using var tracker = new InstallProgressTracker(OnChange, contentLength: Size(825_000_000, asked), wingetTempRoots: [_root], deliveryOptimizationJobs: NoDo, pollInterval: NoPolling);
        Assert.Equal(InstallPhase.Starting, tracker.Current.Phase);

        foreach (var line in AcrobatLines) tracker.Report(line + "\r\n");

        var phases = Seen().Select(p => p.Phase).Distinct().ToList();
        Assert.Equal([InstallPhase.Downloading, InstallPhase.Verifying, InstallPhase.Installing, InstallPhase.Checking], phases);
        Assert.Equal([AcrobatUrl], asked);
        // The size arrived while downloading and is carried on.
        Assert.Contains(Seen(), p => p.Phase == InstallPhase.Downloading && p.DownloadTotalBytes == 825_000_000);
        Assert.Equal(InstallPhase.Checking, tracker.Current.Phase);
        Assert.Equal(825_000_000, tracker.Current.DownloadTotalBytes);
    }

    [Fact]
    public void The_found_and_licence_lines_change_nothing()
    {
        using var tracker = new InstallProgressTracker(OnChange, contentLength: Size(null), wingetTempRoots: [_root], deliveryOptimizationJobs: NoDo, pollInterval: NoPolling);
        foreach (var line in AcrobatLines.Take(3)) tracker.Report(line);
        Assert.Empty(Seen());
        Assert.Equal(new InstallProgress(InstallPhase.Starting), tracker.Current);
    }

    [Fact]
    public void Spinner_and_progress_bar_artefacts_split_by_carriage_returns_are_ignored()
    {
        using var tracker = new InstallProgressTracker(OnChange, contentLength: Size(null), wingetTempRoots: [_root], deliveryOptimizationJobs: NoDo, pollInterval: NoPolling);

        tracker.Report("\r-\r\\\r|\r/\r");
        Assert.Empty(Seen());

        tracker.Report($"\r   \r-\rDownloading {AcrobatUrl}\r  ██████████▒▒▒▒  205 MB /  825 MB\r");
        tracker.Report("  ███████████████  825 MB /  825 MB\r\n\r|\rSuccessfully verified installer hash\r\n");
        tracker.Report("\u2800\u2801\r Starting package install...\r\n");

        Assert.Equal([InstallPhase.Downloading, InstallPhase.Verifying, InstallPhase.Installing], Seen().Select(p => p.Phase).ToList());
    }

    [Fact]
    public void Localized_or_unknown_lines_leave_it_at_starting_and_a_url_line_is_only_asked_for_its_size()
    {
        var asked = new List<string>();
        using var tracker = new InstallProgressTracker(OnChange, contentLength: Size(825_000_000, asked), wingetTempRoots: [_root], deliveryOptimizationJobs: NoDo, pollInterval: NoPolling);

        tracker.Report($"Gefunden Adobe Acrobat Reader (64-bit) [{AcrobatId}] Version {AcrobatVersion}");
        tracker.Report("Diese Anwendung wird Ihnen von ihrem Besitzer lizenziert.");
        tracker.Report($"Wird heruntergeladen {AcrobatUrl}");
        tracker.Report("Der Installer-Hash wurde erfolgreich überprüft");
        tracker.Report("Paketinstallation wird gestartet...");

        Assert.Equal(InstallPhase.Starting, tracker.Current.Phase);
        Assert.Equal([AcrobatUrl], asked);
        // The size alone is no phase: nothing was claimed, and the total waits in the snapshot for a download to show.
        Assert.All(Seen(), p => Assert.Equal(InstallPhase.Starting, p.Phase));
    }

    [Fact]
    public async Task The_download_folder_supplies_the_byte_counts_while_downloading()
    {
        var folder = PackageFolder();
        using var tracker = new InstallProgressTracker(OnChange, contentLength: Size(10_000), wingetTempRoots: [_root], deliveryOptimizationJobs: NoDo, pollInterval: FastPolling,
            wingetId: AcrobatId, version: AcrobatVersion);
        tracker.Report($"Downloading {AcrobatUrl}");

        var file = Path.Combine(folder, "DO1234ABCD.tmp");
        Grow(file, 0);
        Assert.True(await WaitFor(() => tracker.Current.DownloadedBytes == 0));
        Grow(file, 4_000);
        Assert.True(await WaitFor(() => tracker.Current.DownloadedBytes == 4_000));
        Grow(file, 10_000);
        Assert.True(await WaitFor(() => tracker.Current.DownloadedBytes == 10_000));

        Assert.Equal(new InstallProgress(InstallPhase.Downloading, 10_000, 10_000), tracker.Current);
        // Each moved count was raised once; an unchanged one is not raised again.
        var counts = Seen().Where(p => p.DownloadedBytes is not null).Select(p => p.DownloadedBytes).ToList();
        Assert.Equal(counts.Distinct().Count(), counts.Count);
    }

    [Fact]
    public async Task The_found_line_names_the_folder_when_the_caller_did_not()
    {
        var folder = PackageFolder();
        Grow(Path.Combine(folder, "DO1.tmp"), 1_500);
        using var tracker = new InstallProgressTracker(OnChange, contentLength: Size(null), wingetTempRoots: [_root], deliveryOptimizationJobs: NoDo, pollInterval: FastPolling);

        tracker.Report(AcrobatLines[0]);
        tracker.Report($"Downloading {AcrobatUrl}");

        Assert.True(await WaitFor(() => tracker.Current.DownloadedBytes == 1_500));
        Assert.Null(tracker.Current.DownloadTotalBytes);
    }

    [Fact]
    public async Task Without_understood_lines_a_growing_download_counts_as_downloading_and_its_hash_name_as_installing()
    {
        var folder = PackageFolder();
        var file = Path.Combine(folder, "DO5678.tmp");
        Grow(file, 0);
        using var tracker = new InstallProgressTracker(OnChange, contentLength: Size(null), wingetTempRoots: [_root], deliveryOptimizationJobs: NoDo, pollInterval: FastPolling,
            wingetId: AcrobatId, version: AcrobatVersion);

        await Task.Delay(150);
        Assert.Equal(InstallPhase.Starting, tracker.Current.Phase); // a file that does not grow proves nothing

        Grow(file, 3_000);
        Assert.True(await WaitFor(() => tracker.Current.Phase == InstallPhase.Downloading));
        Assert.Equal(3_000, tracker.Current.DownloadedBytes);

        File.Move(file, Path.Combine(folder, new string('a', 32) + new string('F', 32)));
        Assert.True(await WaitFor(() => tracker.Current.Phase == InstallPhase.Installing));
    }

    [Fact]
    public async Task Without_understood_lines_a_download_that_reaches_the_announced_size_counts_as_installing()
    {
        var folder = PackageFolder();
        var file = Path.Combine(folder, "DO9.tmp");
        Grow(file, 100);
        using var tracker = new InstallProgressTracker(OnChange, contentLength: Size(5_000), wingetTempRoots: [_root], deliveryOptimizationJobs: NoDo, pollInterval: FastPolling,
            wingetId: AcrobatId, version: AcrobatVersion);
        tracker.Report($"Wird heruntergeladen {AcrobatUrl}");

        await Task.Delay(100);
        Grow(file, 2_000);
        Assert.True(await WaitFor(() => tracker.Current.Phase == InstallPhase.Downloading));
        Assert.Equal(5_000, tracker.Current.DownloadTotalBytes);
        Grow(file, 5_000);
        Assert.True(await WaitFor(() => tracker.Current.Phase == InstallPhase.Installing));
    }

    [Fact]
    public async Task A_leftover_file_that_does_not_grow_claims_nothing()
    {
        var folder = PackageFolder();
        Grow(Path.Combine(folder, new string('0', 64)), 9_000);
        using var tracker = new InstallProgressTracker(OnChange, contentLength: Size(null), wingetTempRoots: [_root], deliveryOptimizationJobs: NoDo, pollInterval: FastPolling,
            wingetId: AcrobatId, version: AcrobatVersion);

        await Task.Delay(250);
        Assert.Equal(new InstallProgress(InstallPhase.Starting), tracker.Current);
        Assert.Empty(Seen());
    }

    [Fact]
    public void A_failing_size_lookup_leaves_the_total_unknown()
    {
        using var tracker = new InstallProgressTracker(OnChange,
            contentLength: (_, _) => throw new HttpRequestException("405 Method Not Allowed"),
            wingetTempRoots: [_root], deliveryOptimizationJobs: NoDo, pollInterval: NoPolling);

        tracker.Report($"Downloading {AcrobatUrl}");

        Assert.Equal(new InstallProgress(InstallPhase.Downloading), tracker.Current);
    }

    [Fact]
    public async Task A_size_lookup_that_never_answers_does_not_hold_anything_up()
    {
        var never = new TaskCompletionSource<long?>();
        using var tracker = new InstallProgressTracker(OnChange, contentLength: (_, _) => never.Task, wingetTempRoots: [_root], deliveryOptimizationJobs: NoDo, pollInterval: NoPolling);

        tracker.Report($"Downloading {AcrobatUrl}");
        tracker.Report("Successfully verified installer hash");
        await Task.Yield();

        Assert.Equal(InstallPhase.Verifying, tracker.Current.Phase);
        Assert.Null(tracker.Current.DownloadTotalBytes);
    }

    [Fact]
    public async Task Dispose_stops_the_polling_and_the_callbacks()
    {
        var folder = PackageFolder();
        var file = Path.Combine(folder, "DO1.tmp");
        Grow(file, 10);
        var tracker = new InstallProgressTracker(OnChange, contentLength: Size(null), wingetTempRoots: [_root], deliveryOptimizationJobs: NoDo, pollInterval: FastPolling,
            wingetId: AcrobatId, version: AcrobatVersion);
        tracker.Report($"Downloading {AcrobatUrl}");
        Assert.True(await WaitFor(() => tracker.Current.DownloadedBytes == 10));

        tracker.Dispose();
        await Task.Delay(60); // a poll that was already running may finish
        var before = Seen().Count;
        Grow(file, 5_000);
        tracker.Report("Successfully verified installer hash");
        await Task.Delay(200);

        Assert.Equal(before, Seen().Count);
        tracker.Dispose(); // twice is harmless
    }

    [Fact]
    public void A_throwing_handler_never_reaches_the_install()
    {
        using var tracker = new InstallProgressTracker(_ => throw new InvalidOperationException("ui gone"), contentLength: Size(null),
            wingetTempRoots: [_root], deliveryOptimizationJobs: NoDo, pollInterval: NoPolling);

        tracker.Report($"Downloading {AcrobatUrl}");
        tracker.MarkChecking();

        Assert.Equal(InstallPhase.Checking, tracker.Current.Phase);
    }

    [Fact]
    public void Phases_may_go_back_when_winget_runs_again()
    {
        using var tracker = new InstallProgressTracker(OnChange, contentLength: Size(null), wingetTempRoots: [_root], deliveryOptimizationJobs: NoDo, pollInterval: NoPolling);
        // WingetReplaceOnMismatch: an upgrade refused, then an uninstall and an install.
        tracker.Report("Found Oh My Posh [JanDeDobbeleer.OhMyPosh] Version 26.1.0");
        tracker.Report("Downloading https://github.com/JanDeDobbeleer/oh-my-posh/releases/download/v26.1.0/install-x64.msix");
        tracker.Report("Successfully verified installer hash");
        tracker.Report("Starting package install...");
        tracker.MarkChecking();
        tracker.Report("Downloading https://github.com/JanDeDobbeleer/oh-my-posh/releases/download/v26.1.0/install-x64.msix");

        Assert.Equal(InstallPhase.Downloading, tracker.Current.Phase);
        Assert.Equal(
            [InstallPhase.Downloading, InstallPhase.Verifying, InstallPhase.Installing, InstallPhase.Checking, InstallPhase.Downloading],
            Seen().Select(p => p.Phase).ToList());
    }

    [Fact]
    public void The_web_providers_own_messages_map_too()
    {
        using var tracker = new InstallProgressTracker(OnChange, contentLength: Size(null), wingetTempRoots: [_root], deliveryOptimizationJobs: NoDo, pollInterval: NoPolling);
        tracker.Report("Downloading...");
        tracker.Report("Downloading setup.exe: 45%");
        tracker.Report("Verifying download...");
        tracker.Report("Running setup.exe...");

        Assert.Equal([InstallPhase.Downloading, InstallPhase.Verifying, InstallPhase.Installing], Seen().Select(p => p.Phase).ToList());
    }

    [Fact]
    public void The_folder_of_another_package_with_the_same_prefix_is_not_measured()
    {
        Grow(Path.Combine(PackageFolder("Mozilla.Firefox.MSIX", "157.0.0.0"), "DO1.tmp"), 7_000);

        Assert.Null(InstallProgressTracker.MeasureDownload([_root], "Mozilla.Firefox", null));

        Grow(Path.Combine(PackageFolder("Mozilla.Firefox", "157.0"), "DO2.tmp"), 3_000);
        Assert.Equal(3_000, InstallProgressTracker.MeasureDownload([_root], "Mozilla.Firefox", null)?.Bytes);
        // A known version that has no folder of its own falls back to the id's newest folder.
        Assert.Equal(3_000, InstallProgressTracker.MeasureDownload([_root], "Mozilla.Firefox", "158.0")?.Bytes);
        Assert.Null(InstallProgressTracker.MeasureDownload([Path.Combine(_root, "missing")], "Mozilla.Firefox", null));
    }

    [Fact]
    public void The_largest_file_counts_and_a_hash_name_is_recognised()
    {
        var folder = PackageFolder();
        Grow(Path.Combine(folder, "small.tmp"), 10);
        Grow(Path.Combine(folder, new string('b', 64)), 900);

        var measured = InstallProgressTracker.MeasureDownload([_root], AcrobatId, AcrobatVersion);

        Assert.Equal(900, measured?.Bytes);
        Assert.True(measured?.RenamedToHash);
    }

    [Fact]
    public void The_default_roots_include_the_system_temp_folders()
    {
        var roots = InstallProgressTracker.DefaultWingetTempRoots();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.Contains(Path.Combine(windows, "Temp", "WinGet"), roots, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(Path.Combine(windows, "SystemTemp", "WinGet"), roots, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(roots.Count, roots.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    // ---------------------------------------------------------------- the built-in size lookup

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public readonly List<string> Requests = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add($"{request.Method} {request.Headers.Range}");
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage WithLength(HttpStatusCode status, long? length, ContentRangeHeaderValue? range = null)
    {
        var content = new ByteArrayContent([]);
        content.Headers.ContentLength = length;
        if (range is not null) content.Headers.ContentRange = range;
        return new HttpResponseMessage(status) { Content = content };
    }

    [Fact]
    public async Task The_size_lookup_reads_the_content_length_of_a_head_request()
    {
        var handler = new FakeHandler(_ => WithLength(HttpStatusCode.OK, 865_000_000));
        var resolve = InstallProgressTracker.CreateContentLengthResolver(null, handler);

        Assert.Equal(865_000_000, await resolve(AcrobatUrl, CancellationToken.None));
        Assert.Equal(["HEAD "], handler.Requests);
    }

    [Fact]
    public async Task The_size_lookup_falls_back_to_a_one_byte_range_request()
    {
        var handler = new FakeHandler(r => r.Method == HttpMethod.Head
            ? new HttpResponseMessage(HttpStatusCode.MethodNotAllowed)
            : WithLength(HttpStatusCode.PartialContent, 1, new ContentRangeHeaderValue(0, 0, 296_400_000)));
        var resolve = InstallProgressTracker.CreateContentLengthResolver(null, handler);

        Assert.Equal(296_400_000, await resolve(AcrobatUrl, CancellationToken.None));
        Assert.Equal(["HEAD ", "GET bytes=0-0"], handler.Requests);
    }

    [Fact]
    public async Task The_size_lookup_answers_null_when_the_server_does_not_say_or_fails()
    {
        var silent = InstallProgressTracker.CreateContentLengthResolver(null, new FakeHandler(_ => WithLength(HttpStatusCode.OK, null)));
        Assert.Null(await silent(AcrobatUrl, CancellationToken.None));

        var failing = InstallProgressTracker.CreateContentLengthResolver(null, new FakeHandler(_ => throw new HttpRequestException("no route")));
        Assert.Null(await failing(AcrobatUrl, CancellationToken.None));

        Assert.Null(await silent("not a url", CancellationToken.None));
        Assert.Null(await silent("file:///C:/Windows/notepad.exe", CancellationToken.None));
    }
}
