using System.Text;
using Arkimentum.AppMonitor.Install;
using Arkimentum.AppMonitor.Ipc;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Providers;
using Arkimentum.AppMonitor.Service.Policy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// H-SURFACELAP5 (2026-10-19, agent 1.1.48): five SYSTEM-context updates in a row (Acrobat Reader, ASP.NET Core 8, Desktop
/// Runtime 8/9/10), all Windows Installer based, failed within six minutes with winget 0x8A150102 "Another installation is
/// already in progress. Try again later. / Installer failed with exit code: 1618" while something outside AppMonitor held
/// Windows Installer. Each one counted as a failure, raised a "failed" toast and an InstallFailed event. Now the install
/// turn waits for Windows Installer first, and a refusal that still happens is "busy": put back for a later attempt.
/// </summary>
public class WindowsInstallerBusyTests
{
    private const int InstallInProgress = unchecked((int)0x8A150102);

    /// <summary>winget's output on the device (exit 0x8A150102).</summary>
    private const string BusyOutput =
        "Found Microsoft ASP.NET Core Hosting Bundle 8.0 [Microsoft.DotNet.AspNetCore.8] Version 8.0.21\r\n" +
        "Successfully verified installer hash\r\n" +
        "Starting package install...\r\n" +
        "Another installation is already in progress. Try again later.\r\n" +
        "Installer failed with exit code: 1618\r\n";

    // ---------------------------------------------------------------- recognising it

    [Theory]
    [InlineData(InstallInProgress, "", true)]
    [InlineData(InstallerRunner.ExitAnotherInstallInProgress, "", true)]
    [InlineData(unchecked((int)0x8A150011), "Installer failed with exit code: 1618", true)]
    [InlineData(1, "Installer failed with\r\n exit code:   1618", true)]
    [InlineData(unchecked((int)0x8A150011), "Installer failed with exit code: 1603", false)]
    [InlineData(unchecked((int)0x8A15002B), "No applicable upgrade found.", false)]
    [InlineData(0, "Installer failed with exit code: 1618", false)]
    public void Busy_is_recognised_by_wingets_code_the_msi_code_or_the_installer_line(int exitCode, string output, bool expected)
    {
        Assert.Equal(expected, WingetOutputParser.IsInstallerBusy(exitCode, output));
    }

    [Fact]
    public void Wingets_install_in_progress_code_is_a_busy_failure()
    {
        var result = WingetProvider.InterpretWingetExitCode(InstallInProgress, new ProcessRunResult(InstallInProgress, BusyOutput, string.Empty));

        Assert.False(result.Success);
        Assert.True(result.InstallerBusy);
        Assert.Equal(InstallInProgress, result.ExitCode);
        Assert.Contains("Another installation", result.Message);
        Assert.Contains("1618", result.Message);
    }

    [Fact]
    public void An_unmapped_exit_code_with_the_installers_1618_line_is_busy_too()
    {
        const int installFailed = unchecked((int)0x8A150011);
        var result = WingetProvider.InterpretWingetExitCode(installFailed, new ProcessRunResult(installFailed, BusyOutput, string.Empty));

        Assert.True(result.InstallerBusy);
        Assert.False(WingetProvider.InterpretWingetExitCode(installFailed, new ProcessRunResult(installFailed, "Installer failed with exit code: 1603", "")).InstallerBusy);
    }

    [Fact]
    public void The_services_install_for_all_users_keeps_the_flag()
    {
        var result = WingetProvider.InterpretAllUsersInstallExit(InstallInProgress, new ProcessRunResult(InstallInProgress, BusyOutput, string.Empty));

        Assert.False(result.Success);
        Assert.True(result.InstallerBusy);
    }

    [Fact]
    public void Msi_1618_from_a_web_installer_is_busy()
    {
        var result = InstallerRunner.InterpretExitCode(InstallerRunner.ExitAnotherInstallInProgress, InstallerType.Msi);

        Assert.False(result.Success);
        Assert.True(result.InstallerBusy);
        Assert.Equal(1618, result.ExitCode);
    }

    [Fact]
    public void Other_failures_are_not_busy()
    {
        Assert.False(InstallerRunner.InterpretExitCode(InstallerRunner.ExitFatalError, InstallerType.Msi).InstallerBusy);
        Assert.False(InstallResult.Fail("x").InstallerBusy);
    }

    // ---------------------------------------------------------------- the provider stops at a busy answer

    private const string AspNetId = "Microsoft.DotNet.AspNetCore.8";

