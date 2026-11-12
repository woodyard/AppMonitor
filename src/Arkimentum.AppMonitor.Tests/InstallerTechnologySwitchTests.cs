using System.Text;
using Arkimentum.AppMonitor.Configuration;
using Arkimentum.AppMonitor.Install;
using Arkimentum.AppMonitor.Inventory;
using Arkimentum.AppMonitor.Ipc;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Providers;
using Arkimentum.AppMonitor.Service;
using Arkimentum.AppMonitor.Service.Policy;
using Arkimentum.AppMonitor.Service.State;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// A product that changes its installer technology: PowerShell 7.7.0 (2026-11-10) ships only as MSIX, devices have the MSI
/// (7.6.x). On H-PARALLELSVM (agent 1.1.55, 2026-11-11) "winget upgrade" quietly installed 7.6.10, the latest version with an
/// MSI, and the agent reported a failure ("still at 7.6.10.0, expected 7.7.0.0"); from 7.6.10 every attempt was refused
/// with 0x8A15008E and counted as a failure with a toast. Three rules: a partial upgrade is a success for the version it
/// reached and the target stays pending; the refusal without the take-over is a hold, not a failure; and the take-over as
/// LocalSystem switches an MSI install to the MSIX package, provisioned for all users and verified by the package
/// registrations when SYSTEM's winget listing does not show it.
/// </summary>
public class InstallerTechnologySwitchTests
{
    private const string Id = "Microsoft.PowerShell";
    private const string Family = "Microsoft.PowerShell_8wekyb3d8bbwe";
    private const int Mismatch = WingetOutputParser.ExitUpdateInstallTechnologyMismatch;

    private static readonly AppPolicy PowerShell = new()
    {
        AppId = "powershell",
        DisplayName = "PowerShell 7",
        WingetId = Id,
        WingetExtraArgs = "--custom USE_MU=0",
        DetectDisplayNameRegex = "^PowerShell(?: 7(?!.*preview)|$)",
    };

    private static PendingUpdate Pending(string installed = "7.6.9.0", bool replace = false) => new()
    {
        AppId = "powershell",
        DisplayName = "PowerShell 7",
        InstalledVersion = installed,
        AvailableVersion = "7.7.0.0",
        Source = UpdateSource.Winget,
        Context = InstallContext.System,
        WingetId = Id,
        WingetIdAlternatives = Id,
        WingetExtraArgs = "--custom USE_MU=0",
        WingetReplaceOnMismatch = replace,
    };

    // ---------------------------------------------------------------- part 1: the partial upgrade rule

    [Theory]
    [InlineData("7.6.9.0", "7.6.10.0", "7.7.0.0", true)]
    [InlineData("7.6.9", "7.6.10.0", "7.7.0.0", true)]
    [InlineData("7.6.10.0", "7.6.10.0", "7.7.0.0", false)] // did not move: a failure, as before
    [InlineData("7.6.9.0", "7.7.0.0", "7.7.0.0", false)]   // reached the target: a plain success
    [InlineData("7.6.9.0", "7.6.8.0", "7.7.0.0", false)]   // went down
    [InlineData(null, "7.6.10.0", "7.7.0.0", false)]
    [InlineData("Unknown", "7.6.10.0", "7.7.0.0", false)]
    [InlineData("7.6.9.0", null, "7.7.0.0", false)]
    [InlineData("7.6.9.0", "7.6.10.0", null, false)]
    public void A_partial_upgrade_is_above_the_version_before_and_below_the_target(string? before, string? reached, string? target, bool partial)
    {
        Assert.Equal(partial, InstallResult.IsPartialUpgrade(before, reached, target));
    }

    /// <summary>Stands in for winget as LocalSystem; the listing after the upgrade or install is scripted per scenario.</summary>
    private sealed class FakeWinget
    {
        public List<string> Calls { get; } = [];
        public int UpgradeExit { get; init; }
        public string Listed { get; set; } = "7.6.9.0";
        /// <summary>What the listing shows once an install ran: a version, or null for "not installed".</summary>
        public string? ListedAfterInstall { get; init; }
        public string ShowOutput { get; init; } = "Found PowerShell [Microsoft.PowerShell]\r\nVersion: 7.7.0.0\r\nInstaller:\r\n  Installer Type: msix\r\n  Installer Url: https://github.com/PowerShell/PowerShell/releases/download/v7.7.0/PowerShell-7.7.0.msixbundle\r\n";
        public int ShowExit { get; init; }
        public string? DetailsOutput { get; init; }
        private bool _installed;

