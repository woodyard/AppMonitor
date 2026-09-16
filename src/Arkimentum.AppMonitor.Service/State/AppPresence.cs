using Arkimentum.AppMonitor.Models;

namespace Arkimentum.AppMonitor.Service.State;

/// <summary>
/// What the last completed check concluded about one configured application in one context: is it on this machine,
/// at which version, and - for a per-user install - for which user. Persisted with the rest of the service state so
/// a restart does not make every monitored application look relevant again until the next scan.
/// </summary>
public sealed class AppPresence
{
    public string AppId { get; set; } = string.Empty;

    /// <summary>The display name as configured when the check ran; only a fallback for the current configuration's name.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Resolved to System or User (never Auto) - the context the check ran in.</summary>
    public InstallContext Context { get; set; }

    /// <summary>Owner SID for a user-context check; null for a machine-wide one.</summary>
    public string? UserSid { get; set; }

    /// <summary>True when the provider found the application installed in that context.</summary>
    public bool Installed { get; set; }

    public string? InstalledVersion { get; set; }

    public DateTimeOffset CheckedUtc { get; set; }

    /// <summary>Same shape as <see cref="PendingUpdate.Key"/>, so one application/context/user has exactly one entry.</summary>
    public string Key => PendingUpdate.MakeKey(AppId, Context, UserSid);

    public static string MakeKey(string appId, InstallContext context, string? userSid) =>
        PendingUpdate.MakeKey(appId, context, userSid);
}

/// <summary>
/// Decides which of the configured applications are worth showing to one signed-in user. Pure and static so the
/// rule - machine-wide installs for everyone, per-user installs only for their own user - is testable without a
/// service, a registry or a pipe.
/// </summary>
public static class MonitoredAppFilter
{
    /// <summary>
    /// The display names of the enabled applications that were found installed for <paramref name="userSid"/>, sorted.
    /// Returns every enabled application when <paramref name="presence"/> holds nothing yet: before the first scan has
    /// produced results we know nothing, and an empty "Monitored applications" panel would read as a broken agent.
    /// </summary>
    public static List<string> Relevant(IEnumerable<AppPolicy> enabledApps, IEnumerable<AppPresence> presence, string? userSid)
    {
        var apps = enabledApps.ToList();
        var known = presence.ToList();
        if (known.Count == 0) return All(apps);

        var installedIds = known
            .Where(p => p.Installed && IsVisibleTo(p, userSid))
            .Select(p => p.AppId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return apps
            .Where(a => installedIds.Contains(a.AppId))
            .Select(Name)
            .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Every enabled application, sorted - the pre-1.2 behaviour and the fallback before the first scan.</summary>
    public static List<string> All(IEnumerable<AppPolicy> enabledApps) =>
        enabledApps.Select(Name).OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>A machine-wide result belongs to everyone; a per-user result only to the user it was checked for.</summary>
    public static bool IsVisibleTo(AppPresence presence, string? userSid) =>
        presence.Context != InstallContext.User || string.Equals(presence.UserSid, userSid, StringComparison.OrdinalIgnoreCase);

    private static string Name(AppPolicy app) => string.IsNullOrWhiteSpace(app.DisplayName) ? app.AppId : app.DisplayName;
}
