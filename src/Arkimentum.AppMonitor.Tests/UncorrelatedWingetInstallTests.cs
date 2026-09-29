using System.Text;
using Arkimentum.AppMonitor.Install;
using Arkimentum.AppMonitor.Ipc;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Providers;
using Arkimentum.AppMonitor.Service.Policy;
using Arkimentum.AppMonitor.Service.State;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// A product the registry detection finds but winget does not correlate with any package (2026-09-29, two devices,
/// winget 1.30): Node.js from the vendor MSI, whose product code the index carries in OpenJS.NodeJS.LTS and an
/// OpenJS.NodeJS.22 line. winget lists it only as <c>ARP\Machine\X64\{GUID}</c> without a source, never offers it in
/// <c>winget upgrade</c> and answers "No installed package found" to <c>winget list --id OpenJS.NodeJS --exact</c>, while
/// <c>winget show --id OpenJS.NodeJS --exact</c> knows the package's current version. The check takes the installed
/// version from the registry and the available one from <c>winget show</c>; the install runs <c>winget install</c> with an
/// explicit scope and is verified against the registry.
/// </summary>
public class UncorrelatedWingetInstallTests
{
    private static readonly ExecutionContextInfo User = new() { IsSystem = false, UserSid = "S-1-5-21-1", SessionId = 1 };
    private static readonly ExecutionContextInfo System = ExecutionContextInfo.System;

    private const string NodeGuid = "{89850E15-1C2B-4F1E-9D2A-5B7E0F3A6C11}";

    private static readonly AppPolicy Node = new()
    {
        AppId = "node-js",
        DisplayName = "Node.js",
        WingetId = "OpenJS.NodeJS",
        DetectDisplayNameRegex = @"^Node\.js$",
    };

    private static InstalledApp Registry(string version, InstallContext context = InstallContext.System) => new()
    {
        DisplayName = "Node.js",
        DisplayVersion = version,
        Publisher = "Node.js Foundation",
        Context = context,
        UserSid = context == InstallContext.User ? User.UserSid : null,
        RegistryKeyPath = $@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{NodeGuid}",
    };

    /// <summary>A winget table in winget's own layout: every column padded to its widest cell plus one space.</summary>
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

    /// <summary>The machine-scope listing as SYSTEM's winget prints it: Node.js only under its ARP pseudo id, without a source.</summary>
    private static readonly string MachineList = Table(
        ["7-Zip 26.03 (x64 edition)", "7zip.7zip", "26.03", "", "winget"],
        ["Node.js", $@"ARP\Machine\X64\{NodeGuid}", "24.19.0", "", ""]);

    private static string ShowOutput(string id, string version) =>
        $"Found Node.js [{id}]\r\nVersion: {version}\r\nPublisher: OpenJS\r\nDescription: Node.js JavaScript runtime\r\n" +
        "Installer:\r\n  Installer Type: wix\r\n  Installer Url: https://nodejs.org/dist/node-x64.msi\r\n";

    private static readonly ProcessRunResult NoPackage =
        new(WingetOutputParser.ExitNoInstalledPackageFound, "No package found matching input criteria.\r\n", string.Empty);

    private static readonly ProcessRunResult NoInstalledPackage =
        new(WingetOutputParser.ExitNoInstalledPackageFound, "No installed package found matching input criteria.\r\n", string.Empty);

    /// <summary>Stands in for winget, for the scan's lookups and the install's processes alike.</summary>
    private sealed class FakeWinget
    {
        public string ListOutput { get; init; } = MachineList;
        /// <summary>id -> version winget show reports; an id missing here is a package winget does not know.</summary>
        public Dictionary<string, string> Packages { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ProcessRunResult? ShowFailure { get; init; }
        /// <summary>What "winget show" with an installer filter answers during the install (null: the package's details).</summary>
        public ProcessRunResult? FilteredShow { get; init; }
        public ProcessRunResult InstallAnswer { get; set; } = new(0, "Successfully installed\r\n", string.Empty);
        public List<string> Calls { get; } = [];

        public Task<ProcessRunResult> Run(string args, TimeSpan timeout, ExecutionContextInfo context, CancellationToken ct)
        {
            lock (Calls) Calls.Add(args);
            if (args.StartsWith("upgrade", StringComparison.Ordinal))
                return Task.FromResult(NoInstalledPackage);
            if (args.StartsWith("list --id ", StringComparison.Ordinal))
                return Task.FromResult(NoInstalledPackage);
            if (args.StartsWith("list", StringComparison.Ordinal))
                return Task.FromResult(new ProcessRunResult(0, ListOutput, string.Empty));
            if (args.StartsWith("show --id ", StringComparison.Ordinal))
            {
                if (ShowFailure is not null) return Task.FromResult(ShowFailure);
                var id = args.Split(' ')[2];
                if (!Packages.TryGetValue(id, out var version)) return Task.FromResult(NoPackage);
                var filtered = args.Contains("--scope") || args.Contains("--installer-type");
                return Task.FromResult(filtered && FilteredShow is not null ? FilteredShow : new ProcessRunResult(0, ShowOutput(id, version), string.Empty));
            }
            if (args.StartsWith("install", StringComparison.Ordinal))
                return Task.FromResult(InstallAnswer);
            throw new InvalidOperationException($"unexpected winget call: {args}");
        }

        public IEnumerable<string> Shows => Calls.Where(c => c.StartsWith("show ", StringComparison.Ordinal));
        public IEnumerable<string> Installs => Calls.Where(c => c.StartsWith("install ", StringComparison.Ordinal));
    }

