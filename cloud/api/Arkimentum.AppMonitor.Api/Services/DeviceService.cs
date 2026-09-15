using System.Net;
using System.Text.Json;
using Arkimentum.AppMonitor.Api.Contracts;
using Arkimentum.AppMonitor.Api.Data;
using Arkimentum.AppMonitor.Api.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Api.Services;

/// <summary>
/// The device half of the API: enrollment, configuration polling, reporting and the release mirror.
/// Handlers here never see HTTP; the Functions in Functions/DeviceFunctions.cs do the binding and status mapping.
/// </summary>
public sealed class DeviceService(
    AppMonitorDbContext db,
    ServerOptions options,
    IEnrollRateLimiter rateLimiter,
    IReportArchive archive,
    ILogger<DeviceService> logger,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    // Sanity caps so one misbehaving agent cannot write an unbounded report.
    private const int MaxApps = 5000;
    private const int MaxUpdates = 2000;
    private const int MaxEvents = 1000;
    private const int MaxAcknowledgements = 200;

    /// <summary>Commands handed to a device in one config response (the rest follow on the next poll).</summary>
    internal const int MaxCommandsPerResponse = 20;

    // ------------------------------------------------------------------------------------------------ enrollment

    public async Task<ApiOutcome<EnrollResponse>> EnrollAsync(EnrollRequest? request, string clientIp, CancellationToken ct)
    {
        if (request is null) return ApiOutcome<EnrollResponse>.BadRequest("A JSON body is required.");
        if (request.OrganizationId == Guid.Empty) return ApiOutcome<EnrollResponse>.BadRequest("organizationId is required.");
        if (string.IsNullOrWhiteSpace(request.EnrollmentKey)) return ApiOutcome<EnrollResponse>.BadRequest("enrollmentKey is required.");
        if (string.IsNullOrWhiteSpace(request.MachineGuid)) return ApiOutcome<EnrollResponse>.BadRequest("machineGuid is required.");
        if (string.IsNullOrWhiteSpace(request.DeviceName)) return ApiOutcome<EnrollResponse>.BadRequest("deviceName is required.");

        if (!rateLimiter.TryAcquire(clientIp, request.OrganizationId, out var retryAfter))
        {
            logger.LogWarning("Enrollment rate limit hit from {ClientIp} for organization {OrganizationId}", clientIp, request.OrganizationId);
            return ApiOutcome<EnrollResponse>.Failure(HttpStatusCode.TooManyRequests, ErrorCodes.RateLimited,
                "Too many enrollment attempts. Try again later.", retryAfter);
        }

        var organization = await db.Organizations.FirstOrDefaultAsync(o => o.Id == request.OrganizationId, ct);

        // Always run the comparison, even for an unknown organization, so a caller cannot distinguish
        // "no such organization" from "wrong key" by timing.
        var keyValid = Secrets.Verify(request.EnrollmentKey, organization?.EnrollmentKeyHash ?? new byte[32]);
        if (organization is null || !keyValid)
        {
            logger.LogInformation("Rejected enrollment from {ClientIp} for organization {OrganizationId}", clientIp, request.OrganizationId);
            return ApiOutcome<EnrollResponse>.Failure(HttpStatusCode.Unauthorized, ErrorCodes.InvalidEnrollmentKey,
                "The organization id or the enrollment key is not valid.");
        }
        if (!organization.IsActive)
            return ApiOutcome<EnrollResponse>.Failure(HttpStatusCode.Forbidden, ErrorCodes.OrganizationInactive, "The organization is not active.");

        var machineGuid = Normalize(request.MachineGuid)!;
        var now = _time.GetUtcNow();
        var deviceKey = Secrets.NewKey();

        var device = await db.Devices.FirstOrDefaultAsync(d => d.OrganizationId == organization.Id && d.MachineGuid == machineGuid, ct);
        var reattached = device is not null;
        if (device is null)
        {
            device = new Device
            {
                Id = Guid.NewGuid(),
                OrganizationId = organization.Id,
                MachineGuid = machineGuid,
                EnrolledUtc = now,
            };
            db.Devices.Add(device);
        }

        device.DeviceName = Truncate(request.DeviceName, 256)!;
        device.EntraTenantId = Truncate(Normalize(request.EntraTenantId), 64);
        device.EntraDeviceId = Truncate(Normalize(request.EntraDeviceId), 64);
        device.OsVersion = Truncate(Normalize(request.OsVersion), 128);
        device.AgentVersion = Truncate(Normalize(request.AgentVersion), 64);
        device.DeviceKeyHash = Secrets.Hash(deviceKey);
        device.LastSeenUtc = now;
        device.IsDeleted = false;

        var config = await db.OrganizationConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.OrganizationId == organization.Id, ct);

        db.AuditLog.Add(new AuditLogEntry
        {
            OrganizationId = organization.Id,
            Actor = $"device:{machineGuid}",
            Action = reattached ? "device.reenrolled" : "device.enrolled",
            TargetType = "Device",
            TargetId = device.Id.ToString(),
            Utc = now,
            Details = $"{device.DeviceName} from {clientIp}",
        });

        await db.SaveChangesAsync(ct);

        return ApiOutcome<EnrollResponse>.Ok(new EnrollResponse
        {
            DeviceId = device.Id,
            DeviceKey = deviceKey,
            OrganizationName = organization.Name,
            ConfigVersion = config?.ConfigVersion ?? ConfigVersioning.Initial,
        });
    }

    // ------------------------------------------------------------------------------------------------ configuration

    public async Task<ApiOutcome<DeviceConfigResponse>> GetConfigAsync(DeviceIdentity identity, string? ifNoneMatch, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var config = await db.OrganizationConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.OrganizationId == identity.OrganizationId, ct);
        var version = config?.ConfigVersion ?? ConfigVersioning.Initial;

        // Commands ride on this response, so every unacknowledged command is re-delivered on each 200 until the
        // device confirms it in DeviceReport.AcknowledgedCommands. Oldest first, capped so one stuck device
        // cannot grow the payload without bound.
        var commands = await db.DeviceCommands
            .AsNoTracking()
            .Where(c => c.DeviceId == identity.DeviceId && c.AcknowledgedUtc == null)
            .OrderBy(c => c.IssuedUtc)
            .Take(MaxCommandsPerResponse)
            .ToListAsync(ct);

        await TouchLastSeenAsync(identity.DeviceId, now, ct);

        // A 304 would hide pending commands, so only short-circuit when there is nothing to deliver.
        if (commands.Count == 0 && ConfigVersioning.Unquote(ifNoneMatch) is { } tag && tag == version)
            return ApiOutcome<DeviceConfigResponse>.NotModified(version);

        if (commands.Count > 0)
        {
            var delivering = commands.Where(c => c.DeliveredUtc is null).Select(c => c.Id).ToList();
            if (delivering.Count > 0)
                await db.DeviceCommands
                    .Where(c => c.DeviceId == identity.DeviceId && c.DeliveredUtc == null && delivering.Contains(c.Id))
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.DeliveredUtc, now), ct);
        }

        var settings = ParseSettings(config?.SettingsJson);

        return ApiOutcome<DeviceConfigResponse>.Ok(new DeviceConfigResponse
        {
            OrganizationId = identity.OrganizationId,
            OrganizationName = identity.OrganizationName,
            ConfigVersion = version,
            UpdatedUtc = config?.UpdatedUtc ?? default,
            UpdatedBy = config?.UpdatedBy,
            Settings = settings,
            PollIntervalSeconds = options.PollIntervalSeconds,
            Commands = [.. commands.Select(c => new DeviceCommand
            {
                CommandId = c.Id,
                Kind = c.Kind,
                Argument = c.Argument,
                IssuedUtc = c.IssuedUtc,
                IssuedBy = c.IssuedBy,
            })],
        }, version);
    }

    // ------------------------------------------------------------------------------------------------ reporting

    public async Task<ApiOutcome<ReportResponse>> ReportAsync(DeviceIdentity identity, DeviceReport? report, string rawJson, CancellationToken ct)
    {
        if (report is null) return ApiOutcome<ReportResponse>.BadRequest("A JSON body is required.");
        if (string.IsNullOrWhiteSpace(report.AgentVersion)) return ApiOutcome<ReportResponse>.BadRequest("agentVersion is required.");
        if (report.InstalledApps.Count > MaxApps) return ApiOutcome<ReportResponse>.BadRequest($"installedApps may hold at most {MaxApps} entries.");
        if (report.Updates.Count > MaxUpdates) return ApiOutcome<ReportResponse>.BadRequest($"updates may hold at most {MaxUpdates} entries.");
        if (report.Events.Count > MaxEvents) return ApiOutcome<ReportResponse>.BadRequest($"events may hold at most {MaxEvents} entries.");
        if (report.AcknowledgedCommands.Count > MaxAcknowledgements) return ApiOutcome<ReportResponse>.BadRequest($"acknowledgedCommands may hold at most {MaxAcknowledgements} entries.");

        var now = _time.GetUtcNow();
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == identity.DeviceId, ct);
        if (device is null) return ApiOutcome<ReportResponse>.NotFound("The device no longer exists.");

        device.LastSeenUtc = now;
        device.AgentVersion = Truncate(Normalize(report.AgentVersion), 64) ?? device.AgentVersion;
        device.OsVersion = Truncate(Normalize(report.OsVersion), 128) ?? device.OsVersion;
        if (Truncate(Normalize(report.DeviceName), 256) is { } name) device.DeviceName = name;
        device.LastLogonUser = Truncate(Normalize(report.LastLogonUser), 256) ?? device.LastLogonUser;
        if (report.LastScanUtc is { } scan) device.LastScanUtc = scan;
        device.ConfigVersionApplied = Truncate(Normalize(report.ConfigVersionApplied), 64) ?? device.ConfigVersionApplied;
        device.PrerequisitesJson = report.Prerequisites is null ? device.PrerequisitesJson : JsonSerializer.Serialize(report.Prerequisites, CloudJson.Options);

        device.PendingUpdateCount = report.Updates.Count(u => u.State is UpdateState.Available or UpdateState.Deferred
            or UpdateState.Scheduled or UpdateState.WaitingForClose or UpdateState.Installing);
        device.FailedUpdateCount = report.Updates.Count(u => u.State == UpdateState.Failed);

        // The latest snapshot replaces the previous one wholesale - the report is authoritative for what is installed.
        await db.DeviceApps.Where(a => a.DeviceId == device.Id).ExecuteDeleteAsync(ct);
        await db.DeviceUpdates.Where(u => u.DeviceId == device.Id).ExecuteDeleteAsync(ct);

        db.DeviceApps.AddRange(report.InstalledApps
            .Where(a => !string.IsNullOrWhiteSpace(a.DisplayName))
            .Select(a => new DeviceApp
            {
                DeviceId = device.Id,
                OrganizationId = device.OrganizationId,
                DisplayName = Truncate(a.DisplayName, 400)!,
                Version = Truncate(Normalize(a.Version), 128),
                Publisher = Truncate(Normalize(a.Publisher), 256),
                WingetId = Truncate(Normalize(a.WingetId), 256),
                AvailableVersion = Truncate(Normalize(a.AvailableVersion), 128),
                Context = a.Context,
                CatalogAppId = Truncate(Normalize(a.CatalogAppId), 128),
                MonitoredAppId = Truncate(Normalize(a.MonitoredAppId), 128),
            }));

        db.DeviceUpdates.AddRange(report.Updates
            .Where(u => !string.IsNullOrWhiteSpace(u.AppId))
            .Select(u => new DeviceUpdate
            {
                DeviceId = device.Id,
                OrganizationId = device.OrganizationId,
                AppId = Truncate(u.AppId, 128)!,
                DisplayName = Truncate(Normalize(u.DisplayName), 400),
                InstalledVersion = Truncate(Normalize(u.InstalledVersion), 128),
                AvailableVersion = Truncate(Normalize(u.AvailableVersion), 128),
                State = u.State,
                Context = u.Context,
                Mandatory = u.Mandatory,
                DeadlineUtc = u.DeadlineUtc,
                DeferredUntilUtc = u.DeferredUntilUtc,
                DeferralCount = u.DeferralCount,
                FirstDetectedUtc = u.FirstDetectedUtc,
                InstalledAtUtc = u.InstalledAtUtc,
                LastError = Truncate(Normalize(u.LastError), 2000),
            }));

        db.DeviceEvents.AddRange(report.Events.Select(e => new DeviceEvent
        {
            DeviceId = device.Id,
            OrganizationId = device.OrganizationId,
            OccurredUtc = e.OccurredUtc,
            Kind = e.Kind,
            AppId = Truncate(Normalize(e.AppId), 128),
            Message = Truncate(Normalize(e.Message), 2000),
            FromVersion = Truncate(Normalize(e.FromVersion), 128),
            ToVersion = Truncate(Normalize(e.ToVersion), 128),
        }));

        if (report.AcknowledgedCommands.Count > 0)
        {
            var ids = report.AcknowledgedCommands.Distinct().ToList();
            await db.DeviceCommands
                .Where(c => c.DeviceId == device.Id && c.AcknowledgedUtc == null && ids.Contains(c.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.AcknowledgedUtc, now), ct);
        }

        var config = await db.OrganizationConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.OrganizationId == device.OrganizationId, ct);
        var version = config?.ConfigVersion ?? ConfigVersioning.Initial;

        await db.SaveChangesAsync(ct);
        await PruneEventsAsync(device.Id, ct);
        await archive.ArchiveAsync(device.OrganizationId, device.Id, report.ReportedUtc, rawJson, ct);

        return ApiOutcome<ReportResponse>.Ok(new ReportResponse
        {
            Accepted = true,
            ConfigChanged = !string.Equals(version, report.ConfigVersionApplied, StringComparison.Ordinal),
            ConfigVersion = version,
        });
    }

    // ------------------------------------------------------------------------------------------------ release mirror

    public async Task<ApiOutcome<ReleaseManifest>> GetReleaseAsync(string? channel, CancellationToken ct)
    {
        var name = string.IsNullOrWhiteSpace(channel) ? "stable" : channel.Trim().ToLowerInvariant();
        var row = await db.ReleaseManifests.AsNoTracking().FirstOrDefaultAsync(r => r.Channel == name, ct);
        if (row is null) return ApiOutcome<ReleaseManifest>.NotFound($"No release manifest is published for channel '{name}'.");

        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(row.Json, CloudJson.Options);
        return manifest is null
            ? ApiOutcome<ReleaseManifest>.NotFound($"No release manifest is published for channel '{name}'.")
            : ApiOutcome<ReleaseManifest>.Ok(manifest);
    }

    // ------------------------------------------------------------------------------------------------ helpers

    internal static SettingsDocument ParseSettings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return ConfigVersioning.EmptyDocument();
        try
        {
            return SettingsDocument.FromJson(json);
        }
        catch (Exception)
        {
            return ConfigVersioning.EmptyDocument();
        }
    }

    private Task TouchLastSeenAsync(Guid deviceId, DateTimeOffset now, CancellationToken ct) =>
        db.Devices.Where(d => d.Id == deviceId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.LastSeenUtc, now), ct);

    private async Task PruneEventsAsync(Guid deviceId, CancellationToken ct)
    {
        var keep = Math.Max(50, options.MaxEventsPerDevice);
        var count = await db.DeviceEvents.CountAsync(e => e.DeviceId == deviceId, ct);
        if (count <= keep) return;

        var cutoffId = await db.DeviceEvents
            .Where(e => e.DeviceId == deviceId)
            .OrderByDescending(e => e.Id)
            .Skip(keep)
            .Select(e => e.Id)
            .FirstOrDefaultAsync(ct);
        if (cutoffId > 0)
            await db.DeviceEvents.Where(e => e.DeviceId == deviceId && e.Id <= cutoffId).ExecuteDeleteAsync(ct);
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];
}
