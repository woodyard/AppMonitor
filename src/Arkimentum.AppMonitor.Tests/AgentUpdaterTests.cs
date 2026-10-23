using System.Net;
using System.Net.Http;
using System.Text.Json;
using Arkimentum.AppMonitor.Cloud;
using Arkimentum.AppMonitor.Service;
using Arkimentum.AppMonitor.Service.Cloud;
using Arkimentum.AppMonitor.Service.Update;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>Release-feed resolution and the self-update decision rules (no network, no installer).</summary>
public class AgentUpdaterTests
{
    private const string ManifestUrl = "https://example.invalid/appmonitor/manifest.json";
    private const string GitHubLatest = "https://api.github.com/repos/acme/appmonitor/releases/latest";

    private static string ManifestJson(string version, string channel = "stable", string? minimum = null, string package = "https://example.invalid/a-1.0.0.zip") =>
        JsonSerializer.Serialize(new ReleaseManifest
        {
            Version = version,
            Channel = channel,
            PackageUrl = package,
            Sha256 = new string('a', 64),
            SizeBytes = 1234,
            PublishedUtc = DateTimeOffset.Parse("2026-01-02T03:04:05Z"),
            MinimumSupportedVersion = minimum,
            ReleaseNotesUrl = "https://example.invalid/releases/v" + version,
        }, CloudJson.Options);

    // ---------------------------------------------------------------------------------------------------- feed shape

    [Theory]
    [InlineData("", FeedKind.None)]
    [InlineData("   ", FeedKind.None)]
    [InlineData(ManifestUrl, FeedKind.Manifest)]
    [InlineData("https://downloads.example.invalid/channel/preview/manifest.json", FeedKind.Manifest)]
    [InlineData(GitHubLatest, FeedKind.GitHubApi)]
    [InlineData("https://api.github.com/repos/acme/appmonitor/releases/tags/v1.2.3", FeedKind.GitHubApi)]
    [InlineData("https://github.com/acme/appmonitor/releases/latest", FeedKind.Unsupported)]
    [InlineData("not a url", FeedKind.Unsupported)]
    [InlineData("ftp://example.invalid/manifest.json", FeedKind.Unsupported)]
    public void Classify_recognises_the_supported_feed_forms(string url, FeedKind expected) =>
        Assert.Equal(expected, ReleaseFeed.Classify(url));