    private static FakeWinget NodeWinget(string latest = "26.7.0")
    {
        var winget = new FakeWinget();
        winget.Packages["OpenJS.NodeJS"] = latest;
        return winget;
    }

    private static WingetProvider Provider(FakeWinget winget, Func<AppPolicy, ExecutionContextInfo, InstalledApp?>? registry = null, bool includeUnknown = false) =>
        new(NullLogger<WingetProvider>.Instance, new ProviderOptions { WingetIncludeUnknown = includeUnknown })
        {
            LookupRunner = winget.Run,
            InstallRunner = winget.Run,
            RegistryReader = registry ?? ((_, _) => throw new InvalidOperationException("the registry was not expected to be read")),
        };

    // ---------------------------------------------------------------- the check

    [Fact]
    public async Task An_installed_product_winget_does_not_correlate_gets_the_package_version_as_its_update()
    {
        var winget = NodeWinget();
        var result = await Provider(winget).CheckAsync(Node, Registry("24.19.0"), System, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.True(result.IsInstalled);
        Assert.True(result.UpdateAvailable);
        Assert.True(result.WingetUncorrelated);
        Assert.Equal(("24.19.0", "26.7.0", "OpenJS.NodeJS"), (result.InstalledVersion, result.AvailableVersion, result.WingetId));
        Assert.Equal(InstallContext.System, result.ResolvedContext);

        var show = Assert.Single(winget.Shows);
        Assert.Equal("show --id OpenJS.NodeJS --exact --source winget --accept-source-agreements --disable-interactivity", show);
    }

    [Fact]
    public async Task The_first_configured_id_winget_knows_gives_the_version()
    {
        var winget = new FakeWinget();
        winget.Packages["OpenJS.NodeJS"] = "26.7.0";
        winget.Packages["OpenJS.NodeJS.LTS"] = "24.20.0";
        var app = Node with { WingetId = "Vendor.Gone;OpenJS.NodeJS.LTS;OpenJS.NodeJS" };

        var result = await Provider(winget).CheckAsync(app, Registry("24.19.0"), System, CancellationToken.None);

        Assert.Equal(("OpenJS.NodeJS.LTS", "24.20.0", true), (result.WingetId, result.AvailableVersion, result.UpdateAvailable));
        Assert.Equal(["Vendor.Gone", "OpenJS.NodeJS.LTS"], winget.Shows.Select(s => s.Split(' ')[2]));
    }

    [Fact]
    public async Task The_package_is_asked_about_once_per_scan()
    {
        var winget = NodeWinget();
        var provider = Provider(winget);

        await provider.CheckAsync(Node, Registry("24.19.0"), System, CancellationToken.None);
        await provider.CheckAsync(Node with { AppId = "node-js-2" }, Registry("24.19.0"), System, CancellationToken.None);

        Assert.Single(winget.Shows);
    }

    [Fact]
    public async Task The_source_and_global_arguments_are_honoured()
    {
        var winget = NodeWinget();
        var provider = new WingetProvider(NullLogger<WingetProvider>.Instance, new ProviderOptions { WingetGlobalArgs = "--verbose-logs" })
        {
            LookupRunner = winget.Run,
        };

        await provider.CheckAsync(Node with { WingetSourceName = "corp" }, Registry("24.19.0"), System, CancellationToken.None);

        Assert.Equal("show --id OpenJS.NodeJS --exact --source corp --accept-source-agreements --disable-interactivity --verbose-logs", Assert.Single(winget.Shows));
    }

    [Fact]
    public async Task A_package_id_winget_does_not_know_at_all_leaves_the_product_not_installed()
    {
        var winget = new FakeWinget();   // knows no package
        var app = Node with { WingetId = "OpenJS.NodeJS;OpenJS.NodeJS.Other" };

        var result = await Provider(winget).CheckAsync(app, Registry("24.19.0"), System, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.False(result.IsInstalled);
        Assert.False(result.UpdateAvailable);
        Assert.False(result.WingetUncorrelated);
        Assert.Equal(2, winget.Shows.Count());
    }

    [Fact]
    public async Task Without_a_registry_match_winget_is_not_asked_about_the_package()
    {
        var winget = NodeWinget();

        var result = await Provider(winget).CheckAsync(Node, installed: null, System, CancellationToken.None);

        Assert.False(result.IsInstalled);
        Assert.Empty(winget.Shows);
    }

    [Fact]
    public async Task Equal_versions_mean_installed_and_up_to_date()
    {
        var result = await Provider(NodeWinget("24.19.0")).CheckAsync(Node, Registry("24.19.0"), System, CancellationToken.None);

        Assert.True(result.IsInstalled);
        Assert.False(result.UpdateAvailable);
        Assert.True(result.WingetUncorrelated);
        Assert.Equal("24.19.0", result.InstalledVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_registry_entry_without_a_version_follows_WingetIncludeUnknown(bool includeUnknown)
    {
        var result = await Provider(NodeWinget(), includeUnknown: includeUnknown).CheckAsync(Node, Registry(""), System, CancellationToken.None);

        Assert.True(result.IsInstalled);
        Assert.Equal(includeUnknown, result.UpdateAvailable);
    }

    [Fact]
    public async Task A_failing_show_is_a_failed_check_not_an_uninstalled_product()
    {
        var winget = new FakeWinget { ShowFailure = new ProcessRunResult(unchecked((int)0x8A15000F), "Failed when opening source(s)\r\n", string.Empty) };

        var result = await Provider(winget).CheckAsync(Node, Registry("24.19.0"), System, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.False(result.IsInstalled);
    }

    [Fact]
    public async Task A_product_winget_does_correlate_keeps_the_listing_path()
    {
        var winget = new FakeWinget
        {
            ListOutput = Table(["Node.js", "OpenJS.NodeJS", "24.19.0", "", "winget"]),
        };
        winget.Packages["OpenJS.NodeJS"] = "26.7.0";

        var result = await Provider(winget).CheckAsync(Node, Registry("24.19.0"), System, CancellationToken.None);

        Assert.True(result.IsInstalled);
        Assert.False(result.WingetUncorrelated);
        Assert.Empty(winget.Shows);
    }

    [Theory]
    [InlineData("Found Node.js [OpenJS.NodeJS]\r\nVersion: 26.7.0\r\nInstaller:\r\n  Installer Type: wix\r\n", "26.7.0")]
    [InlineData("Found X [Y]\nPublisher: P\nVersion:   1.2.3  \n", "1.2.3")]
    [InlineData("Found X [Y]\r\nInstaller Version: 9\r\n", null)]
    [InlineData("Found X [Y]\r\nVersion: Unknown\r\n", null)]
    [InlineData("", null)]
    public void The_package_version_is_read_from_the_Version_line(string output, string? expected)
    {
        Assert.Equal(expected, WingetProvider.ParseShowVersion(output));
    }

    // ---------------------------------------------------------------- the install

    private static PendingUpdate Pending(InstallContext context = InstallContext.System) => new()
    {
        AppId = "node-js",
        DisplayName = "Node.js",
        Source = UpdateSource.Winget,
        Context = context,
        UserSid = context == InstallContext.User ? User.UserSid : null,
        InstalledVersion = "24.19.0",
        AvailableVersion = "26.7.0",
        WingetId = "OpenJS.NodeJS",
        WingetIdAlternatives = "OpenJS.NodeJS",
        WingetSourceName = "winget",
        WingetUncorrelated = true,
    };

    [Fact]
    public async Task The_install_runs_winget_install_machine_wide_as_SYSTEM_and_never_upgrade()
    {
        var winget = NodeWinget();
        var registry = new List<ExecutionContextInfo>();
        var provider = Provider(winget, (app, ctx) => { registry.Add(ctx); return Registry("26.7.0"); });

        var result = await provider.InstallAsync(Node, Pending(), System, null, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal("26.7.0", result.InstalledVersion);
        Assert.Equal(
            "install --id OpenJS.NodeJS --exact --source winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity --scope machine",
            Assert.Single(winget.Installs));
        Assert.DoesNotContain(winget.Calls, c => c.StartsWith("upgrade", StringComparison.Ordinal));
        Assert.DoesNotContain(winget.Calls, c => c.Contains("--force"));
        Assert.Equal([System], registry);
    }

    [Fact]
    public async Task In_the_users_session_the_install_is_scoped_to_the_user_and_nothing_else()
    {
        var winget = NodeWinget();
        var provider = Provider(winget, (_, _) => Registry("26.7.0", InstallContext.User));

        var result = await provider.InstallAsync(Node, Pending(InstallContext.User), User, null, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        var install = Assert.Single(winget.Installs);
        Assert.EndsWith("--disable-interactivity --scope user", install);
        Assert.DoesNotContain("--installer-type", install);
        Assert.DoesNotContain(winget.Calls, c => c.StartsWith("upgrade", StringComparison.Ordinal));
    }

    [Fact]
    public async Task In_the_users_session_a_machine_only_package_is_not_installed()
    {
        var winget = new FakeWinget
        {
            FilteredShow = new ProcessRunResult(WingetOutputParser.ExitNoApplicableInstaller, "No applicable installer found; see logs for more details.\r\n", string.Empty),
        };
        winget.Packages["OpenJS.NodeJS"] = "26.7.0";

        var result = await Provider(winget).InstallAsync(Node, Pending(InstallContext.User), User, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(WingetOutputParser.ExitNoApplicableInstaller, result.ExitCode);
        Assert.Empty(winget.Installs);
    }

    [Fact]
    public async Task In_the_users_session_a_portable_package_is_not_installed()
    {
        var winget = new FakeWinget
        {
            FilteredShow = new ProcessRunResult(0, "Found Node.js [OpenJS.NodeJS]\r\nVersion: 26.7.0\r\nInstaller:\r\n  Installer Type: portable (zip)\r\n", string.Empty),
        };
        winget.Packages["OpenJS.NodeJS"] = "26.7.0";

        var result = await Provider(winget).InstallAsync(Node, Pending(InstallContext.User), User, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(winget.Installs);
    }

    [Fact]
    public async Task The_install_is_verified_by_the_registry_moving()
    {
        var result = await Provider(NodeWinget(), (_, _) => Registry("26.7.0")).InstallAsync(Node, Pending(), System, null, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal("26.7.0", result.InstalledVersion);
    }

    [Fact]
    public async Task A_registry_that_did_not_move_is_a_failure_even_when_winget_reports_success()
    {
        var result = await Provider(NodeWinget(), (_, _) => Registry("24.19.0")).InstallAsync(Node, Pending(), System, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("24.19.0", result.Message);
        Assert.Contains("26.7.0", result.Message);
        Assert.Contains("0x00000000", result.Message);
    }

    [Fact]
    public async Task A_failed_winget_install_with_an_unchanged_registry_names_both()
    {
        var winget = NodeWinget();
        winget.InstallAnswer = new ProcessRunResult(1603, "Installer failed with exit code: 1603\r\n", string.Empty);

        var result = await Provider(winget, (_, _) => Registry("24.19.0")).InstallAsync(Node, Pending(), System, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(1603, result.ExitCode);
        Assert.Contains("24.19.0", result.Message);
        Assert.Contains("0x00000643", result.Message);
    }

    [Fact]
    public async Task A_missing_registry_entry_afterwards_is_a_failure()
    {
        var result = await Provider(NodeWinget(), (_, _) => null).InstallAsync(Node, Pending(), System, null, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task The_registry_decides_when_winget_exits_with_an_error_but_the_version_moved()
    {
        var winget = NodeWinget();
        winget.InstallAnswer = new ProcessRunResult(unchecked((int)0x8A150061), "Package already installed\r\n", string.Empty);

        var result = await Provider(winget, (_, _) => Registry("26.7.0")).InstallAsync(Node, Pending(), System, null, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal("26.7.0", result.InstalledVersion);
    }

    [Theory]
    [InlineData("26.7.0", "26.7.0", true)]
    [InlineData("26.8.0", "26.7.0", true)]
    [InlineData("24.19.0", "26.7.0", false)]
    [InlineData(null, "26.7.0", false)]
    [InlineData("Unknown", "26.7.0", false)]
    [InlineData("26.7.0", null, false)]
    public void Success_needs_the_registry_version_to_reach_the_expected_one(string? registry, string? expected, bool reached)
    {
        Assert.Equal(reached, WingetProvider.UncorrelatedInstallReached(registry, expected));
    }

    [Fact]
    public async Task An_update_winget_does_correlate_still_goes_through_winget_upgrade()
    {
        var winget = NodeWinget();
        var pending = Pending();
        pending.WingetUncorrelated = false;

        await Provider(winget).InstallAsync(Node, pending, System, null, CancellationToken.None);

        Assert.Empty(winget.Installs);
        Assert.StartsWith("upgrade --id OpenJS.NodeJS --exact", Assert.Single(winget.Calls, c => c.StartsWith("upgrade", StringComparison.Ordinal)));
    }

    // ---------------------------------------------------------------- the flag travels: result -> state -> tray

    [Fact]
    public void A_pending_update_created_from_the_result_carries_the_flag()
    {
        var result = new UpdateCheckResult
        {
            AppId = "node-js",
            Source = UpdateSource.Winget,
            IsInstalled = true,
            InstalledVersion = "24.19.0",
            AvailableVersion = "26.7.0",
            UpdateAvailable = true,
            WingetId = "OpenJS.NodeJS",
            ResolvedContext = InstallContext.System,
            WingetUncorrelated = true,
        };
        var state = new Dictionary<string, PendingUpdate>(StringComparer.OrdinalIgnoreCase);
        var outcome = new ScanOutcome(result, Node, InstallContext.System, null);

        PolicyEngine.Merge(state, [outcome], new HashSet<string> { outcome.Key }, new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));

        var p = state.Values.Single();
        Assert.True(p.WingetUncorrelated);
        Assert.Equal("OpenJS.NodeJS", p.WingetId);
    }

    [Fact]
    public void The_state_file_round_trips_the_flag_and_an_older_one_without_it_means_false()
    {
        var dir = Path.Combine(Path.GetTempPath(), "appmon-uncorrelated-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var settings = new AgentSettings { StateDirectory = dir };
            var store = new StateStore(NullLogger<StateStore>.Instance);
            var state = new ServiceState();
            var pending = Pending();
            state.Updates[pending.Key] = pending;
            store.Save(settings, state);

            Assert.Contains("\"WingetUncorrelated\": true", File.ReadAllText(StateStore.PathFor(settings)));
            Assert.True(store.Load(settings).Updates[pending.Key].WingetUncorrelated);

            File.WriteAllText(StateStore.PathFor(settings),
                "{ \"Updates\": { \"node-js|System|-\": { \"AppId\": \"node-js\", \"Context\": \"System\", \"Source\": \"Winget\", \"WingetId\": \"OpenJS.NodeJS\" } } }");
            var older = store.Load(settings).Updates["node-js|System|-"];
            Assert.False(older.WingetUncorrelated);
            Assert.Equal("OpenJS.NodeJS", older.WingetId);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void The_tray_gets_the_flag_and_the_detection_rule_with_the_install_request()
    {
        var message = new RunUserInstallMessage
        {
            Update = Pending(InstallContext.User),
            DetectDisplayNameRegex = @"^Node\.js$",
            DetectPublisherRegex = "Node",
        };

        var back = Assert.IsType<RunUserInstallMessage>(IpcJson.Deserialize(IpcJson.Serialize(message)));

        Assert.True(back.Update.WingetUncorrelated);
        Assert.Equal((@"^Node\.js$", "Node"), (back.DetectDisplayNameRegex, back.DetectPublisherRegex));
    }

    [Fact]
    public void An_older_services_install_request_without_the_new_fields_still_reads()
    {
        const string line = "{\"$type\":\"runUserInstall\",\"update\":{\"appId\":\"node-js\",\"context\":\"User\",\"wingetId\":\"OpenJS.NodeJS\"},\"timeoutMinutes\":30}";

        var back = Assert.IsType<RunUserInstallMessage>(IpcJson.Deserialize(line));

        Assert.False(back.Update.WingetUncorrelated);
        Assert.Null(back.DetectDisplayNameRegex);
    }

    [Fact]
    public void A_user_scan_result_carries_the_flag_to_the_service()
    {
        var message = new UserScanResultMessage
        {
            Results = [new UpdateCheckResult { AppId = "node-js", Source = UpdateSource.Winget, IsInstalled = true, WingetUncorrelated = true }],
        };

        var back = Assert.IsType<UserScanResultMessage>(IpcJson.Deserialize(IpcJson.Serialize(message)));

        Assert.True(back.Results.Single().WingetUncorrelated);
    }
}
