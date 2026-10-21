using System.Diagnostics;
using Arkimentum.AppMonitor.Install;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Providers;
using Arkimentum.AppMonitor.Service.Policy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// Microsoft.VisualStudioCode 1.142.0 (winget-pkgs, October 2026) pointed at the 1.141.2 installers for over a week: on
/// H-PARALLELSVM every attempt downloaded VSCodeUserSetup-arm64-1.141.2.exe, reinstalled the installed version, and failed
/// as "still at 1.141.2", using up a retry each time. The installer's version resource says 1.141.2, and reading it takes
/// a few MB of the 230 MB file, so the install turn checks it first and holds such an update instead of running it.
/// </summary>
public class MislabeledPackageTests
{
    // ---------------------------------------------------------------- the probe

    [Theory]
    [InlineData("1.141.2", "1.141.2")]
    [InlineData("1.141.2            ", "1.141.2")]
    [InlineData("157.0.1 (x64 en-US)", "157.0.1")]
    [InlineData("10.0.5+3c1e3a", "10.0.5")]
    [InlineData("v2.42304.2", "2.42304.2")]
    [InlineData("26", null)]
    [InlineData("Release", null)]
    [InlineData(null, null)]
    public void The_leading_version_of_a_version_resource_string_is_used(string? value, string? expected) =>
        Assert.Equal(expected, InstallerVersionProbe.LeadingVersion(value));

    [Theory]
    [InlineData("https://vscode.download.prss.microsoft.com/dbazure/download/stable/16507d91be/VSCodeUserSetup-arm64-1.141.2.exe", true)]
    [InlineData("https://example.test/Setup.EXE?sig=abc", true)]
    [InlineData("https://example.test/setup.msi", false)]
    [InlineData("https://example.test/app.msix", false)]
    [InlineData("ftp://example.test/setup.exe", false)]
    [InlineData("not a url", false)]
    [InlineData(null, false)]
    public void Only_http_exe_installers_are_probed(string? url, bool expected) =>
        Assert.Equal(expected, InstallerVersionProbe.IsExeUrl(url, out _));

    [Theory]
    [InlineData("1.141.2", "1.141.2", "1.142.0", true)]
    [InlineData("1.141.2.0", "1.141.2", "1.142.0", true)]
    [InlineData("1.142.0", "1.141.2", "1.142.0", false)]
    [InlineData("1.140.0", "1.141.2", "1.142.0", false)]  // older than installed: a wrapper's own number says nothing
    [InlineData("1.0.0.0", "1.141.2", "1.142.0", false)]  // a wrapper's constant
    [InlineData("1.141.2", "1.141.2", "1.141.2", false)]  // nothing offered
    [InlineData(null, "1.141.2", "1.142.0", false)]
    [InlineData("1.141.2", null, "1.142.0", false)]
    [InlineData("1.141.2", "Unknown", "1.142.0", false)]
    public void Only_an_installer_of_exactly_the_installed_version_below_the_offered_one_is_mislabeled(
        string? installer, string? installed, string? available, bool expected) =>
        Assert.Equal(expected, InstallerVersionProbe.IsMislabeled(installer, installed, available));

    [Fact]
    public void Bytes_that_are_no_pe_image_have_no_resource_section()
    {
        Assert.Null(InstallerVersionProbe.ImageEnd(new byte[4096]));
        Assert.Null(InstallerVersionProbe.ImageEnd("MZ"u8));
    }

    /// <summary>A signed PE file with a version resource that every test run has: the runtime's core library.</summary>
    private static readonly string RealPe = typeof(object).Assembly.Location;

    private static string RealPeVersion => InstallerVersionProbe.LeadingVersion(FileVersionInfo.GetVersionInfo(RealPe).ProductVersion)!;

    /// <summary>Serves a prefix of <paramref name="file"/> as a server that honours ranges would, recording each request.</summary>
    private static InstallerVersionProbe.RangeReader Serve(string file, List<long> requests) => (_, count, _) =>
    {
        lock (requests) requests.Add(count);
        var bytes = File.ReadAllBytes(file);
        return Task.FromResult<byte[]?>(bytes[..(int)Math.Min(count, bytes.Length)]);
    };