    [Fact]
    public async Task Direct_manifest_url_is_read_as_a_release_manifest()
    {
        var handler = new FakeUpdateHandler(_ => FakeUpdateHandler.Json(ManifestJson("1.4.0")));
        using var feed = new ReleaseFeed(NullLogger.Instance, handler);

        var manifest = await feed.ResolveAsync(ManifestUrl, default);

        Assert.NotNull(manifest);
        Assert.Equal("1.4.0", manifest!.Version);
        Assert.Equal("stable", manifest.Channel);
        Assert.Equal(1234, manifest.SizeBytes);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_manifest_without_package_url_is_rejected()
    {
        var handler = new FakeUpdateHandler(_ => FakeUpdateHandler.Json("""{"version":"1.4.0","sha256":"abc"}"""));
        using var feed = new ReleaseFeed(NullLogger.Instance, handler);

        Assert.Null(await feed.ResolveAsync(ManifestUrl, default));
    }

    [Fact]
    public async Task A_feed_that_returns_404_yields_no_manifest()
    {
        var handler = new FakeUpdateHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var feed = new ReleaseFeed(NullLogger.Instance, handler);

        Assert.Null(await feed.ResolveAsync(ManifestUrl, default));
    }

    [Fact]
    public async Task Github_release_api_downloads_the_manifest_asset()
    {
        const string assetUrl = "https://github.com/acme/appmonitor/releases/download/v2.0.0/manifest.json";
        var release = $$"""
            {
              "tag_name": "v2.0.0",
              "assets": [
                { "name": "Arkimentum.AppMonitor-2.0.0.zip", "browser_download_url": "https://github.com/acme/appmonitor/releases/download/v2.0.0/Arkimentum.AppMonitor-2.0.0.zip" },
                { "name": "manifest.json", "browser_download_url": "{{assetUrl}}" }
              ]
            }
            """;
        var handler = new FakeUpdateHandler(request => request.RequestUri!.ToString() switch
        {
            GitHubLatest => FakeUpdateHandler.Json(release),
            assetUrl => FakeUpdateHandler.Json(ManifestJson("2.0.0", package: "https://github.com/acme/appmonitor/releases/download/v2.0.0/Arkimentum.AppMonitor-2.0.0.zip")),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var feed = new ReleaseFeed(NullLogger.Instance, handler);

        var manifest = await feed.ResolveAsync(GitHubLatest, default);

        Assert.NotNull(manifest);
        Assert.Equal("2.0.0", manifest!.Version);
        Assert.EndsWith("Arkimentum.AppMonitor-2.0.0.zip", manifest.PackageUrl);
        // The API call must identify itself and ask for the GitHub media type.
        var apiRequest = handler.Requests[0];
        Assert.Contains("Arkimentum-AppMonitor", apiRequest.Headers.UserAgent.ToString());
        Assert.Contains("application/vnd.github+json", apiRequest.Headers.Accept.ToString());
    }

    [Fact]
    public async Task Github_release_without_a_manifest_asset_yields_nothing()
    {
        var handler = new FakeUpdateHandler(_ => FakeUpdateHandler.Json("""{"tag_name":"v2.0.0","assets":[{"name":"setup.exe","browser_download_url":"https://x/y"}]}"""));
        using var feed = new ReleaseFeed(NullLogger.Instance, handler);

        Assert.Null(await feed.ResolveAsync(GitHubLatest, default));
    }

    [Theory]
    [InlineData(GitHubLatest, "https://github.com/acme/appmonitor/releases/latest/download/manifest.json")]
    [InlineData("https://api.github.com/repos/acme/appmonitor/releases/tags/v1.2.3", "https://github.com/acme/appmonitor/releases/download/v1.2.3/manifest.json")]
    [InlineData("https://api.github.com/repos/acme/appmonitor/releases", null)]
    [InlineData(ManifestUrl, null)]
    public void A_github_api_url_maps_to_the_releases_download_link(string apiUrl, string? expected) =>
        Assert.Equal(expected, ReleaseFeed.DownloadLinkFor(apiUrl));

    [Fact]
    public void The_default_feed_is_the_download_link_not_the_rate_limited_api() =>
        Assert.Equal(FeedKind.Manifest, ReleaseFeed.Classify(Models.AgentSettings.DefaultUpdateFeedUrl));

    [Fact]
    public async Task A_rate_limited_github_api_falls_back_to_the_download_link()
    {
        const string link = "https://github.com/acme/appmonitor/releases/latest/download/manifest.json";
        var handler = new FakeUpdateHandler(request =>
        {
            if (request.RequestUri!.ToString() == link) return FakeUpdateHandler.Json(ManifestJson("2.1.0"));
            var refused = new HttpResponseMessage(HttpStatusCode.Forbidden);
            refused.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
            return refused;
        });
        using var feed = new ReleaseFeed(NullLogger.Instance, handler);

        var manifest = await feed.ResolveAsync(GitHubLatest, default);

        Assert.Equal("2.1.0", manifest?.Version);
        Assert.Null(feed.LastProblem);
        Assert.Equal([GitHubLatest, link], handler.Requests.Select(r => r.RequestUri!.ToString()));
    }

    [Fact]
    public async Task When_both_the_api_and_the_download_link_fail_the_reason_names_the_rate_limit()
    {
        var handler = new FakeUpdateHandler(request =>
        {
            if (request.RequestUri!.Host == "github.com") return new HttpResponseMessage(HttpStatusCode.NotFound);
            var refused = new HttpResponseMessage(HttpStatusCode.Forbidden);
            refused.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
            return refused;
        });
        using var feed = new ReleaseFeed(NullLogger.Instance, handler);

        Assert.Null(await feed.ResolveAsync(GitHubLatest, default));
        Assert.Contains("403", feed.LastProblem);
        Assert.Contains("rate limit", feed.LastProblem);
        Assert.Contains("404", feed.LastProblem);
    }

    [Fact]
    public async Task A_failed_manifest_url_says_what_answered()
    {
        var handler = new FakeUpdateHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var feed = new ReleaseFeed(NullLogger.Instance, handler);

        Assert.Null(await feed.ResolveAsync(ManifestUrl, default));
        Assert.Equal($"{ManifestUrl} answered 503 Service Unavailable", feed.LastProblem);
    }

    [Fact]
    public async Task An_unsupported_feed_url_is_never_fetched()
    {
        var handler = new FakeUpdateHandler(_ => FakeUpdateHandler.Json(ManifestJson("9.9.9")));
        using var feed = new ReleaseFeed(NullLogger.Instance, handler);

        Assert.Null(await feed.ResolveAsync("https://github.com/acme/appmonitor/releases/latest", default));
        Assert.Empty(handler.Requests);
    }

    // ---------------------------------------------------------------------------------------------------- decisions

    private static ReleaseManifest Manifest(string version, string channel = "stable", string? minimum = null) => new()
    {
        Version = version,
        Channel = channel,
        PackageUrl = "https://example.invalid/pkg.zip",
        Sha256 = new string('b', 64),
        MinimumSupportedVersion = minimum,
    };

    [Fact]
    public void A_newer_version_on_the_same_channel_is_an_update() =>
        Assert.Equal(AgentUpdateAction.UpdateAvailable, AgentUpdater.Decide(Manifest("1.1.0"), "1.0.8", "", "stable").Action);

    [Theory]
    [InlineData("1.0.8")]
    [InlineData("1.0.7")]
    public void The_same_or_an_older_version_is_up_to_date(string published) =>
        Assert.Equal(AgentUpdateAction.UpToDate, AgentUpdater.Decide(Manifest(published), "1.0.8", "", "stable").Action);

    [Fact]
    public void A_missing_manifest_is_reported_as_such() =>
        Assert.Equal(AgentUpdateAction.NoManifest, AgentUpdater.Decide(null, "1.0.8", "", "stable").Action);

    [Fact]
    public void A_preview_manifest_is_ignored_on_the_stable_channel() =>
        Assert.Equal(AgentUpdateAction.ChannelMismatch, AgentUpdater.Decide(Manifest("2.0.0", "preview"), "1.0.8", "", "stable").Action);

    [Fact]
    public void A_preview_manifest_applies_on_the_preview_channel() =>
        Assert.Equal(AgentUpdateAction.UpdateAvailable, AgentUpdater.Decide(Manifest("2.0.0", "preview"), "1.0.8", "", "preview").Action);

    [Fact]
    public void An_empty_channel_means_stable_on_both_sides() =>
        Assert.Equal(AgentUpdateAction.UpdateAvailable, AgentUpdater.Decide(Manifest("2.0.0", ""), "1.0.8", "", "").Action);

    [Fact]
    public void A_manifest_above_the_target_version_is_skipped()
    {
        var outcome = AgentUpdater.Decide(Manifest("1.2.0"), "1.0.8", "1.1.0", "stable");
        Assert.Equal(AgentUpdateAction.PinnedByTargetVersion, outcome.Action);
        Assert.Contains("1.1.0", outcome.Reason);
    }

    [Fact]
    public void The_target_version_itself_is_installed() =>
        Assert.Equal(AgentUpdateAction.UpdateAvailable, AgentUpdater.Decide(Manifest("1.1.0"), "1.0.8", "1.1.0", "stable").Action);

    [Fact]
    public void A_pinned_agent_that_already_runs_the_target_stays_put() =>
        Assert.Equal(AgentUpdateAction.UpToDate, AgentUpdater.Decide(Manifest("1.1.0"), "1.1.0", "1.1.0", "stable").Action);

    [Fact]
    public void Being_below_the_minimum_supported_version_still_updates()
    {
        var outcome = AgentUpdater.Decide(Manifest("2.0.0", minimum: "1.5.0"), "1.0.8", "", "stable");
        Assert.Equal(AgentUpdateAction.UpdateAvailable, outcome.Action);
    }

    [Fact]
    public void Versions_are_compared_leniently_not_as_strings() =>
        Assert.Equal(AgentUpdateAction.UpdateAvailable, AgentUpdater.Decide(Manifest("1.0.10"), "1.0.9", "", "stable").Action);

    [Fact]
    public void A_tray_left_on_the_old_release_turns_up_to_date_into_a_repair()
    {
        var outcome = AgentUpdater.DecideRepair(AgentUpdater.Decide(Manifest("1.1.51"), "1.1.51", "", "stable"), "1.1.51", "1.1.49");
        Assert.Equal(AgentUpdateAction.UpdateAvailable, outcome.Action);
        Assert.Equal("1.1.51", outcome.Manifest!.Version);
        Assert.Contains("1.1.49", outcome.Reason);
    }

    [Theory]
    [InlineData("1.1.51")]   // tray matches the service
    [InlineData(null)]       // no installed tray to compare (development run)
    public void A_matching_or_missing_tray_leaves_up_to_date_alone(string? tray) =>
        Assert.Equal(AgentUpdateAction.UpToDate,
            AgentUpdater.DecideRepair(AgentUpdater.Decide(Manifest("1.1.51"), "1.1.51", "", "stable"), "1.1.51", tray).Action);

    [Fact]
    public void No_repair_when_the_feed_offers_another_release_than_the_service_runs() =>
        // Pinned below the running version: reinstalling the older release would be a downgrade, not a repair.
        Assert.Equal(AgentUpdateAction.UpToDate,
            AgentUpdater.DecideRepair(AgentUpdater.Decide(Manifest("1.1.50"), "1.1.51", "", "stable"), "1.1.51", "1.1.49").Action);

    [Fact]
    public void A_real_update_is_not_rewritten_as_a_repair()
    {
        var update = AgentUpdater.Decide(Manifest("1.1.52"), "1.1.51", "", "stable");
        Assert.Same(update, AgentUpdater.DecideRepair(update, "1.1.51", "1.1.49"));
    }

    [Fact]
    public void The_installed_tray_version_is_read_from_the_tray_folder_next_to_the_service()
    {
        var root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            var service = Directory.CreateDirectory(Path.Combine(root, "Service")).FullName;
            Assert.Null(AgentUpdater.InstalledTrayVersion(service));

            var tray = Directory.CreateDirectory(Path.Combine(root, "Tray")).FullName;
            var assembly = typeof(AgentUpdater).Assembly.Location;
            File.Copy(assembly, Path.Combine(tray, "Arkimentum.AppMonitor.Tray.dll"));
            var expected = System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly).ProductVersion!.Split('+')[0];
            Assert.Equal(expected, AgentUpdater.InstalledTrayVersion(service));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void Sha256_of_a_file_is_lower_case_hex()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            File.WriteAllText(path, "arkimentum");
            var hash = AgentUpdater.Sha256OfFile(path);
            Assert.Equal(64, hash.Length);
            Assert.Equal(hash.ToLowerInvariant(), hash);
            Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(), hash);
        }
        finally { File.Delete(path); }
    }

