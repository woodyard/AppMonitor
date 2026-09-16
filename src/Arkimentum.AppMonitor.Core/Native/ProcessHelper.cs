using System.Diagnostics;
using System.Security.Principal;
using Arkimentum.AppMonitor.Models;

namespace Arkimentum.AppMonitor.Native;

/// <summary>Process discovery and graceful/forced termination, optionally scoped to one Terminal Services session.</summary>
public static class ProcessHelper
{
    /// <summary>What a <see cref="KillAsync"/> pass actually achieved.</summary>
    /// <param name="Killed">Processes that are gone because we terminated them.</param>
    /// <param name="Survivors">
    /// Processes that are still running; the caller must not pretend the install can proceed. Each carries the
    /// <see cref="BlockingProcessInfo.Reason"/> the kill failed with and, where readable, its executable path.
    /// </param>
    /// <param name="Restarted">
    /// Instances that appeared under a NEW pid while we were killing the old ones - a service, a scheduled task or a
    /// supervisor brought the process straight back. Terminating the old pid succeeded, so without this the caller
    /// would start the install into a process that is still holding the files.
    /// </param>
    public sealed record KillOutcome(
        IReadOnlyList<BlockingProcessInfo> Killed,
        IReadOnlyList<BlockingProcessInfo> Survivors,
        IReadOnlyList<BlockingProcessInfo> Restarted)
    {
        /// <summary>True when everything that was in the way is really gone.</summary>
        public bool Cleared => Survivors.Count == 0 && Restarted.Count == 0;

        /// <summary>Everything that still blocks the install, survivors first.</summary>
        public IReadOnlyList<BlockingProcessInfo> Blocking => [.. Survivors, .. Restarted];
    }

