using System.ComponentModel;
using System.Diagnostics;

namespace Arkimentum.AppMonitor.Admin.Infrastructure;

/// <summary>Outcome of asking Windows to relaunch this process elevated.</summary>
public enum ElevationResult
{
    /// <summary>The elevated process was started; this one must exit.</summary>
    Relaunched,
    /// <summary>The user dismissed the UAC prompt.</summary>
    Cancelled,
    /// <summary>Windows refused to start the process for another reason.</summary>
    Failed,
}

/// <summary>
/// The console edits HKLM, so it only ever runs elevated. The manifest asks for <c>asInvoker</c> and the process
/// elevates itself here instead, which keeps the unprivileged <c>--user-config</c> testing mode free of UAC prompts.
/// </summary>
public static class Elevation
{
    /// <summary>ERROR_CANCELLED: the user dismissed the consent prompt.</summary>
    private const int ErrorCancelled = 1223;

    /// <summary>
    /// Starts this executable again with the "runas" verb and the same arguments.
    /// <paramref name="waitForExit"/> is used for headless runs so the child's exit code can be propagated.
    /// </summary>
    public static ElevationResult Relaunch(IReadOnlyList<string> arguments, bool waitForExit, out int exitCode, out string? error)
    {
        exitCode = 0;
        error = null;
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = AdminAppInfo.ExecutablePath,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Environment.CurrentDirectory,
            };
            foreach (var arg in arguments) info.ArgumentList.Add(arg);

            using var process = Process.Start(info);
            if (process is null)
            {
                error = "Windows did not start the elevated process.";
                return ElevationResult.Failed;
            }
            if (waitForExit)
            {
                process.WaitForExit();
                exitCode = process.ExitCode;
            }
            return ElevationResult.Relaunched;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            error = ex.Message;
            return ElevationResult.Cancelled;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return ElevationResult.Failed;
        }
    }
}
