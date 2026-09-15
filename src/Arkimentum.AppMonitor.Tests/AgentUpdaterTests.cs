using System.Net;
using System.Net.Http;
using System.Text.Json;
using Arkimentum.AppMonitor.Cloud;
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