    /// <summary>Returns the configured process names that are currently running (optionally only in <paramref name="sessionId"/>).</summary>
    public static IReadOnlyList<string> GetRunning(IEnumerable<string> processNames, int? sessionId = null)
    {
        var wanted = new HashSet<string>(processNames.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0) return [];
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (!wanted.Contains(p.ProcessName)) continue;
                if (sessionId is { } s && p.SessionId != s) continue;
                found.Add(p.ProcessName);
            }
            catch { }
            finally { p.Dispose(); }
        }
        return wanted.Where(found.Contains).ToList();
    }

    /// <summary>
    /// Like <see cref="GetRunning"/>, but one entry per running instance with pid, session, owner and elevation.
    /// The owner and the elevation flag are only readable when the caller may open the process token - SYSTEM can
    /// read every process, a medium-integrity tray agent cannot read an elevated one - so both are optional.
    /// </summary>
    public static IReadOnlyList<BlockingProcessInfo> GetRunningDetails(IEnumerable<string> processNames, int? sessionId = null)
    {
        var wanted = new HashSet<string>(processNames.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0) return [];
        var result = new List<BlockingProcessInfo>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (!wanted.Contains(p.ProcessName)) continue;
                if (sessionId is { } s && p.SessionId != s) continue;
                result.Add(Inspect(p));
            }
            catch { }
            finally { p.Dispose(); }
        }
        return result.OrderBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.ProcessId).ToList();
    }

    /// <summary>
    /// Asks the processes to close (WM_CLOSE to the main window), waits, then kills what is left when <paramref name="force"/>.
    /// Returns the names that are still running afterwards.
    /// </summary>
    public static async Task<IReadOnlyList<string>> CloseAsync(IEnumerable<string> processNames, int? sessionId, TimeSpan gracefulWait, bool force, CancellationToken ct)
    {
        var wanted = new HashSet<string>(processNames.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        var targets = Collect(wanted, sessionId);

        try
        {
            foreach (var p in targets)
            {
                try { p.CloseMainWindow(); } catch { }
            }

            var deadline = DateTime.UtcNow + gracefulWait;
            while (DateTime.UtcNow < deadline && targets.Any(IsRunning))
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
            }

            if (force)
            {
                foreach (var p in targets.Where(IsRunning))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                }
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
            return targets.Where(IsRunning).Select(p => p.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        finally
        {
            foreach (var p in targets) p.Dispose();
        }
    }

    /// <summary>
    /// Terminates the matching processes outright (no WM_CLOSE: console processes and processes in other sessions
    /// have no window to close), waits <paramref name="settleWait"/> for the kernel to catch up, then reports what
    /// died and what survived. Only ever called from the service, which runs as SYSTEM and can therefore reach
    /// elevated processes and other users' sessions.
    /// </summary>
    public static async Task<KillOutcome> KillAsync(IEnumerable<string> processNames, int? sessionId, TimeSpan settleWait, CancellationToken ct)
    {
        var wanted = new HashSet<string>(processNames.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        var killed = new List<BlockingProcessInfo>();
        var survivors = new List<BlockingProcessInfo>();
        var restarted = new List<BlockingProcessInfo>();
        if (wanted.Count == 0) return new KillOutcome(killed, survivors, restarted);

        var targets = Collect(wanted, sessionId);
        // Every pid we saw before the kill; anything outside this set afterwards is a NEW instance, not a survivor.
        var seenPids = new HashSet<int>();
        try
        {
            var attempted = new List<(Process Process, BlockingProcessInfo Info)>();
            foreach (var p in targets)
            {
                var info = Inspect(p);
                seenPids.Add(info.ProcessId);
                try
                {
                    p.Kill(entireProcessTree: true);
                    attempted.Add((p, info));
                }
                catch (InvalidOperationException) { killed.Add(info); } // exited between enumeration and the kill
                catch (Exception ex)
                {
                    // Why SYSTEM could not end it is the whole point of the log line: a protected process, a token
                    // the kernel refuses, an access-denied from a driver. Without the code nobody can act on it.
                    info.Reason = BlockingProcessInfo.DescribeFailure(ex);
                    info.ExecutablePath ??= SafeExecutablePath(p);
                    survivors.Add(info);
                }
            }

            if (attempted.Count > 0 && settleWait > TimeSpan.Zero)
                await Task.Delay(settleWait, ct).ConfigureAwait(false);

            foreach (var (process, info) in attempted)
            {
                if (IsRunning(process))
                {
                    info.Reason ??= "still running after it was terminated";
                    info.ExecutablePath ??= SafeExecutablePath(process);
                    survivors.Add(info);
                }
                else killed.Add(info);
            }
        }
        finally
        {
            foreach (var p in targets) p.Dispose();
        }

        // A process a supervisor restarts comes back under a new pid, and the old pid really is gone - so the kill
        // "succeeded" while the files are still held. Look again in the same scope and say so instead.
        foreach (var info in GetRunningDetails(wanted, sessionId))
        {
            if (seenPids.Contains(info.ProcessId)) continue;
            if (survivors.Any(s => s.ProcessId == info.ProcessId)) continue;
            info.Reason = BlockingProcessInfo.RestartedReason(info.ProcessId);
            info.ExecutablePath ??= SafeExecutablePath(info.ProcessId);
            restarted.Add(info);
        }

        return new KillOutcome(killed, survivors, restarted);
    }

    /// <summary>
    /// One line naming everything that still blocks <paramref name="displayName"/> after a forced close, for the log,
    /// the reported event and the install failure the user sees. Pure, so the wording is testable.
    /// </summary>
    public static string DescribeFailure(KillOutcome outcome, string displayName)
    {
        var parts = new List<string>(2);
        if (outcome.Survivors.Count > 0) parts.Add($"Could not close {BlockingProcessInfo.Describe(outcome.Survivors)}");
        if (outcome.Restarted.Count > 0) parts.Add($"{BlockingProcessInfo.Describe(outcome.Restarted)} started again");
        return parts.Count == 0
            ? $"{displayName} was not updated."
            : $"{string.Join("; ", parts)}; {displayName} was not updated.";
    }

    /// <summary>Opens every live process whose name matches, optionally narrowed to one session. The caller disposes them.</summary>
    private static List<Process> Collect(HashSet<string> wanted, int? sessionId)
    {
        var targets = new List<Process>();
        if (wanted.Count == 0) return targets;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (wanted.Contains(p.ProcessName) && (sessionId is null || p.SessionId == sessionId)) targets.Add(p);
                else p.Dispose();
            }
            catch { p.Dispose(); }
        }
        return targets;
    }

    /// <summary>Reads what we are allowed to read about a process; anything the token refuses stays null.</summary>
    private static BlockingProcessInfo Inspect(Process p)
    {
        var info = new BlockingProcessInfo { ProcessName = p.ProcessName, ProcessId = p.Id, SessionId = -1 };
        try { info.SessionId = p.SessionId; } catch { }
        try
        {
            if (NativeMethods.OpenProcessToken(p.Handle, NativeMethods.TOKEN_QUERY, out var token))
            {
                using (token)
                using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
                {
                    info.UserName = identity.Name;
                    // A filtered (non-elevated) administrator token carries the Administrators group as deny-only,
                    // so this is elevation rather than plain group membership.
                    info.Elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
        }
        catch { /* not readable from this context - leave UserName/Elevated unset */ }
        return info;
    }

    private static bool IsRunning(Process p)
    {
        try { p.Refresh(); return !p.HasExited; } catch { return false; }
    }

    /// <summary>
    /// The executable behind a process, or null when it cannot be read (an exited process, a protected one, or a
    /// bitness mismatch). Never throws: it exists purely so a log line can say which pwsh this actually was.
    /// </summary>
    private static string? SafeExecutablePath(Process p)
    {
        try { return p.MainModule?.FileName; } catch { return null; }
    }

    private static string? SafeExecutablePath(int processId)
    {
        try
        {
            using var p = Process.GetProcessById(processId);
            return SafeExecutablePath(p);
        }
        catch { return null; }
    }

    public static string Normalize(string name)
    {
        var n = name.Trim();
        return n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;
    }
}
