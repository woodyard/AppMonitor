using Arkimentum.AppMonitor.Native;

namespace Arkimentum.AppMonitor.Models;

/// <summary>
/// What the close-apps dialog needs to know about one blocking process *name*, folded together from the per-instance
/// detail the service (running as LocalSystem) could read: does any instance run elevated, does any run in a session
/// this agent does not own, and who owns it.
///
/// It lives in Core rather than in the tray's view model for one reason: this is the logic that decides what the
/// dialog says, and it has to survive every shape the service can hand it - no detail at all (an older service), an
/// empty list, an entry whose session or owner could not be read. A null slipping through here used to throw inside
/// the WPF dispatcher, where the tray's unhandled-exception handler swallowed it and left a half-drawn window.
/// </summary>
public sealed record BlockingProcessSummary(bool IsElevated, bool IsInAnotherSession, string? OtherSessionUser)
{
    /// <summary>True when only the service can end this one; the dialog has to say so or the user retries forever.</summary>
    public bool NeedsService => IsElevated || IsInAnotherSession;

    /// <summary>Nothing readable about this process: the dialog shows the bare name with no markers.</summary>
    public static BlockingProcessSummary None { get; } = new(false, false, null);

    /// <summary>
    /// Folds <paramref name="details"/> down to the markers for <paramref name="processName"/>. Tolerates a null or
    /// empty list, null entries, and instances whose session id could not be read (-1, which is never "another
    /// session": pretending an unreadable process belongs to someone else would be a lie in the dialog).
    /// </summary>
    public static BlockingProcessSummary For(string? processName, IEnumerable<BlockingProcessInfo?>? details, int currentSessionId)
    {
        if (string.IsNullOrWhiteSpace(processName)) return None;

        var mine = (details ?? [])
            .Where(d => d is not null && NameMatches(d!.ProcessName, processName!))
            .Select(d => d!)
            .ToList();
        if (mine.Count == 0) return None;

        var elevated = mine.Any(d => d.Elevated == true);
        var otherSession = mine.Where(d => d.SessionId >= 0 && d.SessionId != currentSessionId).ToList();
        var user = otherSession.Select(d => d.UserName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
        return new BlockingProcessSummary(elevated, otherSession.Count > 0, string.IsNullOrWhiteSpace(user) ? null : user);
    }

    /// <summary>"pwsh" and "pwsh.exe" are the same process to everyone except a string comparison.</summary>
    public static bool NameMatches(string? a, string? b) =>
        a is not null && b is not null &&
        string.Equals(ProcessHelper.Normalize(a), ProcessHelper.Normalize(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The process names the dialog should list for an update: the ones actually seen running, else the configured
    /// set (which is what an older service, or a prompt built before the first detection, leaves us with).
    /// </summary>
    public static IReadOnlyList<string> NamesFor(PendingUpdate? update) =>
        update?.BlockingProcesses is { Count: > 0 } running
            ? [.. running.Where(n => !string.IsNullOrWhiteSpace(n))]
            : [.. (update?.ProcessNames ?? []).Where(n => !string.IsNullOrWhiteSpace(n))];

    /// <summary>
    /// A signature of the running instances, so the dialog only rebuilds its rows when something actually moved.
    /// It covers the markers too - an instance appearing in another session changes what the dialog must say.
    /// </summary>
    public static string SignatureFor(IEnumerable<BlockingProcessInfo?>? details) =>
        string.Join("|", (details ?? [])
            .Where(d => d is not null)
            .Select(d => $"{d!.ProcessName}:{d.ProcessId}:{d.SessionId}:{d.Elevated}"));
}
