using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Cloud;

public sealed class CloudException(string message, HttpStatusCode? status = null, string? code = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
    public string? Code { get; } = code;
    public bool IsUnauthorized => Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
}

public sealed record ConfigFetchResult(bool NotModified, DeviceConfigResponse? Config, string? ETag);

/// <summary>
/// HTTP client for the device side of the AppMonitor cloud API. All calls are best-effort from the caller's point of
/// view: they throw <see cref="CloudException"/> with the server's error code, never partial results.
/// </summary>
public sealed class CloudClient : IDisposable
{
    /// <summary>User-Agent sent to the cloud API and the update feed: product plus the running agent version.</summary>
    public static readonly string UserAgent = "Arkimentum-AppMonitor/" + (typeof(CloudClient).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion?.Split('+')[0] ?? "1.0");

    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private readonly Uri _baseUri;

    public CloudClient(ILogger<CloudClient> logger, string serverUrl, string? proxyUrl = null, HttpMessageHandler? handler = null)
    {
        _logger = logger;
        _baseUri = new Uri(serverUrl.TrimEnd('/') + "/", UriKind.Absolute);
        if (_baseUri.Scheme != Uri.UriSchemeHttps && !_baseUri.IsLoopback)
            throw new ArgumentException("The cloud server URL must use https.", nameof(serverUrl));

        handler ??= new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            // Explicit proxy when configured; otherwise the system proxy (UseProxy=true with Proxy=null).
            Proxy = string.IsNullOrWhiteSpace(proxyUrl) ? null : new WebProxy(proxyUrl),
            UseProxy = true,
        };
        _http = new HttpClient(handler, disposeHandler: true) { BaseAddress = _baseUri, Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public Uri BaseUri => _baseUri;

    // ------------------------------------------------------------------------------------------ device endpoints

    public async Task<EnrollResponse> EnrollAsync(EnrollRequest request, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(Relative(CloudRoutes.Enroll), request, CloudJson.Options, ct).ConfigureAwait(false);
        return await ReadAsync<EnrollResponse>(response, ct).ConfigureAwait(false);
    }

    /// <summary>Fetches the organization configuration; returns NotModified=true when <paramref name="etag"/> still matches.</summary>
    public async Task<ConfigFetchResult> GetConfigAsync(DeviceCredential credential, string? etag, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Relative(CloudRoutes.Config));
        Authenticate(request, credential);
        if (!string.IsNullOrEmpty(etag)) request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(Quote(etag)));
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified) return new ConfigFetchResult(true, null, etag);
        var config = await ReadAsync<DeviceConfigResponse>(response, ct).ConfigureAwait(false);
        var newTag = response.Headers.ETag?.Tag?.Trim('"') ?? config.ConfigVersion;
        return new ConfigFetchResult(false, config, newTag);
    }

    public async Task<ReportResponse> ReportAsync(DeviceCredential credential, DeviceReport report, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Relative(CloudRoutes.Report))
        {
            Content = JsonContent.Create(report, options: CloudJson.Options),
        };
        Authenticate(request, credential);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        return await ReadAsync<ReportResponse>(response, ct).ConfigureAwait(false);
    }

    /// <summary>Release manifest as mirrored by the API (null when the API does not serve one).</summary>
    public async Task<ReleaseManifest?> GetReleaseAsync(DeviceCredential credential, string channel, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Relative(CloudRoutes.Release) + "?channel=" + Uri.EscapeDataString(channel));
        Authenticate(request, credential);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent) return null;
        return await ReadAsync<ReleaseManifest>(response, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------------------------------ public / admin endpoints

    public async Task<AuthConfigResponse> GetAuthConfigAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync(Relative(CloudRoutes.AuthConfig), ct).ConfigureAwait(false);
        return await ReadAsync<AuthConfigResponse>(response, ct).ConfigureAwait(false);
    }

    /// <summary>Generic authenticated admin call (bearer token from Entra ID). Used by the admin console.</summary>
    public async Task<T> AdminAsync<T>(HttpMethod method, string relativePath, string accessToken, object? body, CancellationToken ct, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, Relative(relativePath));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: CloudJson.Options);
        if (!string.IsNullOrEmpty(ifMatch)) request.Headers.IfMatch.Add(new EntityTagHeaderValue(Quote(ifMatch)));
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        return await ReadAsync<T>(response, ct).ConfigureAwait(false);
    }

    /// <summary>Downloads a release package to <paramref name="destinationPath"/> and returns its SHA-256 (lower-case hex).</summary>
    public async Task<string> DownloadAsync(string url, string destinationPath, IProgress<string>? progress, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 17, useAsync: true);
        using var sha = System.Security.Cryptography.SHA256.Create();
        var buffer = new byte[1 << 17];
        long total = response.Content.Headers.ContentLength ?? -1, done = 0; int read, lastPct = -1;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            sha.TransformBlock(buffer, 0, read, null, 0);
            done += read;
            if (total > 0 && progress is not null)
            {
                var pct = (int)(done * 100 / total);
                if (pct / 10 != lastPct / 10) { lastPct = pct; progress.Report($"Downloading… {pct}%"); }
            }
        }
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    // ------------------------------------------------------------------------------------------ helpers

    private static void Authenticate(HttpRequestMessage request, DeviceCredential credential) =>
        request.Headers.Authorization = new AuthenticationHeaderValue(CloudRoutes.DeviceAuthScheme, $"{credential.DeviceId}:{credential.DeviceKey}");

    private static string Relative(string route) => route.TrimStart('/');
    private static string Quote(string etag) => etag.StartsWith('"') ? etag : $"\"{etag}\"";

    private async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.NoContent) return default!;
            var value = await response.Content.ReadFromJsonAsync<T>(CloudJson.Options, ct).ConfigureAwait(false);
            return value ?? throw new CloudException("Empty response from the cloud API.", response.StatusCode);
        }

        string? code = null, message = null;
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ApiError>(CloudJson.Options, ct).ConfigureAwait(false);
            code = error?.Code; message = error?.Message;
        }
        catch (JsonException) { }
        catch (NotSupportedException) { }
        message ??= await SafeReadStringAsync(response, ct).ConfigureAwait(false);
        var text = $"Cloud API returned {(int)response.StatusCode} {response.ReasonPhrase}{(string.IsNullOrWhiteSpace(message) ? "" : ": " + message)}";
        _logger.LogDebug("{Method} {Url} -> {Status} {Code}", response.RequestMessage?.Method, response.RequestMessage?.RequestUri, (int)response.StatusCode, code);
        throw new CloudException(text, response.StatusCode, code);
    }

    private static async Task<string?> SafeReadStringAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var s = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return s.Length > 300 ? s[..300] : s;
        }
        catch { return null; }
    }

    public void Dispose() => _http.Dispose();
}
