using System.Text;
using Arkimentum.AppMonitor.Install;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Providers;
using Arkimentum.AppMonitor.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// One complete winget check per scan (2026-09-29): a scan used to start <c>winget list --id X --exact</c> once per
/// application (58 processes, ~34 s in the service on the owner's device, and the same again in the tray). Now each
/// scope reads one full <c>winget list</c> and one <c>winget upgrade</c> and resolves every application in memory by the
/// same rules. These tests feed captured-style winget output through <see cref="WingetProvider.LookupRunner"/> and hold
/// the snapshot path to the per-id path: the same output must give the same results, with a fixed number of processes.
/// </summary>
public class WingetScanSnapshotTests
{
    private static readonly ExecutionContextInfo User = new() { IsSystem = false, UserSid = "S-1-5-21-1", SessionId = 1 };

    private static readonly string[] ListHeader = ["Name", "Id", "Version", "Available", "Source"];

    /// <summary>A winget table in winget's own layout: every column padded to its widest cell plus one space.</summary>
    private static string Table(string[] header, params string[][] rows)
    {
        var widths = Enumerable.Range(0, header.Length)
            .Select(c => Math.Max(header[c].Length, rows.Select(r => r[c].Length).DefaultIfEmpty(0).Max()) + 1).ToArray();
        string Line(string[] cells) => string.Concat(cells.Select((v, c) => c == cells.Length - 1 ? v : v.PadRight(widths[c]))).TrimEnd();
        var sb = new StringBuilder();
        sb.Append(Line(header)).Append("\r\n").Append(new string('-', widths.Sum())).Append("\r\n");
        foreach (var r in rows) sb.Append(Line(r)).Append("\r\n");
        return sb.ToString();
    }

    private static string[] Row(string name, string id, string version, string available = "", string source = "winget") => [name, id, version, available, source];

    /// <summary>
    /// Stands in for winget: the full listing, the upgrade listing, and per-id lookups derived from the full listing as
    /// winget answers them (<c>--exact</c> on the id, <c>--source</c> filtering to rows of that source), unless a test
    /// overrides the answer for an id.
    /// </summary>
    private sealed class FakeWinget
    {
        public string ListOutput { get; init; } = string.Empty;
        public string UpgradeOutput { get; init; } = "No installed package found matching input criteria.\r\n";
        /// <summary>What the full listing answers instead of <see cref="ListOutput"/>: a failure, or "nothing installed" (the per-id lookups still use ListOutput).</summary>
        public ProcessRunResult? FullListAnswer { get; init; }
        public Dictionary<string, ProcessRunResult> PerId { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Calls { get; } = [];

        public int PerIdCalls { get { lock (Calls) return Calls.Count(c => c.StartsWith("list --id ", StringComparison.Ordinal) && !c.Contains(" --details")); } }

        /// <summary>The <c>winget list --id X --details</c> runs: the MSIX check of an ambiguous offer (see WingetProvider.IsAmbiguousOffer).</summary>
        public int DetailsCalls { get { lock (Calls) return Calls.Count(c => c.StartsWith("list --id ", StringComparison.Ordinal) && c.Contains(" --details")); } }

        public Task<ProcessRunResult> Run(string args, TimeSpan timeout, ExecutionContextInfo context, CancellationToken ct)
        {
            lock (Calls) Calls.Add(args);
            if (args.StartsWith("upgrade", StringComparison.Ordinal))
                return Task.FromResult(new ProcessRunResult(0, UpgradeOutput, string.Empty));
            if (args.StartsWith("list --id ", StringComparison.Ordinal))
            {
                var parts = args.Split(' ');
                var id = parts[2];
                var sourceAt = Array.IndexOf(parts, "--source");
                var source = sourceAt > 0 ? parts[sourceAt + 1] : null;
                return Task.FromResult(PerId.TryGetValue(id, out var fixedAnswer) ? fixedAnswer : PerIdAnswer(id, source));
            }
            return Task.FromResult(FullListAnswer ?? new ProcessRunResult(0, ListOutput, string.Empty));
        }

        private ProcessRunResult PerIdAnswer(string id, string? source)
        {
            var lines = ListOutput.Replace("\r", string.Empty).Split('\n');
            var sep = Array.FindIndex(lines, l => l.Trim().Length >= 8 && l.Trim().All(c => c == '-'));
            var kept = lines.Skip(sep + 1).Where(l =>
            {
                var rows = WingetOutputParser.ParseListOutput(lines[sep - 1] + "\n" + lines[sep] + "\n" + l);
                return rows.Count == 1 && string.Equals(rows[0].Id, id, StringComparison.OrdinalIgnoreCase)
                       && (source is null || string.Equals(rows[0].Source, source, StringComparison.OrdinalIgnoreCase));
            }).ToList();
            return kept.Count == 0
                ? NotInstalledAnswer
                : new ProcessRunResult(0, string.Join("\r\n", new[] { lines[sep - 1], lines[sep] }.Concat(kept)) + "\r\n", string.Empty);
        }
    }

