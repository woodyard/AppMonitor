using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Arkimentum.AppMonitor.Native;

/// <summary>P/Invoke surface used by the service (session enumeration, launching the tray agent) and the pipe server.</summary>
public static partial class NativeMethods
{
    public const int WTS_CURRENT_SERVER_HANDLE = 0;

    public enum WTS_CONNECTSTATE_CLASS
    {
        WTSActive, WTSConnected, WTSConnectQuery, WTSShadow, WTSDisconnected, WTSIdle, WTSListen, WTSReset, WTSDown, WTSInit,
    }

    public enum WTS_INFO_CLASS
    {
        WTSInitialProgram, WTSApplicationName, WTSWorkingDirectory, WTSOEMId, WTSSessionId, WTSUserName, WTSWinStationName,
        WTSDomainName, WTSConnectState, WTSClientBuildNumber, WTSClientName, WTSClientDirectory, WTSClientProductId,
        WTSClientHardwareId, WTSClientAddress, WTSClientDisplay, WTSClientProtocolType, WTSIdleTime, WTSLogonTime,
        WTSIncomingBytes, WTSOutgoingBytes, WTSIncomingFrames, WTSOutgoingFrames, WTSClientInfo, WTSSessionInfo,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WTS_SESSION_INFO
    {
        public int SessionId;
        public IntPtr pWinStationName;
        public WTS_CONNECTSTATE_CLASS State;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    public enum TOKEN_TYPE { TokenPrimary = 1, TokenImpersonation = 2 }
    public enum SECURITY_IMPERSONATION_LEVEL { SecurityAnonymous, SecurityIdentification, SecurityImpersonation, SecurityDelegation }

    public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    public const uint CREATE_NEW_CONSOLE = 0x00000010;
    public const uint CREATE_NO_WINDOW = 0x08000000;
    public const uint NORMAL_PRIORITY_CLASS = 0x00000020;
    public const uint TOKEN_ALL_ACCESS = 0xF01FF;

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WTSEnumerateSessionsW(IntPtr hServer, int reserved, int version, out IntPtr ppSessionInfo, out int pCount);

    [LibraryImport("wtsapi32.dll")]
    public static partial void WTSFreeMemory(IntPtr pMemory);

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WTSQueryUserToken(int sessionId, out SafeAccessTokenHandle phToken);

    [LibraryImport("wtsapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WTSQuerySessionInformationW(IntPtr hServer, int sessionId, WTS_INFO_CLASS infoClass, out IntPtr ppBuffer, out int pBytesReturned);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial int WTSGetActiveConsoleSessionId();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out int clientSessionId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out int clientProcessId);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DuplicateTokenEx(SafeAccessTokenHandle hExistingToken, uint dwDesiredAccess, IntPtr lpTokenAttributes,
        SECURITY_IMPERSONATION_LEVEL impersonationLevel, TOKEN_TYPE tokenType, out SafeAccessTokenHandle phNewToken);

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateEnvironmentBlock(out IntPtr lpEnvironment, SafeAccessTokenHandle hToken, [MarshalAs(UnmanagedType.Bool)] bool bInherit);

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateProcessAsUserW(SafeAccessTokenHandle hToken, string? lpApplicationName, string? lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment,
        string? lpCurrentDirectory, ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(IntPtr hObject);

    public const uint TOKEN_QUERY = 0x0008;

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out SafeAccessTokenHandle tokenHandle);

    // ------------------------------------------------------------------ helpers

    public static int? GetNamedPipeClientSessionId(SafePipeHandle pipe) =>
        GetNamedPipeClientSessionId(pipe, out var id) ? id : null;

    public sealed record SessionInfo(int SessionId, string StationName, WTS_CONNECTSTATE_CLASS State, string? UserName, string? DomainName)
    {
        public bool IsInteractiveUser => !string.IsNullOrEmpty(UserName) && State is WTS_CONNECTSTATE_CLASS.WTSActive or WTS_CONNECTSTATE_CLASS.WTSConnected or WTS_CONNECTSTATE_CLASS.WTSDisconnected;
        public bool IsActive => State == WTS_CONNECTSTATE_CLASS.WTSActive;
        public string QualifiedUser => string.IsNullOrEmpty(DomainName) ? UserName ?? string.Empty : $"{DomainName}\\{UserName}";
    }

    public static IReadOnlyList<SessionInfo> EnumerateSessions()
    {
        var result = new List<SessionInfo>();
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var buffer, out var count)) return result;
        try
        {
            var size = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(buffer + i * size);
                var station = Marshal.PtrToStringUni(info.pWinStationName) ?? string.Empty;
                if (station.Equals("Services", StringComparison.OrdinalIgnoreCase)) continue;
                var user = QuerySessionString(info.SessionId, WTS_INFO_CLASS.WTSUserName);
                var domain = QuerySessionString(info.SessionId, WTS_INFO_CLASS.WTSDomainName);
                result.Add(new SessionInfo(info.SessionId, station, info.State, user, domain));
            }
        }
        finally { WTSFreeMemory(buffer); }
        return result;
    }

    public static string? QuerySessionString(int sessionId, WTS_INFO_CLASS infoClass)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, sessionId, infoClass, out var buffer, out var bytes)) return null;
        try { return bytes > 2 ? Marshal.PtrToStringUni(buffer) : null; }
        finally { WTSFreeMemory(buffer); }
    }

    /// <summary>Returns the SID of the user logged on to <paramref name="sessionId"/>. Requires SYSTEM.</summary>
    public static string? GetSessionUserSid(int sessionId)
    {
        if (!WTSQueryUserToken(sessionId, out var token)) return null;
        using (token)
        {
            try
            {
                using var identity = new WindowsIdentity(token.DangerousGetHandle());
                return identity.User?.Value;
            }
            catch { return null; }
        }
    }

    /// <summary>Launches <paramref name="commandLine"/> in the interactive session as the logged-on user. Requires SYSTEM.</summary>
    public static int LaunchInSession(int sessionId, string applicationPath, string? arguments, string? workingDirectory)
    {
        if (!WTSQueryUserToken(sessionId, out var userToken))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "WTSQueryUserToken failed");
        using (userToken)
        {
            if (!DuplicateTokenEx(userToken, TOKEN_ALL_ACCESS, IntPtr.Zero, SECURITY_IMPERSONATION_LEVEL.SecurityIdentification, TOKEN_TYPE.TokenPrimary, out var primary))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx failed");
            using (primary)
            {
                var env = IntPtr.Zero;
                try
                {
                    CreateEnvironmentBlock(out env, primary, false);
                    var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = Marshal.StringToHGlobalUni(@"winsta0\default") };
                    try
                    {
                        var cmd = string.IsNullOrEmpty(arguments) ? $"\"{applicationPath}\"" : $"\"{applicationPath}\" {arguments}";
                        if (!CreateProcessAsUserW(primary, null, cmd, IntPtr.Zero, IntPtr.Zero, false,
                                CREATE_UNICODE_ENVIRONMENT | NORMAL_PRIORITY_CLASS, env, workingDirectory, ref si, out var pi))
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser failed");
                        CloseHandle(pi.hThread);
                        CloseHandle(pi.hProcess);
                        return pi.dwProcessId;
                    }
                    finally { Marshal.FreeHGlobal(si.lpDesktop); }
                }
                finally { if (env != IntPtr.Zero) DestroyEnvironmentBlock(env); }
            }
        }
    }
}
