using System.Net.Http;
using System.Net;
using System.Windows.Threading;
using Arkimentum.AppMonitor.Admin.Infrastructure;
using Arkimentum.AppMonitor.Cloud;
using Arkimentum.AppMonitor.Configuration;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Admin.Services;

/// <summary>
/// The console's single door to the AppMonitor cloud admin API: the server URL, the signed-in administrator, the
/// selected organization, and one typed method per endpoint.
///
/// <para>
/// Every call goes through <see cref="AdminAsync{T}"/>, which attaches a bearer token (refreshed silently when it
/// is about to expire) and logs the method, the absolute URL and the outcome at Information — so the console's log
/// answers "what did it ask the server, and what did the server say?" without a debugger.
/// </para>
/// </summary>
public sealed class CloudSession : IDisposable
{
    /// <summary>Refresh a token this long before it actually expires, so a slow call cannot outlive it.</summary>
    private static readonly TimeSpan TokenSkew = TimeSpan.FromMinutes(5);

    private readonly ILogger<CloudSession> _log;
    private readonly ICloudClientFactory _clients;
    private readonly IAuthenticator _authenticator;
    private readonly AdminPreferences _preferences;
    private readonly Dispatcher _dispatcher;

    private CloudClient? _client;
    private string? _clientUrl;
    private readonly List<CloudClient> _retired = [];
    private AuthToken? _token;