    private static readonly ProcessRunResult NotInstalledAnswer =
        new(WingetOutputParser.ExitNoInstalledPackageFound, "No installed package found matching input criteria.\r\n", string.Empty);

    private static WingetProvider Provider(FakeWinget winget, bool perApp = false, bool includeUnknown = false) =>
        new(NullLogger<WingetProvider>.Instance, new ProviderOptions { WingetIncludeUnknown = includeUnknown })
        {
            LookupRunner = winget.Run,
            ForcePerAppLookups = perApp,
        };

    private static async Task<List<UpdateCheckResult>> CheckAll(WingetProvider provider, IReadOnlyList<AppPolicy> apps)
    {
        await provider.PrepareScanAsync(apps, User, CancellationToken.None);
        var results = new List<UpdateCheckResult>();
        foreach (var app in apps) results.Add(await provider.CheckAsync(app, null, User, CancellationToken.None));
        return results;
    }

    // ---------------------------------------------------------------- a device: the listings and the configured apps

    /// <summary>A user-scope listing covering every rule: configured ids, alternatives, names, side by side, unknown, pseudo ids.</summary>
    private static readonly string DeviceList = Table(ListHeader,
        Row("7-Zip 26.03 (x64 edition)", "7zip.7zip", "26.03", "26.04"),
        Row("Adobe Acrobat (32-bit)", "Adobe.Acrobat.Reader.32-bit", "26.001.20000"),
        Row("Git", "Git.Git", "2.55.0"),
        Row("Git Extensions", "GitExtensionsTeam.GitExtensions", "5.0.0", "5.1.0"),
        Row("Google Chrome", "Google.Chrome.EXE", "154.0.8037.58"),
        Row("Microsoft Windows Desktop Runtime - 8.0.30 (x64)", "Microsoft.DotNet.DesktopRuntime.8", "8.0.30", "8.0.31"),
        Row("Microsoft Windows Desktop Runtime - 8.0.31 (x64)", "Microsoft.DotNet.DesktopRuntime.8", "8.0.31"),
        Row("Mozilla Firefox", "Mozilla.Firefox.MSIX", "156.0.1.0", "157.0.0.0"),
        Row("Mozilla Firefox", "Mozilla.Firefox", "156.0.1.0", "157.0"),
        Row("PuTTY release 0.78 (64-bit)", "PuTTY.PuTTY", "0.78.0.0", "0.85.0.0"),
        Row("PuTTY release 0.84 (64-bit)", "PuTTY.PuTTY", "0.84.0.0", "0.85.0.0"),
        Row("Some Tool", "Vendor.SomeTool", "Unknown", "2.0.0"),
        Row("Visual Studio Code", @"MSIX\Microsoft.VisualStudioCode_1.0.139.1_neutral__8wekyb3d8bbwe", "1.0.139.1", "", ""),
        Row("IntunewinBuilder", @"ARP\Machine\X64\{404D5DA5-822C-478B-BAC0-DDF937F0D693}", "1.0.4.0", "", ""));

