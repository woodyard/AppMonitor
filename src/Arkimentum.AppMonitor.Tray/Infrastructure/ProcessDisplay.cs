using System.Diagnostics;
using System.Linq;
using Arkimentum.AppMonitor.Native;

namespace Arkimentum.AppMonitor.Tray.Infrastructure;

/// <summary>Turns a bare process name into something a user recognises.</summary>
public static class ProcessDisplay
{
    private const int MaxTitleLength = 70;

    /// <summary>
    /// The main window title of the process in this session when it has one, otherwise the file description from
    /// the executable, otherwise the process name itself.
    /// </summary>
    public static string Friendly(string processName)
    {
        var name = ProcessHelper.Normalize(processName);
        Process[] processes;
        try { processes = Process.GetProcessesByName(name); }
        catch { return name; }

        try
        {
            var mine = processes.Where(p => SafeSessionId(p) == AppInfo.SessionId).ToList();
            foreach (var p in mine)
            {
                var title = SafeTitle(p);
                if (!string.IsNullOrWhiteSpace(title)) return Truncate(title);
            }
            foreach (var p in mine)
            {
                var description = SafeDescription(p);
                if (!string.IsNullOrWhiteSpace(description)) return Truncate(description);
            }
        }
        catch { /* fall through to the process name */ }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }

        return name;
    }

    private static int SafeSessionId(Process p)
    {
        try { return p.SessionId; } catch { return -1; }
    }

    private static string? SafeTitle(Process p)
    {
        try { return p.MainWindowHandle == IntPtr.Zero ? null : p.MainWindowTitle; } catch { return null; }
    }

    private static string? SafeDescription(Process p)
    {
        try { return p.MainModule?.FileVersionInfo.FileDescription; } catch { return null; }
    }

    private static string Truncate(string text) =>
        text.Length <= MaxTitleLength ? text : text[..MaxTitleLength] + "…";
}
