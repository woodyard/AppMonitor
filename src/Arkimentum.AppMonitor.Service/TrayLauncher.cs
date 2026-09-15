using System.Diagnostics;
using Arkimentum.AppMonitor.Ipc;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Arkimentum.AppMonitor.Service;

/// <summary>
/// Makes sure the tray agent is running in every active interactive session. The service runs in session 0 and
/// launches the agent with the logged-on user's token (WTSQueryUserToken + CreateProcessAsUser).
/// </summary>
public sealed class TrayLauncher
{
    private readonly ILogger<TrayLauncher> _logger;
    private readonly PipeServer _pipe;
    private readonly Dictionary<int, DateTimeOffset> _lastLaunch = new();
    private static readonly TimeSpan RelaunchInterval = TimeSpan.FromMinutes(2);
    private string? _trayPath;
    private bool _warnedMissing;

    public TrayLauncher(ILogger<TrayLauncher> logger, PipeServer pipe)
    {
        _logger = logger;
        _pipe = pipe;
    }

    public bool IsInteractiveHost { get; set; }

    public string? ResolveTrayPath()
    {
        if (_trayPath is not null && File.Exists(_trayPath)) return _trayPath;
        var candidates = new List<string>();
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(AgentSettings.RegistryRoot);
            if (key?.GetValue("TrayPath") is string s && !string.IsNullOrWhiteSpace(s)) candidates.Add(Environment.ExpandEnvironmentVariables(s));
        }
        catch { }
        var baseDir = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(baseDir, AgentSettings.TrayExecutableName));
        candidates.Add(Path.GetFullPath(Path.Combine(baseDir, "..", "Tray", AgentSettings.TrayExecutableName)));
        candidates.Add(Path.GetFullPath(Path.Combine(baseDir, "..", "Arkimentum.AppMonitor.Tray", AgentSettings.TrayExecutableName)));
        // developer layout: src\Arkimentum.AppMonitor.Service\bin\Debug\net10.0-windows\win-x64 -> src\Arkimentum.AppMonitor.Tray\bin\Debug\net10.0-windows10.0.19041.0\win-x64
        try
        {
            var dir = new DirectoryInfo(baseDir);
            for (var d = dir; d is not null; d = d.Parent)
            {
                if (!d.Name.Equals("Arkimentum.AppMonitor.Service", StringComparison.OrdinalIgnoreCase)) continue;
                var trayRoot = Path.Combine(d.Parent!.FullName, "Arkimentum.AppMonitor.Tray", "bin");
                if (Directory.Exists(trayRoot))
                    candidates.AddRange(Directory.EnumerateFiles(trayRoot, AgentSettings.TrayExecutableName, SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc));
                break;
            }
        }
        catch { }

        _trayPath = candidates.FirstOrDefault(File.Exists);
        if (_trayPath is null && !_warnedMissing)
        {
            _warnedMissing = true;
            _logger.LogWarning("Tray agent executable not found. Looked in: {Candidates}", string.Join("; ", candidates));
        }
        return _trayPath;
    }

    /// <summary>Launches the agent in every active session that has no connected agent. Only meaningful when running as SYSTEM.</summary>
    public void EnsureRunning(AgentSettings settings)
    {
        if (!settings.LaunchTrayAgent) return;
        var path = ResolveTrayPath();
        if (path is null) return;

        IReadOnlyList<NativeMethods.SessionInfo> sessions;
        try { sessions = NativeMethods.EnumerateSessions(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Session enumeration failed"); return; }

        var connectedSessions = _pipe.Clients.Where(c => c.IsTray).Select(c => c.SessionId).ToHashSet();
        foreach (var s in sessions)
        {
            if (!s.IsActive || string.IsNullOrEmpty(s.UserName)) continue;
            if (connectedSessions.Contains(s.SessionId)) continue;
            if (IsTrayRunningInSession(s.SessionId)) continue;
            if (_lastLaunch.TryGetValue(s.SessionId, out var last) && DateTimeOffset.UtcNow - last < RelaunchInterval) continue;
            _lastLaunch[s.SessionId] = DateTimeOffset.UtcNow;

            if (IsInteractiveHost)
            {
                // Running from a console as an admin user: launch in our own session without token games.
                if (s.SessionId != Process.GetCurrentProcess().SessionId) continue;
                try
                {
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) });
                    _logger.LogInformation("Started tray agent in current session {Session}", s.SessionId);
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not start tray agent"); }
                continue;
            }

            try
            {
                var pid = NativeMethods.LaunchInSession(s.SessionId, path, null, Path.GetDirectoryName(path));
                _logger.LogInformation("Started tray agent (pid {Pid}) for {User} in session {Session}", pid, s.QualifiedUser, s.SessionId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not start tray agent for {User} in session {Session}", s.QualifiedUser, s.SessionId);
            }
        }
    }

    private static bool IsTrayRunningInSession(int sessionId)
    {
        var name = Path.GetFileNameWithoutExtension(AgentSettings.TrayExecutableName);
        foreach (var p in Process.GetProcessesByName(name))
        {
            try { if (p.SessionId == sessionId) return true; } catch { } finally { p.Dispose(); }
        }
        return false;
    }
}