    [Fact]
    public async Task The_version_is_read_from_the_headers_and_the_pe_image_only()
    {
        var requests = new List<long>();
        var temp = Path.Combine(Path.GetTempPath(), "AppMonitorTests", Guid.NewGuid().ToString("N"));

        var version = await InstallerVersionProbe.ReadVersionAsync("https://example.test/setup.exe", temp, Serve(RealPe, requests), CancellationToken.None);

        Assert.Equal(RealPeVersion, version);
        Assert.Equal(InstallerVersionProbe.HeaderBytes, requests[0]);
        var end = InstallerVersionProbe.ImageEnd(File.ReadAllBytes(RealPe)[..InstallerVersionProbe.HeaderBytes]);
        Assert.NotNull(end);
        Assert.True(end <= new FileInfo(RealPe).Length);
        Assert.All(requests.Skip(1), r => Assert.Equal(end, r));
        Assert.Empty(Directory.Exists(temp) ? Directory.GetFiles(temp) : []);
    }

    [Fact]
    public async Task A_failing_or_empty_download_gives_no_version()
    {
        var temp = Path.Combine(Path.GetTempPath(), "AppMonitorTests", Guid.NewGuid().ToString("N"));
        Assert.Null(await InstallerVersionProbe.ReadVersionAsync("https://example.test/setup.exe", temp,
            (_, _, _) => throw new HttpRequestException("refused"), CancellationToken.None));
        Assert.Null(await InstallerVersionProbe.ReadVersionAsync("https://example.test/setup.exe", temp,
            (_, _, _) => Task.FromResult<byte[]?>(null), CancellationToken.None));
        Assert.Null(await InstallerVersionProbe.ReadVersionAsync("https://example.test/setup.exe", temp,
            (_, _, _) => Task.FromResult<byte[]?>(new byte[100]), CancellationToken.None));
    }

    [Fact]
    public void The_installer_url_is_read_from_winget_show()
    {
        Assert.Equal("https://example.test/VSCodeUserSetup-arm64-1.141.2.exe", WingetProvider.ParseInstallerUrl(Show("1.142.0", "https://example.test/VSCodeUserSetup-arm64-1.141.2.exe")));
        Assert.Null(WingetProvider.ParseInstallerUrl("Found Visual Studio Code [Microsoft.VisualStudioCode]\r\nVersion: 1.142.0\r\n"));
    }

    // ---------------------------------------------------------------- the provider's check

    private const string CodeId = "Microsoft.VisualStudioCode";
    private const string Url = "https://vscode.download.prss.microsoft.com/dbazure/download/stable/16507d91be/VSCodeUserSetup-arm64-1.141.2.exe";

    private static readonly AppPolicy Code = new() { AppId = "vscode", DisplayName = "Microsoft Visual Studio Code", WingetId = CodeId, Context = InstallContext.User };

    /// <summary>The update as the scan tracks it: <paramref name="installed"/> -> one major version above the real PE's.</summary>
    private static PendingUpdate Update(string installed, string? available = null) => new()
    {
        AppId = "vscode",
        DisplayName = "Microsoft Visual Studio Code",
        Source = UpdateSource.Winget,
        Context = InstallContext.User,
        UserSid = "S-1-5-21-1-2-3-1001",
        InstalledVersion = installed,
        AvailableVersion = available ?? Above(RealPeVersion),
        WingetId = CodeId,
        WingetSourceName = "winget",
    };

    private static string Above(string version) => $"{int.Parse(version.Split('.')[0]) + 1}.0.0";

    /// <summary>What winget 1.30 prints for <c>winget show --id … --scope user</c>, trimmed.</summary>
    private static string Show(string version, string url, string type = "inno") =>
        $"Found Visual Studio Code [{CodeId}]\r\nVersion: {version}\r\nPublisher: Microsoft Corporation\r\nInstaller:\r\n  Installer Type: {type}\r\n  Installer Url: {url}\r\n  Installer SHA256: 00\r\n";

    private static WingetProvider Provider(List<string> calls, string showOutput, InstallerVersionProbe.RangeReader reader, int showExit = 0) =>
        new(NullLogger<WingetProvider>.Instance, new ProviderOptions { DownloadDirectory = Path.Combine(Path.GetTempPath(), "AppMonitorTests", Guid.NewGuid().ToString("N")) })
        {
            InstallRunner = (args, _, context, _) =>
            {
                calls.Add(args);
                Assert.True(context.IsSystem);
                return Task.FromResult(new ProcessRunResult(showExit, showOutput, string.Empty));
            },
            InstallerRangeReader = reader,
        };

