using System.Text;
using Arkimentum.AppMonitor.Install;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// H-SURFACELAP5 (2026-10-19): the tray updated a per-user GIMP 3.2.4 -> 3.2.6. The manifest says
/// <c>UpgradeBehavior: uninstallPrevious</c>, winget ran the old version's QuietUninstallString
/// <c>"...\unins000.exe" /SILENT</c>, and Inno's /SILENT shows a progress window: the user watched GIMP being removed.
/// For the duration of a user-context install the Inno entries of the application are made /VERYSILENT.
/// </summary>
public sealed class InnoQuietUninstallTests : IDisposable
{
    private readonly string _rootPath = @"SOFTWARE\Arkimentum.AppMonitor.Tests\" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _root;

    private const string GimpKey = "GIMP-3_is1";
    private const string GimpQuiet = @"""C:\Users\hsk\AppData\Local\Programs\GIMP 3\uninst\unins000.exe"" /SILENT";
    private const string GimpVerySilent = @"""C:\Users\hsk\AppData\Local\Programs\GIMP 3\uninst\unins000.exe"" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART";

    private static readonly AppPolicy Gimp = new() { AppId = "gimp", DisplayName = "GIMP", WingetId = "GIMP.GIMP" };

    public InnoQuietUninstallTests() => _root = Registry.CurrentUser.CreateSubKey(_rootPath, writable: true)!;

    public void Dispose()
    {
        _root.Dispose();
        try { Registry.CurrentUser.DeleteSubKeyTree(_rootPath, false); } catch { }
    }

    private void Entry(string keyName, string displayName, string? quiet, bool inno = true)
    {
        using var key = _root.CreateSubKey(keyName, writable: true)!;
        key.SetValue("DisplayName", displayName);
        key.SetValue("DisplayVersion", "3.2.4");
        if (inno) key.SetValue(InnoQuietUninstall.InnoVersionValue, "6.4.3");
        if (quiet is not null) key.SetValue(InnoQuietUninstall.QuietUninstallValue, quiet);
    }

    private string? Quiet(string keyName)
    {
        using var key = _root.OpenSubKey(keyName);
        return key?.GetValue(InnoQuietUninstall.QuietUninstallValue) as string;
    }

    private RegistryKey? OpenRoot() => Registry.CurrentUser.OpenSubKey(_rootPath, writable: false);

    // ---------------------------------------------------------------- the command line

