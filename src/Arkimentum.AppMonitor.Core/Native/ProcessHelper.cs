using System.Diagnostics;

namespace Arkimentum.AppMonitor.Native;

/// <summary>Process discovery and graceful/forced termination, optionally scoped to one Terminal Services session.</summary>
public static class ProcessHelper
{
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
    /// Asks the processes to close (WM_CLOSE to the main window), waits, then kills what is left when <paramref name="force"/>.
    /// Returns the names that are still running afterwards.
    /// </summary>
    public static async Task<IReadOnlyList<string>> CloseAsync(IEnumerable<string> processNames, int? sessionId, TimeSpan gracefulWait, bool force, CancellationToken ct)
    {
        var wanted = new HashSet<string>(processNames.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        var targets = new List<Process>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (wanted.Contains(p.ProcessName) && (sessionId is null || p.SessionId == sessionId)) targets.Add(p);
                else p.Dispose();
            }
            catch { p.Dispose(); }
        }

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

    private static bool IsRunning(Process p)
    {
        try { p.Refresh(); return !p.HasExited; } catch { return false; }
    }

    public static string Normalize(string name)
    {
        var n = name.Trim();
        return n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;
    }
}
