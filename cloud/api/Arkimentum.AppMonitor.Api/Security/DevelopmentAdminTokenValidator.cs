using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Api.Security;

/// <summary>
/// LOCAL DEVELOPMENT ONLY. Registered exclusively when AZURE_FUNCTIONS_ENVIRONMENT=Development *and*
/// DevBypassAdminAuth=true (see Program.cs) - never in Azure, where those settings are not present. It accepts
/// <c>Authorization: Bearer dev:{tenantId}:{upn}:{role,role}</c> so the admin console and curl can be exercised
/// against Azurite/LocalDB without an Entra ID tenant.
/// </summary>
public sealed class DevelopmentAdminTokenValidator(ILogger<DevelopmentAdminTokenValidator> logger) : IAdminTokenValidator
{
    private const string Prefix = "dev:";

    public Task<AdminTokenResult> ValidateAsync(string? authorizationHeader, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader) ||
            !authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AdminTokenResult.Fail("Missing bearer token."));

        var token = authorizationHeader["Bearer ".Length..].Trim();
        if (!token.StartsWith(Prefix, StringComparison.Ordinal))
            return Task.FromResult(AdminTokenResult.Fail("The development token must look like dev:{tenantId}:{upn}:{roles}."));

        var parts = token[Prefix.Length..].Split(':');
        if (parts.Length < 2)
            return Task.FromResult(AdminTokenResult.Fail("The development token must look like dev:{tenantId}:{upn}:{roles}."));

        var roles = parts.Length >= 3
            ? parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            : [AdminPrincipal.RoleAdmin];

        logger.LogWarning("Accepting a DEVELOPMENT admin token for {Upn} in tenant {TenantId}", parts[1], parts[0]);
        return Task.FromResult(AdminTokenResult.Ok(new AdminPrincipal(parts[0], parts[1], parts[1], null, roles)));
    }
}
