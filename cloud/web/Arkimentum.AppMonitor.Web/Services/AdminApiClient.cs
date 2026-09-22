using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Arkimentum.AppMonitor.Api.Contracts;

namespace Arkimentum.AppMonitor.Web.Services;

/// <summary>The configuration document plus the ETag the PUT has to send back as <c>If-Match</c>.</summary>
public sealed record ConfigFetch(OrganizationConfigResponse Config, string? ETag)
{
    /// <summary>The version to publish over: the ETag when the server sent one, the body's version otherwise.</summary>
    public string? BaseVersion => ETag ?? Config.ConfigVersion;
}

/// <summary>
/// Every admin endpoint of the cloud API, typed. The <see cref="HttpClient"/> handed in already carries the Entra
/// access token (an <c>AuthorizationMessageHandler</c> scoped to the API's base URL attaches it), so nothing here
/// deals with tokens.
///
/// <para>
/// Every failure becomes an <see cref="ApiException"/> carrying the status and the server's <c>code</c>, so callers
/// can tell 401 (sign in again) from 403 (no access to this organization) from 409 (somebody else published first)
/// without parsing messages. Serialization always goes through <see cref="CloudJson.Options"/> - camelCase, enums as
/// camelCase strings, nulls omitted - which is the wire format the server and the agent agree on.
/// </para>
/// </summary>
public sealed class AdminApiClient
{
    private readonly HttpClient _http;

    public AdminApiClient(HttpClient http) => _http = http;

    // ---------------------------------------------------------------- identity and organizations

    public Task<AdminMeResponse> GetMeAsync(CancellationToken ct = default) =>
        SendAsync<AdminMeResponse>(HttpMethod.Get, CloudRoutes.AdminMe, ct: ct);

    public Task<List<OrganizationSummary>> GetOrganizationsAsync(CancellationToken ct = default) =>
        SendAsync<List<OrganizationSummary>>(HttpMethod.Get, CloudRoutes.AdminOrganizations, ct: ct);

    /// <summary>
    /// Creates an organization. Global administrators only - the console only offers it when
    /// <see cref="AdminMeResponse.IsGlobalAdmin"/> is set, and the server enforces it again. The response carries the
    /// enrollment key, which is the only time it is ever returned.
    /// </summary>
    public Task<CreateOrganizationResponse> CreateOrganizationAsync(string name, string? entraTenantId, CancellationToken ct = default) =>
        SendAsync<CreateOrganizationResponse>(HttpMethod.Post, CloudRoutes.AdminOrganizations,
            new CreateOrganizationRequest { Name = name.Trim(), EntraTenantId = Blank(entraTenantId) }, ct: ct);

    // ---------------------------------------------------------------- devices

    public Task<PagedResult<DeviceSummary>> GetDevicesAsync(Guid organizationId, string? search, int page, int pageSize, CancellationToken ct = default) =>
        SendAsync<PagedResult<DeviceSummary>>(HttpMethod.Get,
            Org(organizationId, $"/devices?search={Uri.EscapeDataString(search ?? string.Empty)}&page={page}&pageSize={pageSize}"), ct: ct);

    public Task<DeviceDetail> GetDeviceAsync(Guid organizationId, Guid deviceId, CancellationToken ct = default) =>
        SendAsync<DeviceDetail>(HttpMethod.Get, Org(organizationId, $"/devices/{deviceId}"), ct: ct);

    /// <summary>The endpoint answers 204, so there is nothing to return; a failure throws.</summary>
    public Task DeleteDeviceAsync(Guid organizationId, Guid deviceId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, Org(organizationId, $"/devices/{deviceId}"), ct: ct);

    /// <summary>Queues a command; the device picks it up on its next configuration poll.</summary>
    public Task<DeviceCommand> SendCommandAsync(Guid organizationId, Guid deviceId, DeviceCommandKind kind, string? argument = null, CancellationToken ct = default) =>
        SendAsync<DeviceCommand>(HttpMethod.Post, Org(organizationId, $"/devices/{deviceId}/commands"),
            new DeviceCommandRequest { Kind = kind, Argument = argument }, ct: ct);

    // ---------------------------------------------------------------- inventory

    public Task<List<OrganizationInventoryItem>> GetInventoryAsync(Guid organizationId, string? search, bool onlyUnmonitored, CancellationToken ct = default) =>
        SendAsync<List<OrganizationInventoryItem>>(HttpMethod.Get,
            Org(organizationId, $"/inventory?search={Uri.EscapeDataString(search ?? string.Empty)}&onlyUnmonitored={(onlyUnmonitored ? "true" : "false")}"), ct: ct);

    // ---------------------------------------------------------------- configuration

