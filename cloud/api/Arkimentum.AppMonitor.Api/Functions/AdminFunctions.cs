using Arkimentum.AppMonitor.Api.Contracts;
using Arkimentum.AppMonitor.Api.Security;
using Arkimentum.AppMonitor.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace Arkimentum.AppMonitor.Api.Functions;

/// <summary>
/// The administrator endpoints. Every one of them authenticates the Entra ID bearer token first and then lets
/// <see cref="AdminService"/> authorise against the organization; no query in this file runs without an OrganizationId.
/// </summary>
public sealed class AdminFunctions(AdminService admin, IAdminTokenValidator tokens)
{
    private const string Organizations = "v1/admin/organizations";

    [Function("AdminMe")]
    public async Task<IActionResult> MeAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/admin/me")] HttpRequest request, CancellationToken ct)
    {
        var (principal, failure) = await AuthenticateAsync(request, ct);
        if (failure is not null) return failure;
        return (await admin.MeAsync(principal!, ct)).ToResult(request);
    }

    [Function("AdminListOrganizations")]
    public async Task<IActionResult> ListOrganizationsAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = Organizations)] HttpRequest request, CancellationToken ct)
    {
        var (principal, failure) = await AuthenticateAsync(request, ct);
        if (failure is not null) return failure;
        return (await admin.ListOrganizationsAsync(principal!, ct)).ToResult(request);
    }

    [Function("AdminCreateOrganization")]
    public async Task<IActionResult> CreateOrganizationAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = Organizations)] HttpRequest request, CancellationToken ct)
    {
        var (principal, failure) = await AuthenticateAsync(request, ct);
        if (failure is not null) return failure;

        var raw = await request.ReadBodyAsync(ct);
        if (!HttpHelpers.TryDeserialize<CreateOrganizationRequest>(raw, out var body, out var problem))
            return request.BadRequest(problem!);

        return (await admin.CreateOrganizationAsync(principal!, body, ct)).ToResult(request);
    }

    [Function("AdminListDevices")]
    public async Task<IActionResult> ListDevicesAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = Organizations + "/{organizationId:guid}/devices")] HttpRequest request,
        Guid organizationId, CancellationToken ct)
    {
        var (principal, failure) = await AuthenticateAsync(request, ct);
        if (failure is not null) return failure;

        var outcome = await admin.ListDevicesAsync(principal!, organizationId,
            request.Query("search"), request.QueryInt("page", 1), request.QueryInt("pageSize", 50), ct);
        return outcome.ToResult(request);
    }

    [Function("AdminGetDevice")]
    public async Task<IActionResult> GetDeviceAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = Organizations + "/{organizationId:guid}/devices/{deviceId:guid}")] HttpRequest request,
        Guid organizationId, Guid deviceId, CancellationToken ct)
    {
        var (principal, failure) = await AuthenticateAsync(request, ct);
        if (failure is not null) return failure;
        return (await admin.GetDeviceAsync(principal!, organizationId, deviceId, ct)).ToResult(request);
    }

    [Function("AdminDeleteDevice")]
    public async Task<IActionResult> DeleteDeviceAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = Organizations + "/{organizationId:guid}/devices/{deviceId:guid}")] HttpRequest request,
        Guid organizationId, Guid deviceId, CancellationToken ct)
    {
        var (principal, failure) = await AuthenticateAsync(request, ct);
        if (failure is not null) return failure;
        return (await admin.DeleteDeviceAsync(principal!, organizationId, deviceId, ct)).ToResult(request);
    }

    [Function("AdminCreateDeviceCommand")]
    public async Task<IActionResult> CreateCommandAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = Organizations + "/{organizationId:guid}/devices/{deviceId:guid}/commands")] HttpRequest request,
        Guid organizationId, Guid deviceId, CancellationToken ct)
    {
        var (principal, failure) = await AuthenticateAsync(request, ct);
        if (failure is not null) return failure;

        var raw = await request.ReadBodyAsync(ct);
        if (!HttpHelpers.TryDeserialize<DeviceCommandRequest>(raw, out var body, out var problem))
            return request.BadRequest(problem!);

        return (await admin.CreateCommandAsync(principal!, organizationId, deviceId, body, ct)).ToResult(request);
    }

    [Function("AdminInventory")]
    public async Task<IActionResult> InventoryAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = Organizations + "/{organizationId:guid}/inventory")] HttpRequest request,
        Guid organizationId, CancellationToken ct)
    {
        var (principal, failure) = await AuthenticateAsync(request, ct);
        if (failure is not null) return failure;

        var outcome = await admin.InventoryAsync(principal!, organizationId,
            request.Query("search"), request.QueryBool("onlyUnmonitored"), ct);
        return outcome.ToResult(request);
    }

    [Function("AdminGetConfig")]
    public async Task<IActionResult> GetConfigAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = Organizations + "/{organizationId:guid}/config")] HttpRequest request,
        Guid organizationId, CancellationToken ct)
    {
        var (principal, failure) = await AuthenticateAsync(request, ct);
        if (failure is not null) return failure;
        return (await admin.GetConfigAsync(principal!, organizationId, ct)).ToResult(request);
    }

    [Function("AdminPutConfig")]
    public async Task<IActionResult> PutConfigAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = Organizations + "/{organizationId:guid}/config")] HttpRequest request,
        Guid organizationId, CancellationToken ct)
    {
        var (principal, failure) = await AuthenticateAsync(request, ct);
        if (failure is not null) return failure;

        var raw = await request.ReadBodyAsync(ct);
        if (!HttpHelpers.TryDeserialize<OrganizationConfigUpdateRequest>(raw, out var body, out var problem))
            return request.BadRequest(problem!);

        return (await admin.PutConfigAsync(principal!, organizationId, body, request.IfMatch(), ct)).ToResult(request);
    }

    [Function("AdminConfigHistory")]
    public async Task<IActionResult> ConfigHistoryAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = Organizations + "/{organizationId:guid}/config/history")] HttpRequest request,
        Guid organizationId, CancellationToken ct)
    {
        var (principal, failure) = await AuthenticateAsync(request, ct);
        if (failure is not null) return failure;

        var outcome = await admin.ConfigHistoryAsync(principal!, organizationId,
            request.QueryInt("page", 1), request.QueryInt("pageSize", 50), ct);
        return outcome.ToResult(request);
    }

    [Function("AdminGetConfigRevision")]
    public async Task<IActionResult> GetConfigRevisionAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = Organizations + "/{organizationId:guid}/config/history/{historyId:long}")] HttpRequest request,
        Guid organizationId, long historyId, CancellationToken ct)
    {
        var (principal, failure) = await AuthenticateAsync(request, ct);
        if (failure is not null) return failure;
        return (await admin.GetConfigRevisionAsync(principal!, organizationId, historyId, ct)).ToResult(request);
    }

    [Function("AdminGetEnrollment")]
    public async Task<IActionResult> GetEnrollmentAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = Organizations + "/{organizationId:guid}/enrollment")] HttpRequest request,
        Guid organizationId, CancellationToken ct)
    {
        var (principal, failure) = await AuthenticateAsync(request, ct);
        if (failure is not null) return failure;
        return (await admin.GetEnrollmentAsync(principal!, organizationId, ct)).ToResult(request);
    }

    [Function("AdminRotateEnrollment")]
    public async Task<IActionResult> RotateEnrollmentAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = Organizations + "/{organizationId:guid}/enrollment/rotate")] HttpRequest request,
        Guid organizationId, CancellationToken ct)
    {
        var (principal, failure) = await AuthenticateAsync(request, ct);
        if (failure is not null) return failure;
        return (await admin.RotateEnrollmentKeyAsync(principal!, organizationId, ct)).ToResult(request);
    }

    [Function("AdminGetRelease")]
    public async Task<IActionResult> GetReleaseAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = Organizations + "/{organizationId:guid}/release")] HttpRequest request,
        Guid organizationId, CancellationToken ct)
    {
        var (principal, failure) = await AuthenticateAsync(request, ct);
        if (failure is not null) return failure;
        return (await admin.GetReleaseAsync(principal!, organizationId, request.Query("channel"), ct)).ToResult(request);
    }

    [Function("AdminPutRelease")]
    public async Task<IActionResult> PutReleaseAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = Organizations + "/{organizationId:guid}/release")] HttpRequest request,
        Guid organizationId, CancellationToken ct)
    {
        var (principal, failure) = await AuthenticateAsync(request, ct);
        if (failure is not null) return failure;

        var raw = await request.ReadBodyAsync(ct);
        if (!HttpHelpers.TryDeserialize<ReleaseManifest>(raw, out var body, out var problem))
            return request.BadRequest(problem!);

        return (await admin.PutReleaseAsync(principal!, organizationId, request.Query("channel"), body, ct)).ToResult(request);
    }

    [Function("AdminEvents")]
    public async Task<IActionResult> EventsAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = Organizations + "/{organizationId:guid}/events")] HttpRequest request,
        Guid organizationId, CancellationToken ct)
    {
        var (principal, failure) = await AuthenticateAsync(request, ct);
        if (failure is not null) return failure;

        var outcome = await admin.EventsAsync(principal!, organizationId, request.QueryDate("since"),
            request.QueryInt("page", 1), request.QueryInt("pageSize", 50), ct);
        return outcome.ToResult(request);
    }

    private async Task<(AdminPrincipal? Principal, IActionResult? Failure)> AuthenticateAsync(HttpRequest request, CancellationToken ct)
    {
        var result = await tokens.ValidateAsync(request.Authorization(), ct);
        return result.Principal is null
            ? (null, request.Unauthorized(result.Error ?? "Authentication is required.", "Bearer"))
            : (result.Principal, null);
    }
}
