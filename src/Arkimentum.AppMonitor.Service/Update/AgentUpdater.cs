using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arkimentum.AppMonitor.Cloud;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Versioning;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Service.Update;

/// <summary>What the self-updater did (or why it did nothing).</summary>
public enum AgentUpdateAction
{
    /// <summary>No feed configured and no cloud mirror available, or AgentAutoUpdate is off for a scheduled check.</summary>
    Disabled,
    /// <summary>The feed could not be read (network, malformed manifest, missing asset).</summary>
    NoManifest,
    /// <summary>The published version is not newer than the running one.</summary>
    UpToDate,
    /// <summary>The published version is newer than AgentTargetVersion, which pins this device.</summary>
    PinnedByTargetVersion,
    /// <summary>The manifest is for another channel than AgentUpdateChannel.</summary>
    ChannelMismatch,
    /// <summary>An update is available (returned by a check that does not install).</summary>
    UpdateAvailable,
    /// <summary>An application install is running; the agent will not replace itself now.</summary>
    Blocked,
    /// <summary>Download, hash verification or extraction failed; nothing was launched.</summary>
    Failed,
    /// <summary>The installer was started detached; this service is about to be stopped and replaced.</summary>
    Launched,
    /// <summary>--user-config testing mode: everything was verified but the installer was not started.</summary>
    WouldLaunch,
}

/// <param name="Action">Outcome of the check or update.</param>
/// <param name="Reason">Human-readable explanation, suitable for a log line or the console.</param>
/// <param name="Manifest">The resolved manifest, when there was one.</param>
/// <param name="PackageDirectory">Where the package was downloaded and extracted, when it got that far.</param>
public sealed record AgentUpdateOutcome(AgentUpdateAction Action, string Reason, ReleaseManifest? Manifest = null, string? PackageDirectory = null)
{
    public bool UpdateAvailable => Action is AgentUpdateAction.UpdateAvailable or AgentUpdateAction.Launched or AgentUpdateAction.WouldLaunch;
    public override string ToString() => $"{Action}: {Reason}";
}

/// <summary>Written before the installer is launched and read on the next start to report the outcome.</summary>
public sealed class AgentUpdateMarker
{
    public required string FromVersion { get; set; }
    public required string ToVersion { get; set; }
    public DateTimeOffset StartedUtc { get; set; }
    public string? PackageDirectory { get; set; }
    public string? Reason { get; set; }
}

public sealed class AgentUpdaterOptions
{
    public string StateDirectory { get; init; } = AgentSettings.DefaultStateDirectory;
    /// <summary>Testing mode (<c>--user-config</c>): resolve, download, verify and extract, but never launch the installer.</summary>
    public bool DryRun { get; init; }
    /// <summary>Test seam: HTTP handler used for both the feed and the package download.</summary>
    public HttpMessageHandler? Handler { get; init; }
}

