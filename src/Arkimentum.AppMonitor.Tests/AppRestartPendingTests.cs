using System.Text;
using Arkimentum.AppMonitor.Install;
using Arkimentum.AppMonitor.Ipc;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Native;
using Arkimentum.AppMonitor.Providers;
using Arkimentum.AppMonitor.Service;
using Arkimentum.AppMonitor.Service.Policy;
using Arkimentum.AppMonitor.Service.State;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// An MSIX update of an application that is running (DESKTOP-V1CDE3I, Windows Terminal 1.24.11911.0 -> 1.25.2733.0 with
/// Terminal open): winget exits 0 and says "Successfully installed. Restart the application to complete the upgrade.", but
/// the old version stays registered until the application is started again. It was reported as a failure ("winget
/// reported success ... but 'Microsoft.WindowsTerminal' is still at 1.24.11911.0"). Reproduced on 2026-10-06 with
/// PowerShell's MSIX package (7.6.5.0 -> 7.6.6.0); the outputs below are the ones captured there. It is an install that
/// finishes when the application next starts, and it must not be installed again every hour while the application stays open.
/// </summary>
public class AppRestartPendingTests
{
    private static readonly ExecutionContextInfo User = new() { IsSystem = false, UserSid = "S-1-5-21-1", SessionId = 3 };

    private const string PowerShellId = "Microsoft.PowerShell";
    private const string OldPackage = "Microsoft.PowerShell_7.6.5.0_x64__8wekyb3d8bbwe";

    /// <summary>winget's output for the upgrade while pwsh ran from the 7.6.5.0 package folder (exit 0).</summary>
    private const string StagedOutput =
        "Found PowerShell [Microsoft.PowerShell] Version 7.6.6.0\r\n" +
        "This application is licensed to you by its owner.\r\n" +
        "Microsoft is not responsible for, nor does it grant any licenses to, third-party packages.\r\n" +
        "Successfully verified installer hash\r\n" +
        "Starting package install...\r\n" +
        "Successfully installed. Restart the application to complete the upgrade.\r\n";

    /// <summary>The same run on a Windows whose winget speaks German: nothing to match on but the exit code.</summary>
    private const string GermanOutput =
        "PowerShell [Microsoft.PowerShell] Version 7.6.6.0 gefunden\r\n" +
        "Der Installer-Hash wurde erfolgreich überprüft.\r\n" +
        "Paketinstallation wird gestartet...\r\n" +
        "Erfolgreich installiert. Starten Sie die Anwendung neu, um das Upgrade abzuschließen.\r\n";

    private static readonly AppPolicy PowerShell = new()
    {
        AppId = "powershell",
        DisplayName = "PowerShell",
        WingetId = PowerShellId,
        Context = InstallContext.User,
    };

    private static PendingUpdate Pending(string installed = "7.6.5.0", string available = "7.6.6.0") => new()
    {
        AppId = "powershell",
        DisplayName = "PowerShell",
        InstalledVersion = installed,
        AvailableVersion = available,
        Source = UpdateSource.Winget,
        Context = InstallContext.User,
        UserSid = User.UserSid,
        WingetId = PowerShellId,
        WingetIdAlternatives = PowerShellId,
        WingetSourceName = "winget",
    };

    // ---------------------------------------------------------------- winget's line

    [Fact]
    public void Wingets_line_after_a_staged_msix_upgrade_is_recognised()
    {
        Assert.True(WingetOutputParser.IsAppRestartPendingOutput(StagedOutput));
    }