    private static readonly string DeviceUpgrade = Table(ListHeader,
            Row("7-Zip 26.03 (x64 edition)", "7zip.7zip", "26.03", "26.04"),
            Row("Git Extensions", "GitExtensionsTeam.GitExtensions", "5.0.0", "5.1.0"),
            Row("Microsoft Windows Desktop Runtime - 8.0.30 (x64)", "Microsoft.DotNet.DesktopRuntime.8", "8.0.30", "8.0.31"),
            Row("Mozilla Firefox", "Mozilla.Firefox.MSIX", "156.0.1.0", "157.0.0.0"),
            Row("PuTTY release 0.78 (64-bit)", "PuTTY.PuTTY", "0.78.0.0", "0.85.0.0"),
            Row("PuTTY release 0.84 (64-bit)", "PuTTY.PuTTY", "0.84.0.0", "0.85.0.0"),
            Row("Some Tool", "Vendor.SomeTool", "Unknown", "2.0.0"))
        + "7 upgrades available.\r\n";

    private static readonly IReadOnlyList<AppPolicy> DeviceApps =
    [
        new() { AppId = "7zip", DisplayName = "7-Zip", WingetId = "7zip.7zip", DetectDisplayNameRegex = "^7-Zip" },
        new() { AppId = "acrobat", DisplayName = "Adobe Acrobat Reader", WingetId = "Adobe.Acrobat.Reader.64-bit", DetectDisplayNameRegex = "^Adobe Acrobat" },
        new() { AppId = "git", DisplayName = "Git", WingetId = "Git.Git", DetectDisplayNameRegex = @"^Git\b" },
        new() { AppId = "chrome", DisplayName = "Google Chrome", WingetId = "Google.Chrome" },
        new() { AppId = "dotnet8", DisplayName = "Desktop Runtime 8", WingetId = "Microsoft.DotNet.DesktopRuntime.8", DetectDisplayNameRegex = @"^Microsoft Windows Desktop Runtime - 8\." },
        new() { AppId = "firefox", DisplayName = "Mozilla Firefox", WingetId = "Mozilla.Firefox;Mozilla.Firefox.MSIX" },
        new() { AppId = "putty", DisplayName = "PuTTY", WingetId = "PuTTY.PuTTY", DetectDisplayNameRegex = "^PuTTY release" },
        new() { AppId = "sometool", DisplayName = "Some Tool", WingetId = "Vendor.SomeTool" },
        new() { AppId = "discord", DisplayName = "Discord", WingetId = "Discord.Discord" },
        new() { AppId = "vscode", DisplayName = "Visual Studio Code", WingetId = "Microsoft.VisualStudioCode" },
    ];

    private static FakeWinget Device() => new() { ListOutput = DeviceList, UpgradeOutput = DeviceUpgrade };

    // ---------------------------------------------------------------- the snapshot path gives the per-id path's results

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_snapshot_resolves_every_app_exactly_as_the_per_id_lookups_do(bool includeUnknown)
    {
        var snapshot = await CheckAll(Provider(Device(), includeUnknown: includeUnknown), DeviceApps);
        var perApp = await CheckAll(Provider(Device(), perApp: true, includeUnknown: includeUnknown), DeviceApps);

        Assert.Equal(perApp, snapshot);
    }

    [Fact]
    public async Task A_scan_starts_winget_twice_however_many_apps_it_checks()
    {
        var winget = Device();
        await CheckAll(Provider(winget), DeviceApps);

        // Plus one 'winget list --details' for each offer that may be one package's offer for another's install (after
        // 1.1.46): Firefox (one version listed under both configured ids) and PuTTY (two installs under one id). The
        // fake prints no details records, so both offers stand.
        Assert.Equal(2, winget.DetailsCalls);
        Assert.Equal(2 + winget.DetailsCalls, winget.Calls.Count);
        Assert.Single(winget.Calls, c => c.StartsWith("list --accept-source-agreements", StringComparison.Ordinal) && c.Contains("--scope user"));
        Assert.Single(winget.Calls, c => c.StartsWith("upgrade --source winget", StringComparison.Ordinal) && c.Contains("--scope user"));
        Assert.Equal(0, winget.PerIdCalls);
    }

