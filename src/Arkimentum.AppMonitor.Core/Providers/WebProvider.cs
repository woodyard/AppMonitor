using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Arkimentum.AppMonitor.Install;
using Arkimentum.AppMonitor.Inventory;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Versioning;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Providers;

/// <summary>
/// Update provider for applications that are tracked directly on the vendor's web site: a version URL whose body
/// contains the current version (extracted with a regex) and a download URL built from that version.
/// </summary>
public sealed class WebProvider : IUpdateProvider, IDisposable
{
    /// <summary>User-Agent sent with every request, so vendors can identify the traffic.</summary>
    public const string UserAgent = "Arkimentum-AppMonitor/1.0";

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger<WebProvider> _logger;
    private readonly ProviderOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsHandler;

    public WebProvider(ILogger<WebProvider> logger, ProviderOptions options, HttpMessageHandler? handler = null)
    {
        _logger = logger;
        _options = options;
        _ownsHandler = handler is null;
        _http = new HttpClient(handler ?? CreateHandler(options), disposeHandler: _ownsHandler)
        {
            // Per-request timeouts are applied with linked CancellationTokenSources; this is the ceiling.
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _http.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        _http.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip, deflate");
    }

    public UpdateSource Source => UpdateSource.Web;

    private static HttpMessageHandler CreateHandler(ProviderOptions options)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseCookies = false,
        };
        if (!string.IsNullOrWhiteSpace(options.ProxyUrl))
        {
            handler.Proxy = new WebProxy(options.ProxyUrl) { UseDefaultCredentials = true };
            handler.UseProxy = true;
        }
        return handler;
    }

    public async Task<UpdateCheckResult> CheckAsync(AppPolicy app, InstalledApp? installed, ExecutionContextInfo context, CancellationToken ct)
    {
        if (!_options.WebSourcesEnabled)
            return UpdateCheckResult.Failed(app.AppId, UpdateSource.Web, "web sources disabled by configuration");

        if (string.IsNullOrWhiteSpace(app.VersionUrl))
            return UpdateCheckResult.Failed(app.AppId, UpdateSource.Web, $"No VersionUrl configured for '{app.AppId}'.");

        // Installed version: an explicit file version wins over the Uninstall-key DisplayVersion.
        var installedVersion = InstalledAppScanner.FileVersion(app.DetectFilePath);
        var versionSource = installedVersion is null ? "registry" : $"file '{app.DetectFilePath}'";
        installedVersion ??= installed?.DisplayVersion;

        if (string.IsNullOrWhiteSpace(installedVersion))
        {
            _logger.LogDebug("{AppId}: not installed in the {Context} context (no DetectFilePath version and no inventory match).",
                app.AppId, context.Context);
            return UpdateCheckResult.NotInstalled(app.AppId, UpdateSource.Web) with { ResolvedContext = context.Context };
        }

        string body;
        try
        {
            body = await GetStringAsync(app.VersionUrl!, _options.CheckTimeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{AppId}: failed to fetch the version URL {Url}.", app.AppId, app.VersionUrl);
            return UpdateCheckResult.Failed(app.AppId, UpdateSource.Web, $"Failed to fetch '{app.VersionUrl}': {ex.Message}");
        }

        var extracted = ExtractVersion(body, app.VersionRegex);
        if (extracted is null)
        {
            _logger.LogWarning("{AppId}: version regex '{Regex}' did not match the response from {Url} ({Length} bytes).",
                app.AppId, app.VersionRegex, app.VersionUrl, body.Length);
            return UpdateCheckResult.Failed(app.AppId, UpdateSource.Web,
                $"Version regex '{app.VersionRegex}' did not match the response from '{app.VersionUrl}' ({body.Length} characters).");
        }

        var available = VersionComparer.Normalize(extracted);
        var updateAvailable = VersionComparer.IsNewer(available, installedVersion);

        // Per-user installs may need a different installer (e.g. VS Code User Setup).
        var useUser = context.Context == InstallContext.User;
        var urlTemplate = useUser && !string.IsNullOrWhiteSpace(app.UserDownloadUrl) ? app.UserDownloadUrl : app.DownloadUrl;
        var installerArgs = useUser && !string.IsNullOrWhiteSpace(app.UserInstallerArgs) ? app.UserInstallerArgs : app.InstallerArgs;
        var downloadUrl = ExpandPlaceholders(urlTemplate, available);

        var sha256 = app.Sha256;
        if (string.IsNullOrWhiteSpace(sha256) && !string.IsNullOrWhiteSpace(app.Sha256Url))
        {
            sha256 = await TryFetchSha256Async(app, ExpandPlaceholders(app.Sha256Url, available)!, downloadUrl, ct).ConfigureAwait(false);
        }

        _logger.LogDebug("{AppId}: installed {Installed} (from {VersionSource}), latest {Available} from {Url}; download {Download}.",
            app.AppId, installedVersion, versionSource, available, app.VersionUrl, downloadUrl ?? "<none>");

        return new UpdateCheckResult
        {
            AppId = app.AppId,
            Source = UpdateSource.Web,
            IsInstalled = true,
            InstalledVersion = installedVersion,
            AvailableVersion = available,
            UpdateAvailable = updateAvailable,
            DownloadUrl = downloadUrl,
            InstallerArgs = installerArgs,
            InstallerType = app.InstallerType,
            Sha256 = sha256,
            ResolvedContext = context.Context,
        };
    }

    public async Task<InstallResult> InstallAsync(AppPolicy app, PendingUpdate update, ExecutionContextInfo context, IProgress<string>? progress, CancellationToken ct)
    {
        if (!_options.WebSourcesEnabled)
            return InstallResult.Fail("web sources disabled by configuration");

        var url = update.DownloadUrl;
        if (string.IsNullOrWhiteSpace(url))
            return InstallResult.Fail($"No download URL for '{app.AppId}'.");

        // Installers run with high privileges: never fetch them over plain HTTP unless integrity is pinned by a SHA-256.
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(update.Sha256))
        {
            _logger.LogError("{AppId}: refusing to download installer over {Scheme} without a SHA-256 ({Url}).", app.AppId, uri.Scheme, url);
            return InstallResult.Fail($"Download URL for '{app.AppId}' is not HTTPS and no SHA-256 is configured; refusing to install.");
        }

        string file;
        try
        {
            Directory.CreateDirectory(_options.DownloadDirectory);
        }
        catch (Exception ex)
        {
            return InstallResult.Fail($"Cannot create the download directory '{_options.DownloadDirectory}': {ex.Message}");
        }

        using var timeoutCts = new CancellationTokenSource(_options.InstallTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            file = await DownloadAsync(app, url!, update.InstallerType, progress, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            return InstallResult.Fail($"Download of '{url}' timed out after {_options.InstallTimeout.TotalMinutes:0} minutes.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{AppId}: download of {Url} failed.", app.AppId, url);
            return InstallResult.Fail($"Download of '{url}' failed: {ex.Message}");
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(update.Sha256))
            {
                progress?.Report("Verifying download...");
                var actual = await ComputeSha256Async(file, linked.Token).ConfigureAwait(false);
                if (!string.Equals(actual, update.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogError("{AppId}: SHA-256 mismatch for {File}. Expected {Expected}, got {Actual}. The file will not be run.",
                        app.AppId, file, update.Sha256, actual);
                    return InstallResult.Fail($"SHA-256 verification failed for '{Path.GetFileName(file)}': expected {update.Sha256}, got {actual}.");
                }
                _logger.LogDebug("{AppId}: SHA-256 verified ({Hash}).", app.AppId, actual);
            }

            var runner = new InstallerRunner(_logger);
            var args = update.InstallerArgs ?? app.InstallerArgs;
            return await runner.RunAsync(file, update.InstallerType, args, context, _options.InstallTimeout, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(file);
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Applies the version regex; prefers a group named "version", then group 1, then the whole match.</summary>
    public static string? ExtractVersion(string body, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        if (string.IsNullOrWhiteSpace(pattern)) return null;

        Match m;
        try
        {
            m = Regex.Match(body, pattern, RegexOptions.CultureInvariant | RegexOptions.Multiline, RegexTimeout);
        }
        catch (ArgumentException) { return null; }   // invalid pattern
        catch (RegexMatchTimeoutException) { return null; }

        if (!m.Success) return null;

        var named = m.Groups["version"];
        if (named.Success && named.Value.Length > 0) return named.Value.Trim();
        if (m.Groups.Count > 1 && m.Groups[1].Success && m.Groups[1].Value.Length > 0) return m.Groups[1].Value.Trim();
        return m.Value.Trim();
    }

    /// <summary>
    /// Expands the download-URL placeholders: <c>{version}</c>, <c>{version_nodots}</c> (26.03 -&gt; 2603),
    /// <c>{version_underscore}</c> (26.03 -&gt; 26_03), <c>{version_major}</c> and <c>{version_major_minor}</c>.
    /// </summary>
    public static string? ExpandPlaceholders(string? template, string version)
    {
        if (string.IsNullOrWhiteSpace(template)) return template;
        var v = VersionComparer.Normalize(version);
        var parts = v.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var major = parts.Length > 0 ? parts[0] : v;
        var majorMinor = parts.Length > 1 ? $"{parts[0]}.{parts[1]}" : major;

        return template
            .Replace("{version_nodots}", v.Replace(".", string.Empty), StringComparison.OrdinalIgnoreCase)
            .Replace("{version_underscore}", v.Replace('.', '_'), StringComparison.OrdinalIgnoreCase)
            .Replace("{version_major_minor}", majorMinor, StringComparison.OrdinalIgnoreCase)
            .Replace("{version_major}", major, StringComparison.OrdinalIgnoreCase)
            .Replace("{version}", v, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Picks the SHA-256 out of a checksum file. When the file lists several hashes, the line mentioning
    /// <paramref name="downloadFileName"/> wins; otherwise the first 64-hex token is used.
    /// </summary>
    public static string? ExtractSha256(string text, string? downloadFileName)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var hex = new Regex("(?<![0-9A-Fa-f])[0-9A-Fa-f]{64}(?![0-9A-Fa-f])", RegexOptions.CultureInvariant, RegexTimeout);

        if (!string.IsNullOrWhiteSpace(downloadFileName))
        {
            foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.Contains(downloadFileName, StringComparison.OrdinalIgnoreCase)) continue;
                var lm = hex.Match(line);
                if (lm.Success) return lm.Value.ToLowerInvariant();
            }
        }

        var m = hex.Match(text);
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }

    private async Task<string?> TryFetchSha256Async(AppPolicy app, string sha256Url, string? downloadUrl, CancellationToken ct)
    {
        try
        {
            var text = await GetStringAsync(sha256Url, _options.CheckTimeout, ct).ConfigureAwait(false);
            var name = downloadUrl is null ? null : Path.GetFileName(new Uri(downloadUrl).AbsolutePath);
            var hash = ExtractSha256(text, name);
            if (hash is null)
                _logger.LogWarning("{AppId}: no SHA-256 found in {Url}; the download will not be hash-verified.", app.AppId, sha256Url);
            return hash;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{AppId}: failed to fetch the SHA-256 file {Url}; the download will not be hash-verified.", app.AppId, sha256Url);
            return null;
        }
    }

    private async Task<string> GetStringAsync(string url, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        _logger.LogDebug("GET {Url}", url);
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, linked.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
    }

    private async Task<string> DownloadAsync(AppPolicy app, string url, InstallerType type, IProgress<string>? progress, CancellationToken ct)
    {
        _logger.LogInformation("{AppId}: downloading {Url}", app.AppId, url);
        progress?.Report("Downloading...");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var fileName = ResolveFileName(response, url, app.AppId, type);
        var path = Path.Combine(_options.DownloadDirectory, fileName);
        TryDelete(path);

        var total = response.Content.Headers.ContentLength;
        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var destination = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
        {
            var buffer = new byte[128 * 1024];
            long written = 0;
            var lastReported = -1;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                written += read;
                if (progress is null) continue;
                if (total is > 0)
                {
                    var percent = (int)(written * 100 / total.Value);
                    if (percent / 5 != lastReported / 5)
                    {
                        lastReported = percent;
                        progress.Report($"Downloading {fileName}: {percent}%");
                    }
                }
                else if (written / (5 * 1024 * 1024) != lastReported)
                {
                    lastReported = (int)(written / (5 * 1024 * 1024));
                    progress.Report($"Downloading {fileName}: {written / (1024 * 1024)} MB");
                }
            }
        }

        var size = new FileInfo(path).Length;
        _logger.LogInformation("{AppId}: downloaded {Bytes:N0} bytes to {Path}", app.AppId, size, path);
        if (size == 0)
        {
            TryDelete(path);
            throw new IOException($"The download from '{url}' was empty.");
        }
        return path;
    }

    /// <summary>Derives a safe local file name from Content-Disposition, then the final URL, then a generated fallback.</summary>
    public static string ResolveFileName(HttpResponseMessage response, string requestUrl, string appId, InstallerType type)
    {
        string? name = null;

        var cd = response.Content.Headers.ContentDisposition;
        if (cd is not null)
            name = Sanitize(cd.FileNameStar ?? cd.FileName);

        if (name is null)
        {
            var finalUri = response.RequestMessage?.RequestUri ?? new Uri(requestUrl);
            name = Sanitize(Uri.UnescapeDataString(Path.GetFileName(finalUri.AbsolutePath)));
        }

        if (name is null || !Path.HasExtension(name))
        {
            var ext = type switch
            {
                InstallerType.Msi => ".msi",
                InstallerType.Msix => ".msix",
                _ => ".exe",
            };
            name = Sanitize(appId) is { } safeId && safeId.Length > 0
                ? safeId + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + ext
                : "arkimentum-update-" + Guid.NewGuid().ToString("N") + ext;
        }

        return name;
    }

    private static string? Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var trimmed = name.Trim().Trim('"');
        // Defend against path traversal in a hostile Content-Disposition header.
        trimmed = Path.GetFileName(trimmed);
        foreach (var c in Path.GetInvalidFileNameChars()) trimmed = trimmed.Replace(c, '_');
        trimmed = trimmed.Trim();
        if (trimmed.Length == 0 || trimmed is "." or "..") return null;
        return trimmed.Length > 120 ? trimmed[^120..] : trimmed;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not delete the temporary download {Path}.", path); }
    }

    public void Dispose() => _http.Dispose();
}
