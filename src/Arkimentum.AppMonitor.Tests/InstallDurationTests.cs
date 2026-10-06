using Arkimentum.AppMonitor.Install;
using Arkimentum.AppMonitor.Ipc;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Service;
using Arkimentum.AppMonitor.Service.Policy;
using Arkimentum.AppMonitor.Service.State;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// How long installs take: the start time in the history, the expected duration from it, the duration in the log and
/// the cloud events, the progress fields over IPC and the pace at which progress reaches the trays.
/// </summary>
public class InstallDurationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 5, 22, 42, 0, TimeSpan.Zero);
    private const string Alice = "S-1-5-21-1-1001";
    private const string Bob = "S-1-5-21-1-1002";

    private static InstallHistoryEntry Timed(string appId, DateTimeOffset completed, int seconds, InstallContext context = InstallContext.System,
        string? sid = null, bool ok = true) => new()
    {
        AppId = appId,
        DisplayName = appId,
        Succeeded = ok,
        CompletedUtc = completed,
        StartedUtc = completed.AddSeconds(-seconds),
        Context = context,
        UserSid = sid,
    };

    // ---------------------------------------------------------------- history

    [Fact]
    public void A_history_entry_records_when_the_install_started()
    {
        var u = new PendingUpdate { AppId = "acrobat", DisplayName = "Adobe Acrobat Reader", AvailableVersion = "26.002.21996", InstallStartedUtc = T0 };

        var ok = InstallHistory.Succeeded(u, InstallResult.Ok(), T0.AddSeconds(244));
        var failed = InstallHistory.Failed(u, "boom", T0.AddSeconds(10));

        Assert.Equal(T0, ok.StartedUtc);
        Assert.Equal(TimeSpan.FromSeconds(244), InstallHistory.DurationOf(ok));
        Assert.Equal(T0, failed.StartedUtc);
        // An update that never started (a forced close that failed) has no start; nor has a clock that went backwards.
        Assert.Null(InstallHistory.Failed(new PendingUpdate { AppId = "x" }, "kill failed", T0).StartedUtc);
        Assert.Null(InstallHistory.Succeeded(u, InstallResult.Ok(), T0.AddSeconds(-1)).StartedUtc);
    }

    [Fact]
    public void Filling_in_icons_keeps_the_start_time()
    {
        IReadOnlyList<InstallHistoryEntry> history = [Timed("acrobat", T0, 200)];
        var filled = InstallHistory.FillIconPaths(history, [("acrobat", InstallContext.System, null, @"C:\Acrobat.exe")]);
        Assert.Equal(T0.AddSeconds(-200), filled![0].StartedUtc);
    }

    [Fact]
    public void The_expected_duration_is_the_median_of_the_last_three_successful_timed_installs()
    {
        var history = new List<InstallHistoryEntry>
        {
            Timed("acrobat", T0.AddDays(4), 207),
            Timed("acrobat", T0.AddDays(3), 900),                 // the laptop's slow day: the median ignores it
            Timed("acrobat", T0.AddDays(2), 30, ok: false),       // failed: not a duration of an install
            Timed("acrobat", T0.AddDays(1), 230),
            Timed("acrobat", T0, 10),                             // older than the last three
            new() { AppId = "acrobat", Succeeded = true, CompletedUtc = T0.AddDays(5) }, // untimed (before 1.1.42)
            Timed("7zip", T0.AddDays(4), 5),
        };

        Assert.Equal(230, InstallHistory.ExpectedSeconds(history, "Acrobat", InstallContext.System, null));
        Assert.Equal(5, InstallHistory.ExpectedSeconds(history, "7zip", InstallContext.System, null));
        Assert.Null(InstallHistory.ExpectedSeconds(history, "chrome", InstallContext.System, null));
        Assert.Null(InstallHistory.ExpectedSeconds(history, "acrobat", InstallContext.User, Alice));
        Assert.Null(InstallHistory.ExpectedSeconds(null, "acrobat", InstallContext.System, null));
    }

    [Fact]
    public void Two_samples_give_their_mean_rounded_to_whole_seconds()
    {
        var history = new List<InstallHistoryEntry> { Timed("putty", T0, 20), Timed("putty", T0.AddHours(1), 25) };
        Assert.Equal(23, InstallHistory.ExpectedSeconds(history, "putty", InstallContext.System, null)); // 22.5 -> 23

        var instant = new List<InstallHistoryEntry> { Timed("putty", T0, 0) };
        Assert.Equal(1, InstallHistory.ExpectedSeconds(instant, "putty", InstallContext.System, null));
    }

    [Fact]
    public void A_per_user_expectation_comes_from_that_users_installs_only()
    {
        var history = new List<InstallHistoryEntry>
        {
            Timed("vscode", T0, 40, InstallContext.User, Alice),
            Timed("vscode", T0, 90, InstallContext.User, Bob),
            Timed("vscode", T0, 300),
        };
        var update = new PendingUpdate { AppId = "vscode", Context = InstallContext.User, UserSid = Bob };

        Assert.Equal(40, InstallHistory.ExpectedSeconds(history, "vscode", InstallContext.User, Alice));
        Assert.Equal(90, InstallHistory.ExpectedSeconds(history, update));
        Assert.Equal(300, InstallHistory.ExpectedSeconds(history, "vscode", InstallContext.System, null));
    }

    [Theory]
    [InlineData(0, "0 s")]
    [InlineData(37, "37 s")]
    [InlineData(60, "1 min")]
    [InlineData(207, "3 min 27 s")]
    [InlineData(3600, "1 h")]
    [InlineData(3720, "1 h 2 min")]
    public void Durations_read_like_the_log_says_them(int seconds, string expected) =>
        Assert.Equal(expected, InstallHistory.FormatDuration(TimeSpan.FromSeconds(seconds)));

    // ---------------------------------------------------------------- the install's start and end on the tracked update

    [Fact]
    public void Marking_an_update_installing_starts_the_clock_and_ending_it_clears_the_progress()
    {
        var u = new PendingUpdate { AppId = "acrobat", AvailableVersion = "2.0", DownloadedBytes = 5, DownloadTotalBytes = 9 };

        PolicyEngine.MarkInstalling(u, T0, 240);

        Assert.Equal(UpdateState.Installing, u.State);
        Assert.Equal(T0, u.InstallStartedUtc);
        Assert.Equal(InstallPhase.Starting, u.InstallPhase);
        Assert.Null(u.DownloadedBytes);
        Assert.Null(u.DownloadTotalBytes);
        Assert.Equal(240, u.ExpectedInstallSeconds);

        u.InstallPhase = InstallPhase.Installing;
        PolicyEngine.MarkInstalled(u, InstallResult.Ok() with { InstalledVersion = "2.0" }, T0.AddMinutes(4));
        Assert.Null(u.InstallStartedUtc);
        Assert.Null(u.InstallPhase);

        PolicyEngine.MarkInstalling(u, T0);
        u.DownloadedBytes = 1;
        PolicyEngine.MarkFailed(u, "exit 1603", T0.AddMinutes(1));
        Assert.Null(u.InstallStartedUtc);
        Assert.Null(u.InstallPhase);
        Assert.Null(u.DownloadedBytes);
        Assert.Equal(240, u.ExpectedInstallSeconds); // the expectation is not progress; it stays
    }

    [Fact]
    public void The_clone_carries_the_progress_fields()
    {
        var u = new PendingUpdate
        {
            AppId = "acrobat", InstallStartedUtc = T0, InstallPhase = InstallPhase.Downloading, DownloadedBytes = 205_000_000,
            DownloadTotalBytes = 825_000_000, ExpectedInstallSeconds = 244,
        };
        var c = u.Clone();
        Assert.Equal((T0, InstallPhase.Downloading, 205_000_000L, 825_000_000L, 244),
            (c.InstallStartedUtc!.Value, c.InstallPhase!.Value, c.DownloadedBytes!.Value, c.DownloadTotalBytes!.Value, c.ExpectedInstallSeconds!.Value));
    }

    // ---------------------------------------------------------------- cloud event texts

    [Fact]
    public void The_cloud_events_state_the_duration_of_a_timed_install()
    {
        Assert.Equal("Adobe Acrobat Reader installed in 3 min 27 s",
            UpdateCoordinator.InstallSucceededEventText("Adobe Acrobat Reader", InstallResult.Ok("Installed successfully."), TimeSpan.FromSeconds(207)));
        Assert.Equal("7-Zip installed in 12 s (reboot required): Installed for all users; service: evidence",
            UpdateCoordinator.InstallSucceededEventText("7-Zip", InstallResult.Ok("Installed for all users; service: evidence", 0, reboot: true), TimeSpan.FromSeconds(12)));
        Assert.Equal("7-Zip installed", UpdateCoordinator.InstallSucceededEventText("7-Zip", InstallResult.Ok()));

        Assert.Equal("Adobe Acrobat Reader failed after 1 h 5 min: Install timed out",
            UpdateCoordinator.InstallFailedEventText("Adobe Acrobat Reader", "Install timed out", TimeSpan.FromMinutes(65)));
        Assert.Equal("7-Zip: exit code 1603", UpdateCoordinator.InstallFailedEventText("7-Zip", "exit code 1603"));
    }

    [Fact]
    public void The_backfill_still_reads_an_installed_line_with_a_duration()
    {
        var found = InstallHistory.ParseServiceLog(
        [
            "2026-10-05 22:42:01.000 +02:00 [INF] UpdateCoordinator: Installing Adobe Acrobat Reader 25.001 -> 26.002.21996 (Winget, System)",
            "2026-10-05 22:46:05.000 +02:00 [INF] UpdateCoordinator: Installed Adobe Acrobat Reader 26.002.21996 in 4 min 4 s: Installed successfully.",
        ]);

        Assert.Equal("26.002.21996", Assert.Single(found).ToVersion);
    }

    // ---------------------------------------------------------------- IPC

    [Fact]
    public void State_message_round_trips_the_progress_of_a_running_install()
    {
        var message = new StateMessage
        {
            Updates =
            [
                new PendingUpdate
                {
                    AppId = "acrobat", State = UpdateState.Installing, InstallStartedUtc = T0, InstallPhase = InstallPhase.Downloading,
                    DownloadedBytes = 221_000_000, DownloadTotalBytes = 865_000_000, ExpectedInstallSeconds = 244,
                },
            ],
        };

        var back = Assert.IsType<StateMessage>(IpcJson.Deserialize(IpcJson.Serialize(message)));
        var u = Assert.Single(back.Updates);

        Assert.Equal(T0, u.InstallStartedUtc);
        Assert.Equal(InstallPhase.Downloading, u.InstallPhase);
        Assert.Equal(221_000_000, u.DownloadedBytes);
        Assert.Equal(865_000_000, u.DownloadTotalBytes);
        Assert.Equal(244, u.ExpectedInstallSeconds);
    }

    [Fact]
    public void User_install_progress_round_trips_phase_and_bytes_and_an_older_tray_sends_only_the_status()
    {
        var line = IpcJson.Serialize(new UserInstallProgressMessage
        {
            UpdateKey = "acrobat|User|S-1", Status = "Downloading", Phase = InstallPhase.Verifying, DownloadedBytes = 10, DownloadTotalBytes = 20,
        });
        Assert.Contains("\"phase\":\"Verifying\"", line);
        var back = Assert.IsType<UserInstallProgressMessage>(IpcJson.Deserialize(line));
        Assert.Equal((InstallPhase.Verifying, 10L, 20L), (back.Phase!.Value, back.DownloadedBytes!.Value, back.DownloadTotalBytes!.Value));

        // 1.1.41 and older: status only.
        var old = Assert.IsType<UserInstallProgressMessage>(IpcJson.Deserialize("{\"$type\":\"userInstallProgress\",\"updateKey\":\"k\",\"status\":\"Installing\"}"));
        Assert.Null(old.Phase);
        Assert.Null(old.DownloadedBytes);
        Assert.Null(old.DownloadTotalBytes);
    }

    [Fact]
    public void A_state_message_from_an_older_service_has_no_progress()
    {
        const string line = "{\"$type\":\"state\",\"updates\":[{\"appId\":\"acrobat\",\"state\":\"Installing\"}],\"scanInProgress\":false,\"settings\":{\"monitoredApps\":[]}}";
        var u = Assert.Single(Assert.IsType<StateMessage>(IpcJson.Deserialize(line)).Updates);
        Assert.Null(u.InstallPhase);
        Assert.Null(u.InstallStartedUtc);
        Assert.Null(u.ExpectedInstallSeconds);
    }

    // ---------------------------------------------------------------- the pace of progress broadcasts and the log lines

    [Fact]
    public void Phase_changes_are_broadcast_at_once_and_byte_counts_at_most_every_two_seconds()
    {
        var feed = new InstallProgressFeed();
        const string key = "acrobat|System|-";
        feed.Begin(key);

        Assert.True(feed.Offer(key, new(InstallPhase.Downloading), T0).Broadcast);
        Assert.True(feed.Offer(key, new(InstallPhase.Downloading, null, 865_000_000), T0.AddMilliseconds(300)).Broadcast); // the size: at once
        Assert.False(feed.Offer(key, new(InstallPhase.Downloading, 0, 865_000_000), T0.AddSeconds(1)).Broadcast);
        Assert.True(feed.Offer(key, new(InstallPhase.Downloading, 205_000_000, 865_000_000), T0.AddSeconds(2.4)).Broadcast);
        Assert.False(feed.Offer(key, new(InstallPhase.Downloading, 221_000_000, 865_000_000), T0.AddSeconds(3)).Broadcast);
        Assert.True(feed.Offer(key, new(InstallPhase.Verifying, 865_000_000, 865_000_000), T0.AddSeconds(3.1)).Broadcast); // phase: at once
        Assert.False(feed.Offer(key, new(InstallPhase.Verifying, 865_000_000, 865_000_000), T0.AddSeconds(20)).Broadcast); // nothing moved

        // The latest snapshot is laid over a copy of the update while it installs, and only then.
        var copy = new PendingUpdate { AppId = "acrobat", Context = InstallContext.System, State = UpdateState.Installing };
        feed.Apply(copy);
        Assert.Equal((InstallPhase.Verifying, 865_000_000L), (copy.InstallPhase!.Value, copy.DownloadedBytes!.Value));
        var done = new PendingUpdate { AppId = "acrobat", Context = InstallContext.System, State = UpdateState.Installed };
        feed.Apply(done);
        Assert.Null(done.InstallPhase);

        feed.End(key);
        Assert.Null(feed.Latest(key));
        Assert.Equal((false, (string?)null), feed.Offer(key, new(InstallPhase.Installing), T0.AddSeconds(30))); // ended: ignored
    }

    [Fact]
    public void One_log_line_per_phase_and_the_size_once_it_is_known()
    {
        var feed = new InstallProgressFeed();
        const string key = "acrobat|System|-";
        feed.Begin(key);

        Assert.Equal("downloading", feed.Offer(key, new(InstallPhase.Downloading), T0).LogStep);
        Assert.Equal("downloading (825 MB)", feed.Offer(key, new(InstallPhase.Downloading, null, 865_075_200), T0.AddSeconds(1)).LogStep);
        Assert.Null(feed.Offer(key, new(InstallPhase.Downloading, 400_000_000, 865_075_200), T0.AddSeconds(10)).LogStep);
        Assert.Null(feed.Offer(key, new(InstallPhase.Downloading, 865_075_200, 865_075_200), T0.AddSeconds(36)).LogStep);
        Assert.Equal("verifying the download (downloaded 825 MB in 37 s)",
            feed.Offer(key, new(InstallPhase.Verifying, 865_075_200, 865_075_200), T0.AddSeconds(37)).LogStep);
        Assert.Equal("installing", feed.Offer(key, new(InstallPhase.Installing, 865_075_200, 865_075_200), T0.AddSeconds(38)).LogStep);
        Assert.Equal("checking the installed version", feed.Offer(key, new(InstallPhase.Checking), T0.AddSeconds(245)).LogStep);
    }

    [Fact]
    public void A_size_known_from_the_start_is_logged_with_the_first_line()
    {
        var feed = new InstallProgressFeed();
        feed.Begin("k");
        Assert.Equal("downloading (283 MB)", feed.Offer("k", new(InstallPhase.Downloading, null, 296_400_000), T0).LogStep);
        Assert.Null(feed.Offer("k", new(InstallPhase.Downloading, 5, 296_400_000), T0.AddSeconds(5)).LogStep);
        // Straight from downloading to installing (no verify line, e.g. localized output): the download summary moves along.
        Assert.Equal("installing (downloaded 283 MB in 9 s)", feed.Offer("k", new(InstallPhase.Installing, 296_400_000, 296_400_000), T0.AddSeconds(9)).LogStep);
    }

    [Theory]
    [InlineData(512L, "512 B")]
    [InlineData(655_360L, "640 KB")]
    [InlineData(865_075_200L, "825 MB")]
    [InlineData(1_288_490_189L, "1.2 GB")]
    public void Sizes_in_the_log_use_binary_units(long bytes, string expected) =>
        Assert.Equal(expected, InstallProgressFeed.FormatBytes(bytes));
}
