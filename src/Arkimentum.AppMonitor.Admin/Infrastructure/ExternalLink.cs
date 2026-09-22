using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Admin.Infrastructure;

/// <summary>
/// Opens a web address in the administrator's default browser.
///
/// <para>
/// The only address the console opens this way comes from the server (<c>webAdminUrl</c> in
/// <c>/public/auth-config</c>), so it is treated as untrusted input: anything that is not an absolute
/// <c>http</c> or <c>https</c> URL is refused rather than handed to ShellExecute, which would happily start a
/// local program for a <c>file:</c> or an unknown scheme.
/// </para>
/// </summary>
public static class ExternalLink
{
    /// <summary>True when <paramref name="url"/> is an absolute http(s) address the console may open.</summary>
    public static bool IsBrowsable(string? url) =>
        !string.IsNullOrWhiteSpace(url) &&
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>Starts the default browser on <paramref name="url"/>; false when it was refused or failed.</summary>
    public static bool TryOpen(string? url, ILogger log)
    {
        if (!IsBrowsable(url))
        {
            log.LogWarning("Refusing to open {Url}: only absolute http and https addresses are opened.", url);
            return false;
        }

        try
        {
            log.LogInformation("Opening {Url} in the default browser.", url);
            Process.Start(new ProcessStartInfo { FileName = url!.Trim(), UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not open {Url} in the default browser.", url);
            return false;
        }
    }
}
