using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;

namespace Arkimentum.AppMonitor.Tray.Infrastructure;

/// <summary>Identity of this agent process and the per-user paths it owns.</summary>
public static class AppInfo
{
    private static readonly Lazy<string> LazyUserSid = new(() =>
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return id.User?.Value ?? string.Empty;
        }
        catch { return string.Empty; }
    });

    private static readonly Lazy<string> LazyUserName = new(() =>
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return id.Name;
        }
        catch { return Environment.UserName; }
    });

    /// <summary>Assembly version as major.minor.patch.</summary>
    public static string Version { get; } =
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3)
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString(3)
        ?? "1.0.0";

    public static int SessionId { get; } = GetSessionId();

    public static string UserSid => LazyUserSid.Value;

    public static string UserName => LazyUserName.Value;

    /// <summary>%LOCALAPPDATA%\Arkimentum\AppMonitor</summary>
    public static string LocalRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Arkimentum", "AppMonitor");

    public static string LogDirectory { get; } = Path.Combine(LocalRoot, "Logs");

    public static string DownloadDirectory { get; } = Path.Combine(LocalRoot, "Downloads");

    /// <summary>File used to hand a second instance's command line over to the running instance.</summary>
    public static string HandoffFile { get; } = Path.Combine(LocalRoot, "handoff.args");

    public static string ExecutablePath { get; } =
        Environment.ProcessPath ?? Assembly.GetEntryAssembly()?.Location ?? string.Empty;

    private static int GetSessionId()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            return p.SessionId;
        }
        catch { return 0; }
    }

    public static void EnsureDirectories()
    {
        TryCreate(LocalRoot);
        TryCreate(LogDirectory);
        TryCreate(DownloadDirectory);
    }

    private static void TryCreate(string path)
    {
        try { Directory.CreateDirectory(path); } catch { /* logging must never take the process down */ }
    }
}
