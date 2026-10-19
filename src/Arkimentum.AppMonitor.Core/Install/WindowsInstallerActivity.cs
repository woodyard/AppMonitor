using System.Diagnostics;
using System.Management;
using System.Security.AccessControl;

namespace Arkimentum.AppMonitor.Install;

/// <summary>One msiexec.exe process that may hold Windows Installer, for the log (see <see cref="WindowsInstallerActivity.FindInstallers"/>).</summary>
/// <param name="ProcessId">Its pid.</param>
/// <param name="SessionId">Its session (0 for a service-started install).</param>
/// <param name="CommandLine">Its command line, or null when WMI did not give it.</param>
/// <param name="ParentProcessId">The pid of the process that started it.</param>
/// <param name="ParentName">That process's image name, or null when it has exited (or could not be read).</param>
public sealed record WindowsInstallerProcess(int ProcessId, int SessionId, string? CommandLine, int ParentProcessId, string? ParentName)
{
    public string Describe() =>
        $"msiexec.exe pid {ProcessId} (session {SessionId}, started by {ParentName ?? "an exited process"} pid {ParentProcessId}): {CommandLine ?? "command line unknown"}";
}

/// <summary>
/// Whether Windows Installer is busy with an installation right now, and who is running it.
/// <para>
/// Windows Installer holds the named mutex <c>Global\_MSIExecute</c> while an installation's transaction runs; Microsoft
/// documents testing for it as the way to tell whether an installation is in progress. A second installation started
/// meanwhile is refused with 1618 (ERROR_INSTALL_ALREADY_RUNNING; winget 0x8A150102). On H-SURFACELAP5 (2026-10-19) five
/// MSI-based updates in a row failed that way within six minutes while something outside AppMonitor installed.
/// </para>
/// </summary>
public static class WindowsInstallerActivity
{
    /// <summary>The mutex Windows Installer holds while an installation runs.</summary>
    public const string ExecuteMutexName = @"Global\_MSIExecute";

    /// <summary>How long the WMI query for the msiexec processes may take.</summary>
    public static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// True while <see cref="ExecuteMutexName"/> exists. Only opened for <see cref="MutexRights.Synchronize"/> and disposed
    /// at once: never waited on and never owned, which would block Windows Installer itself. A mutex that exists but may
    /// not be opened (access denied; SYSTEM should never see that) counts as busy. Never throws: any other error is "not
    /// busy", so a broken probe can never keep installs from running.
    /// </summary>
    public static bool IsInstallInProgress()
    {
        try
        {
            if (!MutexAcl.TryOpenExisting(ExecuteMutexName, MutexRights.Synchronize, out var mutex)) return false;
            mutex.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException) { return true; }
        catch { return false; }
    }

    /// <summary>
    /// The msiexec.exe processes that are not the Windows Installer service itself (<c>msiexec.exe /V</c>), with what
    /// started them, through WMI's <c>Win32_Process</c>: one of them holds Windows Installer. Throws when WMI fails; the
    /// caller logs and carries on.
    /// </summary>
    public static IReadOnlyList<WindowsInstallerProcess> FindInstallers(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var options = new System.Management.EnumerationOptions { Timeout = QueryTimeout, ReturnImmediately = true, Rewindable = false };
        using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\cimv2"),
            new ObjectQuery("SELECT ProcessId, SessionId, CommandLine, ParentProcessId FROM Win32_Process WHERE Name = 'msiexec.exe'"), options);
        using var results = searcher.Get();
        var found = new List<WindowsInstallerProcess>();
        foreach (var item in results)
        {
            using (item)
            {
                var commandLine = item["CommandLine"]?.ToString();
                if (IsInstallerService(commandLine)) continue;
                var parent = Convert.ToInt32(item["ParentProcessId"] ?? 0);
                found.Add(new WindowsInstallerProcess(
                    Convert.ToInt32(item["ProcessId"] ?? 0),
                    Convert.ToInt32(item["SessionId"] ?? 0),
                    commandLine,
                    parent,
                    ProcessName(parent)));
            }
        }
        return found;
    }

    /// <summary>
    /// Whether an msiexec command line is the Windows Installer service (<c>C:\Windows\system32\msiexec.exe /V</c>), which
    /// runs whenever anything installs and says nothing about who does. Pure, so the rule is testable.
    /// </summary>
    internal static bool IsInstallerService(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return false;
        var tokens = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Length >= 2
            && tokens.Skip(1).All(t => string.Equals(t, "/V", StringComparison.OrdinalIgnoreCase));
    }

    private static string? ProcessName(int pid)
    {
        if (pid <= 0) return null;
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName + ".exe";
        }
        catch { return null; }
    }
}