    [Fact]
    public async Task The_per_id_path_starts_winget_for_every_app()
    {
        var winget = Device();
        await CheckAll(Provider(winget, perApp: true), DeviceApps);

        Assert.True(winget.PerIdCalls >= DeviceApps.Count);
    }

    [Fact]
    public async Task Each_rule_still_holds_on_the_snapshot()
    {
        var results = (await CheckAll(Provider(Device()), DeviceApps)).ToDictionary(r => r.AppId);

        // configured id, update found through the upgrade listing
        Assert.True(results["7zip"].UpdateAvailable);
        Assert.Equal(("26.03", "26.04", "7zip.7zip"), (results["7zip"].InstalledVersion, results["7zip"].AvailableVersion, results["7zip"].WingetId));
        // resolved by name: the 32-bit build under a policy naming the 64-bit id
        Assert.Equal("Adobe.Acrobat.Reader.32-bit", results["acrobat"].WingetId);
        Assert.True(results["acrobat"].IsInstalled);
        // a related product whose name passes the rule does not take over an install that is up to date
        Assert.Equal(("Git.Git", false), (results["git"].WingetId, results["git"].UpdateAvailable));
        // the per-user Chrome build by name
        Assert.Equal("Google.Chrome.EXE", results["chrome"].WingetId);
        // side by side: the highest install counts
        Assert.Equal(("8.0.31", false), (results["dotnet8"].InstalledVersion, results["dotnet8"].UpdateAvailable));
        Assert.Equal(("0.84.0.0", true), (results["putty"].InstalledVersion, results["putty"].UpdateAvailable));
        // alternatives: the upgrade listing names the id that can upgrade the MSIX build
        Assert.Equal(("Mozilla.Firefox.MSIX", true), (results["firefox"].WingetId, results["firefox"].UpdateAvailable));
        // unknown installed version: not acted on unless WingetIncludeUnknown
        Assert.True(results["sometool"].IsInstalled);
        Assert.False(results["sometool"].UpdateAvailable);
        // not installed at all; a pseudo MSIX id is never taken as the package id
        Assert.False(results["discord"].IsInstalled);
        Assert.Equal("Discord.Discord", results["discord"].WingetId);
        Assert.False(results["vscode"].IsInstalled);
        Assert.All(results.Values, r => Assert.Equal(InstallContext.User, r.ResolvedContext));
        Assert.All(results.Values, r => Assert.Null(r.Error));
    }

    // ---------------------------------------------------------------- fallbacks

    [Fact]
    public async Task A_failed_full_listing_falls_back_to_per_id_lookups_for_the_whole_scan()
    {
        var failing = new FakeWinget
        {
            ListOutput = DeviceList,
            UpgradeOutput = DeviceUpgrade,
            FullListAnswer = new ProcessRunResult(unchecked((int)0x8A15000F), string.Empty, "Failed when searching source: msstore"),
        };
        var results = await CheckAll(Provider(failing), DeviceApps);

        Assert.True(failing.PerIdCalls >= DeviceApps.Count);
        // Without the listing the name resolution has nothing to search, exactly as before when it failed; everything
        // found by id is identical to the snapshot's answer.
        var reference = (await CheckAll(Provider(Device()), DeviceApps)).ToDictionary(r => r.AppId);
        foreach (var r in results.Where(r => r.AppId is not ("acrobat" or "chrome")))
            Assert.Equal(reference[r.AppId], r);
        Assert.False(results.Single(r => r.AppId == "acrobat").IsInstalled);
    }