    /// <summary>The current configuration and its ETag - the version a later PUT must send as <c>If-Match</c>.</summary>
    public async Task<ConfigFetch> GetConfigAsync(Guid organizationId, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Relative(Org(organizationId, "/config")));
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var config = await ReadAsync<OrganizationConfigResponse>(response, ct).ConfigureAwait(false);
        config.Settings.Normalize();
        return new ConfigFetch(config, Unquote(response.Headers.ETag?.Tag));
    }

    /// <summary>
    /// Replaces the configuration. The version the editor started from travels both as <c>If-Match</c> and as
    /// <c>baseConfigVersion</c>; the server prefers the header. A 409 means somebody else published in the meantime.
    /// </summary>
    public async Task<ConfigFetch> PutConfigAsync(Guid organizationId, SettingsDocument settings, string? baseConfigVersion,
        string? comment, CancellationToken ct = default)
    {
        var body = new OrganizationConfigUpdateRequest
        {
            BaseConfigVersion = baseConfigVersion,
            Settings = settings,
            Comment = Blank(comment),
        };
        using var request = new HttpRequestMessage(HttpMethod.Put, Relative(Org(organizationId, "/config")))
        {
            Content = JsonContent.Create(body, options: CloudJson.Options),
        };
        if (!string.IsNullOrEmpty(baseConfigVersion))
            request.Headers.IfMatch.Add(new EntityTagHeaderValue(Quote(baseConfigVersion)));

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var config = await ReadAsync<OrganizationConfigResponse>(response, ct).ConfigureAwait(false);
        config.Settings.Normalize();
        return new ConfigFetch(config, Unquote(response.Headers.ETag?.Tag));
    }

    public Task<PagedResult<ConfigHistoryEntry>> GetConfigHistoryAsync(Guid organizationId, int page, int pageSize, CancellationToken ct = default) =>
        SendAsync<PagedResult<ConfigHistoryEntry>>(HttpMethod.Get,
            Org(organizationId, $"/config/history?page={page}&pageSize={pageSize}"), ct: ct);

    /// <summary>
    /// One historical revision. It deliberately carries no ETag: restoring is a normal PUT of these settings against
    /// the <em>current</em> version, which appends a revision instead of rewinding the history.
    /// </summary>
    public async Task<OrganizationConfigResponse> GetConfigRevisionAsync(Guid organizationId, long historyId, CancellationToken ct = default)
    {
        var revision = await SendAsync<OrganizationConfigResponse>(HttpMethod.Get,
            Org(organizationId, $"/config/history/{historyId}"), ct: ct).ConfigureAwait(false);
        revision.Settings.Normalize();
        return revision;
    }

    // ---------------------------------------------------------------- events and enrollment

    public Task<PagedResult<OrganizationEvent>> GetEventsAsync(Guid organizationId, DateTimeOffset? since, int page, int pageSize, CancellationToken ct = default) =>
        SendAsync<PagedResult<OrganizationEvent>>(HttpMethod.Get, Org(organizationId,
            $"/events?{(since is { } s ? "since=" + Uri.EscapeDataString(s.ToString("O")) + "&" : "")}page={page}&pageSize={pageSize}"), ct: ct);

    public Task<EnrollmentInfoResponse> GetEnrollmentAsync(Guid organizationId, CancellationToken ct = default) =>
        SendAsync<EnrollmentInfoResponse>(HttpMethod.Get, Org(organizationId, "/enrollment"), ct: ct);

    /// <summary>Issues a new key; the old one stops working immediately. The key is returned here and never again.</summary>
    public Task<EnrollmentInfoResponse> RotateEnrollmentKeyAsync(Guid organizationId, CancellationToken ct = default) =>
        SendAsync<EnrollmentInfoResponse>(HttpMethod.Post, Org(organizationId, "/enrollment/rotate"), ct: ct);

    // ---------------------------------------------------------------- plumbing

    private static string Org(Guid organizationId, string suffix) => $"{CloudRoutes.AdminOrganizations}/{organizationId}{suffix}";

    /// <summary>The routes are absolute paths; the HttpClient's BaseAddress carries the host.</summary>
    private static string Relative(string route) => route.TrimStart('/');

    private static string Quote(string etag) => etag.StartsWith('"') ? etag : $"\"{etag}\"";

    private static string? Unquote(string? etag) => string.IsNullOrEmpty(etag) ? null : etag.Trim('"');

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<T> SendAsync<T>(HttpMethod method, string route, object? body = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(method, Relative(route));
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: CloudJson.Options);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        return await ReadAsync<T>(response, ct).ConfigureAwait(false);
    }

    private async Task SendAsync(HttpMethod method, string route, object? body = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(method, Relative(route));
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: CloudJson.Options);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw await ErrorAsync(response, ct).ConfigureAwait(false);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode) throw await ErrorAsync(response, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent) return default!;
        var value = await response.Content.ReadFromJsonAsync<T>(CloudJson.Options, ct).ConfigureAwait(false);
        return value ?? throw new ApiException(response.StatusCode, "empty_response", "The server returned an empty response.");
    }

    private static async Task<ApiException> ErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string? code = null, message = null, traceId = null;
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ApiError>(CloudJson.Options, ct).ConfigureAwait(false);
            code = error?.Code;
            message = error?.Message;
            traceId = error?.TraceId;
        }
        catch (JsonException) { }
        catch (NotSupportedException) { }

        if (string.IsNullOrWhiteSpace(message))
        {
            try
            {
                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                message = text.Length > 300 ? text[..300] : text;
            }
            catch { /* the status alone will have to do */ }
        }

        if (string.IsNullOrWhiteSpace(message))
            message = $"The server returned {(int)response.StatusCode} {response.ReasonPhrase}.";

        return new ApiException(response.StatusCode, code, message!, traceId);
    }
}
