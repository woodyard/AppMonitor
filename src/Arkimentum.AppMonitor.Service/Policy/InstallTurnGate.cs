using Arkimentum.AppMonitor.Models;

namespace Arkimentum.AppMonitor.Service.Policy;

/// <summary>
/// Where a queued install stands when the install turn is handed on: a mandatory update past its deadline first, then
/// the one expected to take the shortest time (so a queue of updates is not held up by one long install, and the
/// user sees most of them done early), then the one that asked first.
/// </summary>
/// <param name="Overdue">A mandatory update whose deadline has passed.</param>
/// <param name="ExpectedSeconds">How long the install usually takes on this device (<see cref="InstallTurnGate.UnknownExpectedSeconds"/> when never timed).</param>
public readonly record struct InstallTurnPriority(bool Overdue, int ExpectedSeconds)
{
    /// <param name="expectedSeconds">How long the install usually takes (from the install history); null or 0 = unknown.</param>
    public static InstallTurnPriority For(PendingUpdate u, int? expectedSeconds, DateTimeOffset now) =>
        new(u.IsPastDeadline(now), expectedSeconds is { } s && s > 0 ? s : InstallTurnGate.UnknownExpectedSeconds);
}

/// <summary>
/// The install lock with an order: one install at a time, and when it lets go the waiting install with the highest
/// <see cref="InstallTurnPriority"/> goes next (ties by arrival). A plain semaphore hands the turn to whichever waiter
/// the runtime picks, and the installs of one policy tick all start at once, so the order used to be arbitrary. The
/// priority is asked for when the turn is handed on, not when the install started waiting: a deadline may pass in the
/// meantime. A free gate is taken at once. Cancelling a wait removes it from the queue and fails it with
/// <see cref="OperationCanceledException"/>, as <see cref="SemaphoreSlim.WaitAsync(CancellationToken)"/> does.
/// </summary>
public sealed class InstallTurnGate
{
    /// <summary>The expected duration assumed for an install that was never timed: two minutes, a typical winget upgrade.</summary>
    public const int UnknownExpectedSeconds = 120;

    private readonly object _lock = new();
    private readonly List<Waiter> _waiters = [];
    private bool _held;
    private long _arrivals;

    private sealed class Waiter(Func<InstallTurnPriority> priority, long order)
    {
        public readonly TaskCompletionSource<IDisposable> Turn = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly Func<InstallTurnPriority> Priority = priority;
        public readonly long Order = order;
        public CancellationTokenRegistration Registration;
    }

    /// <summary>True while an install holds the turn.</summary>
    public bool IsHeld { get { lock (_lock) return _held; } }

    /// <summary>How many installs are waiting for their turn.</summary>
    public int Waiting { get { lock (_lock) return _waiters.Count; } }

    /// <summary>
    /// Waits for the turn. Dispose the result to hand it on (once; further disposals do nothing).
    /// <paramref name="priority"/> is called under the gate's lock whenever the turn is handed on, so it must be quick
    /// and must not use the gate.
    /// </summary>
    public Task<IDisposable> EnterAsync(Func<InstallTurnPriority> priority, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(priority);
        if (ct.IsCancellationRequested) return Task.FromCanceled<IDisposable>(ct);
        Waiter waiter;
        lock (_lock)
        {
            if (!_held)
            {
                _held = true;
                return Task.FromResult<IDisposable>(new Turn(this));
            }
            waiter = new Waiter(priority, _arrivals++);
            _waiters.Add(waiter);
        }
        // Registered outside the lock: a token cancelled meanwhile runs the callback right here, which takes the lock.
        waiter.Registration = ct.Register(() => Cancel(waiter, ct));
        return waiter.Turn.Task;
    }

    private void Cancel(Waiter waiter, CancellationToken ct)
    {
        lock (_lock)
        {
            // Already handed the turn: the release removed it under this lock, and the turn is its to dispose.
            if (!_waiters.Remove(waiter)) return;
        }
        waiter.Turn.TrySetCanceled(ct);
    }

    private void Release()
    {
        Waiter? next;
        lock (_lock)
        {
            next = Pick();
            if (next is null) { _held = false; return; }
            _waiters.Remove(next);
        }
        next.Registration.Dispose();
        next.Turn.TrySetResult(new Turn(this));
    }

    /// <summary>The waiter whose turn it is; called under the lock.</summary>
    private Waiter? Pick()
    {
        Waiter? best = null;
        InstallTurnPriority bestPriority = default;
        foreach (var w in _waiters)
        {
            var p = PriorityOf(w);
            if (best is null || Compare(p, w.Order, bestPriority, best.Order) < 0) { best = w; bestPriority = p; }
        }
        return best;
    }

    private static InstallTurnPriority PriorityOf(Waiter w)
    {
        try { return w.Priority(); }
        catch { return new InstallTurnPriority(false, UnknownExpectedSeconds); }
    }

    /// <summary>Negative when (a, orderA) goes before (b, orderB).</summary>
    public static int Compare(InstallTurnPriority a, long orderA, InstallTurnPriority b, long orderB)
    {
        if (a.Overdue != b.Overdue) return a.Overdue ? -1 : 1;
        var bySeconds = a.ExpectedSeconds.CompareTo(b.ExpectedSeconds);
        return bySeconds != 0 ? bySeconds : orderA.CompareTo(orderB);
    }

    private sealed class Turn(InstallTurnGate gate) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) gate.Release();
        }
    }
}
