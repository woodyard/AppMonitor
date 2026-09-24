using System.Collections.Generic;
using System.Linq;
using Arkimentum.AppMonitor.Models;

namespace Arkimentum.AppMonitor.Tray.Services;

/// <summary>
/// The scoreboard of the progress banner: which updates took part in the current round of installs and how each of
/// them ended. A round starts when the service reports something in progress and ends when nothing is any more.
/// An update counts as finished when it installed (the service stops reporting it) or failed (it stays, in state
/// Failed); an update that leaves the round without being attempted - deferred, or back to Available after the close
/// dialog was dismissed - drops out of the total instead of holding the count back. A finished outcome sticks until
/// the update is queued again (a retry), so a failure the next scan turns back into Available still counts as failed.
/// Pure and WPF-free, so the rule is covered by tests.
/// </summary>
public sealed class InstallRound
{
    private enum Outcome
    {
        Pending,
        Installed,
        Failed,
    }

    private readonly Dictionary<string, Outcome> _keys = new(StringComparer.Ordinal);

    /// <summary>The service is working on the update: queued, waiting for applications to close, or installing.</summary>
    public static bool IsInProgress(PendingUpdate u) =>
        u.State is UpdateState.Scheduled or UpdateState.WaitingForClose or UpdateState.Installing;

    /// <summary>Updates in the round: finished ones plus those still in progress.</summary>
    public int Total => _keys.Count;

    /// <summary>Updates of the round that have ended, installed or failed.</summary>
    public int Finished => _keys.Values.Count(o => o != Outcome.Pending);

    /// <summary>Updates of the round that failed; a part of <see cref="Finished"/>.</summary>
    public int Failed => _keys.Values.Count(o => o == Outcome.Failed);

    /// <summary>Updates in progress that are not installing yet (queued, or waiting for applications to close).</summary>
    public int Queued { get; private set; }

    /// <summary>A single install needs no scoreboard; the counts are shown only when the round holds more than one update.</summary>
    public bool ShowCounts => Total > 1;

    /// <summary>Feeds the updates of each state message (and each refresh); calling it twice with the same list changes nothing.</summary>
    public void Observe(IReadOnlyList<PendingUpdate> updates)
    {
        var byKey = new Dictionary<string, PendingUpdate>(StringComparer.Ordinal);
        foreach (var u in updates) byKey.TryAdd(u.Key, u);

        var inProgress = updates.Where(IsInProgress).ToList();
        if (inProgress.Count == 0)
        {
            Reset();
            return;
        }

        foreach (var key in _keys.Keys.ToList())
        {
            if (!byKey.TryGetValue(key, out var u)) _keys[key] = Outcome.Installed; // gone: installed (or no longer needed)
            else if (IsInProgress(u)) continue; // marked pending below
            else if (u.State == UpdateState.Failed) _keys[key] = Outcome.Failed;
            else if (u.State == UpdateState.Installed) _keys[key] = Outcome.Installed;
            else if (_keys[key] == Outcome.Pending) _keys.Remove(key); // left the round without being attempted
        }

        foreach (var u in inProgress) _keys[u.Key] = Outcome.Pending;
        Queued = inProgress.Count(u => u.State != UpdateState.Installing);
    }

    /// <summary>Ends the round: nothing is in progress, or the agent's own update owns the banner.</summary>
    public void Reset()
    {
        _keys.Clear();
        Queued = 0;
    }
}