    [Theory]
    [InlineData("successfully installed. RESTART THE APPLICATION TO COMPLETE THE UPGRADE.")]
    [InlineData("Restart the application to complete the upgrade")]
    [InlineData("Successfully installed. Restart the application\r\n  to complete   the upgrade.")]
    [InlineData("\u001b[2K\r  ██████████████  100%\rSuccessfully installed. Restart the application to complete the upgrade.\r\n")]
    public void The_line_is_matched_in_any_case_and_with_any_whitespace(string output)
    {
        Assert.True(WingetOutputParser.IsAppRestartPendingOutput(output));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Successfully installed")]
    [InlineData("Restart the application")]
    [InlineData(GermanOutput)]
    public void Other_output_is_not_the_line(string? output)
    {
        Assert.False(WingetOutputParser.IsAppRestartPendingOutput(output));
    }

    // ---------------------------------------------------------------- the package folder

    [Theory]
    [InlineData(@"C:\Program Files\WindowsApps\Microsoft.PowerShell_7.6.5.0_x64__8wekyb3d8bbwe\pwsh.exe", true)]
    [InlineData(@"c:\program files\windowsapps\microsoft.powershell_7.6.5.0_x64__8wekyb3d8bbwe\sub\pwsh.exe", true)]
    [InlineData(@"D:\WindowsApps\Microsoft.PowerShell_7.6.5.0_x64__8wekyb3d8bbwe\pwsh.exe", true)]
    [InlineData(@"C:\Program Files\WindowsApps\Microsoft.PowerShell_7.6.6.0_x64__8wekyb3d8bbwe\pwsh.exe", false)]
    [InlineData(@"C:\Program Files\WindowsApps\Microsoft.PowerShellPreview_7.6.5.0_x64__8wekyb3d8bbwe\pwsh.exe", false)]
    [InlineData(@"C:\Program Files\PowerShell\7\pwsh.exe", false)]
    [InlineData(null, false)]
    public void A_process_counts_only_when_it_runs_from_the_packages_own_folder(string? imagePath, bool expected)
    {
        Assert.Equal(expected, ProcessHelper.IsInPackageFolder(imagePath, [OldPackage]));
    }

    [Fact]
    public void Looking_for_processes_of_a_package_nobody_runs_finds_none()
    {
        Assert.False(ProcessHelper.AnyRunningFromPackage(["Contoso.NotInstalled_1.0.0.0_x64__0123456789abc"], sessionId: null));
        Assert.False(ProcessHelper.AnyRunningFromPackage([], sessionId: null));
    }

    private static WingetInstalledDetails Row(string id, string? category, string? localIdentifier) =>
        new("PowerShell", id, "7.6.5.0", localIdentifier, "microsoft.powershell_8wekyb3d8bbwe", category, "X64");

    [Fact]
    public void The_package_full_names_come_from_the_installed_msix_rows_of_the_id()
    {
        var rows = new[]
        {
            Row(PowerShellId, "msix", @"MSIX\" + OldPackage),
            Row("Other.Package", "msix", @"MSIX\Other.Package_1.0.0.0_x64__8wekyb3d8bbwe"),
        };

        Assert.Equal(new[] { OldPackage }, WingetProvider.MsixPackageFullNames(rows, PowerShellId));
    }

    [Fact]
    public void A_classic_install_or_an_unreadable_identifier_gives_no_package_folder()
    {
        Assert.Empty(WingetProvider.MsixPackageFullNames([Row(PowerShellId, "exe", @"ARP\User\X64\PowerShell")], PowerShellId));
        Assert.Empty(WingetProvider.MsixPackageFullNames([Row(PowerShellId, "msix", null)], PowerShellId));
        Assert.Empty(WingetProvider.MsixPackageFullNames([Row(PowerShellId, "msix", @"MSIX\" + OldPackage), Row(PowerShellId, "msi", "{GUID}")], PowerShellId));
        Assert.Empty(WingetProvider.MsixPackageFullNames([], PowerShellId));
    }

    // ---------------------------------------------------------------- the install

    /// <summary>Stands in for winget: the upgrade's answer, the version listed afterwards and what --details shows.</summary>
    private sealed class FakeWinget(int upgradeExit, string upgradeOutput, string versionAfter, string installerCategory = "msix")
    {
        public List<string> Calls { get; } = [];

        public Task<ProcessRunResult> Run(string args, TimeSpan timeout, ExecutionContextInfo context, CancellationToken ct)
        {
            lock (Calls) Calls.Add(args);
            if (args.StartsWith("upgrade --id ", StringComparison.Ordinal))
                return Task.FromResult(new ProcessRunResult(upgradeExit, upgradeOutput, string.Empty));
            if (args.StartsWith($"list --id {PowerShellId} ", StringComparison.Ordinal) && args.Contains("--details", StringComparison.Ordinal))
                return Task.FromResult(new ProcessRunResult(0,
                    $"PowerShell [{PowerShellId}]\r\nVersion: {versionAfter}\r\n" +
                    (installerCategory == "msix" ? $"Local Identifier: MSIX\\{OldPackage}\r\nPackage Family Name: microsoft.powershell_8wekyb3d8bbwe\r\n" : "Local Identifier: ARP\\User\\X64\\PowerShell\r\n") +
                    $"Installer Category: {installerCategory}\r\nInstalled Architecture: X64\r\n", string.Empty));
            if (args.StartsWith($"list --id {PowerShellId} ", StringComparison.Ordinal))
                return Task.FromResult(new ProcessRunResult(0, Table(["PowerShell", PowerShellId, versionAfter]), string.Empty));
            throw new InvalidOperationException($"unexpected winget call: {args}");
        }

        private static string Table(string[] row)
        {
            var header = new[] { "Name", "Id", "Version" };
            var widths = Enumerable.Range(0, header.Length).Select(c => Math.Max(header[c].Length, row[c].Length) + 1).ToArray();
            string Line(string[] cells) => string.Concat(cells.Select((v, c) => c == cells.Length - 1 ? v : v.PadRight(widths[c]))).TrimEnd();
            return new StringBuilder().Append(Line(header)).Append("\r\n").Append(new string('-', widths.Sum())).Append("\r\n")
                .Append(Line(row)).Append("\r\n").ToString();
        }
    }

    private static WingetProvider Provider(FakeWinget winget, Func<IReadOnlyCollection<string>, ExecutionContextInfo, bool>? probe = null) =>
        new(NullLogger<WingetProvider>.Instance, new ProviderOptions())
        {
            LookupRunner = winget.Run,
            InstallRunner = winget.Run,
            PackageProcessProbe = probe ?? ((_, _) => throw new InvalidOperationException("the process probe was not expected to run")),
        };

    [Fact]
    public async Task A_staged_upgrade_of_a_running_app_is_an_install_that_finishes_at_the_next_start()
    {
        var winget = new FakeWinget(0, StagedOutput, versionAfter: "7.6.5.0");

        var result = await Provider(winget).InstallAsync(PowerShell, Pending(), User, null, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.True(result.AppRestartPending);
        Assert.False(result.RebootRequired);
        Assert.False(result.NothingInstalled);
        Assert.Equal("7.6.5.0", result.InstalledVersion);
        Assert.Equal("Installed; Windows finishes the update the next time PowerShell starts.", result.Message);
        // winget's line settles it: no extra listing, no process check (the probe would throw).
        Assert.DoesNotContain(winget.Calls, c => c.Contains("--details", StringComparison.Ordinal));
        Assert.DoesNotContain(winget.Calls, c => c.StartsWith("install ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_wingets_line_a_process_running_from_the_package_folder_says_the_same()
    {
        var winget = new FakeWinget(0, GermanOutput, versionAfter: "7.6.5.0");
        IReadOnlyCollection<string>? asked = null;
        ExecutionContextInfo? askedContext = null;

        var result = await Provider(winget, (packages, context) => { asked = packages; askedContext = context; return true; })
            .InstallAsync(PowerShell, Pending(), User, null, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.True(result.AppRestartPending);
        Assert.Equal("7.6.5.0", result.InstalledVersion);
        Assert.Equal(new[] { OldPackage }, asked);
        Assert.Equal(User.SessionId, askedContext!.SessionId);
    }

    [Fact]
    public async Task Without_wingets_line_and_without_a_running_process_it_is_the_failure_it_was()
    {
        var winget = new FakeWinget(0, GermanOutput, versionAfter: "7.6.5.0");

        var result = await Provider(winget, (_, _) => false).InstallAsync(PowerShell, Pending(), User, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.AppRestartPending);
        Assert.Contains("is still at 7.6.5.0, expected 7.6.6.0", result.Message);
    }

    [Fact]
    public async Task A_classic_install_never_gets_the_process_check()
    {
        var winget = new FakeWinget(0, GermanOutput, versionAfter: "7.6.5.0", installerCategory: "exe");

        var result = await Provider(winget).InstallAsync(PowerShell, Pending(), User, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.AppRestartPending);
    }

    [Fact]
    public async Task An_upgrade_that_moved_the_version_is_an_ordinary_install()
    {
        var winget = new FakeWinget(0, StagedOutput, versionAfter: "7.6.6.0");

        var result = await Provider(winget).InstallAsync(PowerShell, Pending(), User, null, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.False(result.AppRestartPending);
        Assert.Equal("7.6.6.0", result.InstalledVersion);
    }

    [Fact]
    public async Task A_failing_exit_code_is_a_failure_whatever_the_output_says()
    {
        var winget = new FakeWinget(unchecked((int)0x8A150001), StagedOutput, versionAfter: "7.6.5.0");

        var result = await Provider(winget).InstallAsync(PowerShell, Pending(), User, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.AppRestartPending);
    }

    [Fact]
    public async Task As_system_only_wingets_line_counts()
    {
        var system = ExecutionContextInfo.System;
        var policy = PowerShell with { Context = InstallContext.System };
        var update = Pending();
        update.Context = InstallContext.System;
        update.UserSid = null;

        var staged = await Provider(new FakeWinget(0, StagedOutput, versionAfter: "7.6.5.0")).InstallAsync(policy, update, system, null, CancellationToken.None);
        var german = new FakeWinget(0, GermanOutput, versionAfter: "7.6.5.0");
        var other = await Provider(german).InstallAsync(policy, update, system, null, CancellationToken.None);

        Assert.True(staged.AppRestartPending);
        Assert.False(other.Success);
        Assert.DoesNotContain(german.Calls, c => c.Contains("--details", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- the result on the pipe

    private static InstallResult Staged(string registered = "1.24.11911.0") =>
        InstallResult.Ok("Installed; Windows finishes the update the next time Windows Terminal starts.") with { InstalledVersion = registered, AppRestartPending = true };

    [Fact]
    public void The_tray_reports_the_pending_restart_to_the_service()
    {
        var message = new UserInstallResultMessage { UpdateKey = "terminal|User|S-1-5-21-1", Result = Staged() };

        var back = Assert.IsType<UserInstallResultMessage>(IpcJson.Deserialize(IpcJson.Serialize(message)));

        Assert.True(back.Result.Success);
        Assert.True(back.Result.AppRestartPending);
        Assert.Equal("1.24.11911.0", back.Result.InstalledVersion);
    }

    [Fact]
    public void An_older_trays_result_without_the_flag_reads_as_not_pending()
    {
        const string line = "{\"$type\":\"userInstallResult\",\"updateKey\":\"k\",\"result\":{\"success\":true,\"exitCode\":0,\"installedVersion\":\"1.0\"}}";

        var back = Assert.IsType<UserInstallResultMessage>(IpcJson.Deserialize(line));

        Assert.False(back.Result.AppRestartPending);
    }

    [Fact]
    public void A_tracked_update_and_a_history_entry_carry_the_flag_to_the_tray()
    {
        var message = new StateMessage
        {
            Updates = [new PendingUpdate { AppId = "terminal", AppRestartPending = true }],
            RecentInstalls = [new InstallHistoryEntry { AppId = "terminal", Succeeded = true, AppRestartPending = true }],
        };

        var back = Assert.IsType<StateMessage>(IpcJson.Deserialize(IpcJson.Serialize(message)));

        Assert.True(back.Updates.Single().AppRestartPending);
        Assert.True(back.RecentInstalls!.Single().AppRestartPending);
    }

    // ---------------------------------------------------------------- the policy

    private static readonly DateTimeOffset T0 = new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);
    private const string Sid = "S-1-5-21-1";

    private static readonly AppPolicy Terminal = new()
    {
        AppId = "terminal",
        DisplayName = "Windows Terminal",
        WingetId = "Microsoft.WindowsTerminal",
        Context = InstallContext.User,
        AutoInstall = true,
    };

    private static ScanOutcome Scan(string installed, string? available, bool updateAvailable = true) => new(
        new UpdateCheckResult
        {
            AppId = "terminal",
            Source = UpdateSource.Winget,
            IsInstalled = true,
            InstalledVersion = installed,
            AvailableVersion = available,
            UpdateAvailable = updateAvailable,
            WingetId = "Microsoft.WindowsTerminal",
            ResolvedContext = InstallContext.User,
        },
        Terminal, InstallContext.User, Sid);

    /// <summary>The update as the scan found it, then installed at <paramref name="at"/> by a run that staged it.</summary>
    private static (Dictionary<string, PendingUpdate> State, PendingUpdate Update) StagedAt(DateTimeOffset at)
    {
        var state = new Dictionary<string, PendingUpdate>(StringComparer.OrdinalIgnoreCase);
        var first = Scan("1.24.11911.0", "1.25.2733.0");
        PolicyEngine.Merge(state, [first], new HashSet<string> { first.Key }, at.AddMinutes(-1));
        var u = state.Values.Single();
        PolicyEngine.MarkInstalling(u, at.AddSeconds(-40));
        PolicyEngine.MarkInstalled(u, Staged(), at);
        return (state, u);
    }

    [Fact]
    public void A_staged_install_settles_the_update_without_verifying_it()
    {
        var (_, u) = StagedAt(T0);

        Assert.Equal(UpdateState.Installed, u.State);
        Assert.True(u.AppRestartPending);
        Assert.Equal("1.24.11911.0", u.InstalledVersion);
        Assert.False(u.InstallVerified);
        Assert.False(u.RebootPending);
        Assert.Equal(0, u.FailureCount);
        Assert.Null(u.LastError);
        Assert.Equal(PolicyActionKind.None, PolicyEngine.Decide(u, T0.AddHours(1), TimeSpan.FromHours(4), blockingProcessesRunning: false).Kind);
    }

    [Theory]
    [InlineData(5)]                 // the next scan
    [InlineData(3 * 60)]            // after the post-install grace period
    [InlineData(26 * 60)]           // after the retention of installed entries
    [InlineData(6 * 24 * 60)]       // a Terminal left open for most of a week
    public void A_scan_that_still_reads_the_old_version_does_not_offer_it_again(int minutesLater)
    {
        var (state, u) = StagedAt(T0);

        var again = Scan("1.24.11911.0", "1.25.2733.0");
        // Every scan in between, as the service runs them, and then the one at the given time.
        for (var at = T0.AddMinutes(5); at < T0.AddMinutes(minutesLater); at = at.AddHours(1))
            PolicyEngine.Merge(state, [again], new HashSet<string> { again.Key }, at);
        var summary = PolicyEngine.Merge(state, [again], new HashSet<string> { again.Key }, T0.AddMinutes(minutesLater));

        Assert.Same(u, Assert.Single(state.Values));
        Assert.Equal(UpdateState.Installed, u.State);
        Assert.True(u.AppRestartPending);
        Assert.Equal(0, u.FailureCount);
        Assert.Equal((0, 0), (summary.Added, summary.Updated));
        Assert.Empty(summary.NewUpdates);
        Assert.Empty(summary.AppRestartsEnded!);
    }

    [Fact]
    public void The_scan_that_shows_the_new_version_finishes_it()
    {
        var (state, u) = StagedAt(T0);

        var upToDate = Scan("1.25.2733.0", null, updateAvailable: false);
        var summary = PolicyEngine.Merge(state, [upToDate], new HashSet<string> { upToDate.Key }, T0.AddDays(2));

        Assert.False(u.AppRestartPending);
        Assert.Equal("1.25.2733.0", u.InstalledVersion);
        Assert.Equal(new[] { u.Key }, summary.AppRestartsEnded);
        Assert.Equal(0, u.FailureCount);
        // Installed two days ago and no longer held: it goes the way of every installed entry.
        Assert.Empty(state);
    }

    [Fact]
    public void A_newer_version_supersedes_the_pending_one()
    {
        var (state, u) = StagedAt(T0);

        var newer = Scan("1.24.11911.0", "1.26.100.0");
        var summary = PolicyEngine.Merge(state, [newer], new HashSet<string> { newer.Key }, T0.AddDays(1));

        Assert.Equal(UpdateState.Available, u.State);
        Assert.Equal("1.26.100.0", u.AvailableVersion);
        Assert.False(u.AppRestartPending);
        Assert.Equal(0, u.FailureCount);
        Assert.Equal(new[] { u.Key }, summary.AppRestartsEnded);
    }

    [Fact]
    public void Once_the_hold_runs_out_the_update_is_offered_again_without_counting_a_failure()
    {
        var (state, u) = StagedAt(T0);

        var again = Scan("1.24.11911.0", "1.25.2733.0");
        var summary = PolicyEngine.Merge(state, [again], new HashSet<string> { again.Key }, T0 + PolicyEngine.AppRestartPendingHold + TimeSpan.FromMinutes(1));

        Assert.Equal(UpdateState.Available, u.State);
        Assert.False(u.AppRestartPending);
        Assert.Equal(0, u.FailureCount);
        Assert.Equal(new[] { u.Key }, summary.AppRestartsEnded);
    }

    [Fact]
    public void An_ordinary_install_afterwards_clears_the_flag()
    {
        var (_, u) = StagedAt(T0);

        PolicyEngine.MarkInstalled(u, InstallResult.Ok() with { InstalledVersion = "1.25.2733.0" }, T0.AddHours(1));

        Assert.False(u.AppRestartPending);
        Assert.True(u.InstallVerified);
    }

    // ---------------------------------------------------------------- what the user and the cloud see

    [Fact]
    public void The_cloud_event_says_when_the_install_finishes()
    {
        Assert.Equal("Windows Terminal 1.25.2733.0 installed in 12 s; finishes when Windows Terminal is next started",
            UpdateCoordinator.InstallSucceededEventText("Windows Terminal", Staged(), TimeSpan.FromSeconds(12), "1.25.2733.0"));
        Assert.Equal("Windows Terminal installed; finishes when Windows Terminal is next started",
            UpdateCoordinator.InstallSucceededEventText("Windows Terminal", Staged()));
    }

    [Fact]
    public void The_installed_toast_names_the_new_version_and_asks_for_a_restart_of_the_app()
    {
        var (_, u) = StagedAt(T0);

        var (title, body) = UpdateCoordinator.ComposeNotification(u, NotificationKind.Installed);

        Assert.Equal("Windows Terminal updated", title);
        Assert.Equal("Windows Terminal 1.25.2733.0 was installed successfully.", body);
        Assert.Equal("Restart Windows Terminal to finish the update.", UpdateCoordinator.InstalledNotificationNote(u, Staged()));
        Assert.Equal("A restart is required to finish the update.", UpdateCoordinator.InstalledNotificationNote(u, InstallResult.Ok(reboot: true)));
        Assert.Null(UpdateCoordinator.InstalledNotificationNote(u, InstallResult.Ok()));
    }

    [Fact]
    public void The_history_entry_records_the_installed_version_and_the_pending_restart()
    {
        var u = Pending("1.24.11911.0", "1.25.2733.0");
        u.AppId = "terminal";
        u.DisplayName = "Windows Terminal";

        var entry = InstallHistory.Succeeded(u, Staged(), T0);

        Assert.True(entry.Succeeded);
        Assert.True(entry.AppRestartPending);
        Assert.Equal("1.24.11911.0", entry.FromVersion);
        Assert.Equal("1.25.2733.0", entry.ToVersion);
        Assert.False(InstallHistory.Succeeded(u, InstallResult.Ok() with { InstalledVersion = "1.25.2733.0" }, T0).AppRestartPending);
    }

    [Fact]
    public void The_history_stops_asking_for_a_restart_once_it_is_over()
    {
        var mine = new InstallHistoryEntry { AppId = "terminal", Context = InstallContext.User, UserSid = Sid, Succeeded = true, AppRestartPending = true, CompletedUtc = T0 };
        var other = new InstallHistoryEntry { AppId = "terminal", Context = InstallContext.User, UserSid = "S-1-5-21-2", Succeeded = true, AppRestartPending = true, CompletedUtc = T0 };
        var history = new List<InstallHistoryEntry> { mine, other };

        var ended = InstallHistory.EndAppRestarts(history, [PendingUpdate.MakeKey("terminal", InstallContext.User, Sid)]);

        Assert.NotNull(ended);
        Assert.False(ended[0].AppRestartPending);
        Assert.True(ended[1].AppRestartPending);
        Assert.True(mine.AppRestartPending);   // the input is never edited
        Assert.Null(InstallHistory.EndAppRestarts(history, []));
        Assert.Null(InstallHistory.EndAppRestarts(history, null));
    }
}
