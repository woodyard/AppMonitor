using System.IO;
using System.Threading;
using Arkimentum.AppMonitor.Models;

namespace Arkimentum.AppMonitor.Tray.Infrastructure;

/// <summary>
/// One agent per interactive session. The first instance owns a session-local mutex and waits on a named event;
/// later instances write their command line to a hand-off file, signal the event and exit.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string EventNameSuffix = ".Activate";

    private Mutex? _mutex;
    private EventWaitHandle? _event;
    private RegisteredWaitHandle? _registration;
    private bool _owned;

    /// <summary>Raised on a thread-pool thread when another instance asks this one to come forward.</summary>
    public event Action<string[]>? Activated;

    public bool IsPrimary => _owned;

    /// <summary>Tries to become the single instance for this session.</summary>
    public bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, AgentSettings.TrayMutexName, out _owned);
        if (!_owned)
        {
            _mutex.Dispose();
            _mutex = null;
        }
        return _owned;
    }

    /// <summary>Starts listening for activation requests from later instances. Only valid on the primary instance.</summary>
    public void StartListening()
    {
        if (!_owned) return;
        _event = new EventWaitHandle(false, EventResetMode.AutoReset, AgentSettings.TrayMutexName + EventNameSuffix);
        _registration = ThreadPool.RegisterWaitForSingleObject(_event, OnSignalled, null, Timeout.Infinite, false);
    }

    /// <summary>Called by a secondary instance: hands its command line over and wakes the primary instance.</summary>
    public static bool SignalPrimary(string[] args)
    {
        try
        {
            AppInfo.EnsureDirectories();
            try { File.WriteAllLines(AppInfo.HandoffFile, args); } catch { /* best effort */ }
            if (!EventWaitHandle.TryOpenExisting(AgentSettings.TrayMutexName + EventNameSuffix, out var handle))
                return false;
            using (handle) return handle.Set();
        }
        catch { return false; }
    }

    private void OnSignalled(object? state, bool timedOut)
    {
        if (timedOut) return;
        var args = Array.Empty<string>();
        try
        {
            if (File.Exists(AppInfo.HandoffFile))
            {
                args = File.ReadAllLines(AppInfo.HandoffFile);
                File.Delete(AppInfo.HandoffFile);
            }
        }
        catch { /* the signal itself is what matters */ }
        Activated?.Invoke(args);
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _registration = null;
        _event?.Dispose();
        _event = null;
        if (_mutex is not null)
        {
            try { if (_owned) _mutex.ReleaseMutex(); } catch { }
            _mutex.Dispose();
            _mutex = null;
        }
    }
}
