using Arkimentum.AppMonitor.Api.Contracts;
using Arkimentum.AppMonitor.Api.Security;
using Arkimentum.AppMonitor.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace Arkimentum.AppMonitor.Api.Functions;

/// <summary>
/// The device endpoints. Everything except enrollment requires <c>Authorization: Device {deviceId}:{deviceKey}</c>;
/// the Functions authorization level is Anonymous because the API does its own authentication.
/// </summary>
public sealed class DeviceFunctions(DeviceService devices, IDeviceAuthenticator authenticator)
{
    [Function("DeviceEnroll")]
    public async Task<IActionResult> EnrollAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/device/enroll")] HttpRequest request,
        CancellationToken ct)
    {
        var raw = await request.ReadBodyAsync(ct);
        if (!HttpHelpers.TryDeserialize<EnrollRequest>(raw, out var body, out var problem))
            return request.BadRequest(problem!);

        var outcome = await devices.EnrollAsync(body, request.ClientIp(), ct);
        return outcome.ToResult(request);
    }

    [Function("DeviceGetConfig")]
    public async Task<IActionResult> GetConfigAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/device/config")] HttpRequest request,
        CancellationToken ct)
    {
        var identity = await authenticator.AuthenticateAsync(request.Authorization(), ct);
        if (identity is null) return DeviceUnauthorized(request);

        var outcome = await devices.GetConfigAsync(identity, request.IfNoneMatch(), ct);
        return outcome.ToResult(request);
    }

    [Function("DeviceReport")]
    public async Task<IActionResult> ReportAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/device/report")] HttpRequest request,
        CancellationToken ct)
    {
        var identity = await authenticator.AuthenticateAsync(request.Authorization(), ct);
        if (identity is null) return DeviceUnauthorized(request);

        var raw = await request.ReadBodyAsync(ct);
        if (!HttpHelpers.TryDeserialize<DeviceReport>(raw, out var body, out var problem))
            return request.BadRequest(problem!);

        var outcome = await devices.ReportAsync(identity, body, raw, ct);
        return outcome.ToResult(request);
    }

    [Function("DeviceGetRelease")]
    public async Task<IActionResult> GetReleaseAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/device/release")] HttpRequest request,
        CancellationToken ct)
    {
        var identity = await authenticator.AuthenticateAsync(request.Authorization(), ct);
        if (identity is null) return DeviceUnauthorized(request);

        var outcome = await devices.GetReleaseAsync(request.Query("channel"), ct);
        return outcome.ToResult(request);
    }

    private static IActionResult DeviceUnauthorized(HttpRequest request) =>
        request.Unauthorized("A valid device credential is required.", CloudRoutes.DeviceAuthScheme);
}
