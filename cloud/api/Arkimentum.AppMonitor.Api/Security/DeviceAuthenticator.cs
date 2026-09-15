using Arkimentum.AppMonitor.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Arkimentum.AppMonitor.Api.Security;

/// <summary>An authenticated device. Every device query is scoped by <see cref="OrganizationId"/>.</summary>
public sealed record DeviceIdentity(Guid DeviceId, Guid OrganizationId, string OrganizationName, string DeviceName);

public interface IDeviceAuthenticator
{
    Task<DeviceIdentity?> AuthenticateAsync(string? authorizationHeader, CancellationToken ct);
}

/// <summary>
/// Validates <c>Authorization: Device {deviceId}:{deviceKey}</c>. The key is never stored - only its SHA-256 - and the
/// comparison is fixed-time. A deleted or deactivated device, or one whose organization is inactive, fails.
/// </summary>
public sealed class DeviceAuthenticator(AppMonitorDbContext db) : IDeviceAuthenticator
{
    public async Task<DeviceIdentity?> AuthenticateAsync(string? authorizationHeader, CancellationToken ct)
    {
        if (!TryParse(authorizationHeader, out var deviceId, out var deviceKey)) return null;

        var row = await db.Devices
            .AsNoTracking()
            .Where(d => d.Id == deviceId && !d.IsDeleted)
            .Select(d => new { d.Id, d.OrganizationId, d.DeviceKeyHash, d.DeviceName, OrgName = d.Organization!.Name, OrgActive = d.Organization!.IsActive })
            .FirstOrDefaultAsync(ct);

        if (row is null || !row.OrgActive) return null;
        if (!Secrets.Verify(deviceKey, row.DeviceKeyHash)) return null;
        return new DeviceIdentity(row.Id, row.OrganizationId, row.OrgName, row.DeviceName);
    }

    internal static bool TryParse(string? header, out Guid deviceId, out string deviceKey)
    {
        deviceId = Guid.Empty;
        deviceKey = string.Empty;
        if (string.IsNullOrWhiteSpace(header)) return false;

        var space = header.IndexOf(' ');
        if (space <= 0) return false;
        if (!header.AsSpan(0, space).Equals(Contracts.CloudRoutes.DeviceAuthScheme, StringComparison.OrdinalIgnoreCase)) return false;

        var value = header[(space + 1)..].Trim();
        var colon = value.IndexOf(':');
        if (colon <= 0 || colon == value.Length - 1) return false;

        if (!Guid.TryParse(value[..colon], out deviceId)) return false;
        deviceKey = value[(colon + 1)..];
        return deviceKey.Length is > 0 and <= 512;
    }
}