    [Theory]
    [InlineData(GimpQuiet, GimpVerySilent)]
    [InlineData(@"""C:\x\unins000.exe"" /SILENT", @"""C:\x\unins000.exe"" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART")]
    [InlineData(@"""C:\x\unins000.exe"" /silent", @"""C:\x\unins000.exe"" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART")]
    [InlineData(@"  ""C:\x\UNINS001.EXE""   /SILENT  ", @"  ""C:\x\UNINS001.EXE""   /VERYSILENT /SUPPRESSMSGBOXES /NORESTART  ")]
    [InlineData(@"C:\x\unins000.exe /SILENT", @"C:\x\unins000.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART")]
    [InlineData(@"""C:\x\unins000.exe"" /SILENT /NORESTART", @"""C:\x\unins000.exe"" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART")]
    [InlineData(@"""C:\x\unins000.exe"" /LOG /SILENT", @"""C:\x\unins000.exe"" /LOG /VERYSILENT /SUPPRESSMSGBOXES /NORESTART")]
    public void An_inno_silent_uninstall_becomes_very_silent(string quiet, string expected)
    {
        Assert.Equal(expected, InnoQuietUninstall.VerySilentForm(quiet));
    }

    [Theory]
    [InlineData(@"""C:\x\unins000.exe"" /VERYSILENT")]
    [InlineData(@"""C:\x\unins000.exe"" /SILENT /VERYSILENT")]
    [InlineData(@"""C:\x\unins000.exe""")]
    [InlineData(@"""C:\x\unins000.exe"" /SILENTLY")]
    [InlineData(@"""C:\Program Files\Notepad++\uninstall.exe"" /S")]
    [InlineData(@"""C:\x\uninstall.exe"" /SILENT")]
    [InlineData(@"MsiExec.exe /X{2F1F2D8E-1A0B-4C3D-8E5F-6A7B8C9D0E1F} /qn")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_left_alone(string? quiet)
    {
        Assert.Null(InnoQuietUninstall.VerySilentForm(quiet));
    }

    // ---------------------------------------------------------------- the registry

    [Fact]
    public void Only_the_apps_inno_entries_are_rewritten_and_all_are_restored()
    {
        Entry(GimpKey, "GIMP 3.2.4", GimpQuiet);
        Entry("Other_is1", "Inkscape", @"""C:\i\unins000.exe"" /SILENT");
        Entry("GimpNsis", "GIMP Portable", @"""C:\g\unins000.exe"" /SILENT", inno: false);

        var rewrites = InnoQuietUninstall.RewriteEntries(_root, Gimp, NullLogger.Instance);

        Assert.Equal([new InnoQuietUninstall.Rewrite(GimpKey, GimpQuiet, GimpVerySilent)], rewrites);
        Assert.Equal(GimpVerySilent, Quiet(GimpKey));
        Assert.Equal(@"""C:\i\unins000.exe"" /SILENT", Quiet("Other_is1"));
        Assert.Equal(@"""C:\g\unins000.exe"" /SILENT", Quiet("GimpNsis"));

        InnoQuietUninstall.Restore(_root, rewrites, Gimp, NullLogger.Instance);

        Assert.Equal(GimpQuiet, Quiet(GimpKey));
    }

    [Fact]
    public void A_value_the_new_installer_wrote_is_not_restored()
    {
        Entry(GimpKey, "GIMP 3.2.4", GimpQuiet);
        var rewrites = InnoQuietUninstall.RewriteEntries(_root, Gimp, NullLogger.Instance);
        const string fresh = @"""C:\Users\hsk\AppData\Local\Programs\GIMP 3\uninst\unins001.exe"" /SILENT";
        using (var key = _root.OpenSubKey(GimpKey, writable: true)!) key.SetValue(InnoQuietUninstall.QuietUninstallValue, fresh);

        InnoQuietUninstall.Restore(_root, rewrites, Gimp, NullLogger.Instance);

        Assert.Equal(fresh, Quiet(GimpKey));
    }

    [Fact]
    public void An_entry_the_uninstall_removed_is_not_brought_back()
    {
        Entry(GimpKey, "GIMP 3.2.4", GimpQuiet);
        var rewrites = InnoQuietUninstall.RewriteEntries(_root, Gimp, NullLogger.Instance);
        _root.DeleteSubKeyTree(GimpKey);

        InnoQuietUninstall.Restore(_root, rewrites, Gimp, NullLogger.Instance);

        Assert.Null(_root.OpenSubKey(GimpKey));
    }

    // ---------------------------------------------------------------- around the install

    private static string Table(params string[] row)
    {
        var header = new[] { "Name", "Id", "Version" };
        var widths = Enumerable.Range(0, header.Length).Select(c => Math.Max(header[c].Length, row[c].Length) + 1).ToArray();
        string Line(string[] cells) => string.Concat(cells.Select((v, c) => c == cells.Length - 1 ? v : v.PadRight(widths[c]))).TrimEnd();
        return new StringBuilder().Append(Line(header)).Append("\r\n").Append(new string('-', widths.Sum())).Append("\r\n")
            .Append(Line(row)).Append("\r\n").ToString();
    }

    private static readonly ExecutionContextInfo User = new() { IsSystem = false, UserSid = "S-1-5-21-1", SessionId = 2 };

    private static PendingUpdate Pending() => new()
    {
        AppId = "gimp",
        DisplayName = "GIMP",
        InstalledVersion = "3.2.4",
        AvailableVersion = "3.2.6",
        Source = UpdateSource.Winget,
        Context = InstallContext.User,
        UserSid = "S-1-5-21-1",
        WingetId = "GIMP.GIMP",
    };

    private WingetProvider Provider(Func<string, ProcessRunResult> upgrade, string versionAfter, InstallContext context = InstallContext.User)
    {
        Task<ProcessRunResult> Run(string args, TimeSpan timeout, ExecutionContextInfo ctx, CancellationToken ct)
        {
            if (args.StartsWith("upgrade --id ", StringComparison.Ordinal)) return Task.FromResult(upgrade(args));
            if (args.StartsWith("list --id GIMP.GIMP ", StringComparison.Ordinal))
                return Task.FromResult(new ProcessRunResult(0, Table("GIMP", "GIMP.GIMP", versionAfter), string.Empty));
            return Task.FromResult(new ProcessRunResult(1, string.Empty, string.Empty));
        }
        return new WingetProvider(NullLogger<WingetProvider>.Instance, new ProviderOptions())
        {
            LookupRunner = Run,
            InstallRunner = Run,
            UserUninstallRoot = OpenRoot,
        };
    }

    [Fact]
    public async Task Winget_runs_the_very_silent_uninstaller_and_the_new_entry_is_left_alone()
    {
        Entry(GimpKey, "GIMP 3.2.4", GimpQuiet);
        string? seenDuringUpgrade = null;
        const string fresh = @"""C:\Users\hsk\AppData\Local\Programs\GIMP 3\uninst\unins000.exe"" /SILENT";
        var provider = Provider(_ =>
        {
            seenDuringUpgrade = Quiet(GimpKey);
            // uninstallPrevious removed the old entry; the new installer wrote its own.
            _root.DeleteSubKeyTree(GimpKey);
            Entry(GimpKey, "GIMP 3.2.6", fresh);
            return new ProcessRunResult(0, "Successfully installed", string.Empty);
        }, versionAfter: "3.2.6");

        var result = await provider.InstallAsync(Gimp, Pending(), User, null, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal(GimpVerySilent, seenDuringUpgrade);
        Assert.Equal(fresh, Quiet(GimpKey));
    }

    [Fact]
    public async Task A_failed_install_restores_the_original_command()
    {
        Entry(GimpKey, "GIMP 3.2.4", GimpQuiet);
        var provider = Provider(_ => new ProcessRunResult(unchecked((int)0x8A150011), "Installer failed with exit code: 5", string.Empty), versionAfter: "3.2.4");

        var result = await provider.InstallAsync(Gimp, Pending(), User, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(GimpQuiet, Quiet(GimpKey));
    }

    [Fact]
    public async Task The_service_never_rewrites_anything()
    {
        Entry(GimpKey, "GIMP 3.2.4", GimpQuiet);
        string? seenDuringUpgrade = null;
        var provider = Provider(_ => { seenDuringUpgrade = Quiet(GimpKey); return new ProcessRunResult(0, "Successfully installed", string.Empty); }, versionAfter: "3.2.6");

        await provider.InstallAsync(Gimp, Pending(), ExecutionContextInfo.System, null, CancellationToken.None);

        Assert.Equal(GimpQuiet, seenDuringUpgrade);
    }
}
