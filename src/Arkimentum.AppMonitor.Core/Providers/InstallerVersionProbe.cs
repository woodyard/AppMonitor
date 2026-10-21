using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Arkimentum.AppMonitor.Versioning;

namespace Arkimentum.AppMonitor.Providers;

/// <summary>What the check before an install found out about the installer winget would run (see <c>WingetProvider.CheckInstallerVersionAsync</c>).</summary>
/// <param name="WingetId">The id that was checked.</param>
/// <param name="InstallerUrl">The installer <c>winget show</c> selected for the update's scope.</param>
/// <param name="InstallerVersion">The product version in that installer's version resource.</param>
/// <param name="Mislabeled">The installer carries the installed version, below the one the package claims (<see cref="InstallerVersionProbe.IsMislabeled"/>).</param>
public sealed record InstallerVersionCheck(string WingetId, string InstallerUrl, string InstallerVersion, bool Mislabeled)
{
    /// <summary>The installer's file name ("VSCodeUserSetup-arm64-1.141.2.exe"), for messages.</summary>
    public string InstallerFileName => Uri.TryCreate(InstallerUrl, UriKind.Absolute, out var u) ? Path.GetFileName(u.AbsolutePath) : InstallerUrl;
}

/// <summary>
/// Reads the product version an exe installer carries in its version resource without downloading the whole file: the
/// PE headers first, then the PE image up to the end of its last section (an installer's payload comes after it, so
/// for VS Code's 230 MB setup that is the setup stub, a few MB), written to a temporary file for <see cref="FileVersionInfo"/>.
/// Exists for winget manifests whose version label points at an older installer (Microsoft.VisualStudioCode 1.142.0,
/// whose installers were the 1.141.2 ones for a week in October 2026): see <see cref="IsMislabeled"/>.
/// </summary>
public static partial class InstallerVersionProbe
{
    /// <summary>The first request: enough for the DOS header, the PE headers and the section table of any setup stub.</summary>
    internal const int HeaderBytes = 64 * 1024;

    /// <summary>The most the probe downloads; a PE image that ends further in is not read (no answer).</summary>
    internal const long MaxBytes = 32L * 1024 * 1024;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Reads bytes [0, count) of a URL: a ranged GET, or the first <c>count</c> bytes of a full answer from a server that
    /// ignores the range. Null when the server does not answer with content. May throw; the caller catches.
    /// </summary>
    public delegate Task<byte[]?> RangeReader(Uri url, long count, CancellationToken ct);

    /// <summary>
    /// The installer's product version (<see cref="FileVersionInfo.ProductVersion"/>, else the file version), reduced to
    /// its leading numeric version ("1.141.2"), or null when the URL is no exe, the file has no version resource within
    /// <see cref="MaxBytes"/>, or anything fails. Never throws, except for cancellation by <paramref name="ct"/>.
    /// </summary>
    public static async Task<string?> ReadVersionAsync(string installerUrl, string tempDirectory, RangeReader read, CancellationToken ct)
    {
        if (!IsExeUrl(installerUrl, out var uri)) return null;
        string? file = null;
        try
        {
            var head = await read(uri, HeaderBytes, ct).ConfigureAwait(false);
            if (head is null || ImageEnd(head) is not { } end || end > MaxBytes) return null;
            var bytes = end <= head.Length ? head : await read(uri, end, ct).ConfigureAwait(false);
            if (bytes is null || bytes.Length < end) return null;

            Directory.CreateDirectory(tempDirectory);
            file = Path.Combine(tempDirectory, $"installer-head-{Guid.NewGuid():N}.bin");
            await File.WriteAllBytesAsync(file, bytes.AsMemory(0, (int)end), ct).ConfigureAwait(false);
            var info = FileVersionInfo.GetVersionInfo(file);
            return LeadingVersion(info.ProductVersion) ?? LeadingVersion(info.FileVersion);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            return null;
        }
        finally
        {
            if (file is not null) try { File.Delete(file); } catch { /* a temp file; the folder is the agent's own */ }
        }
    }

    /// <summary>Whether <paramref name="url"/> is an http(s) URL whose path ends in ".exe". Pure, so the rule is testable.</summary>
    internal static bool IsExeUrl(string? url, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp) return false;
        if (!parsed.AbsolutePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        uri = parsed;
        return true;
    }