    [Fact]
    public async Task A_timed_out_full_listing_falls_back_as_well()
    {
        var winget = new FakeWinget { ListOutput = DeviceList, UpgradeOutput = DeviceUpgrade, FullListAnswer = new ProcessRunResult(-1, "Name Id", string.Empty, TimedOut: true) };
        var results = await CheckAll(Provider(winget), DeviceApps);

        Assert.True(winget.PerIdCalls >= DeviceApps.Count);
        Assert.Equal("26.03", results.Single(r => r.AppId == "7zip").InstalledVersion);
    }

    [Fact]
    public async Task Nothing_installed_in_the_scope_is_a_complete_answer()
    {
        var winget = new FakeWinget { FullListAnswer = NotInstalledAnswer };
        var results = await CheckAll(Provider(winget), DeviceApps);

        Assert.All(results, r => Assert.False(r.IsInstalled));
        Assert.Equal(0, winget.PerIdCalls);
    }

    [Fact]
    public async Task A_truncated_id_cell_is_settled_by_winget_for_that_id_only()
    {
        // "Microsoft.DotNet.DesktopRunti…" stands for the 8 and the 9 runtime alike; the per-id lookup tells them apart.
        var list = Table(ListHeader,
            Row("Microsoft Windows Desktop Runtime - 8.0.31 (x64)", "Microsoft.DotNet.DesktopRunti…", "8.0.31"),
            Row("Microsoft Windows Desktop Runtime - 9.0.12 (x64)", "Microsoft.DotNet.DesktopRunti…", "9.0.12"),
            Row("7-Zip 26.03 (x64 edition)", "7zip.7zip", "26.03"));
        var winget = new FakeWinget { ListOutput = list };
        winget.PerId["Microsoft.DotNet.DesktopRuntime.8"] = new ProcessRunResult(0, Table(ListHeader,
            Row("Microsoft Windows Desktop Runtime - 8.0.31 (x64)", "Microsoft.DotNet.DesktopRuntime.8", "8.0.31")), string.Empty);
        AppPolicy[] apps =
        [
            new() { AppId = "dotnet8", DisplayName = "Desktop Runtime 8", WingetId = "Microsoft.DotNet.DesktopRuntime.8" },
            new() { AppId = "7zip", DisplayName = "7-Zip", WingetId = "7zip.7zip" },
        ];

        var results = await CheckAll(Provider(winget), apps);

        Assert.Equal(1, winget.PerIdCalls);
        Assert.Equal(("8.0.31", "Microsoft.DotNet.DesktopRuntime.8"), (results[0].InstalledVersion, results[0].WingetId));
        Assert.Equal("26.03", results[1].InstalledVersion);
    }

    [Fact]
    public async Task An_id_winget_prints_but_the_parser_did_not_split_into_a_row_is_asked_about()
    {
        // The id stands in winget's output, but no parsed row carries it (here: after the footer, where the parser stops).
        var list = Table(ListHeader, Row("7-Zip 26.03 (x64 edition)", "7zip.7zip", "26.03")) + "1 package\r\nGIMP 3.0.4  GIMP.GIMP  3.0.4  winget\r\n";
        var winget = new FakeWinget { ListOutput = list };
        winget.PerId["GIMP.GIMP"] = new ProcessRunResult(0, Table(ListHeader, Row("GIMP 3.0.4", "GIMP.GIMP", "3.0.4")), string.Empty);

        var results = await CheckAll(Provider(winget), [new AppPolicy { AppId = "gimp", DisplayName = "GIMP", WingetId = "GIMP.GIMP" }]);

        Assert.Equal(1, winget.PerIdCalls);
        Assert.Equal("3.0.4", results[0].InstalledVersion);
    }

    [Fact]
    public async Task An_app_missing_from_a_complete_listing_is_not_installed_without_asking_winget()
    {
        var winget = Device();
        var results = await CheckAll(Provider(winget), [new AppPolicy { AppId = "discord", DisplayName = "Discord", WingetId = "Discord.Discord;Discord.Discord.Canary" }]);

        Assert.False(results[0].IsInstalled);
        Assert.Null(results[0].Error);
        Assert.Equal(0, winget.PerIdCalls);
    }