    [Fact]
    public async Task An_installer_of_the_installed_version_is_found_mislabeled()
    {
        var calls = new List<string>();
        var update = Update(installed: RealPeVersion);

        var check = await Provider(calls, Show(update.AvailableVersion!, Url), Serve(RealPe, [])).CheckInstallerVersionAsync(Code, update, CancellationToken.None);

        Assert.NotNull(check);
        Assert.True(check.Mislabeled);
        Assert.Equal((CodeId, Url, RealPeVersion, "VSCodeUserSetup-arm64-1.141.2.exe"), (check.WingetId, check.InstallerUrl, check.InstallerVersion, check.InstallerFileName));
        var show = Assert.Single(calls);
        Assert.Equal($"show --id {CodeId} --exact --source winget --scope user --accept-source-agreements --disable-interactivity", show);
    }

    [Fact]
    public async Task An_installer_of_another_version_installs_as_usual()
    {
        var update = Update(installed: "1.0.0");

        var check = await Provider([], Show(update.AvailableVersion!, Url), Serve(RealPe, [])).CheckInstallerVersionAsync(Code, update, CancellationToken.None);

        Assert.NotNull(check);
        Assert.False(check.Mislabeled);
    }

    [Fact]
    public async Task A_machine_wide_update_is_checked_in_machine_scope()
    {
        var calls = new List<string>();
        var update = Update(installed: RealPeVersion);
        update.Context = InstallContext.System;
        update.UserSid = null;

        await Provider(calls, Show(update.AvailableVersion!, Url), Serve(RealPe, [])).CheckInstallerVersionAsync(Code, update, CancellationToken.None);

        Assert.Contains(" --scope machine", Assert.Single(calls));
    }

    [Theory]
    [InlineData("https://example.test/VSCodeSetup.msi", "msi")]
    [InlineData("https://example.test/Code.msix", "msix")]
    public async Task Installers_that_are_no_exe_are_not_downloaded(string url, string type)
    {
        var requests = new List<long>();
        var update = Update(installed: RealPeVersion);

        var check = await Provider([], Show(update.AvailableVersion!, url, type), Serve(RealPe, requests)).CheckInstallerVersionAsync(Code, update, CancellationToken.None);

        Assert.Null(check);
        Assert.Empty(requests);
    }

    [Fact]
    public async Task A_package_of_another_version_or_a_failing_show_is_not_judged()
    {
        var requests = new List<long>();
        var update = Update(installed: RealPeVersion);

        Assert.Null(await Provider([], Show("9.9.9", Url), Serve(RealPe, requests)).CheckInstallerVersionAsync(Code, update, CancellationToken.None));
        Assert.Null(await Provider([], "No applicable installer found; see logs for more details.\r\n", Serve(RealPe, requests),
            showExit: WingetOutputParser.ExitNoApplicableInstaller).CheckInstallerVersionAsync(Code, update, CancellationToken.None));
        Assert.Empty(requests);
    }

    [Fact]
    public async Task Web_sources_and_uncorrelated_installs_are_not_checked()
    {
        var calls = new List<string>();
        var web = Update(installed: RealPeVersion);
        web.Source = UpdateSource.Web;
        var uncorrelated = Update(installed: RealPeVersion);
        uncorrelated.WingetUncorrelated = true;

        Assert.Null(await Provider(calls, Show(web.AvailableVersion!, Url), Serve(RealPe, [])).CheckInstallerVersionAsync(Code, web, CancellationToken.None));
        Assert.Null(await Provider(calls, Show(web.AvailableVersion!, Url), Serve(RealPe, [])).CheckInstallerVersionAsync(Code, uncorrelated, CancellationToken.None));
        Assert.Empty(calls);
    }

    // ---------------------------------------------------------------- the hold

