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

    /// <summary>%LOCALAPPDATA%\Arkimentum\AppMonitor\Logs — the per-user log directory.</summary>
    public static string UserLogDirectory { get; } = Path.Combine(LocalRoot, "Logs");

    /// <summary>
    /// Where this process writes its log: the shared <c>%ProgramData%</c> directory when it can, and the
    /// per-user one otherwise — in <c>--user-config</c> testing mode by definition, and for the organization
    /// console, which runs unelevated and is not allowed to write under <c>%ProgramData%</c>.
    /// </summary>
    public static string LogDirectory(bool userConfig)
    {
        if (userConfig) return EnsureWritable(UserLogDirectory) ? UserLogDirectory : Path.GetTempPath();
        if (EnsureWritable(AgentSettings.DefaultLogDirectory)) return AgentSettings.DefaultLogDirectory;
        return EnsureWritable(UserLogDirectory) ? UserLogDirectory : Path.GetTempPath();
    }

    /// <summary>True when the directory exists (or could be created) and this process may add a file to it.</summary>
    private static bool EnsureWritable(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, $".write-probe-{Environment.ProcessId}");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch { return false; }
    }

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