    // ------------------------------------------------------------------ agent updates before application scans

    [Fact]
    public void A_scan_waits_for_the_agent_update_check_that_runs_at_start() =>
        Assert.NotNull(UpdateCoordinator.ScanHold(updaterAvailable: true, agentUpdateActivity: null, replacementUnderway: null,
            firstAgentCheckSettled: false, sinceStart: TimeSpan.FromSeconds(5)));

    [Fact]
    public void A_scan_runs_once_the_first_agent_update_check_is_over() =>
        Assert.Null(UpdateCoordinator.ScanHold(updaterAvailable: true, agentUpdateActivity: null, replacementUnderway: null,
            firstAgentCheckSettled: true, sinceStart: TimeSpan.FromSeconds(30)));

    [Fact]
    public void A_first_agent_update_check_that_never_ends_holds_scans_only_for_a_while() =>
        Assert.Null(UpdateCoordinator.ScanHold(updaterAvailable: true, agentUpdateActivity: null, replacementUnderway: null,
            firstAgentCheckSettled: false, sinceStart: UpdateCoordinator.FirstAgentCheckWait));

    [Fact]
    public void A_scan_waits_while_an_agent_update_check_runs() =>
        Assert.Equal("Agent update check (scheduled)",
            UpdateCoordinator.ScanHold(updaterAvailable: true, agentUpdateActivity: "Agent update check (scheduled)", replacementUnderway: null,
                firstAgentCheckSettled: true, sinceStart: TimeSpan.FromHours(3)));

