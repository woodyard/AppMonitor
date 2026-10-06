using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Service.Policy;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// The install turn: one install at a time, and when it ends the overdue mandatory update goes first, then the
/// shortest install (never timed = two minutes), then the one that asked first.
/// </summary>
public class InstallTurnGateTests
{
    private static InstallTurnPriority P(int seconds, bool overdue = false) => new(overdue, seconds);

    [Fact]
    public async Task A_free_gate_is_taken_at_once_and_the_next_waits_for_the_release()
    {
        var gate = new InstallTurnGate();
        var first = await gate.EnterAsync(() => P(60), CancellationToken.None);
        var second = gate.EnterAsync(() => P(60), CancellationToken.None);

        Assert.False(second.IsCompleted);
        Assert.Equal(1, gate.Waiting);

        first.Dispose();
        (await second.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        Assert.False(gate.IsHeld);
    }

    [Fact]
    public async Task Waiters_get_the_turn_overdue_first_then_shortest_then_in_arrival_order()
    {
        var gate = new InstallTurnGate();
        var holder = await gate.EnterAsync(() => P(1), CancellationToken.None);
        var order = new List<string>();

        Task Queue(string name, InstallTurnPriority p) => gate.EnterAsync(() => p, CancellationToken.None).ContinueWith(t =>
        {
            lock (order) order.Add(name);
            t.Result.Dispose();
        }, TaskScheduler.Default);

        var all = new[]
        {
            Queue("acrobat (300 s)", P(300)),
            Queue("never timed", P(InstallTurnGate.UnknownExpectedSeconds)),
            Queue("7-zip (30 s)", P(30)),
            Queue("overdue chrome (600 s)", P(600, overdue: true)),
            Queue("putty (30 s, later)", P(30)),
        };
        holder.Dispose();
        await Task.WhenAll(all).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["overdue chrome (600 s)", "7-zip (30 s)", "putty (30 s, later)", "never timed", "acrobat (300 s)"], order);
    }

    [Fact]
    public async Task The_priority_is_read_when_the_turn_is_handed_on_not_when_the_wait_began()
    {
        var gate = new InstallTurnGate();
        var holder = await gate.EnterAsync(() => P(1), CancellationToken.None);
        var overdue = false;

        var shortOne = gate.EnterAsync(() => P(10), CancellationToken.None);
        var mandatory = gate.EnterAsync(() => P(900, overdue), CancellationToken.None);
        overdue = true; // its deadline passed while it waited

        holder.Dispose();
        var turn = await mandatory.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(shortOne.IsCompleted);
        turn.Dispose();
        (await shortOne.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task A_cancelled_wait_leaves_the_queue_and_the_turn_goes_to_the_next()
    {
        var gate = new InstallTurnGate();
        var holder = await gate.EnterAsync(() => P(1), CancellationToken.None);
        using var cts = new CancellationTokenSource();

        var cancelled = gate.EnterAsync(() => P(5), cts.Token);
        var other = gate.EnterAsync(() => P(500), CancellationToken.None);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(1, gate.Waiting);

        holder.Dispose();
        (await other.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        Assert.False(gate.IsHeld);
        Assert.Equal(0, gate.Waiting);
    }

    [Fact]
    public async Task An_already_cancelled_token_fails_at_once_even_on_a_free_gate()
    {
        var gate = new InstallTurnGate();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.EnterAsync(() => P(1), cts.Token));
        Assert.False(gate.IsHeld);
    }

    [Fact]
    public async Task Cancelling_after_the_turn_was_handed_over_changes_nothing()
    {
        var gate = new InstallTurnGate();
        var holder = await gate.EnterAsync(() => P(1), CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var waiting = gate.EnterAsync(() => P(1), cts.Token);

        holder.Dispose();
        var turn = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        Assert.True(gate.IsHeld);
        turn.Dispose();
        Assert.False(gate.IsHeld);
    }

    [Fact]
    public async Task Disposing_a_turn_twice_releases_it_once()
    {
        var gate = new InstallTurnGate();
        var first = await gate.EnterAsync(() => P(1), CancellationToken.None);
        var second = gate.EnterAsync(() => P(1), CancellationToken.None);
        var third = gate.EnterAsync(() => P(1), CancellationToken.None);

        first.Dispose();
        first.Dispose();
        await second.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(third.IsCompleted);
    }

    [Fact]
    public async Task A_throwing_priority_counts_as_a_never_timed_install()
    {
        var gate = new InstallTurnGate();
        var holder = await gate.EnterAsync(() => P(1), CancellationToken.None);
        var broken = gate.EnterAsync(() => throw new InvalidOperationException("update gone"), CancellationToken.None);
        var longer = gate.EnterAsync(() => P(InstallTurnGate.UnknownExpectedSeconds + 1), CancellationToken.None);

        holder.Dispose();
        (await broken.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        (await longer.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public void The_priority_of_an_update_comes_from_its_deadline_and_its_expected_duration()
    {
        var now = new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
        var u = new PendingUpdate { AppId = "chrome", Mandatory = true, DeadlineUtc = now.AddMinutes(-1) };

        Assert.Equal(new InstallTurnPriority(true, 45), InstallTurnPriority.For(u, 45, now));
        Assert.Equal(new InstallTurnPriority(false, InstallTurnGate.UnknownExpectedSeconds), InstallTurnPriority.For(u, null, now.AddMinutes(-2)));
        Assert.Equal(InstallTurnGate.UnknownExpectedSeconds, InstallTurnPriority.For(u, 0, now).ExpectedSeconds);
    }
}
