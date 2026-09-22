namespace Arkimentum.AppMonitor.Tray.Services;

/// <summary>
/// Whether the agent should show a scan as running. The service reports a scan only once its worker loop has picked
/// the request up, which after "Check now" takes a few seconds, so a request shows as running at once. It stays that
/// way until the service has reported the scan and then its end, or until <see cref="RequestTimeout"/> passes without
/// the service ever reporting it (a refused or lost request must not leave the banner up for good). A state message
/// that says "not scanning" before the scan has started is simply the service not having got to it yet.
/// Pure and WPF-free, so the rule is covered by tests.
/// </summary>
public sealed class ScanActivity
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    private DateTimeOffset? _requestedAt;
    private bool _serviceReportedStart;

    /// <summary>The user asked for a scan ("Check now" in the window or the tray menu).</summary>
    public void Requested(DateTimeOffset now)
    {
        _requestedAt = now;
        _serviceReportedStart = false;
    }

    /// <summary>Feeds the service's own flag from each state message.</summary>
    public void ServiceReported(bool scanInProgress)
    {
        if (_requestedAt is null) return;
        if (scanInProgress) _serviceReportedStart = true;
        else if (_serviceReportedStart) _requestedAt = null; // it started and has finished: the request is done
    }

    /// <summary>Forgets a pending request: the connection dropped, or the request could not be sent.</summary>
    public void Reset()
    {
        _requestedAt = null;
        _serviceReportedStart = false;
    }

    public bool IsActive(bool serviceScanInProgress, DateTimeOffset now) =>
        serviceScanInProgress || (_requestedAt is { } at && !_serviceReportedStart && now - at < RequestTimeout);
}
