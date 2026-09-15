using System.Net.Http.Headers;
using System.Text.Json;
using Arkimentum.AppMonitor.Cloud;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Service.Update;

/// <summary>The shape of <c>AgentUpdateFeedUrl</c>.</summary>
public enum FeedKind
{
    /// <summary>Not configured - the cloud API's mirror is used instead, or the self-updater is disabled.</summary>
    None,
    /// <summary>A direct URL to a <c>manifest.json</c> (<see cref="ReleaseManifest"/> JSON).</summary>
    Manifest,
    /// <summary>A GitHub releases API URL (<c>api.github.com/repos/{owner}/{repo}/releases/latest</c> or <c>.../releases/tags/v1.2.3</c>).</summary>
    GitHubApi,
    /// <summary>Something we cannot read; the self-updater logs and does nothing.</summary>
    Unsupported,
}

/// <summary>
/// Resolves a <see cref="ReleaseManifest"/> from the configured update feed. Two forms are supported: a direct
/// <c>manifest.json</c> URL, and a GitHub releases API URL whose release carries a <c>manifest.json</c> asset (the
/// manifest's <c>packageUrl</c> then points at the zip asset's <c>browser_download_url</c>). Public repositories only -
/// authenticated/private feeds are a follow-up; use the cloud API's mirror for those.
/// </summary>
public sealed class ReleaseFeed : IDisposable
{
    public const string ManifestAssetName = "manifest.json";

    private readonly ILogger _logger;
    private readonly HttpClient _http;

    public ReleaseFeed(ILogger logger, HttpMessageHandler? handler = null, string? proxyUrl = null)
    {
        _logger = logger;
        handler ??= new HttpClientHandler
        {
            Proxy = string.IsNullOrWhiteSpace(proxyUrl) ? null : new System.Net.WebProxy(proxyUrl),
            UseProxy = true,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(CloudClient.UserAgent);
    }

    /// <summary>Classifies a feed URL without contacting anything.</summary>
    public static FeedKind Classify(string? feedUrl)
    {
        // Empty or the keyword "cloud": no direct feed, use the cloud API's release mirror when the cloud connection is configured.
        if (string.IsNullOrWhiteSpace(feedUrl) || feedUrl.Trim().Equals("cloud", StringComparison.OrdinalIgnoreCase)) return FeedKind.None;
        if (!Uri.TryCreate(feedUrl.Trim(), UriKind.Absolute, out var uri)) return FeedKind.Unsupported;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return FeedKind.Unsupported;
        if (uri.AbsolutePath.EndsWith("/" + ManifestAssetName, StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.Equals("/" + ManifestAssetName, StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.EndsWith(ManifestAssetName, StringComparison.OrdinalIgnoreCase))
            return FeedKind.Manifest;
        if (uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.Contains("/releases", StringComparison.OrdinalIgnoreCase))
            return FeedKind.GitHubApi;
        return FeedKind.Unsupported;
    }

    /// <summary>Fetches the manifest the feed points at, or null when the feed is unusable (always logged).</summary>
    public async Task<ReleaseManifest?> ResolveAsync(string? feedUrl, CancellationToken ct)
    {
        var kind = Classify(feedUrl);
        switch (kind)
        {
            case FeedKind.Manifest:
                _logger.LogInformation("Agent update: reading the release manifest from {Url}", feedUrl);
                return await GetManifestAsync(feedUrl!, ct).ConfigureAwait(false);
            case FeedKind.GitHubApi:
                return await ResolveGitHubAsync(feedUrl!, ct).ConfigureAwait(false);
            case FeedKind.None:
                return null;
            default:
                _logger.LogWarning("Agent update: AgentUpdateFeedUrl '{Url}' is neither a manifest.json URL nor a GitHub releases API URL; ignoring it", feedUrl);
                return null;
        }
    }

    private async Task<ReleaseManifest?> GetManifestAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        _logger.LogInformation("Agent update: GET {Url} -> {Status}", url, (int)response.StatusCode);
        if (!response.IsSuccessStatusCode) return null;
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try
        {
            var manifest = JsonSerializer.Deserialize<ReleaseManifest>(json, CloudJson.Options);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version) || string.IsNullOrWhiteSpace(manifest.PackageUrl) || string.IsNullOrWhiteSpace(manifest.Sha256))
            {
                _logger.LogWarning("Agent update: the manifest at {Url} is missing version, packageUrl or sha256", url);
                return null;
            }
            _logger.LogInformation("Agent update: manifest {Version} ({Channel}) published {Published}, package {Package} ({Size} bytes)",
                manifest.Version, manifest.Channel, manifest.PublishedUtc?.ToString("u") ?? "?", manifest.PackageUrl, manifest.SizeBytes?.ToString() ?? "?");
            return manifest;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Agent update: the manifest at {Url} is not valid ReleaseManifest JSON", url);
            return null;
        }
    }

    /// <summary>GitHub releases API: find the <c>manifest.json</c> asset of the release and read it.</summary>
    private async Task<ReleaseManifest?> ResolveGitHubAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        _logger.LogInformation("Agent update: GET {Url} -> {Status}", url, (int)response.StatusCode);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Agent update: the GitHub release feed {Url} returned {Status}", url, (int)response.StatusCode);
            return null;
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        if (!doc.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            _logger.LogWarning("Agent update: the GitHub release {Tag} has no assets", tag);
            return null;
        }
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (!string.Equals(name, ManifestAssetName, StringComparison.OrdinalIgnoreCase)) continue;
            var download = asset.TryGetProperty("browser_download_url", out var d) ? d.GetString() : null;
            if (string.IsNullOrWhiteSpace(download)) break;
            _logger.LogInformation("Agent update: GitHub release {Tag} carries {Asset}; reading it from {Url}", tag, name, download);
            return await GetManifestAsync(download, ct).ConfigureAwait(false);
        }
        _logger.LogWarning("Agent update: the GitHub release {Tag} has no {Asset} asset; the release was not published by the release workflow", tag, ManifestAssetName);
        return null;
    }

    public void Dispose() => _http.Dispose();
}