    private static readonly AppPolicy AspNet = new() { AppId = "aspnetcore8", DisplayName = "ASP.NET Core 8", WingetId = AspNetId };

    private static PendingUpdate Pending(bool replaceOnMismatch = false) => new()
    {
        AppId = "aspnetcore8",
        DisplayName = "ASP.NET Core 8",
        InstalledVersion = "8.0.20",
        AvailableVersion = "8.0.21",
        Source = UpdateSource.Winget,
        Context = InstallContext.System,
        WingetId = AspNetId,
        WingetIdAlternatives = AspNetId + ";Microsoft.DotNet.AspNetCore.8.x64",
        WingetReplaceOnMismatch = replaceOnMismatch,
    };

    /// <summary>Stands in for winget: the upgrade's and the uninstall's answers; the listing keeps the old version.</summary>
    private sealed class FakeWinget(int upgradeExit, string upgradeOutput, int uninstallExit = 0, string uninstallOutput = "")
    {
        public List<string> Calls { get; } = [];

        public Task<ProcessRunResult> Run(string args, TimeSpan timeout, ExecutionContextInfo context, CancellationToken ct)
        {
            lock (Calls) Calls.Add(args);
            if (args.StartsWith("upgrade --id ", StringComparison.Ordinal))
                return Task.FromResult(new ProcessRunResult(upgradeExit, upgradeOutput, string.Empty));
            if (args.StartsWith("uninstall --id ", StringComparison.Ordinal))
                return Task.FromResult(new ProcessRunResult(uninstallExit, uninstallOutput, string.Empty));
            if (args.StartsWith($"list --id {AspNetId} ", StringComparison.Ordinal))
                return Task.FromResult(new ProcessRunResult(0, Table("Microsoft ASP.NET Core 8.0", AspNetId, "8.0.20"), string.Empty));
            if (args.StartsWith("install ", StringComparison.Ordinal))
                return Task.FromResult(new ProcessRunResult(0, "Successfully installed", string.Empty));
            // show (dependencies), other lookups: nothing to say.
            return Task.FromResult(new ProcessRunResult(1, string.Empty, string.Empty));
        }

        private static string Table(params string[] row)
        {
            var header = new[] { "Name", "Id", "Version" };
            var widths = Enumerable.Range(0, header.Length).Select(c => Math.Max(header[c].Length, row[c].Length) + 1).ToArray();
            string Line(string[] cells) => string.Concat(cells.Select((v, c) => c == cells.Length - 1 ? v : v.PadRight(widths[c]))).TrimEnd();
            return new StringBuilder().Append(Line(header)).Append("\r\n").Append(new string('-', widths.Sum())).Append("\r\n")
                .Append(Line(row)).Append("\r\n").ToString();
        }
    }

    private static WingetProvider Provider(FakeWinget winget) =>
        new(NullLogger<WingetProvider>.Instance, new ProviderOptions()) { LookupRunner = winget.Run, InstallRunner = winget.Run };

