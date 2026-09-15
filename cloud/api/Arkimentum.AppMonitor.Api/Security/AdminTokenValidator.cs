using System.Security.Claims;
using Arkimentum.AppMonitor.Api.Services;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Arkimentum.AppMonitor.Api.Security;

/// <summary>An authenticated Entra ID administrator.</summary>
public sealed record AdminPrincipal(
    string TenantId,
    string UserPrincipalName,
    string? DisplayName,
    string? ObjectId,
    IReadOnlyList<string> Roles)
{
    public const string RoleAdmin = "AppMonitor.Admin";
    public const string RoleGlobalAdmin = "AppMonitor.GlobalAdmin";

    public bool HasRole(string role) => Roles.Any(r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase));
}

public sealed record AdminTokenResult(AdminPrincipal? Principal, string? Error)
{
    public static AdminTokenResult Ok(AdminPrincipal principal) => new(principal, null);
    public static AdminTokenResult Fail(string error) => new(null, error);
}

public interface IAdminTokenValidator
{
    /// <summary>Validates the raw value of the Authorization header ("Bearer ey...").</summary>
    Task<AdminTokenResult> ValidateAsync(string? authorizationHeader, CancellationToken ct);
}

/// <summary>
/// Validates Entra ID access tokens against the v2.0 OpenID metadata. The API is a multi-tenant app registration, so
/// the issuer is per-tenant: we accept any issuer of the form https://login.microsoftonline.com/{tid}/v2.0 whose
/// {tid} equals the token's own tid claim, and pin the audience to the API application id.
/// </summary>
public sealed class EntraAdminTokenValidator : IAdminTokenValidator
{
    private const string MetadataAddress = "https://login.microsoftonline.com/common/v2.0/.well-known/openid-configuration";

    private readonly ServerOptions _options;
    private readonly ILogger<EntraAdminTokenValidator> _logger;
    private readonly ConfigurationManager<OpenIdConnectConfiguration> _configurationManager;
    private readonly JsonWebTokenHandler _handler = new() { MapInboundClaims = false };

    public EntraAdminTokenValidator(ServerOptions options, ILogger<EntraAdminTokenValidator> logger)
    {
        _options = options;
        _logger = logger;
        _configurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            MetadataAddress, new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever { RequireHttps = true })
        {
            AutomaticRefreshInterval = TimeSpan.FromHours(12),
        };
    }

    public async Task<AdminTokenResult> ValidateAsync(string? authorizationHeader, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader) ||
            !authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AdminTokenResult.Fail("Missing bearer token.");

        var token = authorizationHeader["Bearer ".Length..].Trim();
        if (token.Length == 0) return AdminTokenResult.Fail("Missing bearer token.");

        OpenIdConnectConfiguration configuration;
        try
        {
            configuration = await _configurationManager.GetConfigurationAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read the Entra ID OpenID metadata");
            return AdminTokenResult.Fail("Identity metadata is unavailable.");
        }

        var parameters = new TokenValidationParameters
        {
            ValidateAudience = true,
            ValidAudiences = _options.ValidAudiences,
            ValidateIssuer = true,
            IssuerValidator = ValidateIssuer,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = configuration.SigningKeys,
        };

        var result = await _handler.ValidateTokenAsync(token, parameters);
        if (!result.IsValid)
        {
            _logger.LogInformation("Rejected an admin token: {Reason}", result.Exception?.Message);
            return AdminTokenResult.Fail("The access token is not valid.");
        }

        var principal = FromClaims(result.ClaimsIdentity);
        if (principal is null) return AdminTokenResult.Fail("The access token has no tenant claim.");

        // Both the scope and at least one app role must be present: the console asks for AppMonitor.Admin,
        // and the directory administrator decides who is allowed to use it.
        if (!HasRequiredScope(result.ClaimsIdentity))
            return AdminTokenResult.Fail($"The access token is missing the '{_options.RequiredScope}' scope.");

        return AdminTokenResult.Ok(principal);
    }

    private bool HasRequiredScope(ClaimsIdentity identity)
    {
        var scopes = identity.FindFirst("scp")?.Value ?? identity.FindFirst("http://schemas.microsoft.com/identity/claims/scope")?.Value;
        if (string.IsNullOrEmpty(scopes)) return false;
        return scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(s => string.Equals(s, _options.RequiredScope, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Accepts https://login.microsoftonline.com/{tid}/v2.0 (or sts.windows.net/{tid}/) for the token's own tenant.</summary>
    private static string ValidateIssuer(string issuer, SecurityToken token, TokenValidationParameters parameters)
    {
        var tid = (token as JsonWebToken)?.GetClaim("tid")?.Value;
        if (string.IsNullOrEmpty(tid)) throw new SecurityTokenInvalidIssuerException("The token has no tid claim.");

        string[] accepted =
        [
            $"https://login.microsoftonline.com/{tid}/v2.0",
            $"https://sts.windows.net/{tid}/",
        ];
        if (accepted.Any(a => string.Equals(a, issuer, StringComparison.OrdinalIgnoreCase))) return issuer;
        throw new SecurityTokenInvalidIssuerException($"Issuer '{issuer}' does not match tenant '{tid}'.");
    }

    internal static AdminPrincipal? FromClaims(ClaimsIdentity identity)
    {
        var tid = identity.FindFirst("tid")?.Value;
        if (string.IsNullOrWhiteSpace(tid)) return null;

        var upn = identity.FindFirst("preferred_username")?.Value
                  ?? identity.FindFirst("upn")?.Value
                  ?? identity.FindFirst("unique_name")?.Value
                  ?? identity.FindFirst("appid")?.Value
                  ?? identity.FindFirst("azp")?.Value
                  ?? "unknown";
        var name = identity.FindFirst("name")?.Value;
        var oid = identity.FindFirst("oid")?.Value ?? identity.FindFirst("sub")?.Value;
        var roles = identity.FindAll("roles").Select(c => c.Value)
            .Concat(identity.FindAll(ClaimTypes.Role).Select(c => c.Value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new AdminPrincipal(tid, upn, name, oid, roles);
    }
}
