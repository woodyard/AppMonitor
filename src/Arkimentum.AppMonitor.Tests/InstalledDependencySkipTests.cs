using System.Text;
using Arkimentum.AppMonitor.Install;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// H-SurfaceLap5 (2026-10-09): the Snagit 26.3.1 -> 26.4.0 upgrade as SYSTEM first downloaded and installed the WebView2
/// runtime (~6 min), because Snagit's wix installer declares the package dependency Microsoft.EdgeWebView2Runtime and
/// winget does not correlate the installed runtime at all ("No installed package found"), while its Uninstall entry
/// ("Microsoft Edge WebView2 Runtime" 155.0.4283.45, a SystemComponent under WOW6432Node) is there. With
/// SkipInstalledDependencies the upgrade then runs with --skip-dependencies; a dependency winget lists is left to winget.
/// </summary>
public class InstalledDependencySkipTests
{
    private static readonly ExecutionContextInfo System = ExecutionContextInfo.System;
    private static readonly ExecutionContextInfo User = new() { IsSystem = false, UserSid = "S-1-5-21-1", SessionId = 1 };

    private const string SnagitId = "TechSmith.Snagit.2026";
    private const string WebViewId = "Microsoft.EdgeWebView2Runtime";
    private const string WebViewName = "Microsoft Edge WebView2 Runtime";

    /// <summary>The tail of <c>winget show --id TechSmith.Snagit.2026 --exact --source winget --scope machine</c> (winget 1.30), release notes included.</summary>
    private const string SnagitShow =
        "Found Snagit 2026 [TechSmith.Snagit.2026]\r\n" +
        "Version: 26.4.0\r\n" +
        "Publisher: TechSmith Corporation\r\n" +
        "Description: Snagit is a screen capture and recording software.\r\n" +
        "Release Notes:\r\n" +
        "  Fixes:\r\n" +
        "  - Fixed an issue where capturing with the Clipboard selection option failed to capture all text on the clipboard.\r\n" +
        "  - Fixed an issue where video capture presets may not respect the selected microphone.\r\n" +
        "Release Notes Url: https://download.techsmith.com/update/snagit/enu/26.4.0_updatemessage.html\r\n" +
        "Documentation:\r\n" +
        "  Tutorials: https://www.techsmith.com/learn/tutorials/snagit/\r\n" +
        "Tags:\r\n" +
        "  capture\r\n" +
        "  screenshot\r\n" +
        "Installer:\r\n" +
        "  Installer Type: wix\r\n" +
        "  Installer Url: https://download.techsmith.com/snagit/releases/2640/snagit.msi\r\n" +
        "  Installer SHA256: b8a8223a5674b482fd7a582d634faff81287c3efa410718ecb45f0368a256dbb\r\n" +
        "  Release Date: 2026-10-06\r\n" +
        "  Offline Distribution Supported: true\r\n" +
        "  Dependencies: \r\n" +
        "    - Package Dependencies: \r\n" +
        "        Microsoft.EdgeWebView2Runtime\r\n";

    private const string WebViewShow =
        "Found Microsoft Edge WebView2 Runtime [Microsoft.EdgeWebView2Runtime]\r\n" +
        "Version: 155.0.4283.45\r\n" +
        "Publisher: Microsoft Corporation\r\n" +
        "Installer:\r\n  Installer Type: exe\r\n";

    // ---------------------------------------------------------------- the parser

    [Fact]
    public void The_Snagit_installers_package_dependency_is_read_and_release_notes_are_not()
    {
        var deps = WingetOutputParser.ParseInstallerDependencies(SnagitShow);

        Assert.True(deps.InstallerFound);
        Assert.Equal([new WingetPackageDependency(WebViewId, null)], deps.Packages);
        Assert.Empty(deps.OtherKinds);
    }

