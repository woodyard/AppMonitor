using System.Text.Json;
using System.Text.Json.Serialization;
using Arkimentum.AppMonitor.Models;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Service.State;

public sealed class ServiceState
{
    public Dictionary<string, PendingUpdate> Updates { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset? LastScanUtc { get; set; }
    public DateTimeOffset? NextScanUtc { get; set; }
    public string? LastScanSummary { get; set; }

    /// <summary>
    /// What the last check found for every configured application, keyed like <see cref="PendingUpdate.Key"/>
    /// (app, context and - for per-user installs - the user's SID). Added after 1.1.4 and additive: a state file
    /// written by an older service simply has none, and the first scan fills it in. It is what lets the tray list
    /// only the applications that actually exist on this device instead of the whole configuration.
    /// </summary>
    public Dictionary<string, AppPresence> AppPresence { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Finished install attempts, newest first, at most <see cref="State.InstallHistory.MaxEntries"/> (added after
    /// 1.1.20 and additive: an older state file has none). Kept apart from <see cref="Updates"/>, whose installed entries
    /// are purged after the retention period. Replaced as a whole on every append, never edited in place, because the
    /// state message is built from it on pipe threads without the policy lock.
    /// </summary>
    public List<InstallHistoryEntry> InstallHistory { get; set; } = [];

    /// <summary>
    /// Set once the history has been rebuilt from the service log (for installs made before the history existed), so
    /// the backfill runs a single time per device.
    /// </summary>
    public bool InstallHistoryBackfilled { get; set; }
}

/// <summary>Persists <see cref="ServiceState"/> as JSON (atomic replace) so deadlines and deferrals survive restarts.</summary>
public sealed class StateStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ILogger<StateStore> _logger;
    private readonly object _lock = new();

    public StateStore(ILogger<StateStore> logger) => _logger = logger;

    public static string PathFor(AgentSettings settings) => Path.Combine(settings.StateDirectory, "state.json");

    public ServiceState Load(AgentSettings settings)
    {
        var path = PathFor(settings);
        try
        {
            if (!File.Exists(path)) return new ServiceState();
            var json = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize<ServiceState>(json, Json) ?? new ServiceState();
            state.Updates = new Dictionary<string, PendingUpdate>(state.Updates, StringComparer.OrdinalIgnoreCase);
            state.AppPresence = new Dictionary<string, AppPresence>(state.AppPresence, StringComparer.OrdinalIgnoreCase);
            state.InstallHistory = InstallHistory.Normalize(state.InstallHistory);
            // An install that was in flight when the service stopped cannot be trusted.
            foreach (var u in state.Updates.Values.Where(u => u.State == UpdateState.Installing)) u.State = UpdateState.Available;
            _logger.LogInformation("Loaded state from {Path}: {Count} tracked update(s), {Presence} known application(s)",
                path, state.Updates.Count, state.AppPresence.Count);
            return state;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load state from {Path}; starting empty", path);
            return new ServiceState();
        }
    }

    public void Save(AgentSettings settings, ServiceState state)
    {
        var path = PathFor(settings);
        lock (_lock)
        {
            var json = JsonSerializer.Serialize(state, Json);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, json);
                try
                {
                    File.Move(tmp, path, overwrite: true);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    // Replacing the file can be refused while the old one is open elsewhere or carries the read-only
                    // attribute (seen after a self-update: every save failed with "access denied" although the same
                    // account had written the file minutes before, and the ACL was correct). Losing deadlines and
                    // deferrals is worse than a non-atomic write, so fall back to writing in place - after making sure
                    // this thread really is the service account and saying what the file looks like.
                    var wasImpersonating = Arkimentum.AppMonitor.Native.ImpersonationGuard.RevertIfImpersonating(_logger, "state save");
                    Describe(path, ex, wasImpersonating);
                    if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
                    if (wasImpersonating) File.Move(tmp, path, overwrite: true);
                    else
                    {
                        File.WriteAllText(path, json);
                        try { File.Delete(tmp); } catch { /* best effort */ }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not save state to {Path}", path);
            }
        }
    }

    private bool _describedReplaceFailure;

    /// <summary>Logs, once per process, why the atomic replace was refused: attributes, owner and access rules of the file.</summary>
    private void Describe(string path, Exception ex, bool wasImpersonating)
    {
        if (_describedReplaceFailure) return;
        _describedReplaceFailure = true;
        try
        {
            var info = new FileInfo(path);
            var security = info.GetAccessControl();
            var owner = security.GetOwner(typeof(System.Security.Principal.NTAccount))?.Value ?? "?";
            var rules = security.GetAccessRules(true, true, typeof(System.Security.Principal.NTAccount))
                .Cast<System.Security.AccessControl.FileSystemAccessRule>()
                .Select(r => $"{r.IdentityReference.Value}:{r.AccessControlType}:{r.FileSystemRights}");
            _logger.LogWarning(ex, "Replacing {Path} was refused ({Message}); {Action}. Thread identity now: {Identity}; was impersonating: {Impersonating}. Attributes={Attributes}, owner={Owner}, rules=[{Rules}]",
                path, ex.Message, wasImpersonating ? "retrying as the service account" : "writing it in place instead",
                Arkimentum.AppMonitor.Native.ImpersonationGuard.DescribeCurrentIdentity(), wasImpersonating, info.Attributes, owner, string.Join("; ", rules));
        }
        catch (Exception inner)
        {
            _logger.LogWarning(ex, "Replacing {Path} was refused ({Message}); writing it in place instead (details unavailable: {Inner})", path, ex.Message, inner.Message);
        }
    }
}
