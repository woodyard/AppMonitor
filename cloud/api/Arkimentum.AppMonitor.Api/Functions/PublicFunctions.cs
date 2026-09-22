using Arkimentum.AppMonitor.Api.Contracts;
using Arkimentum.AppMonitor.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace Arkimentum.AppMonitor.Api.Functions;

/// <summary>Unauthenticated endpoints: how to sign in, and whether the API is alive.</summary>
public sealed class PublicFunctions(ServerOptions options)
{
    /// <summary>GET /api/v1/public/auth-config - everything the admin console needs besides the server URL.</summary>
    [Function("GetAuthConfig")]
    public IActionResult GetAuthConfig(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/public/auth-config")] HttpRequest request)
    {
        if (string.IsNullOrWhiteSpace(options.AdminClientId) || string.IsNullOrWhiteSpace(options.ApiClientId))
            return request.Error(StatusCodes.Status503ServiceUnavailable, ErrorCodes.Internal,
                "The Entra ID application registrations are not configured on this server.");

        return ApiOutcome<AuthConfigResponse>.Ok(new AuthConfigResponse
        {
            ClientId = options.AdminClientId,
            Authority = options.Authority,
            Scope = options.ScopeUri,
            ApiVersion = CloudRoutes.ApiVersion,
            WebAdminUrl = string.IsNullOrWhiteSpace(options.PublicWebAdminUrl) ? null : options.PublicWebAdminUrl,
        }).ToResult(request);
    }

    /// <summary>GET /api/v1/public/health - a liveness probe for the deploy script and for monitoring.</summary>
    [Function("GetHealth")]
    public IActionResult GetHealth(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/public/health")] HttpRequest request) =>
        new ContentResult
        {
            Content = $"{{\"status\":\"ok\",\"apiVersion\":\"{CloudRoutes.ApiVersion}\"}}",
            ContentType = HttpHelpers.JsonContentType,
            StatusCode = StatusCodes.Status200OK,
        };
}
