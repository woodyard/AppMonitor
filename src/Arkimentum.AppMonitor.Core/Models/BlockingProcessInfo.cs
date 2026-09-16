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

    /// <summary>
    /// Full path of the executable (<c>MainModule.FileName</c>), when it could be read. Filled in for processes that
    /// survived or restarted a forced close, because "pwsh could not be closed" says nothing about whether the pwsh
    /// belongs to a scheduled task, an RMM agent or the user. Optional and additive; null everywhere else.
    /// </summary>
    public string? ExecutablePath { get; set; }

    /// <summary>
    /// Why this instance is in the way: the exception a forced close threw ("Win32Exception: Access is denied
    /// (Win32 error 5 / 0x00000005)"), or "restarted (pid 1234)" when the process came back under a new pid after
    /// it had been terminated. Optional and additive; null unless a close attempt actually failed.
    /// </summary>
    public string? Reason { get; set; }

    public BlockingProcessInfo Clone() => (BlockingProcessInfo)MemberwiseClone();

    /// <summary>"pwsh (pid 1234, session 2, CONTOSO\bob, elevated)" - used in logs and in install failure messages.</summary>
    public string Describe()
    {
        var parts = new List<string> { $"pid {ProcessId}", $"session {SessionId}" };
        if (!string.IsNullOrWhiteSpace(UserName)) parts.Add(UserName);
        if (Elevated == true) parts.Add("elevated");
        if (!string.IsNullOrWhiteSpace(ExecutablePath)) parts.Add(ExecutablePath!);
        var text = $"{ProcessName} ({string.Join(", ", parts)})";
        return string.IsNullOrWhiteSpace(Reason) ? text : $"{text}: {Reason}";
    }

    /// <summary>Joins several descriptions for a single log line or message.</summary>
    public static string Describe(IEnumerable<BlockingProcessInfo> processes) =>
        string.Join("; ", processes.Select(p => p.Describe()));

    /// <summary>
    /// Turns the exception a kill threw into the one line that goes on <see cref="Reason"/>: the exception type, its
    /// message and - the part that actually names the cause when SYSTEM cannot end a process - the Win32 error code.
    /// </summary>
    public static string DescribeFailure(Exception exception) =>
        exception is System.ComponentModel.Win32Exception win32
            ? $"{exception.GetType().Name}: {exception.Message} (Win32 error {win32.NativeErrorCode} / 0x{win32.NativeErrorCode:X8})"
            : $"{exception.GetType().Name}: {exception.Message}";

    /// <summary>"restarted (pid 1234)" - the process came back under a new pid after it was terminated.</summary>
    public static string RestartedReason(int processId) => $"restarted (pid {processId})";
}