/// <summary>
/// Keeps the agent itself up to date: resolves a <see cref="ReleaseManifest"/> from the configured feed (or the cloud
/// API's mirror), decides whether it applies to this device, downloads and verifies the release zip and hands over to
/// the release's own <c>Install-ArkimentumAppMonitor.ps1</c>, which stops this service, replaces the files and starts it
/// again. See docs/SelfUpdate.md.
/// </summary>
public sealed class AgentUpdater
{
    private const string MarkerFileName = "update-pending.json";
    private const string UpdatesFolderName = "AgentUpdates";
    private const int KeepPackageFolders = 2;
    private static readonly TimeSpan MarkerTimeout = TimeSpan.FromMinutes(30);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogger<AgentUpdater> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SettingsProvider _settings;
    private readonly UpdateCoordinator _coordinator;
    private readonly AgentUpdaterOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AgentUpdater(ILogger<AgentUpdater> logger, ILoggerFactory loggerFactory, SettingsProvider settings,
        UpdateCoordinator coordinator, AgentUpdaterOptions options)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _settings = settings;
        _coordinator = coordinator;
        _options = options;
    }

    /// <summary>Set by the cloud sync service: asks the cloud API for its release mirror when no feed URL is configured.</summary>
    public Func<string, CancellationToken, Task<ReleaseManifest?>>? CloudManifestResolver { get; set; }

    /// <summary>The running agent's informational version.</summary>
    public string CurrentVersion => UpdateCoordinator.ServiceVersion;

    public string UpdatesRoot => Path.Combine(_options.StateDirectory, UpdatesFolderName);
    public string MarkerPath => Path.Combine(_options.StateDirectory, MarkerFileName);

    // =================================================================================================================
    // Decision
    // =================================================================================================================

    /// <summary>
    /// Pure policy: does <paramref name="manifest"/> apply to a device running <paramref name="currentVersion"/>?
    /// Channel must match, <paramref name="targetVersion"/> (AgentTargetVersion) is never exceeded, and being below
    /// <see cref="ReleaseManifest.MinimumSupportedVersion"/> is a warning, not a blocker.
    /// </summary>
    public static AgentUpdateOutcome Decide(ReleaseManifest? manifest, string currentVersion, string? targetVersion, string? channel, ILogger? logger = null)
    {
        if (manifest is null) return new AgentUpdateOutcome(AgentUpdateAction.NoManifest, "No release manifest could be resolved");

        var wanted = string.IsNullOrWhiteSpace(channel) ? "stable" : channel.Trim();
        var published = string.IsNullOrWhiteSpace(manifest.Channel) ? "stable" : manifest.Channel.Trim();
        if (!published.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            return new AgentUpdateOutcome(AgentUpdateAction.ChannelMismatch,
                $"Manifest {manifest.Version} is on the '{published}' channel but this device follows '{wanted}'", manifest);

        if (!string.IsNullOrWhiteSpace(manifest.MinimumSupportedVersion)
            && VersionComparer.Compare(currentVersion, manifest.MinimumSupportedVersion) < 0)
            logger?.LogWarning("Agent update: this agent ({Current}) is older than the minimum supported version {Minimum} published in the manifest; updating is required",
                currentVersion, manifest.MinimumSupportedVersion);

        if (!string.IsNullOrWhiteSpace(targetVersion))
        {
            if (VersionComparer.Compare(manifest.Version, targetVersion) > 0)
                return new AgentUpdateOutcome(AgentUpdateAction.PinnedByTargetVersion,
                    $"Manifest {manifest.Version} is newer than AgentTargetVersion {targetVersion.Trim()}; this device stays on {currentVersion}", manifest);
        }

        if (VersionComparer.Compare(manifest.Version, currentVersion) <= 0)
            return new AgentUpdateOutcome(AgentUpdateAction.UpToDate, $"Running {currentVersion}; the {published} channel offers {manifest.Version}", manifest);

        return new AgentUpdateOutcome(AgentUpdateAction.UpdateAvailable, $"{currentVersion} -> {manifest.Version} ({published})", manifest);
    }

    // =================================================================================================================
    // Resolve / check / update
    // =================================================================================================================

    /// <summary>Resolves the manifest from the configured feed, or from the cloud API when no feed URL is set.</summary>
    public async Task<ReleaseManifest?> ResolveManifestAsync(AgentSettings settings, CancellationToken ct)
    {
        var channel = string.IsNullOrWhiteSpace(settings.AgentUpdateChannel) ? "stable" : settings.AgentUpdateChannel.Trim();
        if (!string.IsNullOrWhiteSpace(settings.AgentUpdateFeedUrl))
        {
            using var feed = new ReleaseFeed(_logger, _options.Handler, settings.ProxyUrl);
            return await feed.ResolveAsync(settings.AgentUpdateFeedUrl, ct).ConfigureAwait(false);
        }
        if (CloudManifestResolver is not null && settings.CloudConfigured)
        {
            _logger.LogInformation("Agent update: no AgentUpdateFeedUrl configured; asking the cloud API for the {Channel} release", channel);
            return await CloudManifestResolver(channel, ct).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>Resolves and decides, without downloading anything (<c>--check-update</c>).</summary>
    public async Task<AgentUpdateOutcome> CheckAsync(string reason, string? targetVersionOverride, CancellationToken ct)
    {
        var settings = _settings.Current;
        if (string.IsNullOrWhiteSpace(settings.AgentUpdateFeedUrl) && !(settings.CloudConfigured && CloudManifestResolver is not null))
            return new AgentUpdateOutcome(AgentUpdateAction.Disabled, "No AgentUpdateFeedUrl is configured and the cloud connection is not available");

        _logger.LogInformation("Agent update check ({Reason}): running {Current}, channel {Channel}{Target}", reason, CurrentVersion,
            string.IsNullOrWhiteSpace(settings.AgentUpdateChannel) ? "stable" : settings.AgentUpdateChannel,
            string.IsNullOrWhiteSpace(targetVersionOverride ?? settings.AgentTargetVersion) ? "" : $", pinned to {targetVersionOverride ?? settings.AgentTargetVersion}");

        ReleaseManifest? manifest;
        try { manifest = await ResolveManifestAsync(settings, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Agent update: could not read the release feed {Url}", settings.AgentUpdateFeedUrl);
            return new AgentUpdateOutcome(AgentUpdateAction.NoManifest, "The release feed could not be read: " + ex.Message);
        }

        var outcome = Decide(manifest, CurrentVersion, targetVersionOverride ?? settings.AgentTargetVersion, settings.AgentUpdateChannel, _logger);
        _logger.LogInformation("Agent update decision: {Outcome}", outcome);
        return outcome;
    }

    /// <summary>
    /// The full pipeline: resolve, decide, download, verify SHA-256, extract, and hand over to the release's installer.
    /// Safe to call concurrently - only one run at a time; never throws.
    /// </summary>
    public async Task<AgentUpdateOutcome> UpdateAsync(string reason, string? targetVersionOverride, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
            return new AgentUpdateOutcome(AgentUpdateAction.Blocked, "An agent update is already running");
        try
        {
            var outcome = await CheckAsync(reason, targetVersionOverride, ct).ConfigureAwait(false);
            if (outcome.Action != AgentUpdateAction.UpdateAvailable) return outcome;
            var manifest = outcome.Manifest!;

            if (_coordinator.InstallInProgress)
            {
                _logger.LogInformation("Agent update to {Version} postponed: an application install is in progress", manifest.Version);
                return outcome with { Action = AgentUpdateAction.Blocked, Reason = "An application install is in progress; the agent update is postponed" };
            }
            if (File.Exists(MarkerPath) && ReadMarker() is { } pending && DateTimeOffset.UtcNow - pending.StartedUtc < MarkerTimeout)
                return outcome with { Action = AgentUpdateAction.Blocked, Reason = $"An update to {pending.ToVersion} started {pending.StartedUtc:u} is still in progress" };

            var directory = Path.Combine(UpdatesRoot, SafeName(manifest.Version));
            var zipPath = Path.Combine(directory, $"Arkimentum.AppMonitor-{SafeName(manifest.Version)}.zip");
            try
            {
                Directory.CreateDirectory(directory);
                var sha = await DownloadAsync(manifest, zipPath, ct).ConfigureAwait(false);
                if (!sha.Equals(manifest.Sha256?.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogError("Agent update: SHA-256 mismatch for {Package} - manifest says {Expected}, the download is {Actual}; the file is deleted and the update is abandoned",
                        manifest.PackageUrl, manifest.Sha256, sha);
                    TryDelete(zipPath);
                    return outcome with { Action = AgentUpdateAction.Failed, Reason = "SHA-256 of the downloaded package does not match the manifest" };
                }
                _logger.LogInformation("Agent update: downloaded {Package} ({Bytes} bytes) to {Path}; SHA-256 verified",
                    manifest.PackageUrl, new FileInfo(zipPath).Length, zipPath);

                Extract(zipPath, directory);
                var script = Path.Combine(directory, "Install-ArkimentumAppMonitor.ps1");
                var serviceExe = Path.Combine(directory, "Service", "Arkimentum.AppMonitor.Service.exe");
                if (!File.Exists(script) || !File.Exists(serviceExe))
                {
                    _logger.LogError("Agent update: the package {Path} does not contain Install-ArkimentumAppMonitor.ps1 and Service\\Arkimentum.AppMonitor.Service.exe; the update is abandoned", zipPath);
                    return outcome with { Action = AgentUpdateAction.Failed, Reason = "The release package is not a complete AppMonitor release" };
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agent update: downloading or extracting {Package} failed", manifest.PackageUrl);
                return outcome with { Action = AgentUpdateAction.Failed, Reason = "Download or extraction failed: " + ex.Message };
            }

            WriteMarker(new AgentUpdateMarker
            {
                FromVersion = CurrentVersion,
                ToVersion = manifest.Version,
                StartedUtc = DateTimeOffset.UtcNow,
                PackageDirectory = directory,
                Reason = reason,
            });
            CleanOldPackages(directory);

            if (_options.DryRun)
            {
                _logger.LogWarning("Agent update: TESTING MODE (--user-config) - the package for {Version} is verified in {Directory}, but the installer is NOT started. " +
                                   "The real service would now run: powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{Script}\" -SourceRoot \"{Directory}\" -NoSampleApps -SkipPrerequisites -Force",
                    manifest.Version, directory, Path.Combine(directory, "Install-ArkimentumAppMonitor.ps1"), directory);
                TryDelete(MarkerPath);
                return outcome with { Action = AgentUpdateAction.WouldLaunch, Reason = $"Verified {manifest.Version} in {directory}; the installer is not started in testing mode", PackageDirectory = directory };
            }

            if (!LaunchInstaller(directory, out var error))
            {
                TryDelete(MarkerPath);
                return outcome with { Action = AgentUpdateAction.Failed, Reason = "The installer could not be started: " + error, PackageDirectory = directory };
            }
            _coordinator.RecordEvent(ReportedEventKind.AgentUpdated, null, $"Agent update to {manifest.Version} started ({reason})", CurrentVersion, manifest.Version);
            return outcome with { Action = AgentUpdateAction.Launched, Reason = $"Installer for {manifest.Version} started; the service is about to restart", PackageDirectory = directory };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent update failed");
            return new AgentUpdateOutcome(AgentUpdateAction.Failed, ex.Message);
        }
        finally { _gate.Release(); }
    }

    private async Task<string> DownloadAsync(ReleaseManifest manifest, string zipPath, CancellationToken ct)
    {
        var origin = new Uri(manifest.PackageUrl, UriKind.Absolute).GetLeftPart(UriPartial.Authority);
        using var client = new CloudClient(_loggerFactory.CreateLogger<CloudClient>(), origin, _settings.Current.ProxyUrl, _options.Handler);
        var progress = new Progress<string>(line => _logger.LogDebug("Agent update: {Line}", line));
        _logger.LogInformation("Agent update: downloading {Package} to {Path}", manifest.PackageUrl, zipPath);
        return await client.DownloadAsync(manifest.PackageUrl, zipPath, progress, ct).ConfigureAwait(false);
    }

    private void Extract(string zipPath, string directory)
    {
        // Re-extract from scratch so a half-extracted earlier attempt cannot be launched.
        foreach (var sub in Directory.GetDirectories(directory)) Directory.Delete(sub, recursive: true);
        foreach (var file in Directory.GetFiles(directory).Where(f => !f.Equals(zipPath, StringComparison.OrdinalIgnoreCase))) TryDelete(file);
        ZipFile.ExtractToDirectory(zipPath, directory, overwriteFiles: true);
        _logger.LogInformation("Agent update: extracted {Zip} to {Directory}", Path.GetFileName(zipPath), directory);
    }

    /// <summary>
    /// Starts the release's installer detached and does not wait: it stops this service, mirrors the new binaries over
    /// the install folder and starts the service again. cmd.exe only provides the output redirection to install.log -
    /// redirecting the pipes here would break the moment this process is stopped by the script itself.
    /// </summary>
    private bool LaunchInstaller(string directory, out string? error)
    {
        var script = Path.Combine(directory, "Install-ArkimentumAppMonitor.ps1");
        var log = Path.Combine(directory, "install.log");
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell)) powershell = "powershell.exe";
        var arguments = $"/d /s /c \"\"{powershell}\" -NoProfile -ExecutionPolicy Bypass -File \"{script}\" -SourceRoot \"{directory}\" " +
                        $"-NoSampleApps -SkipPrerequisites -Force > \"{log}\" 2>&1\"";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = directory,
            };
            var process = Process.Start(psi);
            _logger.LogWarning("Agent update: started the installer (pid {Pid}) from {Directory}; this service will be stopped and replaced. Output goes to {Log}",
                process?.Id, directory, log);
            process?.Dispose();
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent update: could not start the installer from {Directory}", directory);
            error = ex.Message;
            return false;
        }
    }

    // =================================================================================================================
    // Marker + housekeeping
    // =================================================================================================================

    /// <summary>
    /// Called once at start: reports the outcome of an update that was launched before the last stop. A version that
    /// moved is logged (and reported) as AgentUpdated; a marker older than 30 minutes with an unchanged version is a
    /// failed update.
    /// </summary>
    public void ProcessStartupMarker()
    {
        var marker = ReadMarker();
        if (marker is null) return;
        if (VersionComparer.Compare(CurrentVersion, marker.ToVersion) >= 0 && !CurrentVersion.Equals(marker.FromVersion, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Agent updated: {From} -> {To} (started {Started:u}, {Reason})", marker.FromVersion, CurrentVersion, marker.StartedUtc, marker.Reason ?? "scheduled");
            _coordinator.RecordEvent(ReportedEventKind.AgentUpdated, null, $"Agent updated to {CurrentVersion}", marker.FromVersion, CurrentVersion);
            TryDelete(MarkerPath);
            return;
        }
        if (DateTimeOffset.UtcNow - marker.StartedUtc > MarkerTimeout)
        {
            _logger.LogError("Agent update to {To} failed: the agent is still {Current} more than {Minutes} minutes after the installer was started ({Started:u}). See {Log}",
                marker.ToVersion, CurrentVersion, (int)MarkerTimeout.TotalMinutes, marker.StartedUtc,
                marker.PackageDirectory is null ? "the AgentUpdates folder" : Path.Combine(marker.PackageDirectory, "install.log"));
            _coordinator.RecordEvent(ReportedEventKind.AgentUpdated, null, $"Agent update to {marker.ToVersion} failed; still running {CurrentVersion}", marker.FromVersion, marker.ToVersion);
            TryDelete(MarkerPath);
            return;
        }
        _logger.LogInformation("Agent update to {To} is still in progress (started {Started:u})", marker.ToVersion, marker.StartedUtc);
    }

    public AgentUpdateMarker? ReadMarker()
    {
        try
        {
            return File.Exists(MarkerPath) ? JsonSerializer.Deserialize<AgentUpdateMarker>(File.ReadAllText(MarkerPath), Json) : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Agent update: the marker file {Path} is unreadable and will be ignored", MarkerPath);
            TryDelete(MarkerPath);
            return null;
        }
    }

    private void WriteMarker(AgentUpdateMarker marker)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            File.WriteAllText(MarkerPath, JsonSerializer.Serialize(marker, Json));
            _logger.LogInformation("Agent update: {From} -> {To} recorded in {Path}", marker.FromVersion, marker.ToVersion, MarkerPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Agent update: could not write {Path}; the outcome will not be reported after the restart", MarkerPath);
        }
    }

    /// <summary>Keeps the two most recent package folders (plus the one being installed) and deletes the rest.</summary>
    private void CleanOldPackages(string? keep)
    {
        try
        {
            if (!Directory.Exists(UpdatesRoot)) return;
            var stale = new DirectoryInfo(UpdatesRoot).GetDirectories()
                .OrderByDescending(d => d.LastWriteTimeUtc)
                .Skip(KeepPackageFolders)
                .Where(d => keep is null || !d.FullName.Equals(keep, StringComparison.OrdinalIgnoreCase));
            foreach (var dir in stale)
            {
                try { dir.Delete(recursive: true); _logger.LogInformation("Agent update: removed the old package folder {Path}", dir.FullName); }
                catch (Exception ex) { _logger.LogDebug(ex, "Agent update: could not remove {Path}", dir.FullName); }
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Agent update: package cleanup failed"); }
    }

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) { _logger.LogDebug(ex, "Could not delete {Path}", path); }
    }

    private static string SafeName(string version) =>
        string.Concat(version.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    /// <summary>Computes the SHA-256 of a local file as lower-case hex (used by the tests and by --check-update diagnostics).</summary>
    public static string Sha256OfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