    [Fact]
    public async Task A_busy_upgrade_is_final_no_other_id_and_no_fallback_is_tried()
    {
        var winget = new FakeWinget(InstallInProgress, BusyOutput);

        var result = await Provider(winget).InstallAsync(AspNet, Pending(), ExecutionContextInfo.System, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.InstallerBusy);
        Assert.Single(winget.Calls, c => c.StartsWith("upgrade ", StringComparison.Ordinal));
        Assert.DoesNotContain(winget.Calls, c => c.StartsWith("install ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_busy_uninstall_in_the_take_over_leaves_the_current_install_and_reports_busy()
    {
        var winget = new FakeWinget(WingetOutputParser.ExitUpdateInstallTechnologyMismatch, "The install technology is different.",
            uninstallExit: InstallInProgress, uninstallOutput: BusyOutput);

        var result = await Provider(winget).InstallAsync(AspNet, Pending(replaceOnMismatch: true), ExecutionContextInfo.System, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.InstallerBusy, result.Message);
        Assert.Contains("left in place", result.Message);
        Assert.Single(winget.Calls, c => c.StartsWith("uninstall ", StringComparison.Ordinal));
        Assert.DoesNotContain(winget.Calls, c => c.StartsWith("install ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_candidate_order_returns_a_busy_answer_at_once()
    {
        var calls = new List<string>();
        var result = await WingetProvider.RunCandidatesAsync(
            ["A", "B"],
            id => { calls.Add("upgrade " + id); return Task.FromResult(WingetProvider.UpgradeOutcome.Final(InstallResult.Busy("busy", InstallInProgress))); },
            refusal => { calls.Add("fallback " + refusal.WingetId); return Task.FromResult<InstallResult?>(InstallResult.Ok()); });

        Assert.True(result.InstallerBusy);
        Assert.Equal(["upgrade A"], calls);
    }

    // ---------------------------------------------------------------- the flag on the pipe

    [Fact]
    public void The_tray_reports_a_busy_refusal_to_the_service()
    {
        var message = new UserInstallResultMessage { UpdateKey = "k", Result = InstallResult.Busy("Another installation is in progress", InstallInProgress) };

        var back = Assert.IsType<UserInstallResultMessage>(IpcJson.Deserialize(IpcJson.Serialize(message)));

        Assert.False(back.Result.Success);
        Assert.True(back.Result.InstallerBusy);
        Assert.Equal(InstallInProgress, back.Result.ExitCode);
    }

    [Fact]
    public void An_older_trays_result_without_the_flag_reads_as_not_busy()
    {
        const string line = "{\"$type\":\"userInstallResult\",\"updateKey\":\"k\",\"result\":{\"success\":false,\"exitCode\":1603,\"message\":\"x\"}}";

        var back = Assert.IsType<UserInstallResultMessage>(IpcJson.Deserialize(line));

        Assert.False(back.Result.InstallerBusy);
    }

    [Fact]
    public void The_tray_sees_the_wait_and_the_retry_time()
    {
        var since = new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);
        var message = new StateMessage { Updates = [new PendingUpdate { AppId = "a", WaitingForInstallerSinceUtc = since, InstallerBusyUntilUtc = since.AddMinutes(10) }] };

        var back = Assert.IsType<StateMessage>(IpcJson.Deserialize(IpcJson.Serialize(message))).Updates.Single();

        Assert.Equal(since, back.WaitingForInstallerSinceUtc);
        Assert.Equal(since.AddMinutes(10), back.InstallerBusyUntilUtc);
    }

    // ---------------------------------------------------------------- the policy

    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 8, 40, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(4);

    private static ScanOutcome Scan(string available = "8.0.21", AppPolicy? policy = null) => new(new UpdateCheckResult
    {
        AppId = "aspnetcore8",
        Source = UpdateSource.Winget,
        IsInstalled = true,
        InstalledVersion = "8.0.20",
        AvailableVersion = available,
        UpdateAvailable = true,
        WingetId = AspNetId,
        ResolvedContext = InstallContext.System,
    }, policy ?? (AspNet with { AutoInstall = true }), InstallContext.System, null);

    private static (Dictionary<string, PendingUpdate> State, PendingUpdate Update) Tracked(AppPolicy? policy = null)
    {
        var state = new Dictionary<string, PendingUpdate>(StringComparer.OrdinalIgnoreCase);
        var outcome = Scan(policy: policy);
        PolicyEngine.Merge(state, [outcome], new HashSet<string> { outcome.Key }, T0);
        return (state, state.Values.Single());
    }

    private static void Rescan(Dictionary<string, PendingUpdate> state, DateTimeOffset at, string available = "8.0.21")
    {
        var outcome = Scan(available);
        PolicyEngine.Merge(state, [outcome], new HashSet<string> { outcome.Key }, at);
    }

    [Fact]
    public void A_busy_attempt_is_not_a_failure()
    {
        var (_, u) = Tracked();
        u.MaxDeferrals = 3;
        u.DeferralCount = 1;
        PolicyEngine.MarkInstalling(u, T0.AddMinutes(1));

        var at = T0.AddMinutes(2);
        PolicyEngine.MarkInstallerBusy(u, at);

        Assert.Equal(UpdateState.Available, u.State);
        Assert.Equal(0, u.FailureCount);
        Assert.Null(u.FailedAtUtc);
        Assert.Null(u.LastError);
        Assert.Equal(1, u.DeferralCount);
        Assert.Null(u.DeferredUntilUtc);
        Assert.Equal(at + PolicyEngine.InstallerBusyRetryDelay, u.InstallerBusyUntilUtc);
        Assert.Null(u.InstallStartedUtc);
        Assert.Null(u.InstallPhase);
    }

    [Fact]
    public void A_busy_update_is_not_installed_before_the_retry_time_and_is_after_it()
    {
        var (_, u) = Tracked();
        var at = T0.AddMinutes(2);
        PolicyEngine.MarkInstallerBusy(u, at);

        Assert.Equal(PolicyActionKind.None, PolicyEngine.Decide(u, at.AddMinutes(1), Interval, false).Kind);
        Assert.Equal(PolicyActionKind.None, PolicyEngine.Decide(u, at + PolicyEngine.InstallerBusyRetryDelay - TimeSpan.FromSeconds(1), Interval, false).Kind);
        Assert.Equal(PolicyActionKind.Install, PolicyEngine.Decide(u, at + PolicyEngine.InstallerBusyRetryDelay, Interval, false).Kind);
    }

    [Fact]
    public void While_held_nobody_is_asked_to_close_an_application_for_it()
    {
        var (_, u) = Tracked();
        var at = T0.AddMinutes(2);
        PolicyEngine.MarkInstallerBusy(u, at);

        Assert.Equal(PolicyActionKind.None, PolicyEngine.Decide(u, at.AddMinutes(1), Interval, blockingProcessesRunning: true).Kind);
        Assert.Equal(PolicyActionKind.PromptClose, PolicyEngine.Decide(u, at.AddMinutes(11), Interval, blockingProcessesRunning: true).Kind);
    }

    [Fact]
    public void Install_now_was_asked_for_and_is_kept_for_the_retry()
    {
        var (_, u) = Tracked(AspNet with { AutoInstall = false });
        PolicyEngine.RequestInstall(u);
        PolicyEngine.MarkInstalling(u, T0.AddMinutes(1));
        PolicyEngine.MarkInstallerBusy(u, T0.AddMinutes(2));

        Assert.Equal(UpdateState.Available, u.State);
        Assert.True(u.InstallRequested);
        Assert.Equal(PolicyActionKind.Install, PolicyEngine.Decide(u, T0.AddMinutes(13), Interval, false).Kind);
    }

    [Fact]
    public void Install_now_tries_again_at_once()
    {
        var (_, u) = Tracked();
        PolicyEngine.MarkInstallerBusy(u, T0.AddMinutes(2));

        PolicyEngine.RequestInstall(u);

        Assert.Null(u.InstallerBusyUntilUtc);
        Assert.Equal(PolicyActionKind.Install, PolicyEngine.Decide(u, T0.AddMinutes(3), Interval, false).Kind);
    }

    [Fact]
    public void A_scan_of_the_same_version_keeps_the_hold_and_a_newer_version_clears_it()
    {
        var (state, u) = Tracked();
        PolicyEngine.MarkInstallerBusy(u, T0.AddMinutes(2));

        Rescan(state, T0.AddMinutes(3));
        Assert.True(u.IsHeldForInstaller(T0.AddMinutes(4)));

        Rescan(state, T0.AddMinutes(5), available: "8.0.22");
        Assert.Null(u.InstallerBusyUntilUtc);
        Assert.Equal(PolicyActionKind.Install, PolicyEngine.Decide(u, T0.AddMinutes(5), Interval, false).Kind);
    }

    [Fact]
    public void A_mandatory_update_past_its_deadline_waits_too_and_keeps_its_deadline()
    {
        var (_, u) = Tracked(AspNet with { Mandatory = true, DeadlineHours = 1 });
        var deadline = u.DeadlineUtc;
        var at = T0.AddHours(2);
        Assert.True(u.IsPastDeadline(at));

        PolicyEngine.MarkInstallerBusy(u, at);

        Assert.Equal(PolicyActionKind.None, PolicyEngine.Decide(u, at.AddMinutes(5), Interval, false).Kind);
        Assert.Equal(PolicyActionKind.Install, PolicyEngine.Decide(u, at.AddMinutes(10), Interval, false).Kind);
        Assert.Equal(deadline, u.DeadlineUtc);
        Assert.True(u.IsPastDeadline(at.AddMinutes(10)));
    }

    [Fact]
    public void The_hold_does_not_silence_notifications()
    {
        var (_, u) = Tracked(AspNet with { AutoInstall = false });
        PolicyEngine.MarkInstallerBusy(u, T0.AddMinutes(2));

        Assert.Equal(PolicyActionKind.Notify, PolicyEngine.Decide(u, T0.AddMinutes(3), Interval, false).Kind);
    }

    [Fact]
    public void The_install_starting_clears_the_hold_and_the_wait()
    {
        var (_, u) = Tracked();
        u.InstallerBusyUntilUtc = T0;
        u.WaitingForInstallerSinceUtc = T0;

        PolicyEngine.MarkInstalling(u, T0.AddMinutes(1));

        Assert.Null(u.InstallerBusyUntilUtc);
        Assert.Null(u.WaitingForInstallerSinceUtc);
    }

    // ---------------------------------------------------------------- the wait at the install turn

    private sealed class Probe(params bool[] answers)
    {
        private int _asked;
        public int Asked => _asked;
        public bool IsBusy() => answers[Math.Min(_asked++, answers.Length - 1)];
    }

    private static Func<TimeSpan, CancellationToken, Task> Recording(List<TimeSpan> delays) =>
        (d, _) => { delays.Add(d); return Task.CompletedTask; };

    [Fact]
    public async Task A_free_windows_installer_needs_no_wait()
    {
        var started = 0;
        var delays = new List<TimeSpan>();

        var result = await WindowsInstallerWait.WaitUntilFreeAsync(() => false, () => { started++; return Task.CompletedTask; }, CancellationToken.None,
            delay: Recording(delays));

        Assert.Equal(WindowsInstallerWaitResult.NotBusy, result);
        Assert.Equal(0, started);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task Busy_then_free_proceeds_after_the_other_installation()
    {
        var probe = new Probe(true, true, true, false);
        var started = 0;
        var delays = new List<TimeSpan>();

        var result = await WindowsInstallerWait.WaitUntilFreeAsync(probe.IsBusy, () => { started++; return Task.CompletedTask; }, CancellationToken.None,
            delay: Recording(delays));

        Assert.True(result.WasBusy);
        Assert.True(result.Free);
        Assert.Equal(1, started);
        Assert.Equal([WindowsInstallerWait.PollInterval, WindowsInstallerWait.PollInterval, WindowsInstallerWait.PollInterval], delays);
    }

    [Fact]
    public async Task Busy_for_the_whole_cap_hands_back()
    {
        var delays = new List<TimeSpan>();

        var result = await WindowsInstallerWait.WaitUntilFreeAsync(() => true, null, CancellationToken.None, delay: Recording(delays));

        Assert.True(result.WasBusy);
        Assert.False(result.Free);
        Assert.Equal(WindowsInstallerWait.MaxWait, TimeSpan.FromTicks(delays.Sum(d => d.Ticks)));
        Assert.Equal((int)(WindowsInstallerWait.MaxWait / WindowsInstallerWait.PollInterval), delays.Count);
    }

    [Fact]
    public async Task The_last_interval_is_cut_to_the_cap()
    {
        var delays = new List<TimeSpan>();

        var result = await WindowsInstallerWait.WaitUntilFreeAsync(() => true, null, CancellationToken.None,
            pollInterval: TimeSpan.FromSeconds(4), maxWait: TimeSpan.FromSeconds(10), delay: Recording(delays));

        Assert.False(result.Free);
        Assert.Equal([TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(2)], delays);
    }

    [Fact]
    public async Task Cancellation_ends_the_wait()
    {
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WindowsInstallerWait.WaitUntilFreeAsync(() => true,
            () => { cts.Cancel(); return Task.CompletedTask; }, cts.Token, delay: (d, ct) => Task.Delay(d, ct)));
    }

    [Fact]
    public void The_policy_constants_match_the_design()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), WindowsInstallerWait.PollInterval);
        Assert.Equal(TimeSpan.FromMinutes(15), WindowsInstallerWait.MaxWait);
        Assert.Equal(TimeSpan.FromMinutes(10), PolicyEngine.InstallerBusyRetryDelay);
    }

    // ---------------------------------------------------------------- who holds Windows Installer

    [Theory]
    [InlineData(@"C:\WINDOWS\system32\msiexec.exe /V", true)]
    [InlineData(@"C:\WINDOWS\system32\msiexec.exe /v", true)]
    [InlineData(@"C:\WINDOWS\system32\msiexec.exe /i ""C:\Temp\AcroRdr.msi"" /qn", false)]
    [InlineData(@"C:\WINDOWS\syswow64\MsiExec.exe -Embedding 4C1F2D8E1A0B9C3D4E5F6A7B8C9D0E1F E Global\MSI0000", false)]
    [InlineData(@"C:\WINDOWS\system32\msiexec.exe", false)]
    [InlineData(null, false)]
    public void The_windows_installer_service_itself_is_not_listed(string? commandLine, bool isService)
    {
        Assert.Equal(isService, WindowsInstallerActivity.IsInstallerService(commandLine));
    }

    [Fact]
    public void Probing_the_mutex_never_throws()
    {
        // Whatever the machine is doing right now; the probe only opens the mutex, it never owns it.
        _ = WindowsInstallerActivity.IsInstallInProgress();
    }
}
