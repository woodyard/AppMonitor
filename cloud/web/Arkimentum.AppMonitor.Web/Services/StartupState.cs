using Arkimentum.AppMonitor.Api.Contracts;

namespace Arkimentum.AppMonitor.Web.Services;

/// <summary>
/// What the console learned before the first component rendered: which API it talks to, and how that API wants it to
/// sign in.
///
/// <para>
/// The only thing the deployment has to configure is <c>ApiBaseUrl</c> in <c>wwwroot/appsettings.json</c> (the deploy
/// script writes it at publish time). Everything else - client id, authority, scope - comes from the API's own
/// <c>/api/v1/public/auth-config</c>, so a re-registered application never means rebuilding this app.
/// </para>
///
/// <para>
/// When the URL is missing or the probe fails, the app still starts: it renders an explanatory page instead of
/// crashing on a null authentication provider, because a blank page is the one thing an administrator cannot debug.
/// </para>
/// </summary>
public sealed class StartupState
{
    public StartupState(string apiBaseUrl) => ApiBaseUrl = apiBaseUrl;

    /// <summary>Base URL of the Function App, with a trailing slash. Empty when nothing was configured.</summary>
    public string ApiBaseUrl { get; }

    /// <summary>Null when <c>/public/auth-config</c> could not be read; <see cref="Error"/> then says why.</summary>
    public AuthConfigResponse? AuthConfig { get; set; }

    /// <summary>Why the console could not start normally, in one sentence.</summary>
    public string? Error { get; set; }

    /// <summary>True when the app has an API URL and sign-in configuration, i.e. when it can actually be used.</summary>
    public bool IsConfigured => ApiBaseUrl.Length > 0 && AuthConfig is not null;

    /// <summary>True when nothing was configured at all, as opposed to a configured server that did not answer.</summary>
    public bool IsMissingUrl => ApiBaseUrl.Length == 0;

    /// <summary>Normalises a configured base URL: trimmed, with exactly one trailing slash.</summary>
    public static string Normalise(string? url)
    {
        var text = (url ?? string.Empty).Trim();
        return text.Length == 0 ? string.Empty : text.TrimEnd('/') + "/";
    }
}