        public Task<ProcessRunResult> Run(string args, TimeSpan timeout, ExecutionContextInfo context, CancellationToken ct)
        {
            lock (Calls) Calls.Add(args);
            if (args.StartsWith("upgrade --id ", StringComparison.Ordinal))
            {
                // The MSI that winget does have: 7.6.9 -> 7.6.10.
                if (UpgradeExit == 0) Listed = "7.6.10.0";
                return Result(UpgradeExit, UpgradeExit == Mismatch ? "The install technology is different from the current version installed." : "Successfully installed");
            }
            if (args.StartsWith("show --id ", StringComparison.Ordinal))
                return Result(ShowExit, ShowOutput);
            if (args.StartsWith("uninstall --id ", StringComparison.Ordinal))
                return Result(0, "Successfully uninstalled");
            if (args.StartsWith("install --id ", StringComparison.Ordinal))
            {
                _installed = true;
                return Result(0, "Successfully installed");
            }
            if (args.StartsWith($"list --id {Id} ", StringComparison.Ordinal) && args.Contains(" --details"))
                return DetailsOutput is null
                    ? Result(WingetOutputParser.ExitNoInstalledPackageFound, "No installed package found matching input criteria.")
                    : Result(0, DetailsOutput);
            if (args.StartsWith($"list --id {Id} ", StringComparison.Ordinal))
            {
                var version = _installed ? ListedAfterInstall : Listed;
                return version is null
                    ? Result(WingetOutputParser.ExitNoInstalledPackageFound, "No installed package found matching input criteria.")
                    : Result(0, Table(_installed ? "PowerShell" : "PowerShell 7-x64", Id, version));
            }
            return Result(1, string.Empty);
        }

        public string? Install => Calls.SingleOrDefault(c => c.StartsWith("install --id ", StringComparison.Ordinal));

        private static Task<ProcessRunResult> Result(int exit, string output) => Task.FromResult(new ProcessRunResult(exit, output, string.Empty));

        private static string Table(params string[] row)
        {
            var header = new[] { "Name", "Id", "Version", "Source" };
            var cells = row.Append("winget").ToArray();
            var widths = Enumerable.Range(0, header.Length).Select(c => Math.Max(header[c].Length, cells[c].Length) + 1).ToArray();
            string Line(string[] values) => string.Concat(values.Select((v, c) => c == values.Length - 1 ? v : v.PadRight(widths[c]))).TrimEnd();
            return new StringBuilder().Append(Line(header)).Append("\r\n").Append(new string('-', widths.Sum())).Append("\r\n")
                .Append(Line(cells)).Append("\r\n").ToString();
        }
    }

    private static WingetProvider Provider(FakeWinget winget, Func<string, bool, CancellationToken, Task<AppxProbeResult>>? probe = null) =>
        new(NullLogger<WingetProvider>.Instance, new ProviderOptions()) { LookupRunner = winget.Run, InstallRunner = winget.Run, AppxProbeRunner = probe };

