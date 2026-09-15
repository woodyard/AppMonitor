namespace Arkimentum.AppMonitor.Models;

/// <summary>
/// One running instance of a process that blocks an update, with the detail needed to explain why a tray agent
/// could not close it: a process that runs elevated, or in another user's session, is out of a medium-integrity
/// tray's reach and only the service - running as LocalSystem - can terminate it.
/// Added after 1.1.1 and optional throughout: an older service never fills it in and every reader copes.
/// </summary>
public sealed class BlockingProcessInfo
{
    public string ProcessName { get; set; } = string.Empty;

    public int ProcessId { get; set; }

    /// <summary>Terminal Services session the process runs in; -1 when it could not be read.</summary>
    public int SessionId { get; set; }

    /// <summary>DOMAIN\user that owns the process, when the caller was allowed to open its token; null otherwise.</summary>
    public string? UserName { get; set; }

    /// <summary>True or false when the process token could be read, null when it could not.</summary>
    public bool? Elevated { get; set; }

    public BlockingProcessInfo Clone() => (BlockingProcessInfo)MemberwiseClone();

    /// <summary>"pwsh (pid 1234, session 2, CONTOSO\bob, elevated)" - used in logs and in install failure messages.</summary>
    public string Describe()
    {
        var parts = new List<string> { $"pid {ProcessId}", $"session {SessionId}" };
        if (!string.IsNullOrWhiteSpace(UserName)) parts.Add(UserName);
        if (Elevated == true) parts.Add("elevated");
        return $"{ProcessName} ({string.Join(", ", parts)})";
    }

    /// <summary>Joins several descriptions for a single log line or message.</summary>
    public static string Describe(IEnumerable<BlockingProcessInfo> processes) =>
        string.Join("; ", processes.Select(p => p.Describe()));
}
