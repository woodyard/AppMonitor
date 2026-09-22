using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;

namespace Arkimentum.AppMonitor.Web.Services;

/// <summary>
/// Attaches the Entra access token to calls to the admin API - and to nothing else.
///
/// <para>
/// The handler is scoped to the API's base URL on purpose: the console also fetches <c>catalog.json</c> and its own
/// settings files from its own origin, and those must not carry a bearer token. The scope requested is the one the
/// server named in <c>/public/auth-config</c>, so the token always matches what the API validates.
/// </para>
/// </summary>
public sealed class ApiAuthorizationMessageHandler : AuthorizationMessageHandler
{
    public ApiAuthorizationMessageHandler(IAccessTokenProvider provider, NavigationManager navigation, StartupState startup)
        : base(provider, navigation)
    {
        ConfigureHandler(
            authorizedUrls: [startup.ApiBaseUrl],
            scopes: startup.AuthConfig is { Scope.Length: > 0 } auth ? [auth.Scope] : []);
    }
}