    [Fact]
    public void An_installer_without_dependencies_has_none()
    {
        var deps = WingetOutputParser.ParseInstallerDependencies(
            "Found 7-Zip [7zip.7zip]\r\nVersion: 26.03\r\nRelease Notes:\r\n  - Dependencies: fixed\r\nInstaller:\r\n  Installer Type: exe\r\n  Installer Url: https://7-zip.org/a/7z.exe\r\n");

        Assert.True(deps.InstallerFound);
        Assert.Empty(deps.Packages);
        Assert.Empty(deps.OtherKinds);
    }

    [Fact]
    public void Several_package_dependencies_with_minimum_versions_and_Windows_features_are_told_apart()
    {
        const string show =
            "Found App [Vendor.App]\nVersion: 2.0\nInstaller:\n  Installer Type: msix\n  Dependencies:\n" +
            "    - Windows Features:\n        IIS-WebServer\n        NetFx3\n" +
            "    - Package Dependencies:\n        Microsoft.VCLibs.Desktop.14 [>= 14.0.30704.0]\n        Microsoft.UI.Xaml.2.8\n" +
            "    - External Dependencies:\n        Java 17\n" +
            "  Installer Url: https://example.com/app.msix\n";

        var deps = WingetOutputParser.ParseInstallerDependencies(show);

        Assert.Equal(
            [new WingetPackageDependency("Microsoft.VCLibs.Desktop.14", "14.0.30704.0"), new WingetPackageDependency("Microsoft.UI.Xaml.2.8", null)],
            deps.Packages);
        Assert.Equal(["Windows Features", "External Dependencies"], deps.OtherKinds);
    }

    [Fact]
    public void Other_indentation_of_the_same_nesting_reads_the_same()
    {
        const string show =
            "Found Snagit 2026 [TechSmith.Snagit.2026]\nInstaller:\n\tInstaller Type: wix\n\tDependencies:\n\t\t- Package Dependencies:\n\t\t\t\tMicrosoft.EdgeWebView2Runtime\n";

        Assert.Equal([new WingetPackageDependency(WebViewId, null)], WingetOutputParser.ParseInstallerDependencies(show).Packages);
    }