    [Fact]
    public void A_scan_waits_for_the_restart_while_the_agent_is_being_replaced()
    {
        var hold = UpdateCoordinator.ScanHold(updaterAvailable: true, agentUpdateActivity: null, replacementUnderway: "the agent is being replaced by 1.1.42",
            firstAgentCheckSettled: true, sinceStart: TimeSpan.FromHours(3));
        Assert.NotNull(hold);
        Assert.StartsWith("the agent is being replaced by 1.1.42", hold);
    }

    [Fact]
    public void Without_a_self_updater_nothing_holds_a_scan() =>
        Assert.Null(UpdateCoordinator.ScanHold(updaterAvailable: false, agentUpdateActivity: null, replacementUnderway: null,
            firstAgentCheckSettled: false, sinceStart: TimeSpan.Zero));

    // Oliver's PC, 2026-10-23: switched on after 17 days on 1.1.47, it installed an update before the agent updated.
    [Fact]
    public void Installs_wait_for_the_agent_update_check_that_runs_at_start() =>
        Assert.NotNull(UpdateCoordinator.PolicyHold(updaterAvailable: true, replacementUnderway: null,
            firstAgentCheckSettled: false, sinceStart: TimeSpan.FromSeconds(15)));

    [Fact]
    public void Installs_run_once_the_first_agent_update_check_is_over() =>
        Assert.Null(UpdateCoordinator.PolicyHold(updaterAvailable: true, replacementUnderway: null,
            firstAgentCheckSettled: true, sinceStart: TimeSpan.FromSeconds(30)));

