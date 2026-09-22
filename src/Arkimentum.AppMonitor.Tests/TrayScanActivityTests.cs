using Arkimentum.AppMonitor.Tray.Services;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// "Check now" shows progress the moment it is clicked, although the service only reports the scan once its worker
/// loop has picked the request up. The request must neither vanish on a state message that arrives before the scan
/// starts, nor keep the banner up forever when the service never takes it up.
/// </summary>
public class TrayScanActivityTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 22, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Nothing_shows_without_a_request_or_a_scan()
    {
        var scan = new ScanActivity();
        Assert.False(scan.IsActive(serviceScanInProgress: false, T0));
    }

    [Fact]
    public void A_scan_the_service_reports_shows_whoever_started_it()
    {
        // A scheduled scan, or ScanNow from the cloud console: no request from this agent.
        var scan = new ScanActivity();
        Assert.True(scan.IsActive(serviceScanInProgress: true, T0));
    }

    [Fact]
    public void A_request_shows_at_once_and_survives_a_state_message_from_before_the_scan_started()
    {
        var scan = new ScanActivity();
        scan.Requested(T0);
        Assert.True(scan.IsActive(false, T0));

        // An unrelated broadcast while the worker loop has not picked the request up yet.
        scan.ServiceReported(false);
        Assert.True(scan.IsActive(false, T0.AddSeconds(3)));
    }

    [Fact]
    public void Once_the_service_has_reported_the_scan_its_end_ends_the_request()
    {
        var scan = new ScanActivity();
        scan.Requested(T0);
        scan.ServiceReported(true);
        Assert.True(scan.IsActive(true, T0.AddSeconds(10)));

        scan.ServiceReported(false);
        Assert.False(scan.IsActive(false, T0.AddSeconds(40)));
    }

    [Fact]
    public void A_request_the_service_never_takes_up_stops_showing_after_the_timeout()
    {
        var scan = new ScanActivity();
        scan.Requested(T0);

        Assert.True(scan.IsActive(false, T0 + ScanActivity.RequestTimeout - TimeSpan.FromSeconds(1)));
        Assert.False(scan.IsActive(false, T0 + ScanActivity.RequestTimeout));
    }

    [Fact]
    public void A_reset_drops_the_request()
    {
        var scan = new ScanActivity();
        scan.Requested(T0);
        scan.Reset();
        Assert.False(scan.IsActive(false, T0.AddSeconds(1)));
    }

    [Fact]
    public void A_new_request_after_a_finished_scan_shows_again()
    {
        var scan = new ScanActivity();
        scan.Requested(T0);
        scan.ServiceReported(true);
        scan.ServiceReported(false);

        scan.Requested(T0.AddMinutes(5));
        scan.ServiceReported(false); // the previous scan's final state arriving late
        Assert.True(scan.IsActive(false, T0.AddMinutes(5).AddSeconds(2)));
    }
}
