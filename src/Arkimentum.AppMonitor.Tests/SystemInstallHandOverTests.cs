using System.Text;
using Arkimentum.AppMonitor.Install;
using Arkimentum.AppMonitor.Ipc;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Providers;
using Arkimentum.AppMonitor.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// DESKTOP-V1CDE3I (2026-09-29): Microsoft.WindowsAppRuntime.1.6 1.6.6 is registered for the user as an MSIX framework
/// package, winget offers 1.6.9, and its manifest only has an exe that needs elevation and declares no Scope - so
/// <c>--scope user</c> and <c>--installer-type msix</c> both answer "no applicable installer", and the SYSTEM service
/// never sees the package (MSIX registrations are per user). The tray then hands the install to the service, which runs
/// an unscoped <c>winget install</c> as SYSTEM and reports the package registrations before and after.
/// </summary>
public class SystemInstallHandOverTests
{
    private static readonly ExecutionContextInfo User = new() { IsSystem = false, UserSid = "S-1-5-21-1-2-3-1001", SessionId = 1 };

    private const string RuntimeId = "Microsoft.WindowsAppRuntime.1.6";
    private const string Family = "microsoft.windowsappruntime.1.6_8wekyb3d8bbwe";

    private static readonly AppPolicy Runtime = new()
    {
        AppId = "windows-app-runtime-1.6",
        DisplayName = "Windows App Runtime 1.6",
        WingetId = RuntimeId,
        Context = InstallContext.User,
    };

    private static PendingUpdate Pending(string? alternatives = null) => new()
    {
        AppId = "windows-app-runtime-1.6",
        DisplayName = "Windows App Runtime 1.6",
        Source = UpdateSource.Winget,
        Context = InstallContext.User,
        UserSid = User.UserSid,
        InstalledVersion = "1.6.6",
        AvailableVersion = "1.6.9",
        WingetId = RuntimeId,
        WingetIdAlternatives = alternatives,
        WingetSourceName = "winget",
    };

    // ---------------------------------------------------------------- winget list --details

    /// <summary>What winget 1.29 printed on the device for <c>winget list --id … --exact --details --scope user</c>.</summary>
    private const string DetailsSample =
        "   - \r\n   \\ \r\n" +
        "(1/2) WindowsAppRuntime.1.6 [Microsoft.WindowsAppRuntime.1.6]\r\n" +
        "Version: 1.6.9\r\n" +
        "Local Identifier: MSIX\\Microsoft.WindowsAppRuntime.1.6_6000.519.329.0_x86__8wekyb3d8bbwe\r\n" +
        "Package Family Name: microsoft.windowsappruntime.1.6_8wekyb3d8bbwe\r\n" +
        "Installer Category: msix\r\n" +
        "Installed Architecture: X86\r\n" +
        "Installed Location: C:\\Program Files\\WindowsApps\\Microsoft.WindowsAppRuntime.1.6_6000.519.329.0_x86__8wekyb3d8bbwe\r\n" +
        "(2/2) WindowsAppRuntime.1.6 [Microsoft.WindowsAppRuntime.1.6]\r\n" +
        "Version: 1.6.9\r\n" +
        "Local Identifier: MSIX\\Microsoft.WindowsAppRuntime.1.6_6000.519.329.0_x64__8wekyb3d8bbwe\r\n" +
        "Package Family Name: microsoft.windowsappruntime.1.6_8wekyb3d8bbwe\r\n" +
        "Installer Category: msix\r\n" +
        "Installed Architecture: X64\r\n";

