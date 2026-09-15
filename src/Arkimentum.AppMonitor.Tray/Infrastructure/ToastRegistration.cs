using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Arkimentum.AppMonitor.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Arkimentum.AppMonitor.Tray.Infrastructure;

/// <summary>
/// Makes this unpackaged Win32 process visible to the Windows notification platform.
/// <para>
/// Windows only delivers toasts for an application it can identify by AppUserModelID. A packaged app gets one from
/// its manifest; an unpackaged one needs either a Start-menu shortcut carrying the AUMID or — since Windows 10 1709 —
/// a registration under <c>HKCU\SOFTWARE\Classes\AppUserModelId\&lt;aumid&gt;</c>. Microsoft.Toolkit.Uwp.Notifications
/// registers the COM activator (HKCU\SOFTWARE\Classes\CLSID\{guid}\LocalServer32 = this exe + "-ToastActivated") but
/// does not create either of those, so the agent registers itself here: per user, no admin rights, no installer step.
/// </para>
/// </summary>
public static class ToastRegistration
{
    private const string AppUserModelIdRoot = @"SOFTWARE\Classes\AppUserModelId";
    private const string ClassesClsidRoot = @"SOFTWARE\Classes\CLSID";
    private const string ActivatorArgument = "-ToastActivated";
    private const string AssemblyName = "Arkimentum.AppMonitor.Tray";

    /// <summary>Gives the process its AUMID, which is what the notification platform keys everything off.</summary>
    public static void SetProcessAumid(string aumid)
    {
        try { SetCurrentProcessExplicitAppUserModelID(aumid); }
        catch (Exception)
        {
            // Not fatal: toasts will simply not be delivered, which the caller logs.
        }
    }

    /// <summary>
    /// Writes the per-user AUMID registration, pointing at the COM activator the toast library already registered.
    /// Returns the activator CLSID that was linked, or null when it could not be found.
    /// </summary>
    public static string? EnsureRegistered(string aumid, string displayName, ILogger log)
    {
        try
        {
            var clsid = FindActivatorClsid();
            using var key = Registry.CurrentUser.CreateSubKey($@"{AppUserModelIdRoot}\{aumid}", writable: true);
            if (key is null)
            {
                log.LogWarning("Could not create the AppUserModelId registration for {Aumid}", aumid);
                return null;
            }

            key.SetValue("DisplayName", displayName, RegistryValueKind.String);
            var icon = EnsureIconFile();
            if (icon is not null) key.SetValue("IconUri", icon, RegistryValueKind.String);
            if (clsid is not null) key.SetValue("CustomActivator", clsid, RegistryValueKind.String);

            log.LogInformation("Registered toast AUMID {Aumid} (activator {Clsid}, icon {Icon})",
                aumid, clsid ?? "none", icon ?? "none");
            return clsid;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not register the toast AUMID {Aumid}", aumid);
            return null;
        }
    }

    /// <summary>Finds the CLSID the toast library registered for this executable.</summary>
    private static string? FindActivatorClsid()
    {
        var exe = AppInfo.ExecutablePath;
        if (string.IsNullOrEmpty(exe)) return null;

        using var clsidRoot = Registry.CurrentUser.OpenSubKey(ClassesClsidRoot, writable: false);
        if (clsidRoot is null) return null;

        foreach (var name in clsidRoot.GetSubKeyNames())
        {
            try
            {
                using var server = clsidRoot.OpenSubKey($@"{name}\LocalServer32", writable: false);
                if (server?.GetValue(null) is not string command) continue;
                if (command.Contains(exe, StringComparison.OrdinalIgnoreCase) &&
                    command.Contains(ActivatorArgument, StringComparison.OrdinalIgnoreCase))
                {
                    return name;
                }
            }
            catch { /* a CLSID we may not read is a CLSID we do not want */ }
        }
        return null;
    }

    /// <summary>The AUMID registration needs an icon on disk; the application icon is embedded, so unpack it once.</summary>
    private static string? EnsureIconFile()
    {
        try
        {
            var path = Path.Combine(AppInfo.LocalRoot, "app.ico");
            var resource = Application.GetResourceStream(
                new Uri($"pack://application:,,,/{AssemblyName};component/Assets/app.ico", UriKind.Absolute));
            if (resource is null) return File.Exists(path) ? path : null;

            using var stream = resource.Stream;
            if (File.Exists(path) && new FileInfo(path).Length == stream.Length) return path;

            AppInfo.EnsureDirectories();
            using var file = File.Create(path);
            stream.CopyTo(file);
            return path;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appId);
}
