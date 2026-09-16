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

    /// <summary>
    /// Enables SeDebugPrivilege on the process token, which lets the service terminate any process regardless of
    /// its DACL (a scheduled task's or another service's pwsh, for example). SYSTEM holds the privilege but it is
    /// disabled by default; without it OpenProcess(PROCESS_TERMINATE) on such a process fails with error 5.
    /// </summary>
    public static void EnableDebugPrivilege(ILogger logger)
    {
        if (!ProcessIsSystem) return;
        try
        {
            if (!OpenProcessToken(System.Diagnostics.Process.GetCurrentProcess().Handle, TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
            {
                logger.LogDebug("OpenProcessToken failed ({Error}); SeDebugPrivilege not enabled", Marshal.GetLastWin32Error());
                return;
            }
            using (token)
            {
                if (!LookupPrivilegeValue(null, "SeDebugPrivilege", out var luid)) return;
                var privileges = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
                if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero) || Marshal.GetLastWin32Error() != 0)
                    logger.LogDebug("AdjustTokenPrivileges(SeDebugPrivilege) failed ({Error})", Marshal.GetLastWin32Error());
                else
                    logger.LogInformation("SeDebugPrivilege enabled for the service process");
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Enabling SeDebugPrivilege failed");
        }
    }

    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID Luid; public uint Attributes; }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out Microsoft.Win32.SafeHandles.SafeAccessTokenHandle tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(Microsoft.Win32.SafeHandles.SafeAccessTokenHandle tokenHandle, bool disableAllPrivileges,
        ref TOKEN_PRIVILEGES newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RevertToSelf();
}
