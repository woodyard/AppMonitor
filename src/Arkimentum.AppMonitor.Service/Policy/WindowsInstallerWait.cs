using System.Diagnostics;

namespace Arkimentum.AppMonitor.Service.Policy;

/// <summary>How a wait for Windows Installer ended (see <see cref="WindowsInstallerWait.WaitUntilFreeAsync"/>).</summary>
/// <param name="WasBusy">Windows Installer was busy at the first look, so there was a wait at all.</param>
/// <param name="Free">Windows Installer is free now: the install may start. False when the wait ran out.</param>
/// <param name="Waited">How long the wait took (zero when there was none).</param>
public readonly record struct WindowsInstallerWaitResult(bool WasBusy, bool Free, TimeSpan Waited)
{
    public static readonly WindowsInstallerWaitResult NotBusy = new(false, true, TimeSpan.Zero);
}

/// <summary>
/// The wait at the install turn for another installation to release Windows Installer. While one runs (the
/// <c>Global\_MSIExecute</c> mutex exists, see <see cref="Install.WindowsInstallerActivity"/>), an MSI-based installer
/// started now is refused with 1618: on H-SURFACELAP5 (2026-10-19) five updates in a row failed that way within six
/// minutes while Windows Update or Intune installed. So the update that holds the turn looks first and, while Windows
/// Installer is busy, keeps the turn (the installs queued behind it would hit the same wall) and looks again every
/// <see cref="PollInterval"/>, for at most <see cref="MaxWait"/>. Before anything else happens at the turn: the user is
/// never asked to close an application, nor is one closed, only to then wait. Constants, not settings. The probe and the
/// delay are injected, so the rule is testable without a real mutex or a real clock.
/// </summary>
public static class WindowsInstallerWait
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Returns at once with <see cref="WindowsInstallerWaitResult.NotBusy"/> when <paramref name="isBusy"/> says no.
    /// Otherwise runs <paramref name="waitStarted"/> once (to show and log the wait) and asks again after every
    /// <paramref name="pollInterval"/> until Windows Installer is free or <paramref name="maxWait"/> worth of intervals
    /// has passed (counted by the intervals waited, so a slow probe cannot stretch it into a loop without end).
    /// Cancellation ends the wait with <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <param name="delay">The wait between looks; <see cref="Task.Delay(TimeSpan, CancellationToken)"/> unless a test passes its own.</param>
    public static async Task<WindowsInstallerWaitResult> WaitUntilFreeAsync(Func<bool> isBusy, Func<Task>? waitStarted, CancellationToken ct,
        TimeSpan? pollInterval = null, TimeSpan? maxWait = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(isBusy);
        if (!isBusy()) return WindowsInstallerWaitResult.NotBusy;

        var poll = pollInterval is { } p && p > TimeSpan.Zero ? p : PollInterval;
        var cap = maxWait ?? MaxWait;
        delay ??= Task.Delay;
        var started = Stopwatch.GetTimestamp();
        if (waitStarted is not null) await waitStarted().ConfigureAwait(false);

        var waited = TimeSpan.Zero;
        while (waited < cap)
        {
            ct.ThrowIfCancellationRequested();
            var step = cap - waited < poll ? cap - waited : poll;
            await delay(step, ct).ConfigureAwait(false);
            waited += step;
            if (!isBusy()) return new WindowsInstallerWaitResult(true, true, Stopwatch.GetElapsedTime(started));
        }
        return new WindowsInstallerWaitResult(true, false, Stopwatch.GetElapsedTime(started));
    }
}
