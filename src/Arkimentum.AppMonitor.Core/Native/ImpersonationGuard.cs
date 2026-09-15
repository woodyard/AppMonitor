using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Native;

/// <summary>
/// Detects a thread that is still impersonating a pipe client (or anyone else) inside the SYSTEM service, and puts
/// it back on the process identity.
///
/// <para>
/// Why this exists: on one fleet device the service, running as SYSTEM with a clean ACL on its state folder, was
/// refused access to state.json and to the App Installer package folder from the moment it handled the first
/// "install now" from the tray - every later scan and install on that machine failed the same way. The only
/// explanation that fits a SYSTEM process being told "access denied" on files SYSTEM owns is a thread token that is
/// not SYSTEM's. Thread-pool threads are reused by unrelated work, so one leaked impersonation poisons the whole
/// process. This guard makes the leak visible in the log and undoes it.
/// </para>
/// </summary>
public static class ImpersonationGuard
{
    private static readonly bool ProcessIsSystem = DetectSystem();

    private static bool DetectSystem()
    {
        try { using var id = WindowsIdentity.GetCurrent(TokenAccessLevels.Query); return id.IsSystem; }
        catch { return false; }
    }

    /// <summary>
    /// Returns true when the current thread carries an impersonation token although the process runs as SYSTEM,
    /// after logging who it impersonated and reverting to the process identity. Returns false when nothing was wrong.
    /// Never throws.
    /// </summary>
    public static bool RevertIfImpersonating(ILogger logger, string where)
    {
        if (!ProcessIsSystem) return false;
        try
        {
            using var thread = WindowsIdentity.GetCurrent(ifImpersonating: true);
            if (thread is null) return false;   // not impersonating

            var name = thread.Name;
            var level = thread.ImpersonationLevel;
            var reverted = RevertToSelf();
            logger.LogWarning("Thread {ThreadId} was still impersonating {Identity} ({Level}) at {Where}; reverted to the process identity: {Reverted}",
                Environment.CurrentManagedThreadId, name, level, where, reverted);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Impersonation check at {Where} failed", where);
            return false;
        }
    }

    /// <summary>The identity the current thread would use for file access right now, for diagnostics.</summary>
    public static string DescribeCurrentIdentity()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return $"{id.Name} (impersonation {id.ImpersonationLevel}, thread {Environment.CurrentManagedThreadId})";
        }
        catch (Exception ex)
        {
            return $"unknown ({ex.Message})";
        }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RevertToSelf();
}
