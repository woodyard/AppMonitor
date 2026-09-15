using System.Collections.Concurrent;
using Arkimentum.AppMonitor.Api.Services;

namespace Arkimentum.AppMonitor.Api.Security;

public interface IEnrollRateLimiter
{
    /// <summary>False when the caller has exceeded the per-IP or per-organization budget for the current window.</summary>
    bool TryAcquire(string clientIp, Guid organizationId, out int retryAfterSeconds);
}

/// <summary>
/// In-memory sliding-window limiter for POST /device/enroll. It is per worker instance, which is deliberate: it is a
/// brute-force brake, not a quota. An attacker who guesses an enrollment key still has to beat a 256-bit secret, and
/// the operator can rotate the key (see cloud/README.md, "Threat notes").
/// </summary>
public sealed class EnrollRateLimiter(ServerOptions options, TimeProvider? time = null) : IEnrollRateLimiter
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.OrdinalIgnoreCase);

    public bool TryAcquire(string clientIp, Guid organizationId, out int retryAfterSeconds)
    {
        var window = TimeSpan.FromMinutes(Math.Max(1, options.EnrollWindowMinutes));
        var now = _time.GetUtcNow();
        Prune(now);

        if (!TryCount($"ip:{clientIp}", options.EnrollLimitPerIp, now, window, out retryAfterSeconds)) return false;
        if (!TryCount($"org:{organizationId}", options.EnrollLimitPerOrganization, now, window, out retryAfterSeconds)) return false;
        return true;
    }

    private bool TryCount(string key, int limit, DateTimeOffset now, TimeSpan window, out int retryAfterSeconds)
    {
        retryAfterSeconds = 0;
        if (limit <= 0) return true;

        var entry = _windows.GetOrAdd(key, _ => new Window(now));
        lock (entry)
        {
            if (now - entry.Started >= window)
            {
                entry.Started = now;
                entry.Count = 0;
            }
            if (entry.Count >= limit)
            {
                retryAfterSeconds = Math.Max(1, (int)(window - (now - entry.Started)).TotalSeconds);
                return false;
            }
            entry.Count++;
            return true;
        }
    }

    private void Prune(DateTimeOffset now)
    {
        if (_windows.Count < 10_000) return;
        var cutoff = now - TimeSpan.FromMinutes(Math.Max(1, options.EnrollWindowMinutes) * 4);
        foreach (var (key, entry) in _windows)
            if (entry.Started < cutoff)
                _windows.TryRemove(key, out _);
    }

    private sealed class Window(DateTimeOffset started)
    {
        public DateTimeOffset Started = started;
        public int Count;
    }
}
