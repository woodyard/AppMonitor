using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using Arkimentum.AppMonitor.Admin.Infrastructure;
using Arkimentum.AppMonitor.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Arkimentum.AppMonitor.Admin.Services;

/// <summary>What the Overview page knows about the Windows service. Every field is optional: the service may well
/// not be installed, and the console must still open.</summary>
public sealed record ServiceSnapshot
{
    public bool IsInstalled { get; init; }
    public string State { get; init; } = string.Empty;
    public string StartType { get; init; } = string.Empty;
    public string Account { get; init; } = string.Empty;
    public string? ImagePath { get; init; }
    public string? Version { get; init; }
    public bool CanStart { get; init; }
    public bool CanStop { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Reads the service's state and start configuration and performs Start / Stop / Restart.
/// Control operations need elevation and are refused outright in <c>--user-config</c> testing mode.
/// </summary>
public sealed class ServiceControlService
{
    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services\" + AgentSettings.ServiceName;
    private static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(30);

    private readonly ILogger<ServiceControlService> _log;

    public ServiceControlService(ILogger<ServiceControlService> log, CommandLineOptions options)
    {
        _log = log;
        IsAvailable = !options.UserConfig;
    }

    /// <summary>False in testing mode, where the console must not touch the machine's services.</summary>
    public bool IsAvailable { get; }

    public ServiceSnapshot Read()
    {
        var imagePath = ReadImagePath();
        try
        {
            using var controller = new ServiceController(AgentSettings.ServiceName);
            var status = controller.Status;
            return new ServiceSnapshot
            {
                IsInstalled = true,
                State = status.ToString(),
                StartType = controller.StartType.ToString(),
                Account = ReadRegistryString("ObjectName") ?? "LocalSystem",
                ImagePath = imagePath,
                Version = FileVersion(imagePath),
                CanStart = status is ServiceControllerStatus.Stopped or ServiceControllerStatus.Paused,
                CanStop = status is ServiceControllerStatus.Running or ServiceControllerStatus.Paused,
            };
        }
        catch (InvalidOperationException)
        {
            return new ServiceSnapshot { IsInstalled = false, ImagePath = imagePath, Version = FileVersion(imagePath) };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Reading the state of {Service} failed.", AgentSettings.ServiceName);
            return new ServiceSnapshot { IsInstalled = false, Error = ex.Message };
        }
    }

    public string? Start() => Control("Start", c =>
    {
        c.Start();
        c.WaitForStatus(ServiceControllerStatus.Running, ControlTimeout);
    });

    public string? Stop() => Control("Stop", c =>
    {
        c.Stop();
        c.WaitForStatus(ServiceControllerStatus.Stopped, ControlTimeout);
    });

    public string? Restart() => Control("Restart", c =>
    {
        if (c.Status != ServiceControllerStatus.Stopped)
        {
            c.Stop();
            c.WaitForStatus(ServiceControllerStatus.Stopped, ControlTimeout);
        }
        c.Start();
        c.WaitForStatus(ServiceControllerStatus.Running, ControlTimeout);
    });

    /// <summary>Returns null on success, or a message describing why the action failed.</summary>
    private string? Control(string action, Action<ServiceController> body)
    {
        if (!IsAvailable) return Resources.Strings.ServiceControlDisabledHint;
        try
        {
            _log.LogInformation("{Action} requested for service {Service}.", action, AgentSettings.ServiceName);
            using var controller = new ServiceController(AgentSettings.ServiceName);
            body(controller);
            controller.Refresh();
            _log.LogInformation("{Action} completed; {Service} is {Status}.", action, AgentSettings.ServiceName, controller.Status);
            return null;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "{Action} failed for service {Service}.", action, AgentSettings.ServiceName);
            return ex.Message;
        }
    }

    /// <summary>The service executable, taken from the SCM's ImagePath. Also used to locate catalog.json.</summary>
    public static string? ReadImagePath()
    {
        var raw = ReadRegistryString("ImagePath");
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = Environment.ExpandEnvironmentVariables(raw.Trim());
        // ImagePath may be quoted and may carry arguments.
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            if (end > 1) text = text[1..end];
        }
        else
        {
            var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exe > 0) text = text[..(exe + 4)];
        }
        return text.Length == 0 ? null : text;
    }

    private static string? ReadRegistryString(string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ServicesKey);
            return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }
        catch { return null; }
    }

    private static string? FileVersion(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return string.IsNullOrWhiteSpace(info.ProductVersion) ? info.FileVersion : info.ProductVersion;
        }
        catch { return null; }
    }
}