    [Fact]
    public void A_localized_kind_counts_as_another_kind()
    {
        const string show = "Installer:\n  Installer Type: wix\n  Dependencies:\n    - Paketabhängigkeiten:\n        Microsoft.EdgeWebView2Runtime\n";

        var deps = WingetOutputParser.ParseInstallerDependencies(show);

        Assert.Empty(deps.Packages);
        Assert.Equal(["Paketabhängigkeiten"], deps.OtherKinds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("No applicable installer found; see logs for more details.\r\n")]
    [InlineData("Found X [Y]\r\nVersion: 1\r\n")]
    public void Output_without_an_installer_block_says_so(string show)
    {
        Assert.False(WingetOutputParser.ParseInstallerDependencies(show).InstallerFound);
    }

    [Theory]
    [InlineData(WebViewShow, WebViewId, WebViewName)]
    [InlineData("   - \r   \\ \rFound Microsoft Edge WebView2 Runtime [Microsoft.EdgeWebView2Runtime]\r\n", WebViewId, WebViewName)]
    [InlineData("Found X [Other.Id]\r\n", WebViewId, null)]
    [InlineData("", WebViewId, null)]
    public void The_package_name_comes_from_the_Found_line(string show, string id, string? expected)
    {
        Assert.Equal(expected, WingetOutputParser.ParseShowName(show, id));
    }

    // ---------------------------------------------------------------- the rule

    private static readonly WingetInstallerDependencies WebViewOnly = new(true, [new WingetPackageDependency(WebViewId, null)], []);

    private static InstalledApp Entry(string? version = "155.0.4283.45") => new()
    {
        DisplayName = WebViewName,
        DisplayVersion = version,
        RegistryKeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Microsoft EdgeWebView",
    };

    private static WingetProvider.DependencyEvidence Unlisted(InstalledApp? registry, string? name = WebViewName, string id = WebViewId) =>
        new(id, false, name, registry);

    [Fact]
    public void An_installed_dependency_winget_does_not_list_is_skipped()
    {
        Assert.True(WingetProvider.DecideSkipDependencies(WebViewOnly, [Unlisted(Entry())]).Skip);
    }

    [Fact]
    public void A_dependency_winget_lists_is_left_to_winget()
    {
        Assert.False(WingetProvider.DecideSkipDependencies(WebViewOnly, [new WingetProvider.DependencyEvidence(WebViewId, true, null, null)]).Skip);
    }

    [Fact]
    public void A_dependency_that_is_not_installed_at_all_is_installed_by_winget()
    {
        Assert.False(WingetProvider.DecideSkipDependencies(WebViewOnly, [Unlisted(null)]).Skip);
    }

    [Fact]
    public void Unknown_answers_never_skip()
    {
        Assert.False(WingetProvider.DecideSkipDependencies(null, []).Skip);
        Assert.False(WingetProvider.DecideSkipDependencies(WebViewOnly, []).Skip);
        Assert.False(WingetProvider.DecideSkipDependencies(WebViewOnly, [new WingetProvider.DependencyEvidence(WebViewId, null, null, null)]).Skip);
        Assert.False(WingetProvider.DecideSkipDependencies(WebViewOnly, [Unlisted(Entry(), name: null)]).Skip);
        Assert.False(WingetProvider.DecideSkipDependencies(WingetInstallerDependencies.NoInstaller, []).Skip);
    }

    [Fact]
    public void No_package_dependencies_means_nothing_to_skip()
    {
        Assert.False(WingetProvider.DecideSkipDependencies(new WingetInstallerDependencies(true, [], []), []).Skip);
        Assert.False(WingetProvider.DecideSkipDependencies(new WingetInstallerDependencies(true, [], ["Windows Features"]), []).Skip);
    }

    [Fact]
    public void Another_dependency_kind_next_to_the_package_rules_the_skip_out()
    {
        var deps = WebViewOnly with { OtherKinds = ["Windows Features"] };

        Assert.False(WingetProvider.DecideSkipDependencies(deps, [Unlisted(Entry())]).Skip);
    }

    [Fact]
    public void Every_package_dependency_must_be_installed_and_unlisted()
    {
        var deps = new WingetInstallerDependencies(true, [new WingetPackageDependency(WebViewId, null), new WingetPackageDependency("Vendor.Runtime", null)], []);

        Assert.False(WingetProvider.DecideSkipDependencies(deps, [Unlisted(Entry())]).Skip);
        Assert.False(WingetProvider.DecideSkipDependencies(deps, [Unlisted(Entry()), Unlisted(null, "Vendor Runtime", "Vendor.Runtime")]).Skip);
        Assert.True(WingetProvider.DecideSkipDependencies(deps,
            [Unlisted(Entry()), Unlisted(Entry() with { DisplayName = "Vendor Runtime" }, "Vendor Runtime", "Vendor.Runtime")]).Skip);
    }

    [Theory]
    [InlineData("155.0.4283.45", "120.0", true)]
    [InlineData("155.0.4283.45", "155.0.4283.45", true)]
    [InlineData("110.0.1", "120.0", false)]
    [InlineData(null, "120.0", false)]
    public void A_minimum_version_must_be_met_by_the_registry(string? installed, string min, bool skip)
    {
        var deps = new WingetInstallerDependencies(true, [new WingetPackageDependency(WebViewId, min)], []);

        Assert.Equal(skip, WingetProvider.DecideSkipDependencies(deps, [Unlisted(Entry(installed))]).Skip);
    }

    // ---------------------------------------------------------------- the provider

    private static readonly AppPolicy Snagit = new()
    {
        AppId = "snagit",
        DisplayName = "Snagit",
        WingetId = SnagitId,
        DetectDisplayNameRegex = "^Snagit",
    };

    private static PendingUpdate Pending(InstallContext context = InstallContext.System) => new()
    {
        AppId = "snagit",
        DisplayName = "Snagit",
        Source = UpdateSource.Winget,
        Context = context,
        UserSid = context == InstallContext.User ? User.UserSid : null,
        InstalledVersion = "26.3.1",
        AvailableVersion = "26.4.0",
        WingetId = SnagitId,
        WingetSourceName = "winget",
    };

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

    private static readonly ProcessRunResult NoInstalledPackage =
        new(WingetOutputParser.ExitNoInstalledPackageFound, "No installed package found matching input criteria.\r\n", string.Empty);

    private sealed class FakeWinget
    {
        public bool WebViewListed { get; init; }
        public string SnagitShowOutput { get; init; } = SnagitShow;
        public ProcessRunResult? WebViewListAnswer { get; init; }
        public ProcessRunResult UpgradeAnswer { get; set; } = new(0, "Successfully installed\r\n", string.Empty);
        public string SnagitAfter { get; set; } = "26.4.0";
        public List<string> Calls { get; } = [];

        public Task<ProcessRunResult> Run(string args, TimeSpan timeout, ExecutionContextInfo context, CancellationToken ct)
        {
            lock (Calls) Calls.Add(args);
            if (args.StartsWith("show --id " + SnagitId, StringComparison.Ordinal))
                return Task.FromResult(new ProcessRunResult(0, SnagitShowOutput, string.Empty));
            if (args.StartsWith("show --id " + WebViewId, StringComparison.Ordinal))
                return Task.FromResult(new ProcessRunResult(0, WebViewShow, string.Empty));
            if (args.StartsWith("list --id " + WebViewId, StringComparison.Ordinal))
                return Task.FromResult(WebViewListAnswer ?? (WebViewListed
                    ? new ProcessRunResult(0, Table([WebViewName, WebViewId, "155.0.4283.45", "", "winget"]), string.Empty)
                    : NoInstalledPackage));
            if (args.StartsWith("list --id " + SnagitId, StringComparison.Ordinal))
                return Task.FromResult(new ProcessRunResult(0, Table(["Snagit 2026", SnagitId, SnagitAfter, "", "winget"]), string.Empty));
            if (args.StartsWith("upgrade --id ", StringComparison.Ordinal) || args.StartsWith("install --id ", StringComparison.Ordinal))
                return Task.FromResult(UpgradeAnswer);
            throw new InvalidOperationException($"unexpected winget call: {args}");
        }

        public string Upgrade => Calls.Single(c => c.StartsWith("upgrade ", StringComparison.Ordinal));
    }

    private static WingetProvider Provider(FakeWinget winget, bool skipInstalled = true, Func<string, ExecutionContextInfo, InstalledApp?>? registry = null,
        List<(string Name, ExecutionContextInfo Context)>? registryCalls = null) =>
        new(NullLogger<WingetProvider>.Instance, new ProviderOptions { SkipInstalledDependencies = skipInstalled })
        {
            LookupRunner = winget.Run,
            InstallRunner = winget.Run,
            DependencyRegistryReader = registry ?? ((name, ctx) =>
            {
                registryCalls?.Add((name, ctx));
                return string.Equals(name, WebViewName, StringComparison.OrdinalIgnoreCase) ? Entry() : null;
            }),
        };

    [Fact]
    public async Task The_Snagit_upgrade_skips_the_WebView2_runtime_winget_does_not_list()
    {
        var winget = new FakeWinget();
        var registryCalls = new List<(string, ExecutionContextInfo)>();

        var result = await Provider(winget, registryCalls: registryCalls).InstallAsync(Snagit, Pending(), System, null, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal(
            "upgrade --id TechSmith.Snagit.2026 --exact --source winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity --scope machine --skip-dependencies",
            winget.Upgrade);
        // The installer the upgrade would pick: same source and scope.
        Assert.Contains("show --id TechSmith.Snagit.2026 --exact --source winget --scope machine --accept-source-agreements --disable-interactivity", winget.Calls);
        // The dependency is looked for in every scope, not only the application's.
        Assert.Contains("list --id Microsoft.EdgeWebView2Runtime --exact --source winget --accept-source-agreements --disable-interactivity", winget.Calls);
        Assert.Equal([(WebViewName, System)], registryCalls);
    }

    [Fact]
    public async Task A_WebView2_runtime_winget_lists_is_left_to_winget()
    {
        var winget = new FakeWinget { WebViewListed = true };

        var result = await Provider(winget).InstallAsync(Snagit, Pending(), System, null, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain("--skip-dependencies", winget.Upgrade);
        Assert.DoesNotContain(winget.Calls, c => c.StartsWith("show --id " + WebViewId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_runtime_missing_from_the_registry_is_installed_by_winget()
    {
        var winget = new FakeWinget();

        await Provider(winget, registry: (_, _) => null).InstallAsync(Snagit, Pending(), System, null, CancellationToken.None);

        Assert.DoesNotContain("--skip-dependencies", winget.Upgrade);
    }

    [Fact]
    public async Task A_failed_dependency_lookup_never_skips_and_never_fails_the_install()
    {
        var winget = new FakeWinget { WebViewListAnswer = new ProcessRunResult(-1, string.Empty, string.Empty, TimedOut: true) };

        var result = await Provider(winget).InstallAsync(Snagit, Pending(), System, null, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain("--skip-dependencies", winget.Upgrade);
    }

    [Fact]
    public async Task Without_the_option_no_lookups_run()
    {
        var winget = new FakeWinget();

        await Provider(winget, skipInstalled: false).InstallAsync(Snagit, Pending(), System, null, CancellationToken.None);

        Assert.DoesNotContain("--skip-dependencies", winget.Upgrade);
        Assert.DoesNotContain(winget.Calls, c => c.StartsWith("show ", StringComparison.Ordinal));
        Assert.DoesNotContain(winget.Calls, c => c.StartsWith("list --id " + WebViewId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_installer_without_dependencies_costs_one_show_and_nothing_else()
    {
        var winget = new FakeWinget { SnagitShowOutput = "Found Snagit 2026 [TechSmith.Snagit.2026]\r\nVersion: 26.4.0\r\nInstaller:\r\n  Installer Type: wix\r\n" };

        await Provider(winget).InstallAsync(Snagit, Pending(), System, null, CancellationToken.None);

        Assert.DoesNotContain("--skip-dependencies", winget.Upgrade);
        Assert.Single(winget.Calls, c => c.StartsWith("show ", StringComparison.Ordinal));
        Assert.DoesNotContain(winget.Calls, c => c.StartsWith("list --id " + WebViewId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task In_the_users_session_the_show_uses_the_user_scope_and_the_registry_includes_the_users_hive()
    {
        var winget = new FakeWinget();
        var registryCalls = new List<(string, ExecutionContextInfo)>();

        await Provider(winget, registryCalls: registryCalls).InstallAsync(Snagit, Pending(InstallContext.User), User, null, CancellationToken.None);

        Assert.EndsWith("--scope user --skip-dependencies", winget.Upgrade);
        Assert.Contains("show --id TechSmith.Snagit.2026 --exact --source winget --scope user --accept-source-agreements --disable-interactivity", winget.Calls);
        Assert.Equal([(WebViewName, User)], registryCalls);
    }

    [Fact]
    public async Task Configured_arguments_that_already_skip_dependencies_are_not_doubled()
    {
        var winget = new FakeWinget();

        await Provider(winget).InstallAsync(Snagit with { WingetExtraArgs = "--skip-dependencies" }, Pending(), System, null, CancellationToken.None);

        Assert.Single(winget.Upgrade.Split(' '), t => t == "--skip-dependencies");
        Assert.DoesNotContain(winget.Calls, c => c.StartsWith("show ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_verdict_is_cached_per_dependency_for_the_provider_instance()
    {
        var winget = new FakeWinget();
        var provider = Provider(winget);

        await provider.InstallAsync(Snagit, Pending(), System, null, CancellationToken.None);
        await provider.InstallAsync(Snagit with { AppId = "snagit-2" }, Pending(), System, null, CancellationToken.None);

        Assert.Single(winget.Calls, c => c.StartsWith("list --id " + WebViewId, StringComparison.Ordinal));
        Assert.Single(winget.Calls, c => c.StartsWith("show --id " + WebViewId, StringComparison.Ordinal));
    }
}
