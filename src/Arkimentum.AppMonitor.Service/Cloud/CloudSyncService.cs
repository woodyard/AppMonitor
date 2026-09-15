using Arkimentum.AppMonitor.Cloud;
using Arkimentum.AppMonitor.Configuration;
using Arkimentum.AppMonitor.Inventory;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Service.Update;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Service.Cloud;

public sealed class CloudSyncOptions
{
    /// <summary>State directory the credential, the configuration cache and cloud-status.json live in.</summary>
    public string StateDirectory { get; init; } = AgentSettings.DefaultStateDirectory;
    /// <summary>Testing mode (<c>--user-config</c>): the agent updater never launches the installer.</summary>
    public bool DryRun { get; init; }
    /// <summary>Command-line entry points (<c>--cloud-enroll</c>, <c>--report-now</c>) resolve the service but do not run the loop.</summary>
    public bool CliOnly { get; init; }
}

/// <summary>
/// Connects the device to an organization in the AppMonitor cloud service: enrolls once, polls the organization
/// configuration into <see cref="CloudConfigCache"/> (which the configuration reader layers between policy and local
/// preferences), executes the commands an administrator queued, and reports inventory, update state and events back.
/// Every failure is logged and retried with an exponential back-off; nothing here can stop the service from working
/// offline.
/// </summary>
public sealed class CloudSyncService : BackgroundService
{
    private static readonly TimeSpan LoopInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);
    private static readonly TimeSpan MinServerPollInterval = TimeSpan.FromSeconds(60);

    private readonly ILogger<CloudSyncService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SettingsProvider _settings;
    private readonly UpdateCoordinator _coordinator;
    private readonly CloudConfigCache _cache;
    private readonly DeviceCredentialStore _credentials;
    private readonly InstalledAppScanner _scanner;
    private readonly ICatalogProvider _catalog;
    private readonly AgentUpdater _updater;
    private readonly CloudSyncOptions _options;
    private readonly CloudStatusFile _statusFile;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private CloudClient? _client;
    private string? _clientUrl;
    private DeviceCredential? _credential;
    private string? _etag;
    private CloudStatus _status = new();
    private bool _forceEnroll;
    private bool _loggedDisabled;
    private bool _loggedMissingKey;
    private int _failures;
    private DateTimeOffset _retryAfterUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _nextConfigUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _nextReportUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _nextAgentCheckUtc = DateTimeOffset.MinValue;
    private TimeSpan? _serverPollInterval;
    private volatile bool _reportRequested;

    private readonly HashSet<Guid> _processedCommands = [];
    private readonly List<Guid> _pendingAcks = [];
    private readonly List<ReportedEvent> _pendingEvents = [];
    private DateTimeOffset? _discoveryStamp;
    private IReadOnlyList<DiscoveredApp> _discovered = [];

    public CloudSyncService(ILogger<CloudSyncService> logger, ILoggerFactory loggerFactory, SettingsProvider settings, UpdateCoordinator coordinator,
        CloudConfigCache cache, DeviceCredentialStore credentials, InstalledAppScanner scanner, ICatalogProvider catalog,
        AgentUpdater updater, CloudSyncOptions options)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _settings = settings;
        _coordinator = coordinator;
        _cache = cache;
        _credentials = credentials;
        _scanner = scanner;
        _catalog = catalog;
        _updater = updater;
        _options = options;
        _statusFile = new CloudStatusFile(logger, options.StateDirectory);
        _status = _statusFile.Load() ?? new CloudStatus();
        _updater.CloudManifestResolver = GetCloudReleaseAsync;
        _coordinator.CloudStatusSource = () => _status;
    }

    public string StatusFilePath => _statusFile.FilePath;
    public bool HasCredential => (_credential ?? _credentials.Load()) is not null;

    // =================================================================================================================
    // Background loop
    // =================================================================================================================

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.CliOnly) return;
        _coordinator.ScanCompleted += OnScanCompleted;
        _coordinator.InstallCompleted += OnInstallCompleted;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
            _updater.ProcessStartupMarker();
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await TickAsync(stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { _logger.LogError(ex, "Cloud sync iteration failed"); Backoff(ex.Message); }
                await Task.Delay(LoopInterval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex) { _logger.LogCritical(ex, "Cloud sync stopped unexpectedly"); }
        finally
        {
            _coordinator.ScanCompleted -= OnScanCompleted;
            _coordinator.InstallCompleted -= OnInstallCompleted;
            _client?.Dispose();
        }
    }

    private void OnScanCompleted() => _reportRequested = true;
    private void OnInstallCompleted(PendingUpdate update, bool success) => _reportRequested = true;

    private async Task TickAsync(CancellationToken ct)
    {
        var settings = _settings.Current;
        var now = DateTimeOffset.UtcNow;

        if (settings.CloudConfigured)
        {
            _loggedDisabled = false;
            if (now >= _retryAfterUtc && await EnsureEnrolledAsync(settings, ct).ConfigureAwait(false))
            {
                if (settings.CloudConfigEnabled && now >= _nextConfigUtc) await PollConfigAsync(settings, ct).ConfigureAwait(false);
                if (settings.CloudReportingEnabled && (_reportRequested || DateTimeOffset.UtcNow >= _nextReportUtc))
                    await SendReportAsync(settings, "scheduled", ct).ConfigureAwait(false);
            }
        }
        else if (!_loggedDisabled)
        {
            _loggedDisabled = true;
            _logger.LogInformation("Cloud sync is idle: CloudServerUrl/CloudOrganizationId are not configured");
        }

        if (DateTimeOffset.UtcNow >= _nextAgentCheckUtc)
        {
            _nextAgentCheckUtc = DateTimeOffset.UtcNow + TimeSpan.FromHours(Math.Clamp(settings.AgentUpdateCheckIntervalHours, 1, 720));
            if (settings.AgentAutoUpdate) await _updater.UpdateAsync("scheduled", null, ct).ConfigureAwait(false);
            else _logger.LogDebug("Agent self-update is disabled (AgentAutoUpdate=0)");
        }
    }

    // =================================================================================================================
    // Enrollment
    // =================================================================================================================

    /// <summary>Ensures a usable device credential and HTTP client; enrolls when the credential is missing or stale.</summary>
    private async Task<bool> EnsureEnrolledAsync(AgentSettings settings, CancellationToken ct)
    {
        _credential ??= _credentials.Load();
        var organizationId = Guid.Parse(settings.CloudOrganizationId);
        var reason =
            _forceEnroll ? "the server rejected the device credential" :
            _credential is null ? "no device credential is stored" :
            !SameUrl(_credential.ServerUrl, settings.CloudServerUrl) ? $"the server URL changed from {_credential.ServerUrl} to {settings.CloudServerUrl}" :
            _credential.OrganizationId != organizationId ? $"the organization changed from {_credential.OrganizationId} to {organizationId}" :
            null;

        if (reason is null)
        {
            EnsureClient(settings);
            return true;
        }
        return await EnrollAsync(settings, reason, ct).ConfigureAwait(false);
    }

    private async Task<bool> EnrollAsync(AgentSettings settings, string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.CloudEnrollmentKey))
        {
            if (!_loggedMissingKey)
            {
                _loggedMissingKey = true;
                _logger.LogError("Cloud enrollment is needed ({Reason}) but CloudEnrollmentKey is empty; set it in the policy or preference key", reason);
                SaveStatus(s => s.LastError = "CloudEnrollmentKey is not configured");
            }
            Backoff("CloudEnrollmentKey is not configured");
            return false;
        }
        _loggedMissingKey = false;

        EnsureClient(settings);
        var (tenantId, entraDeviceId) = DeviceIdentity.EntraJoinInfo;
        var request = new EnrollRequest
        {
            OrganizationId = Guid.Parse(settings.CloudOrganizationId),
            EnrollmentKey = settings.CloudEnrollmentKey,
            DeviceName = DeviceIdentity.DeviceName,
            MachineGuid = DeviceIdentity.MachineGuid,
            EntraTenantId = tenantId,
            EntraDeviceId = entraDeviceId,
            OsVersion = DeviceIdentity.OsVersion,
            AgentVersion = UpdateCoordinator.ServiceVersion,
        };
        _logger.LogInformation("Enrolling {Device} with {Url}{Route} (organization {Organization}, machine {MachineGuid}, Entra tenant {Tenant}, Entra device {EntraDevice}, agent {Agent}): {Reason}",
            request.DeviceName, settings.CloudServerUrl.TrimEnd('/'), CloudRoutes.Enroll, request.OrganizationId, request.MachineGuid,
            tenantId ?? "-", entraDeviceId ?? "-", request.AgentVersion, reason);
        try
        {
            var response = await _client!.EnrollAsync(request, ct).ConfigureAwait(false);
            _credential = new DeviceCredential
            {
                OrganizationId = request.OrganizationId,
                DeviceId = response.DeviceId,
                DeviceKey = response.DeviceKey,
                ServerUrl = settings.CloudServerUrl.TrimEnd('/'),
                OrganizationName = response.OrganizationName,
                EnrolledUtc = DateTimeOffset.UtcNow,
            };
            _credentials.Save(_credential);
            _forceEnroll = false;
            _failures = 0;
            _etag = null;
            _nextConfigUtc = DateTimeOffset.MinValue;
            _nextReportUtc = DateTimeOffset.MinValue;
            _logger.LogInformation("Enrolled as device {DeviceId} in organization '{Organization}' ({OrganizationId}); server config version {Version}",
                response.DeviceId, response.OrganizationName, request.OrganizationId, response.ConfigVersion ?? "-");
            SaveStatus(s =>
            {
                s.ServerUrl = _credential.ServerUrl;
                s.OrganizationName = response.OrganizationName;
                s.DeviceId = response.DeviceId;
                s.EnrolledUtc = _credential.EnrolledUtc;
                s.LastError = null;
            });
            // The tray shows the organization; tell connected agents straight away rather than at the next scan.
            await _coordinator.BroadcastStateAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Enrollment with {Url} failed", settings.CloudServerUrl);
            Backoff(ex.Message);
            return false;
        }
    }

    private void EnsureClient(AgentSettings settings)
    {
        var url = settings.CloudServerUrl.TrimEnd('/');
        if (_client is not null && SameUrl(_clientUrl, url)) return;
        _client?.Dispose();
        _client = new CloudClient(_loggerFactory.CreateLogger<CloudClient>(), url, settings.ProxyUrl);
        _clientUrl = url;
        _logger.LogInformation("Cloud API client targeting {Url}{Proxy}", url, string.IsNullOrWhiteSpace(settings.ProxyUrl) ? "" : $" through proxy {settings.ProxyUrl}");
    }

    private static bool SameUrl(string? a, string? b) =>
        string.Equals(a?.TrimEnd('/'), b?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    // =================================================================================================================
    // Configuration polling + commands
    // =================================================================================================================

    private async Task PollConfigAsync(AgentSettings settings, CancellationToken ct)
    {
        _etag ??= _cache.Load()?.ConfigVersion;
        var url = _credential!.ServerUrl + CloudRoutes.Config;
        try
        {
            _logger.LogInformation("Polling the organization configuration from {Url} (device {DeviceId}, known version {Version})", url, _credential.DeviceId, _etag ?? "none");
            var result = await _client!.GetConfigAsync(_credential, _etag, ct).ConfigureAwait(false);
            _failures = 0;
            if (result.NotModified)
            {
                _logger.LogInformation("Organization configuration unchanged (304 Not Modified, version {Version})", _etag ?? "-");
            }
            else if (result.Config is { } config)
            {
                var changed = !string.Equals(_etag, result.ETag, StringComparison.Ordinal);
                _cache.Save(config);
                _etag = result.ETag ?? config.ConfigVersion;
                _logger.LogInformation("Organization configuration {Version} from '{Organization}' applied: {Globals} global value(s), {Apps} app(s), updated {Updated} by {By}",
                    config.ConfigVersion, config.OrganizationName, config.Settings.Global.Count, config.Settings.Apps.Count,
                    config.UpdatedUtc == default ? "-" : config.UpdatedUtc.ToString("u"), config.UpdatedBy ?? "-");
                settings = _settings.Reload();
                if (changed) _reportRequested = true;
                SaveStatus(s =>
                {
                    s.OrganizationName = config.OrganizationName;
                    s.LastConfigVersion = config.ConfigVersion;
                    s.LastConfigUtc = DateTimeOffset.UtcNow;
                    s.LastError = null;
                });
                await _coordinator.BroadcastStateAsync(ct).ConfigureAwait(false);
                if (config.PollIntervalSeconds > 0)
                {
                    var server = TimeSpan.FromSeconds(config.PollIntervalSeconds);
                    _serverPollInterval = server > MinServerPollInterval ? server : null;
                }
                await ProcessCommandsAsync(config.Commands, ct).ConfigureAwait(false);
            }
            _nextConfigUtc = DateTimeOffset.UtcNow + ConfigInterval(settings);
        }
        catch (CloudException ex) when (ex.IsUnauthorized)
        {
            _logger.LogWarning("The cloud API rejected the device credential ({Status}) when polling {Url}; the device will re-enroll", ex.Status, url);
            _forceEnroll = true;
            Backoff(ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Polling the organization configuration from {Url} failed", url);
            Backoff(ex.Message);
        }
    }

    private TimeSpan ConfigInterval(AgentSettings settings)
    {
        var configured = TimeSpan.FromMinutes(Math.Clamp(settings.CloudSyncIntervalMinutes, 1, 1440));
        return _serverPollInterval is { } server && server > configured ? server : configured;
    }

    /// <summary>Executes the commands an administrator queued for this device and remembers their ids for the next report.</summary>
    private async Task ProcessCommandsAsync(List<DeviceCommand> commands, CancellationToken ct)
    {
        foreach (var command in commands)
        {
            if (!_processedCommands.Add(command.CommandId)) continue;
            _logger.LogInformation("Cloud command {Kind} ({CommandId}) issued {Issued} by {By}{Argument}", command.Kind, command.CommandId,
                command.IssuedUtc == default ? "-" : command.IssuedUtc.ToString("u"), command.IssuedBy ?? "-",
                string.IsNullOrWhiteSpace(command.Argument) ? "" : $", argument '{command.Argument}'");
            try
            {
                switch (command.Kind)
                {
                    case DeviceCommandKind.ScanNow:
                        _coordinator.RequestScan();
                        break;
                    case DeviceCommandKind.RepairPrerequisites:
                        _ = Task.Run(() => _coordinator.EnsurePrerequisitesAsync($"cloud command {command.CommandId}", force: true, CancellationToken.None), CancellationToken.None);
                        break;
                    case DeviceCommandKind.UpdateAgent:
                        _ = Task.Run(() => _updater.UpdateAsync($"cloud command {command.CommandId}", command.Argument, CancellationToken.None), CancellationToken.None);
                        break;
                    case DeviceCommandKind.ReportNow:
                        _reportRequested = true;
                        break;
                    default:
                        _logger.LogWarning("Cloud command {Kind} is not understood by this agent version", command.Kind);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cloud command {Kind} ({CommandId}) failed", command.Kind, command.CommandId);
            }
            _pendingAcks.Add(command.CommandId);
        }
        // Keep the "already processed" set bounded; ids are monotonically issued per device.
        if (_processedCommands.Count > 500) _processedCommands.Clear();
        await Task.CompletedTask;
    }

    // =================================================================================================================
    // Reporting
    // =================================================================================================================

    private async Task SendReportAsync(AgentSettings settings, string reason, CancellationToken ct)
    {
        _reportRequested = false;
        var url = _credential!.ServerUrl + CloudRoutes.Report;
        DeviceReport report;
        try { report = await BuildReportAsync(settings, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Building the device report failed");
            return;
        }

        try
        {
            _logger.LogInformation("Reporting to {Url} ({Reason}): {Apps} installed app(s), {Updates} tracked update(s), {Events} event(s), {Acks} command ack(s), config {Version}",
                url, reason, report.InstalledApps.Count, report.Updates.Count, report.Events.Count, report.AcknowledgedCommands.Count, report.ConfigVersionApplied ?? "-");
            var response = await _client!.ReportAsync(_credential, report, ct).ConfigureAwait(false);
            _failures = 0;
            _pendingAcks.Clear();
            _pendingEvents.Clear();
            _nextReportUtc = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(Math.Clamp(settings.CloudSyncIntervalMinutes, 1, 1440));
            SaveStatus(s => { s.LastReportUtc = DateTimeOffset.UtcNow; s.LastError = null; });
            _logger.LogInformation("Report accepted={Accepted}, configChanged={Changed}, serverConfigVersion={Version}", response.Accepted, response.ConfigChanged, response.ConfigVersion ?? "-");
            if (response.ConfigChanged)
            {
                _logger.LogInformation("The server has a newer configuration ({Version}); polling it now", response.ConfigVersion ?? "-");
                _nextConfigUtc = DateTimeOffset.MinValue;
            }
        }
        catch (CloudException ex) when (ex.IsUnauthorized)
        {
            _logger.LogWarning("The cloud API rejected the device credential ({Status}) when reporting to {Url}; the device will re-enroll", ex.Status, url);
            _forceEnroll = true;
            Backoff(ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reporting to {Url} failed; {Events} event(s) are kept for the next report", url, _pendingEvents.Count);
            Backoff(ex.Message);
        }
    }

    private async Task<DeviceReport> BuildReportAsync(AgentSettings settings, CancellationToken ct)
    {
        _pendingEvents.AddRange(_coordinator.DrainEvents());
        var installed = await DiscoverInstalledAppsAsync(settings, ct).ConfigureAwait(false);
        return new DeviceReport
        {
            ReportedUtc = DateTimeOffset.UtcNow,
            AgentVersion = UpdateCoordinator.ServiceVersion,
            OsVersion = DeviceIdentity.OsVersion,
            DeviceName = DeviceIdentity.DeviceName,
            LastLogonUser = _coordinator.LastLogonUser,
            LastScanUtc = _coordinator.LastScanUtc,
            ConfigVersionApplied = _cache.Load()?.ConfigVersion,
            Prerequisites = _coordinator.PrerequisiteStatus?.Clone(),
            InstalledApps = installed.Select(a => new ReportedApp
            {
                DisplayName = a.DisplayName,
                Version = a.Version,
                Publisher = a.Publisher,
                WingetId = a.WingetId,
                AvailableVersion = a.AvailableVersion,
                Context = a.Context,
                CatalogAppId = a.CatalogAppId,
                MonitoredAppId = a.ConfiguredAppId,
            }).ToList(),
            Updates = _coordinator.TrackedUpdates().Select(u => new ReportedUpdate
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
            }).ToList(),
            Events = [.. _pendingEvents],
            AcknowledgedCommands = [.. _pendingAcks],
        };
    }

    /// <summary>
    /// The installed-app inventory takes a couple of seconds (it runs <c>winget list</c>), so it is computed once per
    /// scan and reused by every report in between.
    /// </summary>
    private async Task<IReadOnlyList<DiscoveredApp>> DiscoverInstalledAppsAsync(AgentSettings settings, CancellationToken ct)
    {
        var stamp = _coordinator.LastScanUtc;
        if (_discovered.Count > 0 && _discoveryStamp == stamp) return _discovered;
        var discovery = new InstalledAppDiscovery(_loggerFactory.CreateLogger<InstalledAppDiscovery>(), _scanner);
        IReadOnlyList<AppPolicy> catalog = settings.UseCatalog ? _catalog.GetCatalog(settings.CatalogPath) : Array.Empty<AppPolicy>();
        _discovered = await discovery.DiscoverAsync(settings.Apps, catalog, settings.WingetPath, settings.WingetEnabled, null, ct).ConfigureAwait(false);
        _discoveryStamp = stamp;
        return _discovered;
    }

    // =================================================================================================================
    // Command-line entry points
    // =================================================================================================================

    /// <summary>Forces a fresh enrollment (<c>--cloud-enroll</c>). Returns false when it did not succeed.</summary>
    public async Task<bool> EnrollNowAsync(CancellationToken ct)
    {
        var settings = _settings.Reload();
        if (!settings.CloudConfigured)
        {
            _logger.LogError("Cannot enroll: CloudServerUrl and CloudOrganizationId must both be configured");
            return false;
        }
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _credential = null;
            _forceEnroll = true;
            return await EnrollAsync(settings, "requested on the command line (--cloud-enroll)", ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Sends one report immediately (<c>--report-now</c>). Returns false when it did not succeed.</summary>
    public async Task<bool> ReportNowAsync(CancellationToken ct)
    {
        var settings = _settings.Reload();
        if (!settings.CloudConfigured)
        {
            _logger.LogError("Cannot report: CloudServerUrl and CloudOrganizationId must both be configured");
            return false;
        }
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!await EnsureEnrolledAsync(settings, ct).ConfigureAwait(false)) return false;
            var before = _status.LastReportUtc;
            await SendReportAsync(settings, "requested on the command line (--report-now)", ct).ConfigureAwait(false);
            return _status.LastReportUtc is { } after && after != before;
        }
        finally { _gate.Release(); }
    }

    /// <summary>The cloud API's release mirror, used by the self-updater when no AgentUpdateFeedUrl is configured.</summary>
    private async Task<Arkimentum.AppMonitor.Cloud.ReleaseManifest?> GetCloudReleaseAsync(string channel, CancellationToken ct)
    {
        var settings = _settings.Current;
        if (!settings.CloudConfigured) return null;
        if (!await EnsureEnrolledAsync(settings, ct).ConfigureAwait(false)) return null;
        _logger.LogInformation("Asking {Url}{Route} for the {Channel} agent release", _credential!.ServerUrl, CloudRoutes.Release, channel);
        var manifest = await _client!.GetReleaseAsync(_credential, channel, ct).ConfigureAwait(false);
        if (manifest is null) _logger.LogInformation("The cloud API does not mirror a {Channel} release", channel);
        else _logger.LogInformation("The cloud API offers agent {Version} ({Channel}) at {Url}", manifest.Version, manifest.Channel, manifest.PackageUrl);
        return manifest;
    }

    // =================================================================================================================
    // helpers
    // =================================================================================================================

    private void Backoff(string? error)
    {
        _failures++;
        var delay = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, MinBackoff.TotalSeconds * Math.Pow(2, Math.Min(_failures - 1, 10))));
        _retryAfterUtc = DateTimeOffset.UtcNow + delay;
        _logger.LogWarning("Cloud sync retry {Failures} in {Delay}", _failures, delay);
        SaveStatus(s => s.LastError = error);
    }

    private void SaveStatus(Action<CloudStatus> mutate)
    {
        mutate(_status);
        _statusFile.Save(_status);
    }
}