    [Fact]
    public void A_first_agent_update_check_that_never_ends_holds_installs_only_for_a_while() =>
        Assert.Null(UpdateCoordinator.PolicyHold(updaterAvailable: true, replacementUnderway: null,
            firstAgentCheckSettled: false, sinceStart: UpdateCoordinator.FirstAgentCheckWait));

    [Fact]
    public void Installs_wait_while_the_agent_is_being_replaced() =>
        Assert.Equal("the agent is being replaced by 1.1.53",
            UpdateCoordinator.PolicyHold(updaterAvailable: true, replacementUnderway: "the agent is being replaced by 1.1.53",
                firstAgentCheckSettled: true, sinceStart: TimeSpan.FromHours(3)));

    [Fact]
    public void A_first_check_that_could_not_read_the_feed_keeps_the_hold_and_is_retried_within_a_minute()
    {
        Assert.False(CloudSyncService.SettlesFirstAgentCheck(AgentUpdateAction.NoManifest, firstCheckPending: true));
        Assert.Equal(TimeSpan.FromMinutes(1), CloudSyncService.AgentCheckRetry(AgentUpdateAction.NoManifest, hasManifest: false, firstCheckPending: true));
    }

    [Theory]
    [InlineData(AgentUpdateAction.UpToDate)]
    [InlineData(AgentUpdateAction.Launched)]
    [InlineData(AgentUpdateAction.Failed)]
    [InlineData(AgentUpdateAction.Disabled)]
    [InlineData(AgentUpdateAction.PinnedByTargetVersion)]
    public void A_first_check_that_concluded_releases_the_hold(AgentUpdateAction action) =>
        Assert.True(CloudSyncService.SettlesFirstAgentCheck(action, firstCheckPending: true));

    [Fact]
    public void A_later_check_that_could_not_read_the_feed_is_retried_in_half_an_hour_not_at_the_next_interval()
    {
        Assert.True(CloudSyncService.SettlesFirstAgentCheck(AgentUpdateAction.NoManifest, firstCheckPending: false));
        Assert.Equal(TimeSpan.FromMinutes(30), CloudSyncService.AgentCheckRetry(AgentUpdateAction.NoManifest, hasManifest: false, firstCheckPending: false));
        Assert.Equal(TimeSpan.FromMinutes(30), CloudSyncService.AgentCheckRetry(AgentUpdateAction.Failed, hasManifest: true, firstCheckPending: false));
    }