    [Fact]
    public async Task Per_id_fallbacks_are_capped_per_scan()
    {
        var count = WingetProvider.MaxPerAppLookups + 3;
        var rows = Enumerable.Range(1, count).Select(i => Row($"Product {i:00}", $"Vendor.Product{i:00}.Lo…", "1.0")).ToArray();
        var winget = new FakeWinget { ListOutput = Table(ListHeader, rows) };
        foreach (var i in Enumerable.Range(1, count)) winget.PerId[$"Vendor.Product{i:00}.LongName"] = NotInstalledAnswer;
        var apps = Enumerable.Range(1, count).Select(i => new AppPolicy { AppId = $"p{i}", DisplayName = $"No such name {i}", WingetId = $"Vendor.Product{i:00}.LongName" }).ToList();

        var results = await CheckAll(Provider(winget), apps);

        Assert.Equal(WingetProvider.MaxPerAppLookups, winget.PerIdCalls);
        // Past the cap the listing's answer stands, as the per-id lookup's own fallback takes it: the truncated row.
        Assert.All(results, r => Assert.True(r.IsInstalled));
    }

    // ---------------------------------------------------------------- the rule for one id

    [Fact]
    public void Classify_finds_all_rows_of_an_exact_id_and_combines_them()
    {
        var rows = WingetOutputParser.ParseListOutput(DeviceList);
        var (kind, row, _) = WingetProvider.ClassifyInListing(rows, DeviceList, "microsoft.dotnet.desktopruntime.8");

        Assert.Equal(WingetProvider.ListingMatch.Found, kind);
        Assert.Equal("8.0.31", row!.Version);
        Assert.False(row.HasAvailable);
    }

    [Fact]
    public void Classify_leaves_a_sourceless_row_with_the_id_to_winget()
    {
        var list = Table(ListHeader, Row("Odd Tool", "Vendor.OddTool", "1.0", "", ""));
        Assert.Equal(WingetProvider.ListingMatch.Unsettled, WingetProvider.ClassifyInListing(WingetOutputParser.ParseListOutput(list), list, "Vendor.OddTool").Kind);
    }

    [Fact]
    public void Classify_reports_an_id_winget_does_not_mention_as_absent()
    {
        var rows = WingetOutputParser.ParseListOutput(DeviceList);
        Assert.Equal(WingetProvider.ListingMatch.Absent, WingetProvider.ClassifyInListing(rows, DeviceList, "Google.Chrome").Kind);
        Assert.Equal(WingetProvider.ListingMatch.Absent, WingetProvider.ClassifyInListing(rows, DeviceList, "Discord.Discord").Kind);
    }

    [Theory]
    [InlineData("Google.Chrome.EXE 154.0", "Google.Chrome", false)]
    [InlineData("Google Chrome  Google.Chrome  154.0", "Google.Chrome", true)]
    [InlineData("GIMP.GIMP", "gimp.gimp", true)]
    [InlineData("xGIMP.GIMP 1.0", "GIMP.GIMP", false)]
    [InlineData("", "GIMP.GIMP", false)]
    public void An_id_counts_as_mentioned_only_as_a_word_of_its_own(string output, string id, bool expected) =>
        Assert.Equal(expected, WingetProvider.ContainsIdToken(output, id));

    // ---------------------------------------------------------------- the checker: winget lane and web checks

    private sealed class BlockingSnapshotProvider(TaskCompletionSource webChecked) : IUpdateProvider, IScanSnapshotProvider
    {
        public bool WebWasDoneBeforeSnapshot { get; private set; }
        public int SummaryApps { get; private set; } = -1;
        public UpdateSource Source => UpdateSource.Winget;

