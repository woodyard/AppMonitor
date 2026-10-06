using Arkimentum.AppMonitor.Install;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Tray.Services;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// The line under an install's progress bar on the "Installing" toast and the update card. Long installs (Acrobat
/// Reader: an 825 MB download, then minutes of vendor installer) used to show a bare indeterminate bar; the text now
/// says which step runs, how far a download is when that can be seen, and how long it has taken against the usual.
/// A service older than the progress fields sends none of them, and the tray must then look exactly as before.
/// </summary>
public class TrayInstallProgressTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);
    private const long MB = 1024L * 1024L;

    private static PendingUpdate Installing(
        InstallPhase? phase = null, long? downloaded = null, long? total = null, DateTimeOffset? started = null, int? expected = null) => new()
    {
        AppId = "acrobat",
        DisplayName = "Adobe Acrobat Reader",
        State = UpdateState.Installing,
        InstallPhase = phase,
        DownloadedBytes = downloaded,
        DownloadTotalBytes = total,
        InstallStartedUtc = started,
        ExpectedInstallSeconds = expected,
    };

    // ---------------------------------------------------------------- older service, not installing

    [Fact]
    public void An_older_service_sends_nothing_and_nothing_is_said()
    {
        var view = InstallProgressText.Format(Installing(), T0);
        Assert.Null(view.Status);
        Assert.True(view.IsIndeterminate);
        Assert.Equal(string.Empty, view.PercentText);
    }

    [Fact]
    public void An_update_that_is_not_installing_has_no_progress_even_with_leftover_fields()
    {
        var update = Installing(InstallPhase.Downloading, 100 * MB, 825 * MB, T0);
        update.State = UpdateState.Available;
        Assert.Null(InstallProgressText.Format(update, T0).Status);
    }

    // ---------------------------------------------------------------- phases

    [Fact]
    public void Starting_verifying_and_checking_are_named_with_an_indeterminate_bar()
    {
        Assert.Equal(new InstallProgressView("Starting", null), InstallProgressText.Format(Installing(InstallPhase.Starting, started: T0), T0));
        Assert.Equal(new InstallProgressView("Verifying the download", null), InstallProgressText.Format(Installing(InstallPhase.Verifying, started: T0), T0));
        Assert.Equal(new InstallProgressView("Finishing up", null), InstallProgressText.Format(Installing(InstallPhase.Checking, started: T0), T0));
    }

    [Fact]
    public void A_download_under_way_shows_both_sizes_and_a_determinate_bar()
    {
        var view = InstallProgressText.Format(Installing(InstallPhase.Downloading, 312 * MB, 825 * MB, T0), T0.AddMinutes(1));
        Assert.Equal("Downloading · 312 of 825 MB", view.Status);
        Assert.False(view.IsIndeterminate);
        Assert.Equal(312d / 825d, view.Value!.Value, 6);
        Assert.Equal("37%", view.PercentText);
    }

    [Fact]
    public void A_known_size_with_nothing_on_disk_yet_shows_the_size_and_an_indeterminate_bar()
    {
        // Delivery Optimization keeps the file at 0 bytes for a while.
        Assert.Equal(new InstallProgressView("Downloading · 825 MB", null),
            InstallProgressText.Format(Installing(InstallPhase.Downloading, 0, 825 * MB, T0), T0));
        Assert.Equal(new InstallProgressView("Downloading · 825 MB", null),
            InstallProgressText.Format(Installing(InstallPhase.Downloading, null, 825 * MB, T0), T0));
    }

    [Fact]
    public void A_complete_download_is_not_drawn_as_a_full_bar_that_then_waits()
    {
        Assert.Equal(new InstallProgressView("Downloading · 825 MB", null),
            InstallProgressText.Format(Installing(InstallPhase.Downloading, 825 * MB, 825 * MB, T0), T0));
        Assert.Equal(new InstallProgressView("Downloading · 825 MB", null),
            InstallProgressText.Format(Installing(InstallPhase.Downloading, 900 * MB, 825 * MB, T0), T0));
    }

    [Fact]
    public void An_unknown_size_shows_the_count_alone_without_a_bar()
    {
        Assert.Equal(new InstallProgressView("Downloading · 312 MB so far", null),
            InstallProgressText.Format(Installing(InstallPhase.Downloading, 312 * MB, null, T0), T0));
        Assert.Equal(new InstallProgressView("Downloading", null),
            InstallProgressText.Format(Installing(InstallPhase.Downloading, null, null, T0), T0));
    }

    [Fact]
    public void Gigabytes_get_one_decimal_and_mixed_units_are_spelled_out()
    {
        Assert.Equal("Downloading · 1.2 of 3.4 GB",
            InstallProgressText.Format(Installing(InstallPhase.Downloading, 1229 * MB, 3482 * MB, T0), T0).Status);
        Assert.Equal("Downloading · 312 MB of 1.2 GB",
            InstallProgressText.Format(Installing(InstallPhase.Downloading, 312 * MB, 1229 * MB, T0), T0).Status);
        Assert.Equal("1 MB", InstallProgressText.Size(10_000));
        Assert.Equal("1.0 GB", InstallProgressText.Size(1024 * MB));
    }

    [Fact]
    public void Installing_says_how_long_so_far_and_how_long_it_usually_takes()
    {
        var view = InstallProgressText.Format(Installing(InstallPhase.Installing, started: T0, expected: 225), T0.AddSeconds(150));
        Assert.Equal("Installing · 2 min so far · usually about 4 min", view.Status);
        Assert.True(view.IsIndeterminate);
    }

    [Fact]
    public void A_short_usual_duration_is_not_mentioned()
    {
        Assert.Equal("Installing · less than a minute so far",
            InstallProgressText.Format(Installing(InstallPhase.Installing, started: T0, expected: 45), T0.AddSeconds(20)).Status);
    }

    [Fact]
    public void The_estimate_is_dropped_once_the_install_runs_three_times_as_long()
    {
        var update = Installing(InstallPhase.Installing, started: T0, expected: 240);
        Assert.Equal("Installing · 11 min so far · usually about 4 min", InstallProgressText.Format(update, T0.AddMinutes(11).AddSeconds(59)).Status);
        Assert.Equal("Installing · 12 min so far", InstallProgressText.Format(update, T0.AddMinutes(12)).Status);
    }

    [Fact]
    public void Without_a_start_time_there_is_no_time_so_far_but_the_usual_one_stays()
    {
        Assert.Equal("Installing · usually about 4 min", InstallProgressText.Format(Installing(InstallPhase.Installing, expected: 240), T0).Status);
        Assert.Equal("Installing", InstallProgressText.Format(Installing(InstallPhase.Installing), T0).Status);
    }

    [Fact]
    public void A_start_time_without_a_phase_reads_as_installing()
    {
        Assert.Equal("Installing · 3 min so far", InstallProgressText.Format(Installing(started: T0), T0.AddMinutes(3)).Status);
    }

    [Fact]
    public void A_clock_behind_the_service_never_shows_negative_time()
    {
        Assert.Equal("Installing · less than a minute so far",
            InstallProgressText.Format(Installing(InstallPhase.Installing, started: T0), T0.AddSeconds(-30)).Status);
    }

    // ---------------------------------------------------------------- the tray's own reading of a user-context install

    [Fact]
    public void The_agents_own_reading_wins_and_works_with_an_older_service()
    {
        // An older service knows nothing (no phase, and the state may still be Scheduled when the tray starts).
        var update = Installing(expected: 300);
        update.State = UpdateState.Scheduled;
        var local = new LocalInstallProgress(InstallPhase.Downloading, 100 * MB, 200 * MB, T0);
        var view = InstallProgressText.Format(update, T0.AddSeconds(30), local);
        Assert.Equal("Downloading · 100 of 200 MB", view.Status);
        Assert.Equal(0.5, view.Value!.Value, 6);

        var installing = InstallProgressText.Format(update, T0.AddMinutes(2), local with { Phase = InstallPhase.Installing });
        Assert.Equal("Installing · 2 min so far · usually about 5 min", installing.Status);
    }

    // ---------------------------------------------------------------- queued

    [Fact]
    public void A_queued_update_says_how_long_it_usually_takes_when_that_is_known()
    {
        var update = Installing(expected: 225);
        update.State = UpdateState.Scheduled;
        Assert.Equal("Usually takes about 4 min", InstallProgressText.QueuedHint(update));

        update.ExpectedInstallSeconds = 30;
        Assert.Null(InstallProgressText.QueuedHint(update));
        update.ExpectedInstallSeconds = null;
        Assert.Null(InstallProgressText.QueuedHint(update));

        update.ExpectedInstallSeconds = 225;
        update.State = UpdateState.Available;
        Assert.Null(InstallProgressText.QueuedHint(update));
    }

    // ---------------------------------------------------------------- durations

    [Theory]
    [InlineData(0, "less than a minute")]
    [InlineData(59, "less than a minute")]
    [InlineData(60, "1 min")]
    [InlineData(299, "4 min")]
    [InlineData(3599, "59 min")]
    [InlineData(3600, "1 h")]
    [InlineData(3900, "1 h 5 min")]
    [InlineData(7200, "2 h")]
    public void Durations_are_short_and_whole_minutes(int seconds, string expected) =>
        Assert.Equal(expected, InstallProgressText.Duration(TimeSpan.FromSeconds(seconds)));

    // ---------------------------------------------------------------- clamp

    [Fact]
    public void A_bar_never_goes_backwards_within_one_phase()
    {
        var clamp = new InstallProgressClamp();
        Assert.Equal<double?>(0.5, clamp.Apply("k", InstallPhase.Downloading, 100, 0.5));
        Assert.Equal<double?>(0.5, clamp.Apply("k", InstallPhase.Downloading, 100, 0.4));
        Assert.Equal<double?>(0.7, clamp.Apply("k", InstallPhase.Downloading, 100, 0.7));
        // An indeterminate moment does not reset it.
        Assert.Null(clamp.Apply("k", InstallPhase.Downloading, 100, null));
        Assert.Equal<double?>(0.7, clamp.Apply("k", InstallPhase.Downloading, 100, 0.6));
        // Another update is another bar.
        Assert.Equal<double?>(0.1, clamp.Apply("other", InstallPhase.Downloading, 100, 0.1));
    }

    [Fact]
    public void A_new_phase_a_new_total_or_a_new_attempt_starts_a_new_bar()
    {
        var clamp = new InstallProgressClamp();
        clamp.Apply("k", InstallPhase.Downloading, 100, 0.8);
        Assert.Equal<double?>(0.2, clamp.Apply("k", InstallPhase.Downloading, 200, 0.2));
        Assert.Equal<double?>(0.1, clamp.Apply("k", InstallPhase.Installing, 200, 0.1));
        clamp.Apply("k", InstallPhase.Downloading, 100, 0.9);
        clamp.Forget("k");
        Assert.Equal<double?>(0.3, clamp.Apply("k", InstallPhase.Downloading, 100, 0.3));
    }

    [Fact]
    public void Values_outside_the_bar_are_clamped_to_it()
    {
        var clamp = new InstallProgressClamp();
        Assert.Equal<double?>(1, clamp.Apply("k", InstallPhase.Downloading, 100, 1.4));
        Assert.Equal<double?>(0, clamp.Apply("j", InstallPhase.Downloading, 100, -0.2));
    }

    // ---------------------------------------------------------------- throttle (user-context installs run by the tray)

    private static InstallProgress Download(long bytes) => new(InstallPhase.Downloading, bytes, 1000);

    [Fact]
    public void The_first_snapshot_and_every_phase_change_go_out_at_once()
    {
        var throttle = new InstallProgressThrottle(TimeSpan.FromSeconds(2));
        Assert.True(throttle.Offer(new InstallProgress(InstallPhase.Starting), T0));
        Assert.True(throttle.Offer(Download(0), T0.AddMilliseconds(100)));
        Assert.True(throttle.Offer(new InstallProgress(InstallPhase.Verifying), T0.AddMilliseconds(200)));
        Assert.True(throttle.Offer(new InstallProgress(InstallPhase.Installing), T0.AddMilliseconds(300)));
    }

    [Fact]
    public void Byte_counts_go_out_at_most_every_interval_and_the_held_one_is_not_lost()
    {
        var throttle = new InstallProgressThrottle(TimeSpan.FromSeconds(2));
        Assert.True(throttle.Offer(Download(100), T0));
        Assert.False(throttle.Offer(Download(200), T0.AddSeconds(1)));
        Assert.False(throttle.Offer(Download(300), T0.AddSeconds(1.5)));
        Assert.Equal(TimeSpan.FromSeconds(0.5), throttle.DueIn(T0.AddSeconds(1.5)));

        // Not due yet; then the latest held reading, once.
        Assert.Null(throttle.TakeDue(T0.AddSeconds(1.9)));
        Assert.Equal(Download(300), throttle.TakeDue(T0.AddSeconds(2)));
        Assert.Null(throttle.TakeDue(T0.AddSeconds(5)));
        Assert.Null(throttle.DueIn(T0.AddSeconds(5)));

        // After the interval a moving count goes straight out.
        Assert.True(throttle.Offer(Download(400), T0.AddSeconds(4.5)));
    }

    [Fact]
    public void A_repeated_snapshot_is_dropped()
    {
        var throttle = new InstallProgressThrottle(TimeSpan.FromSeconds(2));
        Assert.True(throttle.Offer(Download(100), T0));
        Assert.False(throttle.Offer(Download(100), T0.AddSeconds(1)));
        Assert.Null(throttle.DueIn(T0.AddSeconds(1)));
        Assert.False(throttle.Offer(Download(100), T0.AddSeconds(10)));
    }

    [Fact]
    public void A_phase_change_clears_a_held_byte_count()
    {
        var throttle = new InstallProgressThrottle(TimeSpan.FromSeconds(2));
        throttle.Offer(Download(100), T0);
        throttle.Offer(Download(900), T0.AddSeconds(1));
        Assert.True(throttle.Offer(new InstallProgress(InstallPhase.Verifying), T0.AddSeconds(1.2)));
        Assert.Null(throttle.TakeDue(T0.AddSeconds(10)));
    }
}
