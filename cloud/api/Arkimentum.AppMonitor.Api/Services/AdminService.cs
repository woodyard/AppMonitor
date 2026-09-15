using System.Text.Json;
using Arkimentum.AppMonitor.Api.Contracts;
using Arkimentum.AppMonitor.Api.Data;
using Arkimentum.AppMonitor.Api.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Api.Services;

/// <summary>
/// The administrator half of the API. Every method takes the authenticated <see cref="AdminPrincipal"/> and resolves
/// the organization through <see cref="ResolveAsync"/>, which is the single tenant-isolation gate: a caller either
/// holds AppMonitor.GlobalAdmin in the operator tenant, or manages exactly the organization mapped to its own tid.
/// </summary>
public sealed class AdminService(
    AppMonitorDbContext db,
    ServerOptions options,
    ILogger<AdminService> logger,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private const int MaxPageSize = 200;

    public bool IsGlobalAdmin(AdminPrincipal principal) =>
        principal.HasRole(AdminPrincipal.RoleGlobalAdmin) &&
        !string.IsNullOrWhiteSpace(options.OperatorTenantId) &&
        string.Equals(principal.TenantId, options.OperatorTenantId, StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------------------------------------ me / organizations

    public async Task<ApiOutcome<AdminMeResponse>> MeAsync(AdminPrincipal principal, CancellationToken ct)
    {
        var organizations = await LoadSummariesAsync(principal, ct);
        return ApiOutcome<AdminMeResponse>.Ok(new AdminMeResponse
        {
            UserPrincipalName = principal.UserPrincipalName,
            DisplayName = principal.DisplayName,
            TenantId = principal.TenantId,
            IsGlobalAdmin = IsGlobalAdmin(principal),
            Organizations = organizations,
        });
    }

    public async Task<ApiOutcome<List<OrganizationSummary>>> ListOrganizationsAsync(AdminPrincipal principal, CancellationToken ct) =>
        ApiOutcome<List<OrganizationSummary>>.Ok(await LoadSummariesAsync(principal, ct));

    public async Task<ApiOutcome<CreateOrganizationResponse>> CreateOrganizationAsync(AdminPrincipal principal, CreateOrganizationRequest? request, CancellationToken ct)
    {
        if (!IsGlobalAdmin(principal))
            return ApiOutcome<CreateOrganizationResponse>.Forbidden("Only Arkimentum staff may create organizations.");
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
            return ApiOutcome<CreateOrganizationResponse>.BadRequest("name is required.");

        var tenantId = string.IsNullOrWhiteSpace(request.EntraTenantId) ? null : request.EntraTenantId.Trim();
        if (tenantId is not null && !Guid.TryParse(tenantId, out _))
            return ApiOutcome<CreateOrganizationResponse>.Invalid("entraTenantId must be a GUID.");
        if (tenantId is not null && await db.Organizations.AnyAsync(o => o.EntraTenantId == tenantId, ct))
            return ApiOutcome<CreateOrganizationResponse>.Conflict($"Another organization is already mapped to tenant {tenantId}.");

        var now = _time.GetUtcNow();
        var key = Secrets.NewEnrollmentKey();
        var organization = new Organization
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            EntraTenantId = tenantId,
            EnrollmentKeyHash = Secrets.Hash(key),
            EnrollmentKeyRotatedUtc = now,
            CreatedUtc = now,
            IsActive = true,
        };
        db.Organizations.Add(organization);

        var settings = ConfigVersioning.EmptyDocument();
        var json = settings.ToCompactJson();
        db.OrganizationConfigs.Add(new OrganizationConfig
        {
            OrganizationId = organization.Id,
            ConfigVersion = ConfigVersioning.Next(null, json),
            SettingsJson = json,
            UpdatedUtc = now,
            UpdatedBy = principal.UserPrincipalName,
        });

        Audit(organization.Id, principal, "organization.created", "Organization", organization.Id.ToString(), organization.Name);
        await db.SaveChangesAsync(ct);

        var summary = await BuildSummaryAsync(organization.Id, ct);
        return new ApiOutcome<CreateOrganizationResponse>(201, new CreateOrganizationResponse
        {
            Organization = summary!,
            Enrollment = new EnrollmentInfoResponse
            {
                OrganizationId = organization.Id,
                EnrollmentKey = key,
                KeyRotatedUtc = now,
                ServerUrl = options.PublicServerUrl,
            },
        });
    }

    // ------------------------------------------------------------------------------------------------ devices

    public async Task<ApiOutcome<PagedResult<DeviceSummary>>> ListDevicesAsync(
        AdminPrincipal principal, Guid organizationId, string? search, int page, int pageSize, CancellationToken ct)
    {
        var access = await ResolveAsync<PagedResult<DeviceSummary>>(principal, organizationId, ct);
        if (access.Error is { } error) return error;

        (page, pageSize) = NormalizePaging(page, pageSize);

        var query = db.Devices.AsNoTracking().Where(d => d.OrganizationId == organizationId && !d.IsDeleted);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var needle = search.Trim();
            query = query.Where(d =>
                EF.Functions.Like(d.DeviceName, $"%{needle}%") ||
                (d.LastLogonUser != null && EF.Functions.Like(d.LastLogonUser, $"%{needle}%")) ||
                (d.OsVersion != null && EF.Functions.Like(d.OsVersion, $"%{needle}%")) ||
                (d.AgentVersion != null && EF.Functions.Like(d.AgentVersion, $"%{needle}%")));
        }

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderBy(d => d.DeviceName).ThenBy(d => d.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct);

        return ApiOutcome<PagedResult<DeviceSummary>>.Ok(new PagedResult<DeviceSummary>
        {
            Items = [.. rows.Select(ToSummary)],
            Total = total,
            Page = page,
            PageSize = pageSize,
        });
    }

    public async Task<ApiOutcome<DeviceDetail>> GetDeviceAsync(AdminPrincipal principal, Guid organizationId, Guid deviceId, CancellationToken ct)
    {
        var access = await ResolveAsync<DeviceDetail>(principal, organizationId, ct);
        if (access.Error is { } error) return error;

        var device = await db.Devices.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == deviceId && d.OrganizationId == organizationId && !d.IsDeleted, ct);
        if (device is null) return ApiOutcome<DeviceDetail>.NotFound("No such device.");

        var apps = await db.DeviceApps.AsNoTracking().Where(a => a.DeviceId == deviceId)
            .OrderBy(a => a.DisplayName).ToListAsync(ct);
        var updates = await db.DeviceUpdates.AsNoTracking().Where(u => u.DeviceId == deviceId)
            .OrderBy(u => u.AppId).ToListAsync(ct);
        var events = await db.DeviceEvents.AsNoTracking().Where(e => e.DeviceId == deviceId)
            .OrderByDescending(e => e.OccurredUtc).ThenByDescending(e => e.Id).Take(100).ToListAsync(ct);
        var commands = await db.DeviceCommands.AsNoTracking().Where(c => c.DeviceId == deviceId && c.AcknowledgedUtc == null)
            .OrderBy(c => c.IssuedUtc).ToListAsync(ct);

        return ApiOutcome<DeviceDetail>.Ok(new DeviceDetail
        {
            Summary = ToSummary(device),
            MachineGuid = device.MachineGuid,
            EntraDeviceId = device.EntraDeviceId,
            Prerequisites = ParsePrerequisites(device.PrerequisitesJson),
            InstalledApps = [.. apps.Select(a => new ReportedApp
            {
                DisplayName = a.DisplayName,
                Version = a.Version,
                Publisher = a.Publisher,
                WingetId = a.WingetId,
                AvailableVersion = a.AvailableVersion,
                Context = a.Context,
                CatalogAppId = a.CatalogAppId,
                MonitoredAppId = a.MonitoredAppId,
            })],
            Updates = [.. updates.Select(u => new ReportedUpdate
            {
                AppId = u.AppId,
                DisplayName = u.DisplayName,
                InstalledVersion = u.InstalledVersion,
                AvailableVersion = u.AvailableVersion,
                State = u.State,
                Context = u.Context,
                Mandatory = u.Mandatory,
                DeadlineUtc = u.DeadlineUtc,
                DeferredUntilUtc = u.DeferredUntilUtc,
                DeferralCount = u.DeferralCount,
                FirstDetectedUtc = u.FirstDetectedUtc,
                InstalledAtUtc = u.InstalledAtUtc,
                LastError = u.LastError,
            })],
            RecentEvents = [.. events.Select(e => new ReportedEvent
            {
                OccurredUtc = e.OccurredUtc,
                Kind = e.Kind,
                AppId = e.AppId,
                Message = e.Message,
                FromVersion = e.FromVersion,
                ToVersion = e.ToVersion,
            })],
            PendingCommands = [.. commands.Select(ToCommand)],
        });
    }

    public async Task<ApiOutcome<object>> DeleteDeviceAsync(AdminPrincipal principal, Guid organizationId, Guid deviceId, CancellationToken ct)
    {
        var access = await ResolveAsync<object>(principal, organizationId, ct);
        if (access.Error is { } error) return error;

        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId && d.OrganizationId == organizationId, ct);
        if (device is null || device.IsDeleted) return ApiOutcome<object>.NotFound("No such device.");

        // Soft-delete the row (so a re-enrolling machine re-attaches to its own history) but drop the snapshot
        // tables immediately - they are the bulk of the data and are rebuilt by the next report.
        device.IsDeleted = true;
        device.DeviceKeyHash = new byte[32];
        await db.DeviceApps.Where(a => a.DeviceId == deviceId).ExecuteDeleteAsync(ct);
        await db.DeviceUpdates.Where(u => u.DeviceId == deviceId).ExecuteDeleteAsync(ct);
        await db.DeviceCommands.Where(c => c.DeviceId == deviceId && c.AcknowledgedUtc == null).ExecuteDeleteAsync(ct);

        Audit(organizationId, principal, "device.deleted", "Device", deviceId.ToString(), device.DeviceName);
        await db.SaveChangesAsync(ct);
        return ApiOutcome<object>.NoContent();
    }

    public async Task<ApiOutcome<DeviceCommand>> CreateCommandAsync(
        AdminPrincipal principal, Guid organizationId, Guid deviceId, DeviceCommandRequest? request, CancellationToken ct)
    {
        var access = await ResolveAsync<DeviceCommand>(principal, organizationId, ct);
        if (access.Error is { } error) return error;
        if (request is null) return ApiOutcome<DeviceCommand>.BadRequest("A JSON body is required.");
        if (!Enum.IsDefined(request.Kind)) return ApiOutcome<DeviceCommand>.Invalid("kind is not a known command.");

        var exists = await db.Devices.AnyAsync(d => d.Id == deviceId && d.OrganizationId == organizationId && !d.IsDeleted, ct);
        if (!exists) return ApiOutcome<DeviceCommand>.NotFound("No such device.");

        var row = new DeviceCommandRow
        {
            Id = Guid.NewGuid(),
            DeviceId = deviceId,
            OrganizationId = organizationId,
            Kind = request.Kind,
            Argument = Truncate(request.Argument, 1000),
            IssuedUtc = _time.GetUtcNow(),
            IssuedBy = principal.UserPrincipalName,
        };
        db.DeviceCommands.Add(row);
        Audit(organizationId, principal, "device.command", "Device", deviceId.ToString(), request.Kind.ToString());
        await db.SaveChangesAsync(ct);

        return ApiOutcome<DeviceCommand>.Created(ToCommand(row));
    }

    // ------------------------------------------------------------------------------------------------ inventory

    public async Task<ApiOutcome<List<OrganizationInventoryItem>>> InventoryAsync(
        AdminPrincipal principal, Guid organizationId, string? search, bool onlyUnmonitored, CancellationToken ct)
    {
        var access = await ResolveAsync<List<OrganizationInventoryItem>>(principal, organizationId, ct);
        if (access.Error is { } error) return error;

        var rows = await db.DeviceApps.AsNoTracking()
            .Where(a => a.OrganizationId == organizationId && !a.Device!.IsDeleted)
            .Select(a => new InventoryRow(
                a.DeviceId, a.DisplayName, a.Version, a.Publisher, a.WingetId, a.AvailableVersion,
                a.Context, a.CatalogAppId, a.MonitoredAppId, a.Device!.LastSeenUtc))
            .ToListAsync(ct);

        var config = await db.OrganizationConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.OrganizationId == organizationId, ct);
        var settings = DeviceService.ParseSettings(config?.SettingsJson);

        return ApiOutcome<List<OrganizationInventoryItem>>.Ok(InventoryAggregator.Build(rows, settings, search, onlyUnmonitored));
    }

    // ------------------------------------------------------------------------------------------------ configuration

    public async Task<ApiOutcome<OrganizationConfigResponse>> GetConfigAsync(AdminPrincipal principal, Guid organizationId, CancellationToken ct)
    {
        var access = await ResolveAsync<OrganizationConfigResponse>(principal, organizationId, ct);
        if (access.Error is { } error) return error;

        var config = await db.OrganizationConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.OrganizationId == organizationId, ct);
        var version = config?.ConfigVersion ?? ConfigVersioning.Initial;

        return ApiOutcome<OrganizationConfigResponse>.Ok(new OrganizationConfigResponse
        {
            OrganizationId = organizationId,
            ConfigVersion = version,
            UpdatedUtc = config?.UpdatedUtc ?? default,
            UpdatedBy = config?.UpdatedBy,
            Settings = DeviceService.ParseSettings(config?.SettingsJson),
        }, version);
    }

    public async Task<ApiOutcome<OrganizationConfigResponse>> PutConfigAsync(
        AdminPrincipal principal, Guid organizationId, OrganizationConfigUpdateRequest? request, string? ifMatch, CancellationToken ct)
    {
        var access = await ResolveAsync<OrganizationConfigResponse>(principal, organizationId, ct);
        if (access.Error is { } error) return error;
        if (request?.Settings is null) return ApiOutcome<OrganizationConfigResponse>.BadRequest("settings is required.");

        var settings = request.Settings;
        settings.Normalize();
        if (!string.Equals(settings.Schema, SettingsDocument.CurrentSchema, StringComparison.OrdinalIgnoreCase))
            return ApiOutcome<OrganizationConfigResponse>.Invalid(
                $"Unsupported settings document schema '{settings.Schema}' (expected '{SettingsDocument.CurrentSchema}').");

        var problems = settings.Validate();
        if (problems.Count > 0)
            return ApiOutcome<OrganizationConfigResponse>.Invalid(string.Join(" ", problems.Take(20)));

        var config = await db.OrganizationConfigs.FirstOrDefaultAsync(c => c.OrganizationId == organizationId, ct);
        var currentVersion = config?.ConfigVersion ?? ConfigVersioning.Initial;

        // Optimistic concurrency: If-Match wins over the body's baseConfigVersion; both are optional.
        var expected = ConfigVersioning.Unquote(ifMatch) ?? ConfigVersioning.Unquote(request.BaseConfigVersion);
        if (expected is not null && expected != "*" && !string.Equals(expected, currentVersion, StringComparison.Ordinal))
            return ApiOutcome<OrganizationConfigResponse>.Conflict(
                $"The configuration has changed since {expected}; it is now {currentVersion}. Reload and re-apply your edit.");

        var now = _time.GetUtcNow();
        settings.ExportedUtc = now;
        settings.ExportedBy = principal.UserPrincipalName;
        var json = settings.ToCompactJson();
        var version = ConfigVersioning.Next(currentVersion, json);

        if (config is null)
        {
            config = new OrganizationConfig { OrganizationId = organizationId };
            db.OrganizationConfigs.Add(config);
        }
        config.ConfigVersion = version;
        config.SettingsJson = json;
        config.UpdatedUtc = now;
        config.UpdatedBy = principal.UserPrincipalName;

        db.OrganizationConfigHistory.Add(new OrganizationConfigHistory
        {
            OrganizationId = organizationId,
            ConfigVersion = version,
            SettingsJson = json,
            UpdatedUtc = now,
            UpdatedBy = principal.UserPrincipalName,
            Comment = Truncate(request.Comment, 1000),
        });

        Audit(organizationId, principal, "config.updated", "OrganizationConfig", organizationId.ToString(), version);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Organization {OrganizationId} configuration is now {ConfigVersion}", organizationId, version);

        return ApiOutcome<OrganizationConfigResponse>.Ok(new OrganizationConfigResponse
        {
            OrganizationId = organizationId,
            ConfigVersion = version,
            UpdatedUtc = now,
            UpdatedBy = principal.UserPrincipalName,
            Settings = settings,
        }, version);
    }

    public async Task<ApiOutcome<PagedResult<ConfigHistoryEntry>>> ConfigHistoryAsync(
        AdminPrincipal principal, Guid organizationId, int page, int pageSize, CancellationToken ct)
    {
        var access = await ResolveAsync<PagedResult<ConfigHistoryEntry>>(principal, organizationId, ct);
        if (access.Error is { } error) return error;

        (page, pageSize) = NormalizePaging(page, pageSize);
        var query = db.OrganizationConfigHistory.AsNoTracking().Where(h => h.OrganizationId == organizationId);
        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(h => h.UpdatedUtc).ThenByDescending(h => h.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(h => new ConfigHistoryEntry
            {
                Id = h.Id,
                ConfigVersion = h.ConfigVersion,
                UpdatedUtc = h.UpdatedUtc,
                UpdatedBy = h.UpdatedBy,
                Comment = h.Comment,
            })
            .ToListAsync(ct);

        return ApiOutcome<PagedResult<ConfigHistoryEntry>>.Ok(new PagedResult<ConfigHistoryEntry>
        {
            Items = rows,
            Total = total,
            Page = page,
            PageSize = pageSize,
        });
    }

    /// <summary>
    /// One historical revision, for "show" and "restore" in the console. Deliberately returns no ETag: the caller
    /// must not send a historical version as If-Match when restoring - a restore is a normal PUT of these settings
    /// against the *current* version.
    /// </summary>
    public async Task<ApiOutcome<OrganizationConfigResponse>> GetConfigRevisionAsync(
        AdminPrincipal principal, Guid organizationId, long historyId, CancellationToken ct)
    {
        var access = await ResolveAsync<OrganizationConfigResponse>(principal, organizationId, ct);
        if (access.Error is { } error) return error;

        // The organization id is part of the predicate, so a history id belonging to another organization is a
        // 404 here rather than a cross-tenant read.
        var revision = await db.OrganizationConfigHistory.AsNoTracking()
            .FirstOrDefaultAsync(h => h.Id == historyId && h.OrganizationId == organizationId, ct);
        if (revision is null) return ApiOutcome<OrganizationConfigResponse>.NotFound("No such configuration revision.");

        return ApiOutcome<OrganizationConfigResponse>.Ok(new OrganizationConfigResponse
        {
            OrganizationId = organizationId,
            ConfigVersion = revision.ConfigVersion,
            UpdatedUtc = revision.UpdatedUtc,
            UpdatedBy = revision.UpdatedBy,
            Settings = DeviceService.ParseSettings(revision.SettingsJson),
        });
    }

    // ------------------------------------------------------------------------------------------------ enrollment key

    public async Task<ApiOutcome<EnrollmentInfoResponse>> GetEnrollmentAsync(AdminPrincipal principal, Guid organizationId, CancellationToken ct)
    {
        var access = await ResolveAsync<EnrollmentInfoResponse>(principal, organizationId, ct);
        if (access.Error is { } error) return error;

        return ApiOutcome<EnrollmentInfoResponse>.Ok(new EnrollmentInfoResponse
        {
            OrganizationId = organizationId,
            EnrollmentKey = null,                  // never returned again after creation or rotation
            KeyRotatedUtc = access.Organization!.EnrollmentKeyRotatedUtc,
            ServerUrl = options.PublicServerUrl,
        });
    }

    public async Task<ApiOutcome<EnrollmentInfoResponse>> RotateEnrollmentKeyAsync(AdminPrincipal principal, Guid organizationId, CancellationToken ct)
    {
        var access = await ResolveAsync<EnrollmentInfoResponse>(principal, organizationId, ct);
        if (access.Error is { } error) return error;

        var organization = await db.Organizations.FirstAsync(o => o.Id == organizationId, ct);
        var key = Secrets.NewEnrollmentKey();
        var now = _time.GetUtcNow();
        organization.EnrollmentKeyHash = Secrets.Hash(key);
        organization.EnrollmentKeyRotatedUtc = now;

        Audit(organizationId, principal, "enrollment.rotated", "Organization", organizationId.ToString(), null);
        await db.SaveChangesAsync(ct);

        return ApiOutcome<EnrollmentInfoResponse>.Ok(new EnrollmentInfoResponse
        {
            OrganizationId = organizationId,
            EnrollmentKey = key,
            KeyRotatedUtc = now,
            ServerUrl = options.PublicServerUrl,
        });
    }

    // ------------------------------------------------------------------------------------------------ release mirror

    public async Task<ApiOutcome<ReleaseManifest>> GetReleaseAsync(AdminPrincipal principal, Guid organizationId, string? channel, CancellationToken ct)
    {
        var access = await ResolveAsync<ReleaseManifest>(principal, organizationId, ct);
        if (access.Error is { } error) return error;

        var name = NormalizeChannel(channel);
        var row = await db.ReleaseManifests.AsNoTracking().FirstOrDefaultAsync(r => r.Channel == name, ct);
        if (row is null) return ApiOutcome<ReleaseManifest>.NotFound($"No release manifest is published for channel '{name}'.");

        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(row.Json, CloudJson.Options);
        return manifest is null
            ? ApiOutcome<ReleaseManifest>.NotFound($"No release manifest is published for channel '{name}'.")
            : ApiOutcome<ReleaseManifest>.Ok(manifest);
    }

    public async Task<ApiOutcome<ReleaseManifest>> PutReleaseAsync(
        AdminPrincipal principal, Guid organizationId, string? channel, ReleaseManifest? manifest, CancellationToken ct)
    {
        var access = await ResolveAsync<ReleaseManifest>(principal, organizationId, ct);
        if (access.Error is { } error) return error;
        if (!IsGlobalAdmin(principal))
            return ApiOutcome<ReleaseManifest>.Forbidden("Only Arkimentum staff may publish a release manifest.");
        if (manifest is null) return ApiOutcome<ReleaseManifest>.BadRequest("A JSON body is required.");

        var problems = ValidateManifest(manifest).ToList();
        if (problems.Count > 0) return ApiOutcome<ReleaseManifest>.Invalid(string.Join(" ", problems));

        var name = NormalizeChannel(channel ?? manifest.Channel);
        manifest.Channel = name;
        var now = _time.GetUtcNow();

        var row = await db.ReleaseManifests.FirstOrDefaultAsync(r => r.Channel == name, ct);
        if (row is null)
        {
            row = new ReleaseManifestRow { Channel = name };
            db.ReleaseManifests.Add(row);
        }
        row.Json = JsonSerializer.Serialize(manifest, CloudJson.Options);
        row.UpdatedUtc = now;
        row.UpdatedBy = principal.UserPrincipalName;

        Audit(null, principal, "release.published", "ReleaseManifest", name, manifest.Version);
        await db.SaveChangesAsync(ct);

        return ApiOutcome<ReleaseManifest>.Ok(manifest);
    }

    // ------------------------------------------------------------------------------------------------ events

    public async Task<ApiOutcome<PagedResult<OrganizationEvent>>> EventsAsync(
        AdminPrincipal principal, Guid organizationId, DateTimeOffset? since, int page, int pageSize, CancellationToken ct)
    {
        var access = await ResolveAsync<PagedResult<OrganizationEvent>>(principal, organizationId, ct);
        if (access.Error is { } error) return error;

        (page, pageSize) = NormalizePaging(page, pageSize);
        var query = db.DeviceEvents.AsNoTracking().Where(e => e.OrganizationId == organizationId);
        if (since is { } from) query = query.Where(e => e.OccurredUtc >= from);

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(e => e.OccurredUtc).ThenByDescending(e => e.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(e => new OrganizationEvent
            {
                DeviceId = e.DeviceId,
                DeviceName = e.Device!.DeviceName,
                OccurredUtc = e.OccurredUtc,
                Kind = e.Kind,
                AppId = e.AppId,
                Message = e.Message,
                FromVersion = e.FromVersion,
                ToVersion = e.ToVersion,
            })
            .ToListAsync(ct);

        return ApiOutcome<PagedResult<OrganizationEvent>>.Ok(new PagedResult<OrganizationEvent>
        {
            Items = rows,
            Total = total,
            Page = page,
            PageSize = pageSize,
        });
    }

    // ------------------------------------------------------------------------------------------------ authorisation

    public sealed record Access<T>(Organization? Organization, ApiOutcome<T>? Error);

    /// <summary>
    /// The single tenant-isolation gate. A global administrator (AppMonitor.GlobalAdmin, and only when the token comes
    /// from the operator tenant) reaches every organization; anyone else reaches exactly the organization whose
    /// EntraTenantId equals the token's tid, and only with the AppMonitor.Admin role.
    /// </summary>
    public async Task<Access<T>> ResolveAsync<T>(AdminPrincipal principal, Guid organizationId, CancellationToken ct)
    {
        if (organizationId == Guid.Empty)
            return new Access<T>(null, ApiOutcome<T>.BadRequest("An organization id is required."));

        var organization = await db.Organizations.AsNoTracking().FirstOrDefaultAsync(o => o.Id == organizationId, ct);
        if (organization is null)
        {
            // Do not distinguish "no such organization" from "not yours" for a non-global caller.
            return new Access<T>(null, IsGlobalAdmin(principal)
                ? ApiOutcome<T>.NotFound("No such organization.")
                : ApiOutcome<T>.Forbidden());
        }

        if (IsGlobalAdmin(principal)) return new Access<T>(organization, null);

        if (!principal.HasRole(AdminPrincipal.RoleAdmin))
            return new Access<T>(null, ApiOutcome<T>.Forbidden($"The '{AdminPrincipal.RoleAdmin}' app role is required."));

        if (string.IsNullOrWhiteSpace(organization.EntraTenantId) ||
            !string.Equals(organization.EntraTenantId, principal.TenantId, StringComparison.OrdinalIgnoreCase))
            return new Access<T>(null, ApiOutcome<T>.Forbidden());

        if (!organization.IsActive)
            return new Access<T>(null, ApiOutcome<T>.Forbidden("The organization is not active."));

        return new Access<T>(organization, null);
    }

    // ------------------------------------------------------------------------------------------------ helpers

    private async Task<List<OrganizationSummary>> LoadSummariesAsync(AdminPrincipal principal, CancellationToken ct)
    {
        IQueryable<Organization> query = db.Organizations.AsNoTracking();
        if (!IsGlobalAdmin(principal))
        {
            if (!principal.HasRole(AdminPrincipal.RoleAdmin)) return [];
            query = query.Where(o => o.EntraTenantId != null && o.EntraTenantId == principal.TenantId && o.IsActive);
        }

        var ids = await query.OrderBy(o => o.Name).Select(o => o.Id).ToListAsync(ct);
        var summaries = new List<OrganizationSummary>(ids.Count);
        foreach (var id in ids)
            if (await BuildSummaryAsync(id, ct) is { } summary)
                summaries.Add(summary);
        return summaries;
    }

    private async Task<OrganizationSummary?> BuildSummaryAsync(Guid organizationId, CancellationToken ct)
    {
        var organization = await db.Organizations.AsNoTracking().FirstOrDefaultAsync(o => o.Id == organizationId, ct);
        if (organization is null) return null;

        var stale = _time.GetUtcNow().AddDays(-7);
        var devices = db.Devices.AsNoTracking().Where(d => d.OrganizationId == organizationId && !d.IsDeleted);
        var config = await db.OrganizationConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.OrganizationId == organizationId, ct);

        return new OrganizationSummary
        {
            OrganizationId = organization.Id,
            Name = organization.Name,
            EntraTenantId = organization.EntraTenantId,
            DeviceCount = await devices.CountAsync(ct),
            DevicesWithPendingUpdates = await devices.CountAsync(d => d.PendingUpdateCount > 0, ct),
            DevicesNotSeenIn7Days = await devices.CountAsync(d => d.LastSeenUtc == null || d.LastSeenUtc < stale, ct),
            ConfigVersion = config?.ConfigVersion,
            ConfigUpdatedUtc = config?.UpdatedUtc,
        };
    }

    private static DeviceSummary ToSummary(Device d) => new()
    {
        DeviceId = d.Id,
        DeviceName = d.DeviceName,
        LastLogonUser = d.LastLogonUser,
        OsVersion = d.OsVersion,
        AgentVersion = d.AgentVersion,
        EnrolledUtc = d.EnrolledUtc,
        LastSeenUtc = d.LastSeenUtc,
        LastScanUtc = d.LastScanUtc,
        ConfigVersionApplied = d.ConfigVersionApplied,
        PendingUpdateCount = d.PendingUpdateCount,
        FailedUpdateCount = d.FailedUpdateCount,
        PrerequisitesHealthy = ParsePrerequisites(d.PrerequisitesJson)?.IsHealthy ?? true,
    };

    private static DeviceCommand ToCommand(DeviceCommandRow c) => new()
    {
        CommandId = c.Id,
        Kind = c.Kind,
        Argument = c.Argument,
        IssuedUtc = c.IssuedUtc,
        IssuedBy = c.IssuedBy,
    };

    private static PrerequisiteStatus? ParsePrerequisites(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<PrerequisiteStatus>(json, CloudJson.Options); }
        catch (JsonException) { return null; }
    }

    private static IEnumerable<string> ValidateManifest(ReleaseManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Version)) yield return "version is required.";
        if (string.IsNullOrWhiteSpace(manifest.PackageUrl) ||
            !Uri.TryCreate(manifest.PackageUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
            yield return "packageUrl must be an absolute https URL.";
        if (manifest.Sha256 is not { Length: 64 } || !manifest.Sha256.All(Uri.IsHexDigit))
            yield return "sha256 must be 64 hexadecimal characters.";
    }

    private static string NormalizeChannel(string? channel) =>
        string.IsNullOrWhiteSpace(channel) ? "stable" : channel.Trim().ToLowerInvariant();

    private static (int Page, int PageSize) NormalizePaging(int page, int pageSize) =>
        (page < 1 ? 1 : page, pageSize < 1 ? 50 : pageSize > MaxPageSize ? MaxPageSize : pageSize);

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max];

    private void Audit(Guid? organizationId, AdminPrincipal principal, string action, string targetType, string? targetId, string? details) =>
        db.AuditLog.Add(new AuditLogEntry
        {
            OrganizationId = organizationId,
            Actor = principal.UserPrincipalName,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            Utc = _time.GetUtcNow(),
            Details = Truncate(details, 4000),
        });
}