    [Fact]
    public void An_agent_update_kept_out_by_an_install_is_retried_in_five_minutes_and_an_up_to_date_check_waits_for_the_interval()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), CloudSyncService.AgentCheckRetry(AgentUpdateAction.Blocked, hasManifest: true, firstCheckPending: false));
        Assert.Null(CloudSyncService.AgentCheckRetry(AgentUpdateAction.Blocked, hasManifest: false, firstCheckPending: false));
        Assert.Null(CloudSyncService.AgentCheckRetry(AgentUpdateAction.UpToDate, hasManifest: true, firstCheckPending: true));
    }

    // ------------------------------------------------------------------ client-initiated update (UpdateAgentMessage)

    [Fact]
    public void A_client_may_ask_for_an_update_when_nothing_stands_in_the_way() =>
        Assert.Null(UpdateCoordinator.RefuseAgentUpdate(updaterAvailable: true, autoUpdateEnabled: true, applicationInstallRunning: false, agentUpdateRunning: false));

    [Fact]
    public void A_client_request_is_refused_when_the_administrator_turned_agent_updates_off() =>
        Assert.Equal("Agent updates are disabled by policy",
            UpdateCoordinator.RefuseAgentUpdate(updaterAvailable: true, autoUpdateEnabled: false, applicationInstallRunning: false, agentUpdateRunning: false));

    [Fact]
    public void A_client_request_is_refused_while_an_application_is_being_installed() =>
        Assert.Equal("An application is being updated; try the agent update again when it has finished",
            UpdateCoordinator.RefuseAgentUpdate(updaterAvailable: true, autoUpdateEnabled: true, applicationInstallRunning: true, agentUpdateRunning: false));

    [Fact]
    public void A_client_request_is_refused_while_an_agent_update_is_already_running() =>
        Assert.Equal("An agent update is already running",
            UpdateCoordinator.RefuseAgentUpdate(updaterAvailable: true, autoUpdateEnabled: true, applicationInstallRunning: false, agentUpdateRunning: true));

    [Fact]
    public void A_client_request_is_refused_when_this_process_has_no_self_updater() =>
        Assert.Equal("Agent updates are not available in this mode",
            UpdateCoordinator.RefuseAgentUpdate(updaterAvailable: false, autoUpdateEnabled: true, applicationInstallRunning: false, agentUpdateRunning: false));

    [Fact]
    public void A_check_reports_an_available_update_without_promising_an_install()
    {
        var outcome = AgentUpdater.Decide(Manifest("1.1.4"), "1.1.3", null, "stable");

        var (ok, message) = UpdateCoordinator.DescribeAgentUpdate(outcome, checkOnly: true);

        Assert.True(ok);
        Assert.Equal("Update 1.1.4 available", message);
    }

    [Fact]
    public void An_install_request_answers_that_the_agent_is_about_to_restart()
    {
        var outcome = AgentUpdater.Decide(Manifest("1.1.4"), "1.1.3", null, "stable");

        var (ok, message) = UpdateCoordinator.DescribeAgentUpdate(outcome, checkOnly: false);

        Assert.True(ok);
        Assert.Equal("Updating to 1.1.4 - the agent will restart", message);
    }

    [Fact]
    public void An_unreadable_feed_is_answered_as_a_failed_check()
    {
        var outcome = new AgentUpdateOutcome(AgentUpdateAction.NoManifest, "The release feed could not be read: no such host");

        var (ok, message) = UpdateCoordinator.DescribeAgentUpdate(outcome, checkOnly: true);

        Assert.False(ok);
        Assert.StartsWith("Check failed: ", message);
    }

    [Fact]
    public void A_pinned_device_is_told_why_it_stays_where_it_is()
    {
        var outcome = AgentUpdater.Decide(Manifest("1.1.4"), "1.1.3", "1.1.3", "stable");

        var (ok, message) = UpdateCoordinator.DescribeAgentUpdate(outcome, checkOnly: true);

        Assert.True(ok);
        Assert.Contains("AgentTargetVersion", message);
    }

    /// <summary>HttpMessageHandler stub; disposal is a no-op so one handler can serve several clients.</summary>
    private sealed class FakeUpdateHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var response = responder(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }

        protected override void Dispose(bool disposing) { }

        public static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
    }
}