    public CloudSession(ILogger<CloudSession> log, ICloudClientFactory clients, IAuthenticator authenticator, AdminPreferences preferences, Dispatcher dispatcher)
    {
        _log = log;
        _clients = clients;
        _authenticator = authenticator;
        _preferences = preferences;
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// Raised whenever the connection state, the account or the selected organization changes. Always raised on
    /// the UI thread: the view models answer it by refilling bound collections, which WPF only allows there, and
    /// the async methods below complete on thread-pool threads.
    /// </summary>
    public event Action? Changed;

    private void RaiseChanged()
    {
        if (_dispatcher.CheckAccess()) Changed?.Invoke();
        else _dispatcher.BeginInvoke(() => Changed?.Invoke());
    }

    public string? ServerUrl { get; private set; }

    public AuthConfigResponse? AuthConfig { get; private set; }

    public AdminMeResponse? Me { get; private set; }

    public OrganizationSummary? Organization { get; private set; }

    public bool IsSignedIn => Me is not null && _token is not null;

    public string? SignedInAccount => Me?.UserPrincipalName ?? _authenticator.SignedInAccount;

    public string? SignedInDisplayName => Me?.DisplayName;

    public Guid? OrganizationId => Organization?.OrganizationId;

    public string OrganizationName => Organization?.Name ?? string.Empty;

    // ---------------------------------------------------------------- connect / sign in / sign out

    /// <summary>
    /// Points the session at a server and reads its unauthenticated sign-in configuration. Signs the current
    /// administrator out first when the URL changes, so a token for one deployment can never leak into another.
    /// </summary>
    public async Task<AuthConfigResponse> ConnectAsync(string serverUrl, CancellationToken ct)
    {
        var normalised = Normalise(serverUrl);
        if (!string.Equals(normalised, ServerUrl, StringComparison.OrdinalIgnoreCase)) ResetSession();

        ServerUrl = normalised;
        var client = Client();
        _log.LogInformation("Cloud GET {Url}", Absolute(CloudRoutes.AuthConfig));
        try
        {
            AuthConfig = await client.GetAuthConfigAsync(ct).ConfigureAwait(false);
            _log.LogInformation("Cloud GET {Url} -> 200; clientId={ClientId} authority={Authority} scope={Scope}",
                Absolute(CloudRoutes.AuthConfig), AuthConfig.ClientId, AuthConfig.Authority, AuthConfig.Scope);
            _preferences.Remember(normalised);
            RaiseChanged();
            return AuthConfig;
        }
        catch (CloudException ex)
        {
            _log.LogInformation("Cloud GET {Url} -> {Status} {Code}", Absolute(CloudRoutes.AuthConfig), (int?)ex.Status ?? 0, ex.Code);
            throw;
        }
    }

    /// <summary>
    /// Acquires a token (silently when the cache still has one) and reads <c>/admin/me</c>. When the administrator
    /// administers exactly one organization it is selected straight away, which is the common case.
    /// </summary>
    public async Task<AdminMeResponse> SignInAsync(bool allowInteractive, CancellationToken ct)
    {
        var config = AuthConfig ?? throw new InvalidOperationException("Connect to a server before signing in.");
        _token = await _authenticator.AcquireTokenAsync(config, allowInteractive, ct).ConfigureAwait(false);

        Me = await AdminAsync<AdminMeResponse>(HttpMethod.Get, CloudRoutes.AdminMe, ct: ct).ConfigureAwait(false);
        _log.LogInformation("Signed in as {User} ({Tenant}); {Count} organization(s).",
            Me.UserPrincipalName, Me.TenantId, Me.Organizations.Count);

        if (Me.Organizations.Count == 1) SelectOrganization(Me.Organizations[0]);
        else RaiseChanged();
        return Me;
    }

    public void SelectOrganization(OrganizationSummary? organization)
    {
        if (ReferenceEquals(Organization, organization)) return;
        Organization = organization;
        if (organization is not null)
            _log.LogInformation("Organization selected: {Name} ({Id}).", organization.Name, organization.OrganizationId);
        RaiseChanged();
    }

    public async Task SignOutAsync(CancellationToken ct)
    {
        _log.LogInformation("Signing out of {Url}.", ServerUrl);
        await _authenticator.SignOutAsync(AuthConfig, ct).ConfigureAwait(false);
        ResetSession();
        RaiseChanged();
    }

    private void ResetSession()
    {
        Me = null;
        Organization = null;
        _token = null;
    }

    // ---------------------------------------------------------------- the one call everything goes through

    public async Task<T> AdminAsync<T>(HttpMethod method, string relativePath, object? body = null,
        string? ifMatch = null, CancellationToken ct = default)
    {
        var client = Client();
        var token = await TokenAsync(ct).ConfigureAwait(false);
        var url = Absolute(relativePath);
        _log.LogInformation("Cloud {Method} {Url}", method.Method, url);
        try
        {
            var result = await client.AdminAsync<T>(method, relativePath, token, body, ct, ifMatch).ConfigureAwait(false);
            _log.LogInformation("Cloud {Method} {Url} -> 200", method.Method, url);
            return result;
        }
        catch (CloudException ex)
        {
            _log.LogInformation("Cloud {Method} {Url} -> {Status} {Code}: {Message}",
                method.Method, url, (int?)ex.Status ?? 0, ex.Code ?? "-", ex.Message);
            throw;
        }
    }

    // ---------------------------------------------------------------- typed endpoints

    private string OrganizationRoute(string suffix = "") =>
        $"{CloudRoutes.AdminOrganizations}/{OrganizationId ?? throw new InvalidOperationException("No organization is selected.")}{suffix}";

    public Task<List<OrganizationSummary>> GetOrganizationsAsync(CancellationToken ct) =>
        AdminAsync<List<OrganizationSummary>>(HttpMethod.Get, CloudRoutes.AdminOrganizations, ct: ct);

    /// <summary>
    /// Creates an organization. Global administrators only — the console only offers it when
    /// <see cref="AdminMeResponse.IsGlobalAdmin"/> is set, and the server enforces it again. The response carries
    /// the enrollment key, which is the only time it is ever returned.
    /// </summary>
    public Task<CreateOrganizationResponse> CreateOrganizationAsync(string name, string? entraTenantId, CancellationToken ct) =>
        AdminAsync<CreateOrganizationResponse>(HttpMethod.Post, CloudRoutes.AdminOrganizations,
            new CreateOrganizationRequest { Name = name, EntraTenantId = string.IsNullOrWhiteSpace(entraTenantId) ? null : entraTenantId.Trim() },
            ct: ct);

    public Task<PagedResult<DeviceSummary>> GetDevicesAsync(string? search, int page, int pageSize, CancellationToken ct) =>
        AdminAsync<PagedResult<DeviceSummary>>(HttpMethod.Get,
            OrganizationRoute($"/devices?search={Uri.EscapeDataString(search ?? string.Empty)}&page={page}&pageSize={pageSize}"), ct: ct);

    public Task<DeviceDetail> GetDeviceAsync(Guid deviceId, CancellationToken ct) =>
        AdminAsync<DeviceDetail>(HttpMethod.Get, OrganizationRoute($"/devices/{deviceId}"), ct: ct);

    /// <summary>The endpoint answers 204, so there is nothing to return; a failure throws.</summary>
    public async Task DeleteDeviceAsync(Guid deviceId, CancellationToken ct) =>
        await AdminAsync<string>(HttpMethod.Delete, OrganizationRoute($"/devices/{deviceId}"), ct: ct).ConfigureAwait(false);

    public Task<DeviceCommand> SendCommandAsync(Guid deviceId, DeviceCommandKind kind, string? argument, CancellationToken ct) =>
        AdminAsync<DeviceCommand>(HttpMethod.Post, OrganizationRoute($"/devices/{deviceId}/commands"),
            new DeviceCommandRequest { Kind = kind, Argument = argument }, ct: ct);

    public Task<List<OrganizationInventoryItem>> GetInventoryAsync(string? search, bool onlyUnmonitored, CancellationToken ct) =>
        AdminAsync<List<OrganizationInventoryItem>>(HttpMethod.Get,
            OrganizationRoute($"/inventory?search={Uri.EscapeDataString(search ?? string.Empty)}&onlyUnmonitored={(onlyUnmonitored ? "true" : "false")}"), ct: ct);

    public Task<OrganizationConfigResponse> GetConfigAsync(CancellationToken ct) =>
        AdminAsync<OrganizationConfigResponse>(HttpMethod.Get, OrganizationRoute("/config"), ct: ct);

    public Task<OrganizationConfigResponse> PutConfigAsync(SettingsDocument settings, string? baseConfigVersion, string? comment, CancellationToken ct) =>
        AdminAsync<OrganizationConfigResponse>(HttpMethod.Put, OrganizationRoute("/config"),
            new OrganizationConfigUpdateRequest { BaseConfigVersion = baseConfigVersion, Settings = settings, Comment = comment },
            ifMatch: baseConfigVersion, ct: ct);

    /// <summary>Who changed the configuration, when and why. The entries carry the version, not the document.</summary>
    public Task<PagedResult<ConfigHistoryEntry>> GetConfigHistoryAsync(int page, int pageSize, CancellationToken ct) =>
        AdminAsync<PagedResult<ConfigHistoryEntry>>(HttpMethod.Get,
            OrganizationRoute($"/config/history?page={page}&pageSize={pageSize}"), ct: ct);

    /// <summary>
    /// The settings exactly as they were at one revision. The response deliberately carries no ETag: restoring is
    /// a normal PUT of these settings against the <em>current</em> version, which appends a revision rather than
    /// rewinding the history — sending the historical version as If-Match would always 409.
    /// </summary>
    public Task<OrganizationConfigResponse> GetConfigRevisionAsync(long historyId, CancellationToken ct) =>
        AdminAsync<OrganizationConfigResponse>(HttpMethod.Get, OrganizationRoute($"/config/history/{historyId}"), ct: ct);

    /// <summary>The organization-wide event feed. Newest first; <paramref name="since"/> is optional.</summary>
    public Task<PagedResult<OrganizationEvent>> GetEventsAsync(DateTimeOffset? since, int page, int pageSize, CancellationToken ct) =>
        AdminAsync<PagedResult<OrganizationEvent>>(HttpMethod.Get,
            OrganizationRoute($"/events?{(since is { } s ? "since=" + Uri.EscapeDataString(s.ToString("O")) + "&" : "")}page={page}&pageSize={pageSize}"), ct: ct);

    public Task<EnrollmentInfoResponse> GetEnrollmentAsync(CancellationToken ct) =>
        AdminAsync<EnrollmentInfoResponse>(HttpMethod.Get, OrganizationRoute("/enrollment"), ct: ct);

    public Task<EnrollmentInfoResponse> RotateEnrollmentKeyAsync(CancellationToken ct) =>
        AdminAsync<EnrollmentInfoResponse>(HttpMethod.Post, OrganizationRoute("/enrollment/rotate"), ct: ct);

    // ---------------------------------------------------------------- helpers

    /// <summary>The message an <see cref="ApiError"/> (or any other failure) should show inline on a page.</summary>
    public static string Describe(Exception exception) => exception switch
    {
        CloudException { Status: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } ex =>
            Resources.Strings.CloudNotAuthorised(ex.Message),
        CloudException ex => ex.Message,
        AuthenticationRequiredException ex => ex.Message,
        OperationCanceledException => Resources.Strings.CloudCancelled,
        HttpRequestException ex => Resources.Strings.CloudUnreachable(ex.Message),
        _ => $"{exception.GetType().Name}: {exception.Message}",
    };

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        var config = AuthConfig ?? throw new InvalidOperationException("Connect to a server before calling the admin API.");
        if (_token is { } token && token.ExpiresOn - TokenSkew > DateTimeOffset.UtcNow) return token.AccessToken;
        _token = await _authenticator.AcquireTokenAsync(config, allowInteractive: false, ct).ConfigureAwait(false);
        return _token.AccessToken;
    }