    [Fact]
    public void List_details_are_parsed_into_one_record_per_installed_row()
    {
        var rows = WingetOutputParser.ParseListDetails(DetailsSample);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal(("WindowsAppRuntime.1.6", RuntimeId, "1.6.9"), (r.Name, r.Id, r.Version));
            Assert.Equal(Family, r.PackageFamilyName);
            Assert.Equal("msix", r.InstallerCategory);
        });
        Assert.Equal(["X86", "X64"], rows.Select(r => r.Architecture));
        Assert.Equal(@"MSIX\Microsoft.WindowsAppRuntime.1.6_6000.519.329.0_x64__8wekyb3d8bbwe", rows[1].LocalIdentifier);
    }

    [Fact]
    public void A_single_row_without_a_counter_and_with_lf_line_ends_is_parsed()
    {
        var output = DetailsSample.Replace("\r\n", "\n").Replace("(1/2) ", string.Empty);
        output = output[..output.IndexOf("(2/2)", StringComparison.Ordinal)];

        var row = Assert.Single(WingetOutputParser.ParseListDetails(output));

        Assert.Equal(RuntimeId, row.Id);
        Assert.Equal("X86", row.Architecture);
    }

    [Fact]
    public void Missing_fields_stay_null_and_unknown_lines_are_ignored()
    {
        const string output = "Windows Package Manager v1.29\r\nFound Contoso App [Contoso.App]\r\nPublisher: Contoso\r\nInstaller Category: exe\r\n";

        var row = Assert.Single(WingetOutputParser.ParseListDetails(output));

        Assert.Equal(("Contoso App", "Contoso.App", "exe"), (row.Name, row.Id, row.InstallerCategory));
        Assert.Null(row.Version);
        Assert.Null(row.PackageFamilyName);
        Assert.Null(row.LocalIdentifier);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("No installed package found matching input criteria.")]
    [InlineData("Version: 1.2.3\r\nInstaller Category: msix\r\n")]
    public void Output_without_a_record_gives_no_rows(string? output)
    {
        Assert.Empty(WingetOutputParser.ParseListDetails(output));
    }

    [Theory]
    [InlineData(@"MSIX\Microsoft.WindowsAppRuntime.1.6_6000.519.329.0_x64__8wekyb3d8bbwe", "6000.519.329.0")]
    [InlineData(@"msix\Microsoft.WindowsAppRuntime.1.6_6000.519.297.0_x86__8wekyb3d8bbwe", "6000.519.297.0")]
    [InlineData("Microsoft.VCLibs.140.00_14.0.33519.0_x64__8wekyb3d8bbwe", "14.0.33519.0")]
    [InlineData(@"ARP\Machine\X64\{89850E15-1C2B-4F1E-9D2A-5B7E0F3A6C11}", null)]
    [InlineData(@"MSIX\Microsoft.WindowsAppRuntime.1.6_x64__8wekyb3d8bbwe", null)]
    [InlineData(@"MSIX\Name_notaversion_x64__8wekyb3d8bbwe", null)]
    [InlineData(null, null)]
    public void The_package_version_is_read_from_the_local_identifier(string? localIdentifier, string? expected)
    {
        Assert.Equal(expected, WingetOutputParser.MsixPackageVersion(localIdentifier));
    }

    // ---------------------------------------------------------------- the rules

    private static WingetInstalledDetails Row(string category = "msix", string? family = Family, string arch = "X64", string package = "6000.519.297.0", string id = RuntimeId) =>
        new("WindowsAppRuntime.1.6", id, "1.6.6", $@"MSIX\Microsoft.WindowsAppRuntime.1.6_{package}_{arch.ToLowerInvariant()}__8wekyb3d8bbwe", family, category, arch);

    [Fact]
    public void Every_row_an_msix_package_of_one_family_is_handed_over()
    {
        Assert.True(WingetProvider.ShouldHandOverToSystem([Row(arch: "X86"), Row(family: Family.ToUpperInvariant())], RuntimeId, out var family));
        Assert.Equal(Family, family, ignoreCase: true);
    }

    [Fact]
    public void Rows_of_other_ids_do_not_count()
    {
        Assert.True(WingetProvider.ShouldHandOverToSystem([Row(), Row(category: "exe", id: "Other.Package")], RuntimeId, out _));
    }

    [Theory]
    [InlineData("exe", Family)]
    [InlineData("msi", Family)]
    [InlineData(null, Family)]
    [InlineData("msix", null)]
    [InlineData("msix", "")]
    public void A_row_that_is_not_an_msix_package_with_a_family_keeps_the_old_failure(string? category, string? family)
    {
        Assert.False(WingetProvider.ShouldHandOverToSystem([Row(), Row(category: category!, family: family)], RuntimeId, out var pfn));
        Assert.Null(pfn);
    }

    [Fact]
    public void No_rows_or_two_families_are_not_handed_over()
    {
        Assert.False(WingetProvider.ShouldHandOverToSystem([], RuntimeId, out _));
        Assert.False(WingetProvider.ShouldHandOverToSystem([Row(), Row(family: "microsoft.other_8wekyb3d8bbwe")], RuntimeId, out _));
    }

    private static SystemInstallHandOverReply Reply(bool ok = true, string? machine = "6000.519.329.0") => new(ok, "evidence", 0, machine);

    [Theory]
    [InlineData("1.6.9", "6000.519.329.0", HandOverVerdict.Installed)]
    [InlineData("1.7.0", null, HandOverVerdict.Installed)]
    [InlineData("1.6.6", "6000.519.329.0", HandOverVerdict.PendingSignIn)]
    [InlineData(null, "6000.519.329.0", HandOverVerdict.PendingSignIn)]
    [InlineData("1.6.6", "6000.519.297.0", HandOverVerdict.NoNewerPackage)]
    [InlineData("1.6.6", "6000.519.100.0", HandOverVerdict.NoNewerPackage)]
    [InlineData("1.6.6", null, HandOverVerdict.NoNewerPackage)]
    [InlineData("Unknown", null, HandOverVerdict.NoNewerPackage)]
    public void The_hand_over_is_judged_by_the_users_version_then_the_machines_package(string? userAfter, string? machine, HandOverVerdict expected)
    {
        Assert.Equal(expected, WingetProvider.JudgeHandOver(Reply(machine: machine), userAfter, "1.6.9", "6000.519.297.0"));
    }

    [Fact]
    public void A_failed_or_missing_reply_is_a_failed_hand_over()
    {
        Assert.Equal(HandOverVerdict.Failed, WingetProvider.JudgeHandOver(null, "1.6.9", "1.6.9", "6000.519.297.0"));
        Assert.Equal(HandOverVerdict.Failed, WingetProvider.JudgeHandOver(Reply(ok: false), "1.6.9", "1.6.9", "6000.519.297.0"));
    }

    [Fact]
    public void Without_the_users_package_version_a_newer_machine_package_proves_nothing()
    {
        Assert.Equal(HandOverVerdict.NoNewerPackage, WingetProvider.JudgeHandOver(Reply(), "1.6.6", "1.6.9", null));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(WingetOutputParser.ExitNoApplicableUpgrade, true)]
    [InlineData(WingetOutputParser.ExitPackageAlreadyInstalled, true)]
    [InlineData(WingetOutputParser.ExitRebootRequiredToFinish, true)]
    [InlineData(WingetOutputParser.ExitNoApplicableInstaller, false)]
    [InlineData(1603, false)]
    public void Already_installed_is_not_an_error_for_the_install_for_all_users(int exitCode, bool success)
    {
        Assert.Equal(success, WingetProvider.InterpretAllUsersInstallExit(exitCode).Success);
    }

    // ---------------------------------------------------------------- the service's rules

    [Theory]
    [InlineData("Microsoft.WindowsAppRuntime.1.6_8wekyb3d8bbwe", true)]
    [InlineData(Family, true)]
    [InlineData("Microsoft.VCLibs.140.00.UWPDesktop_8wekyb3d8bbwe", true)]
    [InlineData("Microsoft.WindowsAppRuntime.1.6", false)]
    [InlineData("Microsoft.WindowsAppRuntime.1.6_8wekyb3d8bbw", false)]
    [InlineData("Evil'; Remove-Item C:\\ -Recurse; '_8wekyb3d8bbwe", false)]
    [InlineData("Name with space_8wekyb3d8bbwe", false)]
    [InlineData("Name\"quote_8wekyb3d8bbwe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_a_well_formed_package_family_name_is_accepted(string? family, bool valid)
    {
        Assert.Equal(valid, AppxPackageProbe.IsValidPackageFamilyName(family));
    }

    [Fact]
    public void The_probe_script_has_no_double_quotes_and_refuses_an_invalid_family()
    {
        var script = AppxPackageProbe.BuildScript("Microsoft.WindowsAppRuntime.1.6_8wekyb3d8bbwe");

        Assert.DoesNotContain('"', script);
        Assert.Contains("$pfn='Microsoft.WindowsAppRuntime.1.6_8wekyb3d8bbwe'", script);
        Assert.Contains("$name='Microsoft.WindowsAppRuntime.1.6'", script);
        Assert.Throws<ArgumentException>(() => AppxPackageProbe.BuildScript("x'; exit; '_8wekyb3d8bbwe"));
    }

    [Fact]
    public void The_candidate_ids_are_the_updates_own_and_the_applications()
    {
        var ids = UpdateCoordinator.SystemInstallCandidateIds(Pending("Microsoft.WindowsAppRuntime.1.6.Alt"), Runtime with { WingetId = "Configured.One;Configured.Two" });

        Assert.Equal([RuntimeId, "Microsoft.WindowsAppRuntime.1.6.Alt", "Configured.One", "Configured.Two"], ids);
    }

    [Fact]
    public void A_hand_over_for_the_waiting_install_of_one_of_its_own_ids_is_accepted()
    {
        Assert.Null(UpdateCoordinator.RefuseSystemInstall(true, true, false, Pending(), Runtime, "microsoft.windowsappruntime.1.6", Family));
    }

    [Fact]
    public void Every_other_hand_over_is_refused()
    {
        var pending = Pending();
        Assert.NotNull(UpdateCoordinator.RefuseSystemInstall(false, true, false, pending, Runtime, RuntimeId, Family));   // nothing waiting
        Assert.NotNull(UpdateCoordinator.RefuseSystemInstall(true, false, false, pending, Runtime, RuntimeId, Family));   // another connection
        Assert.NotNull(UpdateCoordinator.RefuseSystemInstall(true, true, true, pending, Runtime, RuntimeId, Family));     // already handed over
        Assert.NotNull(UpdateCoordinator.RefuseSystemInstall(true, true, false, null, Runtime, RuntimeId, Family));
        Assert.NotNull(UpdateCoordinator.RefuseSystemInstall(true, true, false, pending, null, RuntimeId, Family));
        Assert.NotNull(UpdateCoordinator.RefuseSystemInstall(true, true, false, pending, Runtime, "Evil.Package", Family));
        Assert.NotNull(UpdateCoordinator.RefuseSystemInstall(true, true, false, pending, Runtime, null, Family));
        Assert.NotNull(UpdateCoordinator.RefuseSystemInstall(true, true, false, pending, Runtime, RuntimeId, "x'; Remove-Item C:\\; '_8wekyb3d8bbwe"));

        var machineWide = Pending();
        machineWide.Context = InstallContext.System;
        Assert.NotNull(UpdateCoordinator.RefuseSystemInstall(true, true, false, machineWide, Runtime, RuntimeId, Family));
        var web = Pending();
        web.Source = UpdateSource.Web;
        Assert.NotNull(UpdateCoordinator.RefuseSystemInstall(true, true, false, web, Runtime, RuntimeId, Family));
    }

    // ---------------------------------------------------------------- the service's evidence

    private const string ProbeArray =
        "{\"packages\":[{\"fullName\":\"Microsoft.WindowsAppRuntime.1.6_6000.519.329.0_x64__8wekyb3d8bbwe\",\"version\":\"6000.519.329.0\",\"architecture\":\"X64\"," +
        "\"users\":[\"S-1-5-18=Installed\",\"S-1-5-21-1-2-3-1001=Installed\"]}," +
        "{\"fullName\":\"Microsoft.WindowsAppRuntime.1.6_6000.519.297.0_x64__8wekyb3d8bbwe\",\"version\":\"6000.519.297.0\",\"architecture\":\"X64\",\"users\":[\"S-1-5-21-1-2-3-1001=Staged\"]}]," +
        "\"provisioned\":[{\"displayName\":\"Microsoft.WindowsAppRuntime.1.6\",\"version\":\"6000.519.329.0\"}],\"errors\":[]}";

    [Fact]
    public void The_probe_json_is_parsed_with_every_users_state()
    {
        var probe = AppxPackageProbe.Parse("WARNING: noise\r\n" + ProbeArray + "\r\n");

        Assert.Null(probe.Failure);
        Assert.Equal(2, probe.Packages.Count);
        Assert.Equal([new AppxPackageUser("S-1-5-18", "Installed"), new AppxPackageUser("S-1-5-21-1-2-3-1001", "Installed")], probe.Packages[0].Users);
        Assert.Equal("6000.519.329.0", Assert.Single(probe.Provisioned).Version);
        Assert.Equal("6000.519.329.0", probe.HighestVersion);
        Assert.Equal(
            "6000.519.329.0 X64 [S-1-5-18 Installed, S-1-5-21-1-2-3-1001 Installed]; 6000.519.297.0 X64 [S-1-5-21-1-2-3-1001 Staged]; provisioned 6000.519.329.0",
            probe.Summarize());
    }

    [Fact]
    public void A_list_of_one_that_arrives_as_an_object_or_a_string_is_still_a_list()
    {
        const string json = "{\"packages\":{\"fullName\":\"P_1.0.0.0_x64__8wekyb3d8bbwe\",\"version\":\"1.0.0.0\",\"architecture\":\"X64\",\"users\":\"S-1-5-21-9=Installed\"}," +
                            "\"provisioned\":{\"displayName\":\"P\",\"version\":\"2.0.0.0\"},\"errors\":\"Get-AppxProvisionedPackage: The requested operation requires elevation.\\r\\n\"}";

        var probe = AppxPackageProbe.Parse(json);

        var package = Assert.Single(probe.Packages);
        Assert.Equal(new AppxPackageUser("S-1-5-21-9", "Installed"), Assert.Single(package.Users));
        Assert.Equal("2.0.0.0", probe.HighestVersion);
        Assert.Equal("Get-AppxProvisionedPackage: The requested operation requires elevation.", Assert.Single(probe.Errors));
    }

    [Fact]
    public void Empty_lists_and_nulls_mean_nothing_found()
    {
        var probe = AppxPackageProbe.Parse("{\"packages\":[],\"provisioned\":null,\"errors\":[]}");

        Assert.Null(probe.Failure);
        Assert.Empty(probe.Packages);
        Assert.Null(probe.HighestVersion);
        Assert.Equal("no package of the family; not provisioned", probe.Summarize());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Access is denied.")]
    [InlineData("{not json")]
    public void Output_that_is_no_json_object_is_a_probe_failure(string? output)
    {
        var probe = AppxPackageProbe.Parse(output);

        Assert.NotNull(probe.Failure);
        Assert.Null(probe.HighestVersion);
        Assert.StartsWith("probe failed", probe.Summarize());
    }

    [Fact]
    public async Task The_probe_runs_windows_powershell_non_interactively()
    {
        string? exe = null, args = null;
        var probe = await AppxPackageProbe.RunAsync(NullLogger.Instance, Family, CancellationToken.None,
            (e, a, _, _) => { (exe, args) = (e, a); return Task.FromResult(new ProcessRunResult(0, ProbeArray, string.Empty)); });

        Assert.Equal("6000.519.329.0", probe.HighestVersion);
        Assert.EndsWith(@"WindowsPowerShell\v1.0\powershell.exe", exe, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"", args);
        Assert.Equal(2, args!.Count(c => c == '"'));
    }

    [Fact]
    public void The_services_summary_puts_the_outcome_and_the_state_afterwards_first()
    {
        var before = AppxPackageProbe.Parse("{\"packages\":{\"fullName\":\"x\",\"version\":\"6000.519.297.0\",\"architecture\":\"X64\",\"users\":\"S-1-5-21-1-2-3-1001=Installed\"}}");
        var after = AppxPackageProbe.Parse(ProbeArray);

        var text = UpdateCoordinator.DescribeSystemInstall(InstallResult.Ok("Installed successfully."), before, after);

        Assert.StartsWith("winget install for all users: exit 0x00000000; after: 6000.519.329.0 X64 [S-1-5-18 Installed", text);
        Assert.EndsWith("before: 6000.519.297.0 X64 [S-1-5-21-1-2-3-1001 Installed]; not provisioned", text);
        Assert.Contains("failed: boom", UpdateCoordinator.DescribeSystemInstall(InstallResult.Fail("boom", 1603), before, AppxProbeResult.Failed("timed out")));
    }

    [Fact]
    public void A_success_event_carries_the_providers_message_unless_it_is_the_generic_one()
    {
        Assert.Equal("7-Zip installed", UpdateCoordinator.InstallSucceededEventText("7-Zip", InstallResult.Ok("Installed successfully.")));
        Assert.Equal("7-Zip installed (reboot required)",
            UpdateCoordinator.InstallSucceededEventText("7-Zip", InstallResult.Ok("Installed successfully; a restart is required to complete the update.", 0, reboot: true)));
        Assert.Equal("Runtime installed (reboot required): Installed for all users; service: evidence",
            UpdateCoordinator.InstallSucceededEventText("Runtime", InstallResult.Ok("Installed for all users; service: evidence", 0, reboot: true)));
        Assert.Equal("7-Zip installed", UpdateCoordinator.InstallSucceededEventText("7-Zip", InstallResult.Ok()));
    }

    // ---------------------------------------------------------------- the messages

    [Fact]
    public void The_hand_over_messages_round_trip()
    {
        var request = new RequestSystemInstallMessage { UpdateKey = "k", WingetId = RuntimeId, PackageFamilyName = Family, UserPackageVersion = "6000.519.297.0" };
        var backRequest = Assert.IsType<RequestSystemInstallMessage>(IpcJson.Deserialize(IpcJson.Serialize(request)));
        Assert.Equal((request.MessageId, "k", RuntimeId, Family, "6000.519.297.0"),
            (backRequest.MessageId, backRequest.UpdateKey, backRequest.WingetId, backRequest.PackageFamilyName, backRequest.UserPackageVersion));

        var result = new SystemInstallResultMessage { InReplyTo = request.MessageId, UpdateKey = "k", Ok = true, Message = "m", ExitCode = 5, MachinePackageVersion = "6000.519.329.0" };
        var backResult = Assert.IsType<SystemInstallResultMessage>(IpcJson.Deserialize(IpcJson.Serialize(result)));
        Assert.Equal((request.MessageId, "k", true, "m", 5, "6000.519.329.0"),
            (backResult.InReplyTo, backResult.UpdateKey, backResult.Ok, backResult.Message, backResult.ExitCode, backResult.MachinePackageVersion));
        Assert.Contains("\"$type\":\"requestSystemInstall\"", IpcJson.Serialize(request));
        Assert.Contains("\"$type\":\"systemInstallResult\"", IpcJson.Serialize(result));
    }

    // ---------------------------------------------------------------- end to end, user context

    /// <summary>A winget table in winget's own layout.</summary>
    private static string Table(params string[][] rows)
    {
        string[] header = ["Name", "Id", "Version", "Available", "Source"];
        var widths = Enumerable.Range(0, header.Length)
            .Select(c => Math.Max(header[c].Length, rows.Select(r => r[c].Length).DefaultIfEmpty(0).Max()) + 1).ToArray();
        string Line(string[] cells) => string.Concat(cells.Select((v, c) => c == cells.Length - 1 ? v : v.PadRight(widths[c]))).TrimEnd();
        var sb = new StringBuilder();
        sb.Append(Line(header)).Append("\r\n").Append(new string('-', widths.Sum())).Append("\r\n");
        foreach (var r in rows) sb.Append(Line(r)).Append("\r\n");
        return sb.ToString();
    }

    private static string Details(string id, string category, string package, params string[] architectures)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < architectures.Length; i++)
        {
            var arch = architectures[i];
            sb.Append($"({i + 1}/{architectures.Length}) WindowsAppRuntime.1.6 [{id}]\r\n")
              .Append("Version: 1.6.6\r\n")
              .Append($"Local Identifier: MSIX\\{id}_{package}_{arch.ToLowerInvariant()}__8wekyb3d8bbwe\r\n")
              .Append($"Package Family Name: {id.ToLowerInvariant()}_8wekyb3d8bbwe\r\n")
              .Append($"Installer Category: {category}\r\n")
              .Append($"Installed Architecture: {arch}\r\n");
        }
        return sb.ToString();
    }

    private static readonly ProcessRunResult NoApplicableInstaller =
        new(WingetOutputParser.ExitNoApplicableInstaller, "No applicable installer found; see logs for more details.\r\n", string.Empty);

    /// <summary>Stands in for winget in the user's session: every upgrade refused, no installer for the user, the runtime registered as MSIX.</summary>
    private sealed class FakeWinget
    {
        public string Category { get; init; } = "msix";
        public string VersionBefore { get; init; } = "1.6.6";
        /// <summary>The user's version once the service has answered the hand-over.</summary>
        public string VersionAfter { get; set; } = "1.6.9";
        public bool HandedOver { get; set; }
        public List<string> Calls { get; } = [];

        public Task<ProcessRunResult> Run(string args, TimeSpan timeout, ExecutionContextInfo context, CancellationToken ct)
        {
            lock (Calls) Calls.Add(args);
            var id = args.StartsWith("list --id ", StringComparison.Ordinal) || args.StartsWith("upgrade --id ", StringComparison.Ordinal)
                     || args.StartsWith("show --id ", StringComparison.Ordinal) || args.StartsWith("install --id ", StringComparison.Ordinal)
                ? args.Split(' ')[2] : string.Empty;
            if (args.StartsWith("upgrade", StringComparison.Ordinal))
                return Ok(new ProcessRunResult(WingetOutputParser.ExitNoApplicableUpgrade, "No available upgrade found.\r\n", string.Empty));
            if (args.StartsWith("list --id ", StringComparison.Ordinal) && args.Contains(" --details"))
                return Ok(new ProcessRunResult(0, "   - \r\n" + Details(id, Category, HandedOver && VersionAfter != VersionBefore ? "6000.519.329.0" : "6000.519.297.0", "X86", "X64"), string.Empty));
            if (args.StartsWith("list --id ", StringComparison.Ordinal))
                return Ok(new ProcessRunResult(0, Table(["WindowsAppRuntime.1.6", id, HandedOver ? VersionAfter : VersionBefore, "1.6.9", "winget"]), string.Empty));
            if (args.StartsWith("show --id ", StringComparison.Ordinal) || args.StartsWith("install --id ", StringComparison.Ordinal))
                return Ok(NoApplicableInstaller);
            throw new InvalidOperationException($"unexpected winget call: {args}");
        }

        private static Task<ProcessRunResult> Ok(ProcessRunResult result) => Task.FromResult(result);

        public IEnumerable<string> Installs => Calls.Where(c => c.StartsWith("install ", StringComparison.Ordinal));
        public IEnumerable<string> DetailLookups => Calls.Where(c => c.Contains(" --details"));
    }

    private static WingetProvider Provider(FakeWinget winget, Func<SystemInstallHandOverRequest, CancellationToken, Task<SystemInstallHandOverReply?>>? handOver) =>
        new(NullLogger<WingetProvider>.Instance, new ProviderOptions { SystemInstallHandOver = handOver })
        {
            LookupRunner = winget.Run,
            InstallRunner = winget.Run,
        };

    /// <summary>A service that installs the runtime and reports <paramref name="machine"/> as the highest package afterwards.</summary>
    private static Func<SystemInstallHandOverRequest, CancellationToken, Task<SystemInstallHandOverReply?>> Service(FakeWinget winget, List<SystemInstallHandOverRequest> requests,
        string? machine = "6000.519.329.0", bool ok = true) =>
        (request, _) =>
        {
            requests.Add(request);
            winget.HandedOver = true;
            return Task.FromResult<SystemInstallHandOverReply?>(new SystemInstallHandOverReply(ok, "winget install for all users: exit 0x00000000; after: …; before: …", 0, machine));
        };

    /// <summary>The failure the user context reported before the hand-over existed (no hook at all).</summary>
    private static async Task<InstallResult> MachineOnlyBaseline()
    {
        var winget = new FakeWinget();
        return await Provider(winget, null).InstallAsync(Runtime, Pending(), User, null, CancellationToken.None);
    }

    private static void AssertNeverUnscopedInTheUsersSession(FakeWinget winget)
    {
        Assert.NotEmpty(winget.Installs);
        Assert.All(winget.Installs, c => Assert.True(c.Contains(" --scope user") || c.Contains(" --installer-type msix"), c));
        Assert.All(winget.Calls.Where(c => c.StartsWith("upgrade", StringComparison.Ordinal)), c => Assert.Contains(" --scope user", c));
    }

    [Fact]
    public async Task An_msix_package_without_a_user_installer_is_installed_for_all_users_by_the_service()
    {
        var winget = new FakeWinget();
        var requests = new List<SystemInstallHandOverRequest>();
        var pending = Pending();

        var result = await Provider(winget, Service(winget, requests)).InstallAsync(Runtime, pending, User, null, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.False(result.RebootRequired);
        Assert.Equal("1.6.9", result.InstalledVersion);
        Assert.Contains("installed for all users by the AppMonitor service", result.Message);
        Assert.Contains("6000.519.297.0 -> 6000.519.329.0", result.Message);
        Assert.Contains("Service: winget install for all users: exit 0x00000000", result.Message);

        var request = Assert.Single(requests);
        Assert.Equal(new SystemInstallHandOverRequest(pending.Key, RuntimeId, Family, "6000.519.297.0"), request);
        Assert.Equal(
            $"list --id {RuntimeId} --exact --source winget --details --accept-source-agreements --disable-interactivity --scope user",
            winget.DetailLookups.First());
        AssertNeverUnscopedInTheUsersSession(winget);
    }

    [Fact]
    public async Task A_user_copy_that_is_still_old_next_to_a_newer_machine_package_waits_for_the_next_sign_in()
    {
        var winget = new FakeWinget { VersionAfter = "1.6.6" };
        var requests = new List<SystemInstallHandOverRequest>();

        var result = await Provider(winget, Service(winget, requests)).InstallAsync(Runtime, Pending(), User, null, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.True(result.RebootRequired);
        Assert.Equal("1.6.6", result.InstalledVersion);
        Assert.Contains("for all users", result.Message);
        Assert.Contains("(package 6000.519.329.0)", result.Message);
        Assert.Contains("your copy is still 1.6.6", result.Message);
        Assert.Contains("next sign-in", result.Message);
        Assert.Contains("Service: ", result.Message);
    }

    [Fact]
    public async Task A_reported_success_without_a_newer_package_on_the_device_is_a_failure()
    {
        var winget = new FakeWinget { VersionAfter = "1.6.6" };
        var baseline = await MachineOnlyBaseline();

        var result = await Provider(winget, Service(winget, [], machine: "6000.519.297.0")).InstallAsync(Runtime, Pending(), User, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.StartsWith(baseline.Message!, result.Message);
        Assert.Contains($"no newer {Family} package is on the device", result.Message);
        Assert.Contains("machine 6000.519.297.0", result.Message);
    }

    [Fact]
    public async Task No_answer_from_the_service_keeps_the_old_failure_text_first()
    {
        var winget = new FakeWinget();
        var baseline = await MachineOnlyBaseline();

        var result = await Provider(winget, (_, _) => Task.FromResult<SystemInstallHandOverReply?>(null)).InstallAsync(Runtime, Pending(), User, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(WingetOutputParser.ExitNoApplicableInstaller, result.ExitCode);
        Assert.StartsWith(baseline.Message!, result.Message);
        Assert.Contains("The AppMonitor service could not install it for all users: it did not answer", result.Message);
        AssertNeverUnscopedInTheUsersSession(winget);
    }

    [Fact]
    public async Task A_failed_install_by_the_service_names_the_services_reason()
    {
        var winget = new FakeWinget();
        var baseline = await MachineOnlyBaseline();
        Func<SystemInstallHandOverRequest, CancellationToken, Task<SystemInstallHandOverReply?>> failing =
            (_, _) => Task.FromResult<SystemInstallHandOverReply?>(new SystemInstallHandOverReply(false, "winget install for all users failed: exit 1603.", 1603, null));

        var result = await Provider(winget, failing).InstallAsync(Runtime, Pending(), User, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(1603, result.ExitCode);
        Assert.StartsWith(baseline.Message!, result.Message);
        Assert.EndsWith("could not install it for all users: winget install for all users failed: exit 1603.", result.Message);
    }

    [Fact]
    public async Task A_classic_install_is_never_handed_over_and_fails_as_before()
    {
        var winget = new FakeWinget { Category = "exe" };
        var called = false;
        var baseline = await MachineOnlyBaseline();

        var result = await Provider(winget, (_, _) => { called = true; return Task.FromResult<SystemInstallHandOverReply?>(null); })
            .InstallAsync(Runtime, Pending(), User, null, CancellationToken.None);

        Assert.False(called);
        Assert.False(result.Success);
        Assert.Equal((baseline.Message, baseline.ExitCode), (result.Message, result.ExitCode));
        Assert.Equal(WingetOutputParser.ExitNoApplicableInstaller, result.ExitCode);
        Assert.Single(winget.DetailLookups);
    }

    [Fact]
    public async Task Without_the_hook_nothing_changes()
    {
        var winget = new FakeWinget();

        var result = await Provider(winget, null).InstallAsync(Runtime, Pending(), User, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(WingetOutputParser.ExitNoApplicableInstaller, result.ExitCode);
        Assert.Equal(
            $"winget could not upgrade '{RuntimeId}' (User scope): no applicable upgrade found (the manifest's installer does not match how the product is installed, typically its scope). {WingetProvider.MachineOnlyMessage(RuntimeId)}",
            result.Message);
        Assert.Empty(winget.DetailLookups);
        AssertNeverUnscopedInTheUsersSession(winget);
    }

    [Fact]
    public async Task One_install_hands_over_at_most_once_even_with_several_candidate_ids()
    {
        var winget = new FakeWinget();
        var calls = 0;

        var result = await Provider(winget, (_, _) => { calls++; return Task.FromResult<SystemInstallHandOverReply?>(null); })
            .InstallAsync(Runtime, Pending(alternatives: "Microsoft.WindowsAppRuntime.1.6.Alt"), User, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(1, calls);
    }

    // ---------------------------------------------------------------- the service's install

    [Fact]
    public async Task The_install_for_all_users_runs_unscoped_as_SYSTEM()
    {
        var calls = new List<(string Args, ExecutionContextInfo Context)>();
        var provider = new WingetProvider(NullLogger<WingetProvider>.Instance, new ProviderOptions { WingetGlobalArgs = "--verbose-logs" })
        {
            InstallRunner = (args, _, context, _) =>
            {
                calls.Add((args, context));
                return Task.FromResult(new ProcessRunResult(WingetOutputParser.ExitPackageAlreadyInstalled, "Found an existing package already installed.\r\n", string.Empty));
            },
        };

        var result = await provider.InstallForAllUsersAsync(Runtime, Pending(), RuntimeId, null, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        var (args, context) = Assert.Single(calls);
        Assert.True(context.IsSystem);
        Assert.Equal(
            $"install --id {RuntimeId} --exact --source winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity --verbose-logs",
            args);
        Assert.DoesNotContain("--scope", args);
        Assert.DoesNotContain("--installer-type", args);
        Assert.DoesNotContain("--force", args);
    }
}
