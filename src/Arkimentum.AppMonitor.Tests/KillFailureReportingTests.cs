using System.ComponentModel;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Native;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// "Could not close pwsh (pid 9, session 0, NT AUTHORITY\SYSTEM, elevated)" never said why SYSTEM failed, nor which
/// pwsh it was, nor that the one it did close came straight back. Nothing here starts a process: the wording is a
/// pure function of what a kill pass reported.
/// </summary>
public class KillFailureReportingTests
{
    private static BlockingProcessInfo Info(int pid, string? reason = null, string? path = null) => new()
    {
        ProcessName = "pwsh",
        ProcessId = pid,
        SessionId = 0,
        UserName = @"NT AUTHORITY\SYSTEM",
        Elevated = true,
        Reason = reason,
        ExecutablePath = path,
    };

    // ---------------------------------------------------------------- the reason on one process

    [Fact]
    public void A_win32_failure_carries_its_error_code()
    {
        var reason = BlockingProcessInfo.DescribeFailure(new Win32Exception(5));

        Assert.Contains("Win32Exception", reason);
        Assert.Contains("Win32 error 5", reason);
        Assert.Contains("0x00000005", reason);
    }

    [Fact]
    public void Any_other_failure_carries_its_type_and_message()
    {
        var reason = BlockingProcessInfo.DescribeFailure(new NotSupportedException("no remote kill"));

        Assert.Equal("NotSupportedException: no remote kill", reason);
    }

    [Fact]
    public void A_restart_is_reported_with_the_new_pid()
    {
        Assert.Equal("restarted (pid 4711)", BlockingProcessInfo.RestartedReason(4711));
    }

    // ---------------------------------------------------------------- how it reads in a log line

    [Fact]
    public void Describe_appends_the_executable_and_the_reason()
    {
        var info = Info(9, reason: "Win32Exception: Access is denied (Win32 error 5 / 0x00000005)",
            path: @"C:\Program Files\PowerShell\7\pwsh.exe");

        Assert.Equal(
            @"pwsh (pid 9, session 0, NT AUTHORITY\SYSTEM, elevated, C:\Program Files\PowerShell\7\pwsh.exe): " +
            "Win32Exception: Access is denied (Win32 error 5 / 0x00000005)",
            info.Describe());
    }

    [Fact]
    public void Describe_is_unchanged_when_nothing_failed()
    {
        // The killed list and the "waiting for you to close" log lines go through the same method.
        Assert.Equal(@"pwsh (pid 9, session 0, NT AUTHORITY\SYSTEM, elevated)", Info(9).Describe());
    }

    [Fact]
    public void Clone_copies_the_reason_and_the_path()
    {
        var clone = Info(9, reason: "boom", path: @"C:\x\pwsh.exe").Clone();

        Assert.Equal("boom", clone.Reason);
        Assert.Equal(@"C:\x\pwsh.exe", clone.ExecutablePath);
    }

    // ---------------------------------------------------------------- the message the user and the report get

    [Fact]
    public void A_survivor_names_itself_and_why()
    {
        var outcome = new ProcessHelper.KillOutcome([], [Info(9, "Win32Exception: Access is denied (Win32 error 5 / 0x00000005)", @"C:\x\pwsh.exe")], []);

        var message = ProcessHelper.DescribeFailure(outcome, "PowerShell 7");

        Assert.StartsWith("Could not close pwsh (pid 9", message);
        Assert.Contains(@"C:\x\pwsh.exe", message);
        Assert.Contains("Win32 error 5", message);
        Assert.EndsWith("PowerShell 7 was not updated.", message);
        Assert.False(outcome.Cleared);
    }

    [Fact]
    public void A_restarted_process_fails_the_install_rather_than_passing_as_closed()
    {
        var outcome = new ProcessHelper.KillOutcome([Info(9)], [], [Info(4711, BlockingProcessInfo.RestartedReason(4711), @"C:\rmm\pwsh.exe")]);

        var message = ProcessHelper.DescribeFailure(outcome, "PowerShell 7");

        Assert.False(outcome.Cleared);
        Assert.Contains("pwsh (pid 4711", message);
        Assert.Contains(@"C:\rmm\pwsh.exe", message);
        Assert.Contains("started again", message);
        Assert.EndsWith("PowerShell 7 was not updated.", message);
    }

    [Fact]
    public void Both_kinds_are_reported_in_one_message()
    {
        var outcome = new ProcessHelper.KillOutcome([], [Info(9, "Win32Exception: Access is denied (Win32 error 5 / 0x00000005)")],
            [Info(4711, BlockingProcessInfo.RestartedReason(4711))]);

        var message = ProcessHelper.DescribeFailure(outcome, "PowerShell 7");

        Assert.Contains("Could not close pwsh (pid 9", message);
        Assert.Contains("pwsh (pid 4711", message);
        Assert.Contains("started again", message);
        Assert.Equal(2, outcome.Blocking.Count);
    }

    [Fact]
    public void A_clean_pass_blocks_nothing()
    {
        var outcome = new ProcessHelper.KillOutcome([Info(9)], [], []);

        Assert.True(outcome.Cleared);
        Assert.Empty(outcome.Blocking);
    }

    [Fact]
    public async Task Killing_nothing_is_a_cleared_outcome()
    {
        // No configured process names: nothing to look for, nothing to report - and nothing enumerated either.
        var outcome = await ProcessHelper.KillAsync([], null, TimeSpan.Zero, CancellationToken.None);

        Assert.True(outcome.Cleared);
        Assert.Empty(outcome.Killed);
    }

    [Fact]
    public async Task A_process_that_is_not_running_leaves_everything_empty()
    {
        var outcome = await ProcessHelper.KillAsync(["arkimentum-no-such-process"], null, TimeSpan.Zero,
            CancellationToken.None);

        Assert.True(outcome.Cleared);
        Assert.Empty(outcome.Killed);
        Assert.Empty(outcome.Restarted);
    }
}
