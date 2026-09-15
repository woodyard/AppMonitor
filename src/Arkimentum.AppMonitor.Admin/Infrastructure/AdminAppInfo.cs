using System.IO;
using System.Reflection;
using System.Security.Principal;
using Arkimentum.AppMonitor.Models;

namespace Arkimentum.AppMonitor.Admin.Infrastructure;

/// <summary>Identity of this process and the paths it writes to.</summary>
public static class AdminAppInfo
{
    /// <summary>Assembly version as major.minor.patch.</summary>
    public static string Version { get; } =
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3)
        ?? typeof(AdminAppInfo).Assembly.GetName().Version?.ToString(3)
        ?? "1.0.0";

    public static string ExecutablePath { get; } =
        Environment.ProcessPath ?? Assembly.GetEntryAssembly()?.Location ?? string.Empty;

    /// <summary>%LOCALAPPDATA%\Arkimentum\AppMonitor — used for logs in --user-config testing mode.</summary>
    public static string LocalRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Arkimentum", "AppMonitor");

    /// <summary>Where this process writes its log: ProgramData normally, LocalAppData in testing mode.</summary>
    public static string LogDirectory(bool userConfig) =>
        userConfig ? Path.Combine(LocalRoot, "Logs") : AgentSettings.DefaultLogDirectory;

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    public static string UserName
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return identity.Name;
            }
            catch { return Environment.UserName; }
        }
    }

    public static void TryCreateDirectory(string path)
    {
        try { Directory.CreateDirectory(path); } catch { /* logging must never take the process down */ }
    }
}