    [Fact]
    public async Task An_upgrade_that_stops_at_the_last_msi_is_a_success_for_that_version()
    {
        var winget = new FakeWinget { UpgradeExit = 0 };

        var result = await Provider(winget).InstallAsync(PowerShell, Pending(), ExecutionContextInfo.System, null, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.True(result.PartialUpgrade);
        Assert.Equal("7.6.10.0", result.InstalledVersion);
        Assert.Contains("7.7.0.0", result.Message);
        Assert.False(result.TechnologyMismatch);
    }

    [Fact]
    public async Task An_upgrade_that_did_not_move_stays_a_failure()
    {
        var winget = new FakeWinget { UpgradeExit = 0 };

        var result = await Provider(winget).InstallAsync(PowerShell, Pending(installed: "7.6.10.0"), ExecutionContextInfo.System, null, CancellationToken.None);

        // The fake moves the listing to 7.6.10.0, which is where it already was.
        Assert.False(result.Success);
        Assert.False(result.PartialUpgrade);
        Assert.Contains("still at 7.6.10.0", result.Message);
    }

    [Fact]
    public void A_partial_upgrade_and_a_hold_cross_the_pipe_and_an_older_trays_result_has_neither()
    {
        var partial = new UserInstallResultMessage { UpdateKey = "k", Result = InstallResult.Ok("x") with { PartialUpgrade = true, InstalledVersion = "7.6.10.0" } };
        var held = new UserInstallResultMessage { UpdateKey = "k", Result = InstallResult.Fail("y", Mismatch) with { TechnologyMismatch = true } };

        var p = Assert.IsType<UserInstallResultMessage>(IpcJson.Deserialize(IpcJson.Serialize(partial))).Result;
        var h = Assert.IsType<UserInstallResultMessage>(IpcJson.Deserialize(IpcJson.Serialize(held))).Result;
        var old = Assert.IsType<UserInstallResultMessage>(IpcJson.Deserialize(
            "{\"$type\":\"userInstallResult\",\"updateKey\":\"k\",\"result\":{\"success\":false,\"exitCode\":-1978335090,\"message\":\"x\"}}")).Result;

        Assert.True(p.PartialUpgrade);
        Assert.Equal("7.6.10.0", p.InstalledVersion);
        Assert.True(h.TechnologyMismatch);
        Assert.False(old.TechnologyMismatch);
        Assert.False(old.PartialUpgrade);
    }

    // ---------------------------------------------------------------- part 1: the policy

    private static readonly DateTimeOffset T0 = new(2026, 11, 11, 17, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(4);

    private static ScanOutcome Scan(string installed = "7.6.9.0", string available = "7.7.0.0", bool replace = false) => new(new UpdateCheckResult
    {
        AppId = "powershell",
        Source = UpdateSource.Winget,
        IsInstalled = true,
        InstalledVersion = installed,
        AvailableVersion = available,
        UpdateAvailable = true,
        WingetId = Id,
        ResolvedContext = InstallContext.System,
    }, PowerShell with { AutoInstall = true, WingetReplaceOnMismatch = replace }, InstallContext.System, null);

    private static (Dictionary<string, PendingUpdate> State, PendingUpdate Update) Tracked()
    {
        var state = new Dictionary<string, PendingUpdate>(StringComparer.OrdinalIgnoreCase);
        var outcome = Scan();
        PolicyEngine.Merge(state, [outcome], new HashSet<string> { outcome.Key }, T0);
        return (state, state.Values.Single());
    }

    private static void Rescan(Dictionary<string, PendingUpdate> state, DateTimeOffset at, string installed = "7.6.10.0", string available = "7.7.0.0", bool replace = false)
    {
        var outcome = Scan(installed, available, replace);
        PolicyEngine.Merge(state, [outcome], new HashSet<string> { outcome.Key }, at);
    }

    private static InstallResult PartialResult => InstallResult.Ok("Installed 7.6.10.0; ...") with { InstalledVersion = "7.6.10.0", PartialUpgrade = true };

    [Fact]
    public void A_partial_upgrade_keeps_the_target_pending_without_grace_or_failure()
    {
        var (state, u) = Tracked();
        PolicyEngine.RequestInstall(u, T0);
        PolicyEngine.MarkInstalling(u, T0.AddMinutes(1));

        PolicyEngine.MarkPartiallyInstalled(u, PartialResult, T0.AddMinutes(2));

        Assert.Equal(UpdateState.Available, u.State);
        Assert.Equal("7.6.10.0", u.InstalledVersion);
        Assert.Equal("7.7.0.0", u.AvailableVersion);
        Assert.Null(u.InstalledAtUtc);
        Assert.False(u.InstallVerified);
        Assert.Equal(0, u.FailureCount);
        Assert.Null(u.LastError);
        Assert.False(u.InstallRequested);
        Assert.Null(u.PrioritizedUtc);
        Assert.Null(u.InstallPhase);

        // A scan right after it still offers 7.7.0.0: no post-install grace hides it, and nothing counts as a failure.
        Rescan(state, T0.AddMinutes(3));
        Assert.Equal(UpdateState.Available, u.State);
        Assert.Equal("7.6.10.0", u.InstalledVersion);
        Assert.Equal(0, u.FailureCount);
        Assert.Equal(PolicyActionKind.Install, PolicyEngine.Decide(u, T0.AddMinutes(3), Interval, false).Kind);
    }

    [Fact]
    public void The_history_entry_event_and_toast_say_the_version_reached()
    {
        var (_, u) = Tracked();

        var entry = InstallHistory.Succeeded(u, PartialResult, T0);
        PolicyEngine.MarkPartiallyInstalled(u, PartialResult, T0);
        var (_, body) = UpdateCoordinator.ComposeNotification(u, NotificationKind.Installed);
        var note = UpdateCoordinator.InstalledNotificationNote(u, PartialResult);
        var text = UpdateCoordinator.InstallSucceededEventText("PowerShell 7", PartialResult, TimeSpan.FromSeconds(62), "7.7.0.0");

        Assert.Equal("7.6.9.0", entry.FromVersion);
        Assert.Equal("7.6.10.0", entry.ToVersion);
        Assert.True(entry.Succeeded);
        Assert.Contains("7.6.10.0 was installed", body);
        Assert.Contains("7.7.0.0", note);
        Assert.StartsWith("PowerShell 7 7.6.10.0 installed in 1 min 2 s; 7.7.0.0 still pending: Installed 7.6.10.0", text);
    }

    // ---------------------------------------------------------------- part 2: the refusal without the take-over

    [Fact]
    public async Task The_refusal_without_the_take_over_is_a_hold()
    {
        var winget = new FakeWinget { UpgradeExit = Mismatch, Listed = "7.6.10.0" };

        var result = await Provider(winget).InstallAsync(PowerShell, Pending(installed: "7.6.10.0"), ExecutionContextInfo.System, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.TechnologyMismatch);
        Assert.Equal(Mismatch, result.ExitCode);
        Assert.Contains("Replace mismatched installs", result.Message);
        Assert.DoesNotContain(winget.Calls, c => c.StartsWith("uninstall ", StringComparison.Ordinal) || c.StartsWith("install ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(Mismatch, false, false, true)]
    [InlineData(Mismatch, true, false, false)]  // the take-over (or "install --force") ran: its own result stands
    [InlineData(Mismatch, false, true, false)]  // a busy Windows Installer is a hold of its own
    [InlineData(WingetOutputParser.ExitNoApplicableUpgrade, false, false, false)]
    [InlineData(-1, false, false, false)]
    public void Only_the_plain_refusal_is_a_hold(int exitCode, bool fallbackRan, bool busy, bool hold)
    {
        var result = InstallResult.Fail("x", exitCode) with { InstallerBusy = busy };

        Assert.Equal(hold, WingetProvider.IsTechnologyMismatchHold(result, fallbackRan));
    }

    [Fact]
    public void A_held_version_is_not_installed_prompted_or_failed()
    {
        var (_, u) = Tracked();
        u.ProcessNames = ["pwsh"];
        PolicyEngine.MarkInstalling(u, T0.AddMinutes(1));

        PolicyEngine.MarkTechnologyMismatch(u, "winget offers 7.7.0.0 only with a different installer type", T0.AddMinutes(2));

        Assert.Equal(UpdateState.Available, u.State);
        Assert.Equal("7.7.0.0", u.TechnologyMismatchVersion);
        Assert.Equal(0, u.FailureCount);
        Assert.Null(u.FailedAtUtc);
        Assert.Contains("installer type", u.LastError);
        Assert.True(u.IsHeldAsTechnologyMismatch());
        // No time limit.
        Assert.Equal(PolicyActionKind.None, PolicyEngine.Decide(u, T0.AddDays(30), Interval, false).Kind);
        Assert.Equal(PolicyActionKind.None, PolicyEngine.Decide(u, T0.AddDays(30), Interval, blockingProcessesRunning: true).Kind);
    }

    [Fact]
    public void A_mandatory_update_past_its_deadline_is_held_too()
    {
        var (_, u) = Tracked();
        u.Mandatory = true;
        u.DeadlineUtc = T0.AddHours(1);
        u.ForceCloseAtDeadline = true;
        PolicyEngine.MarkTechnologyMismatch(u, "held", T0);

        Assert.Equal(PolicyActionKind.None, PolicyEngine.Decide(u, T0.AddHours(2), Interval, false).Kind);
        Assert.Equal(PolicyActionKind.None, PolicyEngine.Decide(u, T0.AddHours(2), Interval, blockingProcessesRunning: true).Kind);
    }

    [Fact]
    public void A_scan_of_the_same_version_keeps_the_hold_and_a_newer_version_ends_it()
    {
        var (state, u) = Tracked();
        PolicyEngine.MarkTechnologyMismatch(u, "held", T0);

        Rescan(state, T0.AddHours(4));
        Assert.True(u.IsHeldAsTechnologyMismatch());

        Rescan(state, T0.AddHours(8), available: "7.7.1.0");
        Assert.False(u.IsHeldAsTechnologyMismatch());
        Assert.Null(u.TechnologyMismatchVersion);
        Assert.Null(u.LastError);
        Assert.Equal(PolicyActionKind.Install, PolicyEngine.Decide(u, T0.AddHours(8), Interval, false).Kind);
    }

    [Fact]
    public void Switching_the_take_over_on_ends_the_hold()
    {
        var (state, u) = Tracked();
        PolicyEngine.MarkTechnologyMismatch(u, "held", T0);

        // The policy tick copies the setting over at once; a scan does it too.
        u.WingetReplaceOnMismatch = true;
        Assert.False(u.IsHeldAsTechnologyMismatch());
        Assert.Equal(PolicyActionKind.Install, PolicyEngine.Decide(u, T0.AddMinutes(1), Interval, false).Kind);

        u.WingetReplaceOnMismatch = false;
        Rescan(state, T0.AddMinutes(2), replace: true);
        Assert.True(u.WingetReplaceOnMismatch);
        Assert.False(u.IsHeldAsTechnologyMismatch());
    }

    [Fact]
    public void The_users_update_tries_again_and_may_be_held_again()
    {
        var (_, u) = Tracked();
        PolicyEngine.MarkTechnologyMismatch(u, "held", T0);

        PolicyEngine.RequestInstall(u, T0.AddMinutes(1));

        Assert.Null(u.TechnologyMismatchVersion);
        Assert.Equal(PolicyActionKind.Install, PolicyEngine.Decide(u, T0.AddMinutes(1), Interval, false).Kind);

        PolicyEngine.MarkInstalling(u, T0.AddMinutes(2));
        PolicyEngine.MarkTechnologyMismatch(u, "held again", T0.AddMinutes(3));
        Assert.True(u.IsHeldAsTechnologyMismatch());
        Assert.False(u.InstallRequested);
        Assert.Null(u.PrioritizedUtc);
        Assert.Equal(0, u.FailureCount);
    }

    [Fact]
    public void The_hold_survives_the_state_file_and_an_older_state_file_has_none()
    {
        var message = new StateMessage { Updates = [new PendingUpdate { AppId = "powershell", AvailableVersion = "7.7.0.0", TechnologyMismatchVersion = "7.7.0.0" }] };

        var back = Assert.IsType<StateMessage>(IpcJson.Deserialize(IpcJson.Serialize(message))).Updates.Single();
        var old = System.Text.Json.JsonSerializer.Deserialize<PendingUpdate>("{\"appId\":\"powershell\",\"availableVersion\":\"7.7.0.0\"}", IpcJson.Options)!;

        Assert.Equal("7.7.0.0", back.TechnologyMismatchVersion);
        Assert.True(back.IsHeldAsTechnologyMismatch());
        Assert.Null(old.TechnologyMismatchVersion);
        Assert.False(old.IsHeldAsTechnologyMismatch());
    }

    // ---------------------------------------------------------------- part 3: the take-over as LocalSystem, MSI -> MSIX

    private static Func<string, bool, CancellationToken, Task<AppxProbeResult>> Probe(AppxProbeResult result, List<(string Key, bool ByName)>? calls = null) =>
        (key, byName, _) => { calls?.Add((key, byName)); return Task.FromResult(result); };

    private static readonly AppxProbeResult Provisioned = new(
        [new AppxPackageRegistration("Microsoft.PowerShell_7.7.0.0_x64__8wekyb3d8bbwe", "7.7.0.0", "X64", [], Family)],
        [new AppxProvisionedPackage("Microsoft.PowerShell", "7.7.0.0", "Microsoft.PowerShell_7.7.0.0_x64__8wekyb3d8bbwe")],
        []);

    [Fact]
    public async Task The_take_over_provisions_the_msix_and_the_package_registrations_verify_it()
    {
        var winget = new FakeWinget { UpgradeExit = Mismatch, Listed = "7.6.10.0", ListedAfterInstall = null };
        var probes = new List<(string, bool)>();

        var result = await Provider(winget, Probe(Provisioned, probes))
            .InstallAsync(PowerShell, Pending(installed: "7.6.10.0", replace: true), ExecutionContextInfo.System, null, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal("7.7.0.0", result.InstalledVersion);
        Assert.Equal(Family, result.ProvisionedPackageFamilyName);
        Assert.False(result.TechnologyMismatch);
        // The machine-scope show came before the uninstall, and the install asked for the MSIX installer, without the MSI's switches.
        Assert.True(winget.Calls.FindIndex(c => c.StartsWith("show --id ", StringComparison.Ordinal) && c.Contains("--scope machine"))
                    < winget.Calls.FindIndex(c => c.StartsWith("uninstall ", StringComparison.Ordinal)));
        Assert.Contains("--scope machine --installer-type msix", winget.Install);
        Assert.DoesNotContain("--custom", winget.Install);
        // No family from SYSTEM's listing: the packages are looked up by the winget id as their name.
        Assert.Equal([(Id, true)], probes);
    }

    [Fact]
    public async Task When_the_listing_shows_the_msix_it_verifies_and_names_the_family()
    {
        var details = "PowerShell [Microsoft.PowerShell]\r\nVersion: 7.7.0.0\r\nLocal Identifier: MSIX\\Microsoft.PowerShell_7.7.0.0_x64__8wekyb3d8bbwe\r\n" +
                      "Package Family Name: microsoft.powershell_8wekyb3d8bbwe\r\nInstaller Category: msix\r\n";
        var winget = new FakeWinget { UpgradeExit = Mismatch, Listed = "7.6.10.0", ListedAfterInstall = "7.7.0.0", DetailsOutput = details };
        var probes = new List<(string, bool)>();

        var result = await Provider(winget, Probe(Provisioned, probes))
            .InstallAsync(PowerShell, Pending(installed: "7.6.10.0", replace: true), ExecutionContextInfo.System, null, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal("7.7.0.0", result.InstalledVersion);
        Assert.Equal(Family, result.ProvisionedPackageFamilyName);
        Assert.Equal([("microsoft.powershell_8wekyb3d8bbwe", false)], probes);
    }

    [Fact]
    public async Task Without_any_proof_the_take_over_still_fails_and_says_the_application_may_be_missing()
    {
        var winget = new FakeWinget { UpgradeExit = Mismatch, Listed = "7.6.10.0", ListedAfterInstall = null };

        var result = await Provider(winget, Probe(new AppxProbeResult([], [], [])))
            .InstallAsync(PowerShell, Pending(installed: "7.6.10.0", replace: true), ExecutionContextInfo.System, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("may now be missing", result.Message);
        Assert.Null(result.ProvisionedPackageFamilyName);
    }

    [Fact]
    public async Task No_machine_scope_installer_means_nothing_is_uninstalled()
    {
        var winget = new FakeWinget
        {
            UpgradeExit = Mismatch, Listed = "7.6.10.0",
            ShowExit = WingetOutputParser.ExitNoApplicableInstaller, ShowOutput = "No applicable installer found; see logs for more details.",
        };

        var result = await Provider(winget).InstallAsync(PowerShell, Pending(installed: "7.6.10.0", replace: true), ExecutionContextInfo.System, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("left in place", result.Message);
        Assert.DoesNotContain(winget.Calls, c => c.StartsWith("uninstall ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, 0, "Installer:\r\n  Installer Type: msix\r\n", true, "msix")]
    [InlineData(true, 0, "Installer:\r\n  Installer Type: wix\r\n", true, null)]
    [InlineData(true, WingetOutputParser.ExitNoApplicableInstaller, "No applicable installer found", false, null)]
    [InlineData(true, 0, "Installer:\r\n  No applicable installer found; see logs for more details.\r\n", false, null)]
    [InlineData(true, 1, "", true, null)]   // winget could not say: as before
    [InlineData(false, 0, "", true, null)]
    public void The_machine_scope_show_plans_the_take_over(bool answered, int exitCode, string output, bool proceed, string? installerType)
    {
        Assert.Equal((proceed, installerType), WingetProvider.PlanSystemTakeOver(answered, exitCode, output));
    }

    [Theory]
    [InlineData("--custom USE_MU=0", null, "--custom USE_MU=0")]
    [InlineData("--custom \"A=1 B=2\" --architecture x64", "--architecture x64", "--custom \"A=1 B=2\"")]
    [InlineData("--override=/qn --locale en-US", "--locale en-US", "--override=/qn")]
    [InlineData("--architecture arm64", "--architecture arm64", null)]
    [InlineData("--CUSTOM X --log C:\\a.log", "--log C:\\a.log", "--CUSTOM X")]
    public void An_msix_install_leaves_the_installer_switches_off(string arguments, string? kept, string? removed)
    {
        Assert.Equal(kept, WingetProvider.StripInstallerSwitches(arguments, out var dropped));
        Assert.Equal(removed, dropped);
    }

    [Fact]
    public void The_registrations_verify_only_a_package_at_the_target_or_newer()
    {
        var older = new AppxProbeResult([new AppxPackageRegistration("Microsoft.PowerShell_7.6.0.0_x64__8wekyb3d8bbwe", "7.6.0.0", "X64", [], Family)], [], []);

        Assert.Equal(("7.7.0.0", Family), WingetProvider.JudgeProvisionedMsix(Provisioned, "7.7.0.0", null));
        Assert.Equal(("7.7.0.0", Family), WingetProvider.JudgeProvisionedMsix(Provisioned, "7.7.0.0", "microsoft.powershell_8wekyb3d8bbwe"));
        Assert.Equal((null, Family), WingetProvider.JudgeProvisionedMsix(older, "7.7.0.0", null));
        Assert.Equal((null, null), WingetProvider.JudgeProvisionedMsix(AppxProbeResult.Failed("no PowerShell"), "7.7.0.0", null));
    }

    [Fact]
    public void A_provisioned_bundle_never_gives_the_installed_version()
    {
        // CPC-hsk-HSNE00X, 2026-11-12 19:17Z: the take-over provisioned PowerShell's msixbundle; the probe found the
        // package 7.7.0.0 registered for the user and the bundle provisioned as 2026.1110.418.0, and the old rule recorded
        // "installed 2026.1110.418.0".
        const string bundle = "Microsoft.PowerShell_2026.1110.418.0_neutral_~_8wekyb3d8bbwe";
        var probe = new AppxProbeResult(
            [new AppxPackageRegistration("Microsoft.PowerShell_7.7.0.0_x64__8wekyb3d8bbwe", "7.7.0.0", "X64",
                [new AppxPackageUser("S-1-12-1-1760109370-1192172739-1438914203-1853124917", "Installed")], Family)],
            [new AppxProvisionedPackage("Microsoft.PowerShell", "2026.1110.418.0", bundle)],
            []);

        Assert.True(AppxProbeResult.IsBundleFullName(bundle));
        Assert.False(AppxProbeResult.IsBundleFullName("Microsoft.PowerShell_7.7.0.0_x64__8wekyb3d8bbwe"));
        Assert.False(AppxProbeResult.IsBundleFullName(null));
        Assert.Equal(("7.7.0.0", Family), WingetProvider.JudgeProvisionedMsix(probe, "7.7.0.0", null));
        Assert.Equal("7.7.0.0", probe.HighestVersion);

        // Only the bundle provisioned, no package listed: nothing proves the version.
        var bundleOnly = new AppxProbeResult([], [new AppxProvisionedPackage("Microsoft.PowerShell", "2026.1110.418.0", bundle)], []);
        Assert.Null(WingetProvider.JudgeProvisionedMsix(bundleOnly, "7.7.0.0", Family).Version);
        Assert.Null(bundleOnly.HighestVersion);

        // A provisioned package (not a bundle) still counts when no package is listed.
        var packageOnly = new AppxProbeResult([], [new AppxProvisionedPackage("Microsoft.PowerShell", "7.7.0.0", "Microsoft.PowerShell_7.7.0.0_x64__8wekyb3d8bbwe")], []);
        Assert.Equal("7.7.0.0", WingetProvider.JudgeProvisionedMsix(packageOnly, "7.7.0.0", Family).Version);
        Assert.Equal("7.7.0.0", packageOnly.HighestVersion);
    }

    [Fact]
    public void Packages_of_two_publishers_with_one_name_prove_nothing()
    {
        var two = new AppxProbeResult(
            [new AppxPackageRegistration("Microsoft.PowerShell_7.7.0.0_x64__8wekyb3d8bbwe", "7.7.0.0", "X64", [], Family),
             new AppxPackageRegistration("Microsoft.PowerShell_7.8.0.0_x64__abcdefghjkmnp", "7.8.0.0", "X64", [], "Microsoft.PowerShell_abcdefghjkmnp")],
            [], []);

        Assert.Null(WingetProvider.JudgeProvisionedMsix(two, "7.7.0.0", null).Version);
        // With the family known, only its packages count.
        Assert.Equal(("7.7.0.0", Family), WingetProvider.JudgeProvisionedMsix(two, "7.7.0.0", Family));
    }

    [Theory]
    [InlineData("Microsoft.PowerShell_7.7.0.0_x64__8wekyb3d8bbwe", Family)]
    [InlineData("Microsoft.PowerShell_7.7.0.0_neutral_~_8wekyb3d8bbwe", Family)]
    [InlineData("Microsoft.PowerShell_8wekyb3d8bbwe", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_family_of_a_full_name(string? fullName, string? family)
    {
        Assert.Equal(family, AppxPackageProbe.FamilyFromFullName(fullName));
    }

    [Fact]
    public void The_probe_by_name_has_no_double_quotes_reads_the_family_and_refuses_an_invalid_name()
    {
        var script = AppxPackageProbe.BuildScriptForName(Id);
        var parsed = AppxPackageProbe.Parse(
            "{\"packages\":{\"fullName\":\"Microsoft.PowerShell_7.7.0.0_x64__8wekyb3d8bbwe\",\"version\":\"7.7.0.0\",\"architecture\":\"X64\",\"family\":\"Microsoft.PowerShell_8wekyb3d8bbwe\",\"users\":null}," +
            "\"provisioned\":{\"displayName\":\"Microsoft.PowerShell\",\"version\":\"7.7.0.0\",\"packageName\":\"Microsoft.PowerShell_7.7.0.0_x64__8wekyb3d8bbwe\"},\"errors\":[]}");

        Assert.DoesNotContain('"', script);
        Assert.Contains("$name='Microsoft.PowerShell'", script);
        Assert.Contains("$_.Name -eq $name", script);
        Assert.Equal(Family, Assert.Single(parsed.Packages).PackageFamilyName);
        Assert.Equal(Family, Assert.Single(parsed.Provisioned).PackageFamilyName);
        Assert.Throws<ArgumentException>(() => AppxPackageProbe.BuildScriptForName("x'; exit; '"));
        Assert.False(AppxPackageProbe.IsValidPackageName("ab"));
    }

    [Fact]
    public void The_service_asks_the_trays_to_register_the_family_and_an_older_tray_ignores_it()
    {
        var message = new RegisterPackageMessage { PackageFamilyName = Family, AppId = "powershell", DisplayName = "PowerShell 7", Version = "7.7.0.0" };

        var back = Assert.IsType<RegisterPackageMessage>(IpcJson.Deserialize(IpcJson.Serialize(message)));

        Assert.Equal(Family, back.PackageFamilyName);
        Assert.Equal("7.7.0.0", back.Version);
    }

    // ---------------------------------------------------------------- detection after the switch

    [Theory]
    [InlineData("PowerShell 7-x64", true)]
    [InlineData("PowerShell 7-arm64", true)]
    [InlineData("PowerShell 7.6.10.0-x64", true)]
    [InlineData("PowerShell", true)]                  // the MSIX package's display name
    [InlineData("PowerShell 7-preview-x64", false)]
    [InlineData("PowerShell 7-Preview-arm64", false)]
    [InlineData("PowerShell Preview", false)]
    [InlineData("Microsoft.PowerShellPreview", false)]
    [InlineData("Windows PowerShell", false)]
    [InlineData("PowerShell Universal", false)]
    public void The_catalogs_rule_finds_the_msi_and_the_msix_but_no_preview(string name, bool matches)
    {
        var catalog = new JsonCatalogProvider(NullLogger<JsonCatalogProvider>.Instance).GetCatalog(RepositoryCatalogPath());
        var app = catalog.Single(a => a.AppId == "powershell");

        Assert.Equal(matches, InstalledAppScanner.NameMatches(app, name));
    }

    private static string RepositoryCatalogPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "catalog", "catalog.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate catalog/catalog.json above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void The_scan_resolves_the_msix_row_by_name_when_the_configured_id_is_not_listed()
    {
        var rows = new[]
        {
            new WingetRow("PowerShell", Id, "7.7.0.0", "", "winget"),
            new WingetRow("PowerShell 7-preview-x64", "Microsoft.PowerShell.Preview", "7.8.0.1", "", "winget"),
        };

        var row = WingetProvider.ResolveByName(rows, PowerShell with { WingetId = "Some.Other.Id" }, ["Some.Other.Id"]);

        Assert.Equal(Id, row?.Id);
    }
}
