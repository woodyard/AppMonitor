namespace Arkimentum.AppMonitor.Service.Policy;

/// <summary>
/// The application installs that have really begun - past the waits of their turn (the install turn itself, a busy
/// Windows Installer), from the close of blocking applications to the recorded result - and the rule that keeps them
/// apart from the agent replacing itself. An update that is only waiting does not count: it held off an agent update
/// for minutes on H-SURFACELAP5 (2026-10-30, PowerToys waiting 4 min 41 s for another msiexec) although nothing of
/// ours ran. Instead it may not begin while the agent is being replaced, and stays queued for the restarted service.
/// <para>
/// The two sides cannot both go ahead: the agent updater first announces the replacement and then asks
/// <see cref="Running"/>, while <see cref="TryBegin"/> asks for a replacement and adds the install under one lock, so
/// either the updater sees the install or the install sees the replacement.
/// </para>
/// </summary>
public sealed class RunningInstallers
{
    private readonly object _lock = new();
    private readonly Dictionary<string, string> _running = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Begins the install of <paramref name="key"/> unless <paramref name="replacementUnderway"/> (asked under the lock)
    /// says the agent is being replaced; <paramref name="refusal"/> is then what it said.
    /// </summary>
    public bool TryBegin(string key, string displayName, Func<string?> replacementUnderway, out string? refusal)
    {
        lock (_lock)
        {
            refusal = replacementUnderway();
            if (refusal is not null) return false;
            _running[key] = displayName;
            return true;
        }
    }

    /// <summary>Ends the install of <paramref name="key"/>; nothing happens when it never began.</summary>
    public void End(string key)
    {
        lock (_lock) _running.Remove(key);
    }

    /// <summary>The display name of an install that has begun, or null when none has.</summary>
    public string? Running
    {
        get { lock (_lock) return _running.Values.FirstOrDefault(); }
    }
}
