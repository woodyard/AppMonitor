using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Service.State;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// The one-time backfill that rebuilds install history from the service log, so installs made before the history
/// existed show in the tray. The sample lines are real ones from CPC-hsk-HSNE00X (2026-09-22/23).
/// </summary>
public class InstallHistoryBackfillTests
{
    private const string Sid = "S-1-12-1-1760109370-1192172739-1438914203-1853124917";

    private static readonly string[] RealLog =
    [
        "2026-09-22 08:38:03.846 +00:00 [INF] UpdateCoordinator: Installing Microsoft Azure Storage Explorer 1.45.0 -> 1.46.0 (Winget, User for " + Sid + ")",
        "2026-09-22 08:38:04.100 +00:00 [INF] UpdateCoordinator: Notified Installing for Microsoft Azure Storage Explorer: being installed.",
        "2026-09-22 08:38:50.273 +00:00 [INF] UpdateCoordinator: Installed Microsoft Azure Storage Explorer 1.46.0: Installed successfully.",
        "2026-09-23 04:29:43.368 +00:00 [INF] UpdateCoordinator: Installing Google Chrome 153.0.8010.53 -> 154.0.8037.58 (Winget, System)",
        "2026-09-23 04:31:26.637 +00:00 [INF] UpdateChecker: Google Chrome: installed successfully (version 154.0.8037.58). Installed successfully.",
        "2026-09-23 04:31:26.641 +00:00 [INF] UpdateCoordinator: Installed Google Chrome 154.0.8037.58: Installed successfully.",
        "2026-09-23 11:48:14.464 +00:00 [INF] UpdateCoordinator: Installing Mozilla Firefox 156.0.0.0 -> 156.0.1 (Winget, User for " + Sid + ")",
        "2026-09-23 11:48:51.835 +00:00 [ERR] UpdateCoordinator: Install of Mozilla Firefox failed (exit 0): winget could not upgrade 'Mozilla.Firefox' (User scope): no applicable upgrade found",
    ];

    [Fact]
    public void Real_log_lines_become_three_entries_with_versions_context_and_times()
    {
        var entries = InstallHistory.ParseServiceLog(RealLog);

        Assert.Equal(3, entries.Count);

        var explorer = entries[0];
        Assert.Equal("Microsoft Azure Storage Explorer", explorer.DisplayName);
        Assert.True(explorer.Succeeded);
        Assert.Equal("1.45.0", explorer.FromVersion);
        Assert.Equal("1.46.0", explorer.ToVersion);
        Assert.Equal(InstallContext.User, explorer.Context);
        Assert.Equal(Sid, explorer.UserSid);
        Assert.Equal(new DateTimeOffset(2026, 9, 22, 8, 38, 50, 273, TimeSpan.Zero), explorer.CompletedUtc);

        var chrome = entries[1];
        Assert.True(chrome.Succeeded);
        Assert.Equal("153.0.8010.53", chrome.FromVersion);
        Assert.Equal("154.0.8037.58", chrome.ToVersion);
        Assert.Equal(InstallContext.System, chrome.Context);
        Assert.Null(chrome.UserSid);

        var firefox = entries[2];
        Assert.False(firefox.Succeeded);
        Assert.Equal("156.0.1", firefox.ToVersion);
        Assert.StartsWith("winget could not upgrade 'Mozilla.Firefox'", firefox.Message);
    }

    [Fact]
    public void The_app_id_comes_from_the_mapping_and_falls_back_to_the_display_name()
    {
        var entries = InstallHistory.ParseServiceLog(RealLog, name => name == "Google Chrome" ? "chrome" : null);

        Assert.Equal("chrome", entries.Single(e => e.DisplayName == "Google Chrome").AppId);
        Assert.Equal("Mozilla Firefox", entries.Single(e => e.DisplayName == "Mozilla Firefox").AppId);
    }

    [Fact]
    public void A_start_without_a_result_is_dropped_and_the_next_install_still_pairs()
    {
        var entries = InstallHistory.ParseServiceLog(
        [
            "2026-09-23 10:00:00.000 +00:00 [INF] UpdateCoordinator: Installing 7-Zip 26.02 -> 26.03 (Winget, System)",
            // the service restarted here: no result for 7-Zip
            "2026-09-23 10:05:00.000 +00:00 [INF] UpdateCoordinator: Installing Notepad++ 8.9.1 -> 8.9.8 (Winget, System)",
            "2026-09-23 10:05:30.000 +00:00 [INF] UpdateCoordinator: Installed Notepad++ 8.9.8: Installed successfully.",
        ]);

        var only = Assert.Single(entries);
        Assert.Equal("Notepad++", only.DisplayName);
        Assert.Equal("8.9.8", only.ToVersion);
    }

    [Fact]
    public void An_unknown_installed_version_and_a_reboot_suffix_are_handled()
    {
        var entries = InstallHistory.ParseServiceLog(
        [
            "2026-09-23 10:00:00.000 +00:00 [INF] UpdateCoordinator: Installing Contoso Tool  -> 2.0 (Web, System)",
            "2026-09-23 10:01:00.000 +00:00 [INF] UpdateCoordinator: Installed Contoso Tool 2.0 (reboot required): Installed.",
        ]);

        var only = Assert.Single(entries);
        Assert.Equal("Contoso Tool", only.DisplayName);
        Assert.Null(only.FromVersion);
        Assert.Equal("2.0", only.ToVersion);
    }

    [Fact]
    public void Lines_from_other_components_are_ignored()
    {
        Assert.Empty(InstallHistory.ParseServiceLog(
        [
            "2026-09-23 04:31:26.637 +00:00 [INF] UpdateChecker: Google Chrome: installed successfully (version 154.0.8037.58).",
            "not a log line",
            "",
        ]));
    }

    [Fact]
    public void Merge_does_not_duplicate_an_install_already_in_the_history()
    {
        var parsed = InstallHistory.ParseServiceLog(RealLog);
        var existing = new List<InstallHistoryEntry> { parsed[1] }; // Chrome already recorded live

        var merged = InstallHistory.Merge(existing, parsed);

        Assert.Equal(3, merged.Count);
        Assert.Single(merged, e => e.DisplayName == "Google Chrome");
        Assert.Equal("Mozilla Firefox", merged[0].DisplayName); // newest first
    }
}
