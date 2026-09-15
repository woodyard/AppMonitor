using System.IO;
using Arkimentum.AppMonitor.Admin.Infrastructure;
using Arkimentum.AppMonitor.Cloud;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace Arkimentum.AppMonitor.Admin.Services;

/// <summary>
/// Entra ID sign-in through MSAL, as a public client.
///
/// <para>
/// The client id, the authority and the scope all come from the server's unauthenticated
/// <c>/api/v1/public/auth-config</c> endpoint, so the console holds no tenant configuration of its own and can be
/// pointed at any AppMonitor deployment. The redirect URI is <c>http://localhost</c>: MSAL opens the machine's
/// default browser (never an embedded web view, which cannot do modern conditional access) and listens on a
/// loopback port for the reply.
/// </para>
///
/// <para>
/// Tokens are cached in <c>%LOCALAPPDATA%\Arkimentum\AppMonitor\msal.cache</c>, encrypted with DPAPI for the
/// current user by <c>Microsoft.Identity.Client.Extensions.Msal</c>, so the next start of the console is silent.
/// Sign out removes every account from that cache.
/// </para>
/// </summary>
public sealed class MsalAuthenticator : IAuthenticator
{
    /// <summary>The file under <see cref="AdminAppInfo.LocalRoot"/> holding the encrypted token cache.</summary>
    public const string CacheFileName = "msal.cache";

    /// <summary>Loopback redirect; MSAL picks the port. The app registration must allow http://localhost.</summary>
    public const string RedirectUri = "http://localhost";

    private readonly ILogger<MsalAuthenticator> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IPublicClientApplication? _application;
    private string? _applicationKey;

    public MsalAuthenticator(ILogger<MsalAuthenticator> log) => _log = log;

    public string? SignedInAccount { get; private set; }

    public static string CachePath => Path.Combine(AdminAppInfo.LocalRoot, CacheFileName);

    public async Task<AuthToken> AcquireTokenAsync(AuthConfigResponse config, bool allowInteractive, CancellationToken ct)
    {
        var scopes = Scopes(config);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var app = await GetApplicationAsync(config).ConfigureAwait(false);
            var accounts = await app.GetAccountsAsync().ConfigureAwait(false);
            var account = accounts.FirstOrDefault();

            if (account is not null)
            {
                try
                {
                    _log.LogInformation("Acquiring an access token silently for {Account} from the MSAL cache.", account.Username);
                    var silent = await app.AcquireTokenSilent(scopes, account).ExecuteAsync(ct).ConfigureAwait(false);
                    return Remember(silent);
                }
                catch (MsalUiRequiredException ex)
                {
                    _log.LogInformation("The cached token cannot be refreshed silently ({Code}); interactive sign-in is required.", ex.ErrorCode);
                }
            }

            if (!allowInteractive)
                throw new AuthenticationRequiredException(Resources.Strings.CloudSignInRequired);

            _log.LogInformation("Starting an interactive sign-in against {Authority} for scope {Scope}.", config.Authority, config.Scope);
            var interactive = await app.AcquireTokenInteractive(scopes)
                .WithUseEmbeddedWebView(false)
                .ExecuteAsync(ct)
                .ConfigureAwait(false);
            return Remember(interactive);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SignOutAsync(AuthConfigResponse? config, CancellationToken ct)
    {
        SignedInAccount = null;
        if (config is null) { _application = null; _applicationKey = null; return; }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var app = await GetApplicationAsync(config).ConfigureAwait(false);
            foreach (var account in await app.GetAccountsAsync().ConfigureAwait(false))
            {
                _log.LogInformation("Removing {Account} from the MSAL token cache.", account.Username);
                await app.RemoveAsync(account).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Clearing the MSAL token cache failed; the cache file may be stale.");
        }
        finally
        {
            _application = null;
            _applicationKey = null;
            _gate.Release();
        }
    }

    // ---------------------------------------------------------------- plumbing

    private AuthToken Remember(AuthenticationResult result)
    {
        SignedInAccount = result.Account?.Username;
        _log.LogInformation("Acquired an access token for {Account}, valid until {Expires:u}.", SignedInAccount, result.ExpiresOn);
        return new AuthToken(result.AccessToken, SignedInAccount, result.ExpiresOn);
    }

    /// <summary>MSAL wants the scope as a collection; auth-config gives exactly one.</summary>
    private static string[] Scopes(AuthConfigResponse config) =>
        config.Scope.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private async Task<IPublicClientApplication> GetApplicationAsync(AuthConfigResponse config)
    {
        var key = config.ClientId + "|" + config.Authority;
        if (_application is not null && _applicationKey == key) return _application;

        var application = PublicClientApplicationBuilder
            .Create(config.ClientId)
            .WithAuthority(config.Authority)
            .WithRedirectUri(RedirectUri)
            .WithClientName(Resources.Strings.ProductName)
            .WithClientVersion(AdminAppInfo.Version)
            .Build();

        await AttachCacheAsync(application).ConfigureAwait(false);
        _application = application;
        _applicationKey = key;
        return application;
    }

    /// <summary>
    /// Binds the persistent cache. A failure here is not fatal: the console still signs in, it just asks again
    /// next time, which is much better than refusing to work because a cache file could not be created.
    /// </summary>
    private async Task AttachCacheAsync(IPublicClientApplication application)
    {
        try
        {
            AdminAppInfo.TryCreateDirectory(AdminAppInfo.LocalRoot);
            var storage = new StorageCreationPropertiesBuilder(CacheFileName, AdminAppInfo.LocalRoot).Build();
            var helper = await MsalCacheHelper.CreateAsync(storage).ConfigureAwait(false);
            helper.RegisterCache(application.UserTokenCache);
            _log.LogInformation("MSAL token cache at {Path}.", CachePath);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "The persistent MSAL token cache could not be opened; sign-in will not be remembered.");
        }
    }
}
