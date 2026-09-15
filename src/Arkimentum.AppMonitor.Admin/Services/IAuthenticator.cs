using Arkimentum.AppMonitor.Cloud;

namespace Arkimentum.AppMonitor.Admin.Services;

/// <summary>An access token and who it belongs to.</summary>
/// <param name="AccessToken">Bearer token for the admin API.</param>
/// <param name="Account">UPN of the signed-in account, for the navigation footer.</param>
/// <param name="ExpiresOn">When the token stops working; the session refreshes silently before then.</param>
public sealed record AuthToken(string AccessToken, string? Account, DateTimeOffset ExpiresOn);

/// <summary>
/// Sign-in for the organization pages. The interface exists so the token acquisition can be replaced in a test
/// harness: the real implementation opens the system browser, which no automated run may do.
/// </summary>
public interface IAuthenticator
{
    /// <summary>The account the cached token belongs to, or null when nobody is signed in.</summary>
    string? SignedInAccount { get; }

    /// <summary>
    /// A token for <paramref name="config"/>.<c>Scope</c>. Tries the persistent cache first; falls back to an
    /// interactive sign-in only when <paramref name="allowInteractive"/> is true. Throws
    /// <see cref="AuthenticationRequiredException"/> when a silent attempt finds nothing to work with.
    /// </summary>
    Task<AuthToken> AcquireTokenAsync(AuthConfigResponse config, bool allowInteractive, CancellationToken ct);

    /// <summary>Forgets every cached account and token for this machine's user.</summary>
    Task SignOutAsync(AuthConfigResponse? config, CancellationToken ct);
}

/// <summary>A silent acquisition found no usable account; the caller must offer an interactive sign-in.</summary>
public sealed class AuthenticationRequiredException(string message) : Exception(message);
