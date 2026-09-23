using System.Diagnostics;
using System.Text;
using Arkimentum.AppMonitor.Models;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Install;

/// <summary>Outcome of a child process started by <see cref="ProcessRunner"/>.</summary>
/// <param name="ExitCode">The process exit code, or -1 when it could not be started / was killed.</param>
/// <param name="StandardOutput">Captured stdout (UTF-8).</param>
/// <param name="StandardError">Captured stderr (UTF-8).</param>
/// <param name="TimedOut">True when the process exceeded the timeout and was killed.</param>
/// <param name="StartFailure">Set when the process could not be started at all.</param>
public sealed record ProcessRunResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut = false,
    string? StartFailure = null)
{
    public bool Started => StartFailure is null;

    /// <summary>stdout and stderr merged, in that order.</summary>
    public string CombinedOutput =>
        string.IsNullOrWhiteSpace(StandardError) ? StandardOutput
        : string.IsNullOrWhiteSpace(StandardOutput) ? StandardError
        : StandardOutput + Environment.NewLine + StandardError;

    /// <summary>The last <paramref name="count"/> non-empty output lines, for error messages.</summary>
    public string LastLines(int count = 4)
    {
        var lines = CombinedOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Length > 0)
            .ToList();
        if (lines.Count == 0) return string.Empty;
        return string.Join(" | ", lines.Skip(Math.Max(0, lines.Count - count)));
    }
}

/// <summary>
/// Starts console processes without ever showing a window or prompting, captures stdout/stderr as UTF-8, and kills the
/// whole process tree on timeout or cancellation. Safe to use from LocalSystem.
/// </summary>
public static class ProcessRunner
{
    /// <summary>
    /// Extra environment variables for every child process the agent starts in the given context, or null for none.
    /// In a user's session (the tray agent) this sets <c>__COMPAT_LAYER=RunAsInvoker</c>: Windows then does not raise a
    /// UAC prompt for an executable whose manifest requests administrator rights or that installer detection flags as a
    /// setup program; it runs with the user's own rights and either succeeds per user or fails, and that failure is
    /// reported. The variable is inherited by grandchildren, so it also covers the installers winget starts. It does
    /// NOT stop a process that explicitly asks for elevation (ShellExecute with the "runas" verb), which is why the
    /// winget provider never starts a machine-wide installer in the user context in the first place; this is the
    /// second line of defence. Null for LocalSystem: the service is already elevated and must not change how
    /// installers behave there.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? ChildEnvironment(ExecutionContextInfo context) =>
        context.IsSystem ? null : UserContextEnvironment;

    private static readonly IReadOnlyDictionary<string, string> UserContextEnvironment =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["__COMPAT_LAYER"] = "RunAsInvoker" };

    /// <summary>
    /// Runs <paramref name="fileName"/> with <paramref name="arguments"/> and waits for it to exit.
    /// </summary>
    /// <param name="logger">Logger; the exact command line is logged at Debug level.</param>
    /// <param name="fileName">Executable path.</param>
    /// <param name="arguments">Command line arguments (already quoted).</param>
    /// <param name="timeout">Maximum run time; the process tree is killed when it elapses.</param>
    /// <param name="onOutputLine">Optional per-line callback for progress reporting.</param>
    /// <param name="workingDirectory">Optional working directory. Must not be a user profile path under LocalSystem.</param>
    /// <param name="environment">Optional extra environment variables.</param>
    /// <param name="ct">Cancellation token; cancelling kills the process tree.</param>
    public static async Task<ProcessRunResult> RunAsync(
        ILogger logger,
        string fileName,
        string arguments,
        TimeSpan timeout,
        Action<string>? onOutputLine = null,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
            psi.WorkingDirectory = workingDirectory;
        if (environment is not null)
        {
            foreach (var (k, v) in environment) psi.Environment[k] = v;
        }

        logger.LogDebug("Running: \"{FileName}\" {Arguments} (timeout {Timeout})", fileName, arguments, timeout);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stdoutDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) { stdoutDone.TrySetResult(); return; }
            stdout.Append(e.Data).Append('\n');
            if (onOutputLine is not null) { try { onOutputLine(e.Data); } catch { } }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) { stderrDone.TrySetResult(); return; }
            stderr.Append(e.Data).Append('\n');
        };

        try
        {
            if (!process.Start())
                return new ProcessRunResult(-1, string.Empty, string.Empty, StartFailure: $"Failed to start '{fileName}'.");
            // Which session the child landed in is the one fact that decides whether it can ever show a prompt to a
            // user: a service's children belong in session 0. Logged at Information because it was exactly what was
            // missing when a device showed UAC prompts for installs the SYSTEM service had started.
            try { logger.LogInformation("Started \"{FileName}\" as pid {Pid} in session {Session}", Path.GetFileName(fileName), process.Id, process.SessionId); }
            catch { /* the child may already be gone */ }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start \"{FileName}\" {Arguments}", fileName, arguments);
            return new ProcessRunResult(-1, string.Empty, string.Empty, StartFailure: $"Failed to start '{fileName}': {ex.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try { process.StandardInput.Close(); } catch { }   // never let an installer block on stdin

        var timedOut = false;
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutCts.IsCancellationRequested;
            logger.LogWarning("{Reason} after {Timeout}; killing process tree of \"{FileName}\" (pid {Pid}).",
                timedOut ? "Timed out" : "Cancelled", timeout, fileName, SafePid(process));
            Kill(process);
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false); } catch { }
            if (!timedOut) ct.ThrowIfCancellationRequested();
        }

        // Let the async readers drain (they complete when the pipes close).
        try
        {
            await Task.WhenAll(stdoutDone.Task, stderrDone.Task).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException) { }

        var exitCode = -1;
        try { exitCode = process.ExitCode; } catch { }

        var result = new ProcessRunResult(exitCode, stdout.ToString(), stderr.ToString(), timedOut);
        logger.LogDebug("\"{FileName}\" exited with {ExitCode} (0x{Hex}){Timeout}. Output:\n{Output}",
            fileName, exitCode, exitCode.ToString("X8"), timedOut ? " [TIMED OUT]" : string.Empty, Truncate(result.CombinedOutput, 8000));
        return result;
    }

    private static int SafePid(Process p) { try { return p.Id; } catch { return -1; } }

    private static void Kill(Process p)
    {
        try { p.Kill(entireProcessTree: true); }
        catch { try { p.Kill(); } catch { } }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + $"... [{s.Length - max} more characters]";
}