        public async Task PrepareScanAsync(IReadOnlyList<AppPolicy> apps, ExecutionContextInfo context, CancellationToken ct)
        {
            // Reading the snapshot is slow; the web check must not wait for it.
            await webChecked.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            WebWasDoneBeforeSnapshot = true;
        }

        public void LogScanSummary(ExecutionContextInfo context, int apps, TimeSpan matching) => SummaryApps = apps;

        public Task<UpdateCheckResult> CheckAsync(AppPolicy app, InstalledApp? installed, ExecutionContextInfo context, CancellationToken ct) =>
            Task.FromResult(UpdateCheckResult.NotInstalled(app.AppId, UpdateSource.Winget));

        public Task<InstallResult> InstallAsync(AppPolicy app, PendingUpdate update, ExecutionContextInfo context, IProgress<string>? progress, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class SignallingWebProvider(TaskCompletionSource webChecked) : IUpdateProvider
    {
        public UpdateSource Source => UpdateSource.Web;

        public Task<UpdateCheckResult> CheckAsync(AppPolicy app, InstalledApp? installed, ExecutionContextInfo context, CancellationToken ct)
        {
            webChecked.TrySetResult();
            return Task.FromResult(UpdateCheckResult.NotInstalled(app.AppId, UpdateSource.Web));
        }

        public Task<InstallResult> InstallAsync(AppPolicy app, PendingUpdate update, ExecutionContextInfo context, IProgress<string>? progress, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task Web_checks_run_while_the_winget_snapshot_is_being_read()
    {
        var webChecked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var winget = new BlockingSnapshotProvider(webChecked);
        var checker = new UpdateChecker(NullLogger<UpdateChecker>.Instance, [winget, new SignallingWebProvider(webChecked)]);
        AppPolicy[] apps =
        [
            new() { AppId = "a", WingetId = "A.A" },
            new() { AppId = "b", WingetId = "B.B" },
            new() { AppId = "web", Source = UpdateSource.Web, VersionUrl = "https://example.invalid" },
        ];

        var results = await checker.CheckAsync(apps, [], User, new ProviderOptions(), CancellationToken.None);

        Assert.True(winget.WebWasDoneBeforeSnapshot);
        Assert.Equal(2, winget.SummaryApps);
        Assert.Equal(new[] { "a", "b", "web" }, results.Select(r => r.AppId));
    }

    [Fact]
    public async Task The_checker_answers_every_winget_app_from_one_snapshot()
    {
        var winget = Device();
        var checker = new UpdateChecker(NullLogger<UpdateChecker>.Instance, [Provider(winget)]);

        var results = await checker.CheckAsync(DeviceApps, [], User, new ProviderOptions(), CancellationToken.None);

        Assert.Equal(DeviceApps.Count, results.Count);
        // The two listings, and the MSIX check of the two ambiguous offers (see A_scan_starts_winget_twice_however_many_apps_it_checks).
        Assert.Equal(2 + winget.DetailsCalls, winget.Calls.Count);
        Assert.Equal(2, winget.DetailsCalls);
    }

    // ---------------------------------------------------------------- the coordinator: system and user scans overlap

    [Fact]
    public async Task The_system_scan_and_the_user_scans_run_at_the_same_time()
    {
        var systemStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var userStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Each side only finishes once the other has started: this deadlocks (and times out) if one waits for the other.
        await UpdateCoordinator.RunScanContextsAsync(
            async () => { systemStarted.SetResult(); await userStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)); },
            [async () => { userStarted.SetResult(); await systemStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)); }]);
    }

    [Fact]
    public async Task A_failing_system_scan_is_reported_after_the_user_scans_finished()
    {
        var userFinished = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateCoordinator.RunScanContextsAsync(
            () => throw new InvalidOperationException("boom"),
            [async () => { await Task.Delay(50); userFinished = true; }]));
        Assert.True(userFinished);
    }

    [Fact]
    public async Task No_system_apps_and_no_trays_is_nothing_to_wait_for() =>
        await UpdateCoordinator.RunScanContextsAsync(null, []);
}