    /// <summary>
    /// Where the PE image ends in the file - the end of its last section, before any appended data such as a setup's
    /// payload or a signature - which is what Windows needs to read the version resource (a file cut right after the
    /// resource section is rejected when another section follows it, and the resources need not have a section of their
    /// own), or null when <paramref name="header"/> is no PE image, has no sections, or its section table lies beyond the
    /// bytes given. Pure, so the parsing is testable.
    /// </summary>
    internal static long? ImageEnd(ReadOnlySpan<byte> header)
    {
        if (header.Length < 0x40 || header[0] != 'M' || header[1] != 'Z') return null;
        var pe = BinaryPrimitives.ReadInt32LittleEndian(header[0x3C..]);
        if (pe <= 0 || pe > header.Length - 24) return null;
        if (header[pe] != 'P' || header[pe + 1] != 'E' || header[pe + 2] != 0 || header[pe + 3] != 0) return null;
        var sections = BinaryPrimitives.ReadUInt16LittleEndian(header[(pe + 6)..]);
        var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(header[(pe + 20)..]);
        var table = pe + 24 + optionalSize;
        if (sections == 0 || table + sections * 40 > header.Length) return null;
        long end = 0;
        for (var i = 0; i < sections; i++)
        {
            var s = header.Slice(table + i * 40, 40);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(s[16..]);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(s[20..]);
            if (size > 0 && offset > 0) end = Math.Max(end, (long)offset + size);
        }
        return end > 0 ? end : null;
    }

    [GeneratedRegex(@"^\s*v?(\d+(?:\.\d+){1,3})", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingVersionRegex();

    /// <summary>
    /// The leading numeric version of a version-resource string ("1.141.2", "157.0.1 (x64 en-US)" -> "157.0.1",
    /// "10.0.5+3c1e3a" -> "10.0.5"), or null. Pure, so the rule is testable.
    /// </summary>
    internal static string? LeadingVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var m = LeadingVersionRegex().Match(value);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// Whether winget's package is mislabeled: the installer it would run carries exactly the installed version, which is
    /// below the version the package claims. Pure, so the rule is testable. Deliberately narrow - only equality with the
    /// installed version counts - because many setups carry a version of their own (a wrapper's constant 1.0.0.0, a
    /// build number) that says nothing about the product; such a version never matches what is installed, so it never
    /// holds an update back.
    /// </summary>
    public static bool IsMislabeled(string? installerVersion, string? installedVersion, string? availableVersion)
    {
        if (string.IsNullOrWhiteSpace(installerVersion) || string.IsNullOrWhiteSpace(installedVersion) || string.IsNullOrWhiteSpace(availableVersion)) return false;
        if (VersionComparer.IsUnknown(installedVersion) || VersionComparer.IsUnknown(availableVersion)) return false;
        return VersionComparer.Compare(installerVersion, installedVersion) == 0
               && VersionComparer.Compare(installerVersion, availableVersion) < 0;
    }

    /// <summary>
    /// The built-in <see cref="RangeReader"/>: a ranged GET through <paramref name="proxyUrl"/> when one is configured,
    /// else the system proxy; redirects followed (GitHub, CDNs). At most <c>count</c> bytes are read even when the
    /// server ignores the range and sends the whole file.
    /// </summary>
    public static RangeReader CreateReader(string? proxyUrl) => async (url, count, ct) =>
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 10, ConnectTimeout = Timeout, UseCookies = false };
        if (!string.IsNullOrWhiteSpace(proxyUrl))
        {
            handler.Proxy = new WebProxy(proxyUrl) { UseDefaultCredentials = true };
            handler.UseProxy = true;
        }
        using var http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(WebProvider.UserAgent);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(0, count - 1);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode is not (HttpStatusCode.PartialContent or HttpStatusCode.OK)) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[count];
        var filled = 0;
        while (filled < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(filled, (int)Math.Min(count - filled, 1 << 20)), ct).ConfigureAwait(false);
            if (n == 0) break;
            filled += n;
        }
        return filled == count ? buffer : buffer[..filled];
    };
}