    private CloudClient Client()
    {
        var url = ServerUrl ?? throw new InvalidOperationException("No cloud server URL has been set.");
        if (_client is not null && string.Equals(_clientUrl, url, StringComparison.OrdinalIgnoreCase)) return _client;

        // The previous client may still have a request in flight (the log showed "The CancellationTokenSource has
        // been disposed" from exactly that): retire it and dispose it with the session instead of right now.
        if (_client is not null) _retired.Add(_client);
        var proxy = _preferences.EffectiveSettings.ProxyUrl;
        _client = _clients.Create(url, string.IsNullOrWhiteSpace(proxy) ? null : proxy);
        _clientUrl = url;
        _log.LogInformation("Cloud client created for {Url}{Proxy}.", url,
            string.IsNullOrWhiteSpace(proxy) ? string.Empty : $" through proxy {proxy}");
        return _client;
    }

    private string Absolute(string relativePath) =>
        ServerUrl is null ? relativePath : ServerUrl.TrimEnd('/') + relativePath;

    /// <summary>Trailing slashes off, and a bare host gets https:// so "appmonitor.example.com" just works.</summary>
    public static string Normalise(string serverUrl)
    {
        var text = (serverUrl ?? string.Empty).Trim().TrimEnd('/');
        if (text.Length == 0) return text;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        return text;
    }

    public void Dispose()
    {
        foreach (var retired in _retired) retired.Dispose();
        _retired.Clear();
        _client?.Dispose();
        _client = null;
    }
}