    private static readonly DateTimeOffset T0 = new(2026, 10, 21, 19, 53, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(4);
    private const string Reason = "winget's package 'Microsoft.VisualStudioCode' 1.142.0 installs 1.141.2 (VSCodeUserSetup-arm64-1.141.2.exe), the version already installed; nothing was installed.";

    private static ScanOutcome Scan(string available = "1.142.0") => new(new UpdateCheckResult
    {
        AppId = "vscode",
        Source = UpdateSource.Winget,
        IsInstalled = true,
        InstalledVersion = "1.141.2",
        AvailableVersion = available,
        UpdateAvailable = true,
        WingetId = CodeId,
        ResolvedContext = InstallContext.System,
    }, Code with { AutoInstall = true, Context = InstallContext.System }, InstallContext.System, null);

    private static (Dictionary<string, PendingUpdate> State, PendingUpdate Update) Tracked()
    {
        var state = new Dictionary<string, PendingUpdate>(StringComparer.OrdinalIgnoreCase);
        var outcome = Scan();
        PolicyEngine.Merge(state, [outcome], new HashSet<string> { outcome.Key }, T0);
        return (state, state.Values.Single());
    }

    [Fact]
    public void A_mislabeled_package_is_held_without_counting_a_failure()
    {
        var (_, u) = Tracked();
        PolicyEngine.RequestInstall(u);

        PolicyEngine.MarkMislabeled(u, "1.141.2", Reason, T0.AddMinutes(1));

        Assert.Equal(UpdateState.Available, u.State);
        Assert.Equal(0, u.FailureCount);
        Assert.Null(u.FailedAtUtc);
        Assert.Equal(Reason, u.LastError);
        Assert.Equal("1.141.2", u.MislabeledInstallerVersion);
        Assert.Equal(T0.AddMinutes(1) + PolicyEngine.MislabeledRetryDelay, u.MislabeledUntilUtc);
        Assert.Equal(PolicyActionKind.None, PolicyEngine.Decide(u, T0.AddMinutes(30), Interval, false).Kind);
        Assert.Equal(PolicyActionKind.None, PolicyEngine.Decide(u, T0.AddMinutes(30), Interval, blockingProcessesRunning: true).Kind);
        Assert.Equal(PolicyActionKind.Install, PolicyEngine.Decide(u, T0.AddMinutes(1) + PolicyEngine.MislabeledRetryDelay, Interval, false).Kind);
    }

    [Fact]
    public void A_failed_update_whose_retries_were_used_up_keeps_its_count()
    {
        var (_, u) = Tracked();
        u.State = UpdateState.Failed;
        u.FailureCount = PolicyEngine.MaxAutomaticRetries;
        PolicyEngine.RequestInstall(u);

        PolicyEngine.MarkMislabeled(u, "1.141.2", Reason, T0.AddMinutes(1));

        Assert.Equal(UpdateState.Available, u.State);
        Assert.Equal(PolicyEngine.MaxAutomaticRetries, u.FailureCount);
    }

    [Fact]
    public void Install_now_checks_again_at_once()
    {
        var (_, u) = Tracked();
        PolicyEngine.MarkMislabeled(u, "1.141.2", Reason, T0.AddMinutes(1));

        PolicyEngine.RequestInstall(u);

        Assert.Null(u.MislabeledUntilUtc);
        Assert.Equal(PolicyActionKind.Install, PolicyEngine.Decide(u, T0.AddMinutes(2), Interval, false).Kind);
    }

    [Fact]
    public void A_scan_of_the_same_version_keeps_the_hold_and_a_newer_version_clears_it()
    {
        var (state, u) = Tracked();
        PolicyEngine.MarkMislabeled(u, "1.141.2", Reason, T0.AddMinutes(1));

        PolicyEngine.Merge(state, [Scan()], new HashSet<string> { u.Key }, T0.AddMinutes(10));
        Assert.True(u.IsHeldAsMislabeled(T0.AddMinutes(11)));
        Assert.Equal("1.141.2", u.MislabeledInstallerVersion);

        PolicyEngine.Merge(state, [Scan("1.143.0")], new HashSet<string> { u.Key }, T0.AddMinutes(20));
        Assert.Null(u.MislabeledUntilUtc);
        Assert.Null(u.MislabeledInstallerVersion);
        Assert.Equal(PolicyActionKind.Install, PolicyEngine.Decide(u, T0.AddMinutes(20), Interval, false).Kind);
    }

    [Fact]
    public void The_install_starting_clears_the_hold()
    {
        var (_, u) = Tracked();
        PolicyEngine.MarkMislabeled(u, "1.141.2", Reason, T0.AddMinutes(1));

        PolicyEngine.MarkInstalling(u, T0.AddHours(3));

        Assert.Null(u.MislabeledUntilUtc);
        Assert.Null(u.MislabeledInstallerVersion);
        Assert.Null(u.LastError);
    }
}
