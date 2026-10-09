using System.Text;
using System.Text.RegularExpressions;
using Arkimentum.AppMonitor.Install;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// Installer logs: every winget run that installs or uninstalls passes <c>--log &lt;file&gt;</c> into
/// <see cref="ProviderOptions.InstallerLogDirectory"/>, the folder is pruned before each install (14 days, 50 files,
/// empty leftovers), and a failed install names the log it wrote.
/// </summary>
public sealed class InstallerLogsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "appmon-installer-logs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ---------------------------------------------------------------- names

    [Fact]
    public void The_file_name_is_app_winget_id_timestamp_and_step()
    {
        var at = new DateTime(2026, 10, 9, 14, 3, 7);

        Assert.Equal("snagit_TechSmith.Snagit.2026_20261009-140307.log", InstallerLogs.FileName("snagit", "TechSmith.Snagit.2026", at));
        Assert.Equal("snagit_TechSmith.Snagit.2026_20261009-140307_uninstall-all.log", InstallerLogs.FileName("snagit", "TechSmith.Snagit.2026", at, "uninstall-all"));
    }

    [Fact]
    public void Characters_a_file_name_cannot_hold_and_separators_are_replaced_and_long_parts_cut()
    {
        var name = InstallerLogs.FileName("my app_x", "ARP\\Machine\\X64\\{GUID}:*?", new DateTime(2026, 1, 2, 3, 4, 5), "in stall");

        Assert.Equal("my-app-x_ARP-Machine-X64-{GUID}_20260102-030405_in-stall.log", name);
        var longName = InstallerLogs.FileName(new string('a', 100), new string('b', 100), new DateTime(2026, 1, 2));
        Assert.Equal($"{new string('a', 40)}_{new string('b', 40)}_20260102-000000.log", longName);
        Assert.Equal("app_winget_20260102-000000.log", InstallerLogs.FileName("", " ", new DateTime(2026, 1, 2)));
    }

    [Fact]
    public void A_new_path_creates_the_folder_and_is_unique_within_the_second()
    {
        var at = new DateTime(2026, 10, 9, 14, 3, 7);
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var first = InstallerLogs.NewPath(_dir, "snagit", "TechSmith.Snagit.2026", null, at, reserved);
        var second = InstallerLogs.NewPath(_dir, "snagit", "TechSmith.Snagit.2026", null, at, reserved);
        File.WriteAllText(Path.Combine(_dir, "x_y_20261009-140307.log"), "taken");
        var third = InstallerLogs.NewPath(_dir, "x", "y", null, at);

        Assert.True(Directory.Exists(_dir));
        Assert.Equal(Path.Combine(_dir, "snagit_TechSmith.Snagit.2026_20261009-140307.log"), first);
        Assert.Equal(Path.Combine(_dir, "snagit_TechSmith.Snagit.2026_20261009-140307-2.log"), second);
        Assert.Equal(Path.Combine(_dir, "x_y_20261009-140307-2.log"), third);
    }

    [Fact]
    public void A_folder_that_cannot_be_created_gives_no_path()
    {
        var file = Path.Combine(Path.GetTempPath(), "appmon-not-a-folder-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(file, "x");
        try
        {
            Assert.Null(InstallerLogs.NewPath(Path.Combine(file, "Installers"), "a", "b", null, DateTime.Now));
        }
        finally { File.Delete(file); }
    }

    // ---------------------------------------------------------------- retention

    private string Log(string name, DateTime lastWriteUtc, string content = "MSI (s) log")
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    [Fact]
    public void Logs_older_than_14_days_are_deleted()
    {
        var now = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
        var old = Log("old.log", now.AddDays(-15));
        var recent = Log("recent.log", now.AddDays(-13));
        var other = Log("notes.txt", now.AddDays(-30));

        Assert.Equal(1, InstallerLogs.Prune(_dir, now));

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(other));   // only *.log files are the agent's
    }

    [Fact]
    public void Only_the_50_newest_logs_are_kept()
    {
        var now = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 55; i++) Log($"log{i:00}.log", now.AddMinutes(-i));

        Assert.Equal(5, InstallerLogs.Prune(_dir, now));

        var left = Directory.GetFiles(_dir).Select(Path.GetFileName).OrderBy(n => n).ToList();
        Assert.Equal(50, left.Count);
        Assert.Equal("log00.log", left[0]);
        Assert.Equal("log49.log", left[^1]);
    }

    [Fact]
    public void Empty_leftovers_are_deleted_but_an_empty_log_of_a_running_install_stays()
    {
        var now = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
        var leftover = Log("leftover.log", now.AddHours(-7), content: string.Empty);
        var running = Log("running.log", now.AddMinutes(-5), content: string.Empty);

        InstallerLogs.Prune(_dir, now);

        Assert.False(File.Exists(leftover));
        Assert.True(File.Exists(running));
    }

    [Fact]
    public void Pruning_a_missing_folder_or_a_locked_file_never_throws()
    {
        Assert.Equal(0, InstallerLogs.Prune(Path.Combine(_dir, "missing"), DateTime.UtcNow));
        Assert.Equal(0, InstallerLogs.Prune(string.Empty, DateTime.UtcNow));

        var now = DateTime.UtcNow;
        var locked = Log("locked.log", now.AddDays(-20));
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Equal(0, InstallerLogs.Prune(_dir, now));
        Assert.True(File.Exists(locked));
    }

    // ---------------------------------------------------------------- the provider

    private static readonly ExecutionContextInfo System = ExecutionContextInfo.System;
    private static readonly ExecutionContextInfo User = new() { IsSystem = false, UserSid = "S-1-5-21-1", SessionId = 1 };

    private static readonly AppPolicy Cloudflare = new()
    {
        AppId = "cloudflare-warp",
        DisplayName = "Cloudflare WARP",
        WingetId = "Cloudflare.Warp",
        DetectDisplayNameRegex = "^Cloudflare WARP$",
    };

    private static PendingUpdate Pending(InstallContext context = InstallContext.System) => new()
    {
        AppId = "cloudflare-warp",
        DisplayName = "Cloudflare WARP",
        Source = UpdateSource.Winget,
        Context = context,
        UserSid = context == InstallContext.User ? User.UserSid : null,
        InstalledVersion = "2026.9.1",
        AvailableVersion = "2026.10.0",
        WingetId = "Cloudflare.Warp",
        WingetSourceName = "winget",
    };

    private static string Table(string version) =>
        "Name            Id              Version   Source\r\n" +
        "------------------------------------------------\r\n" +
        $"Cloudflare WARP Cloudflare.Warp {version,-9} winget\r\n";

    private static readonly Regex LogArgument = new("--log (\"[^\"]+\"|\\S+)", RegexOptions.CultureInvariant);

    private static string? LogPath(string args)
    {
        var m = LogArgument.Match(args);
        return m.Success ? m.Groups[1].Value.Trim('"') : null;
    }

    private sealed class FakeWinget
    {
        public ProcessRunResult UpgradeAnswer { get; set; } = new(0, "Successfully installed\r\n", string.Empty);
        public string VersionAfter { get; set; } = "2026.10.0";
        public string? LogContent { get; set; } = "=== Verbose logging started ===";
        public List<string> Calls { get; } = [];

        public Task<ProcessRunResult> Run(string args, TimeSpan timeout, ExecutionContextInfo context, CancellationToken ct)
        {
            lock (Calls) Calls.Add(args);
            if (args.StartsWith("list --id ", StringComparison.Ordinal))
                return Task.FromResult(new ProcessRunResult(0, Table(VersionAfter), string.Empty));
            if (args.StartsWith("upgrade", StringComparison.Ordinal) || args.StartsWith("install", StringComparison.Ordinal) || args.StartsWith("uninstall", StringComparison.Ordinal))
            {
                // The installer writes its log where winget told it to.
                if (LogContent is not null && LogPath(args) is { } path) File.WriteAllText(path, LogContent);
                return Task.FromResult(UpgradeAnswer);
            }
            throw new InvalidOperationException($"unexpected winget call: {args}");
        }

        public string Upgrade => Calls.Single(c => c.StartsWith("upgrade ", StringComparison.Ordinal));
    }

    private WingetProvider Provider(FakeWinget winget, string? directory = "") =>
        new(NullLogger<WingetProvider>.Instance, new ProviderOptions { InstallerLogDirectory = directory == "" ? _dir : directory })
        {
            LookupRunner = winget.Run,
            InstallRunner = winget.Run,
        };

    [Fact]
    public async Task The_upgrade_writes_its_installer_log_into_the_folder()
    {
        var winget = new FakeWinget();

        var result = await Provider(winget).InstallAsync(Cloudflare, Pending(), System, null, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        var path = LogPath(winget.Upgrade);
        Assert.NotNull(path);
        Assert.Equal(_dir, Path.GetDirectoryName(path));
        Assert.Matches(@"^cloudflare-warp_Cloudflare\.Warp_\d{8}-\d{6}\.log$", Path.GetFileName(path));
        Assert.Contains(" --scope machine --log ", winget.Upgrade);
        Assert.DoesNotContain("Installer log:", result.Message ?? string.Empty);
    }

    [Fact]
    public async Task A_failed_install_names_the_installer_log()
    {
        var winget = new FakeWinget
        {
            UpgradeAnswer = new ProcessRunResult(1603, "Installer failed with exit code: 1603\r\n", string.Empty),
            VersionAfter = "2026.9.1",
        };

        var result = await Provider(winget).InstallAsync(Cloudflare, Pending(), System, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.EndsWith($" Installer log: {LogPath(winget.Upgrade)}", result.Message);
    }

    [Fact]
    public async Task A_failed_install_whose_installer_wrote_no_log_names_none()
    {
        var winget = new FakeWinget
        {
            UpgradeAnswer = new ProcessRunResult(1603, "Installer failed with exit code: 1603\r\n", string.Empty),
            VersionAfter = "2026.9.1",
            LogContent = null,
        };

        var result = await Provider(winget).InstallAsync(Cloudflare, Pending(), System, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.DoesNotContain("Installer log:", result.Message);
    }

    [Fact]
    public async Task The_folder_is_pruned_before_the_install()
    {
        var old = Log("old_app_20260901-000000.log", DateTime.UtcNow.AddDays(-20));
        var winget = new FakeWinget();

        await Provider(winget).InstallAsync(Cloudflare, Pending(), System, null, CancellationToken.None);

        Assert.False(File.Exists(old));
    }

    [Fact]
    public async Task Without_a_folder_winget_gets_no_log_argument()
    {
        var winget = new FakeWinget();

        await Provider(winget, directory: null).InstallAsync(Cloudflare, Pending(), System, null, CancellationToken.None);

        Assert.DoesNotContain("--log", winget.Upgrade);
    }

    [Fact]
    public async Task A_log_argument_in_the_configured_arguments_is_not_doubled()
    {
        var winget = new FakeWinget { LogContent = null };

        await Provider(winget).InstallAsync(Cloudflare with { WingetExtraArgs = "--log C:\\own.log" }, Pending(), System, null, CancellationToken.None);

        Assert.Single(winget.Upgrade.Split(' '), t => t == "--log");
        Assert.EndsWith("--log C:\\own.log", winget.Upgrade);
    }

    [Fact]
    public async Task The_user_contexts_install_attempts_each_get_their_own_log()
    {
        // winget refuses the upgrade (no applicable upgrade) and the user context installs over it: --scope user first,
        // which finds no installer, then --installer-type msix.
        var calls = new List<string>();
        Task<ProcessRunResult> Run(string args, TimeSpan t, ExecutionContextInfo c, CancellationToken ct)
        {
            lock (calls) calls.Add(args);
            if (args.StartsWith("list --id ", StringComparison.Ordinal))
                return Task.FromResult(new ProcessRunResult(0, Table(calls.Any(a => a.StartsWith("install") && a.Contains("--installer-type msix")) ? "2026.10.0" : "2026.9.1"), string.Empty));
            if (args.StartsWith("upgrade", StringComparison.Ordinal))
                return Task.FromResult(new ProcessRunResult(WingetOutputParser.ExitNoApplicableUpgrade, "No applicable upgrade found.\r\n", string.Empty));
            if (args.StartsWith("show", StringComparison.Ordinal))
                return Task.FromResult(new ProcessRunResult(0, "Found Cloudflare WARP [Cloudflare.Warp]\r\nVersion: 2026.10.0\r\nInstaller:\r\n  Installer Type: wix\r\n", string.Empty));
            if (args.StartsWith("install", StringComparison.Ordinal))
                return Task.FromResult(args.Contains("--scope user")
                    ? new ProcessRunResult(WingetOutputParser.ExitNoApplicableInstaller, "No applicable installer found; see logs for more details.\r\n", string.Empty)
                    : new ProcessRunResult(0, "Successfully installed\r\n", string.Empty));
            throw new InvalidOperationException($"unexpected winget call: {args}");
        }
        var provider = new WingetProvider(NullLogger<WingetProvider>.Instance, new ProviderOptions { InstallerLogDirectory = _dir })
        {
            LookupRunner = Run,
            InstallRunner = Run,
        };

        var result = await provider.InstallAsync(Cloudflare, Pending(InstallContext.User), User, null, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        var installs = calls.Where(c => c.StartsWith("install ", StringComparison.Ordinal)).Select(c => Path.GetFileName(LogPath(c))!).ToList();
        Assert.Equal(2, installs.Count);
        Assert.EndsWith("_reinstall.log", installs[0]);
        Assert.EndsWith("_reinstall-msix.log", installs[1]);
    }

    [Theory]
    [InlineData("--log C:\\a.log", true)]
    [InlineData("-o C:\\a.log", true)]
    [InlineData("--log=C:\\a.log", true)]
    [InlineData("--logs", false)]
    [InlineData("--verbose-logs", false)]
    [InlineData(null, false)]
    public void A_configured_log_argument_is_recognised(string? args, bool expected)
    {
        Assert.Equal(expected, WingetProvider.HasArgument(args, "--log", "-o"));
    }
}
