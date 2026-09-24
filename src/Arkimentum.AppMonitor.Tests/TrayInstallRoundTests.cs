using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Tray.Resources;
using Arkimentum.AppMonitor.Tray.Services;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// The "x of y finished" scoreboard of the tray's progress banner. An installed update disappears from the state
/// message, a failed one stays in state Failed; both must count as finished, or the counter sits at "0 of N" while
/// the failures pile up. An update that leaves the round without being attempted drops out of the total.
/// </summary>
public class TrayInstallRoundTests
{
    private static PendingUpdate U(string app, UpdateState state) =>
        new() { AppId = app, DisplayName = app, Context = InstallContext.System, State = state };

    /// <summary>One state message: the updates the service reports, in the states given.</summary>
    private static List<PendingUpdate> State(params (string App, UpdateState State)[] updates) =>
        updates.Select(u => U(u.App, u.State)).ToList();

    [Fact]
    public void All_succeed_counts_each_install_as_it_disappears()
    {
        var round = new InstallRound();

        round.Observe(State(("A", UpdateState.Installing), ("B", UpdateState.Scheduled), ("C", UpdateState.Scheduled)));
        Assert.Equal((0, 3, 0, 2), (round.Finished, round.Total, round.Failed, round.Queued));

        round.Observe(State(("B", UpdateState.Installing), ("C", UpdateState.Scheduled)));
        Assert.Equal((1, 3, 0, 1), (round.Finished, round.Total, round.Failed, round.Queued));

        round.Observe(State(("C", UpdateState.Installing)));
        Assert.Equal((2, 3, 0, 0), (round.Finished, round.Total, round.Failed, round.Queued));

        round.Observe(State());
        Assert.Equal(0, round.Total);
    }

    [Fact]
    public void A_failed_update_counts_as_finished_and_as_failed()
    {
        var round = new InstallRound();
        round.Observe(State(("A", UpdateState.Installing), ("B", UpdateState.Scheduled), ("C", UpdateState.Scheduled)));

        // A failed and stays reported; B installs next.
        round.Observe(State(("A", UpdateState.Failed), ("B", UpdateState.Installing), ("C", UpdateState.Scheduled)));
        Assert.Equal((1, 3, 1, 1), (round.Finished, round.Total, round.Failed, round.Queued));

        // B installed (gone), C installing.
        round.Observe(State(("A", UpdateState.Failed), ("C", UpdateState.Installing)));
        Assert.Equal((2, 3, 1, 0), (round.Finished, round.Total, round.Failed, round.Queued));

        // The same state message again (the window's clock refresh) changes nothing.
        round.Observe(State(("A", UpdateState.Failed), ("C", UpdateState.Installing)));
        Assert.Equal((2, 3, 1, 0), (round.Finished, round.Total, round.Failed, round.Queued));
    }

    [Fact]
    public void A_failure_the_scan_turns_back_into_available_still_counts_until_it_is_queued_again()
    {
        var round = new InstallRound();
        round.Observe(State(("A", UpdateState.Installing), ("B", UpdateState.Scheduled)));
        round.Observe(State(("A", UpdateState.Failed), ("B", UpdateState.Installing)));

        // A scan re-arms the failed update for an automatic retry.
        round.Observe(State(("A", UpdateState.Available), ("B", UpdateState.Installing)));
        Assert.Equal((1, 2, 1), (round.Finished, round.Total, round.Failed));

        // The retry is queued: A is pending again, not finished.
        round.Observe(State(("A", UpdateState.Scheduled), ("B", UpdateState.Installing)));
        Assert.Equal((0, 2, 0, 1), (round.Finished, round.Total, round.Failed, round.Queued));
    }

    [Fact]
    public void An_update_deferred_mid_round_drops_out_of_the_total()
    {
        var round = new InstallRound();
        round.Observe(State(("A", UpdateState.Installing), ("B", UpdateState.Scheduled), ("C", UpdateState.Scheduled)));

        round.Observe(State(("A", UpdateState.Installing), ("B", UpdateState.Deferred), ("C", UpdateState.Scheduled)));
        Assert.Equal((0, 2, 0, 1), (round.Finished, round.Total, round.Failed, round.Queued));

        // A installed; C finishes the round's count without B holding it back.
        round.Observe(State(("B", UpdateState.Deferred), ("C", UpdateState.Installing)));
        Assert.Equal((1, 2, 0, 0), (round.Finished, round.Total, round.Failed, round.Queued));
    }

    [Fact]
    public void A_dismissed_close_dialog_drops_the_update_out_of_the_total()
    {
        var round = new InstallRound();
        round.Observe(State(("A", UpdateState.Installing), ("B", UpdateState.WaitingForClose), ("C", UpdateState.Scheduled)));
        Assert.Equal(2, round.Queued);

        round.Observe(State(("A", UpdateState.Installing), ("B", UpdateState.Available), ("C", UpdateState.Scheduled)));
        Assert.Equal((0, 2, 0, 1), (round.Finished, round.Total, round.Failed, round.Queued));
    }

    [Fact]
    public void A_single_install_shows_no_counts()
    {
        var round = new InstallRound();
        round.Observe(State(("A", UpdateState.Installing)));
        Assert.Equal(1, round.Total);
        Assert.False(round.ShowCounts);

        // Two queued shows counts; once one of them is deferred it is a single install again.
        round.Reset();
        round.Observe(State(("A", UpdateState.Installing), ("B", UpdateState.Scheduled)));
        Assert.True(round.ShowCounts);
        round.Observe(State(("A", UpdateState.Installing), ("B", UpdateState.Deferred)));
        Assert.False(round.ShowCounts);
    }

    [Fact]
    public void A_new_round_starts_from_zero_and_ignores_the_failures_of_the_last_one()
    {
        var round = new InstallRound();
        round.Observe(State(("A", UpdateState.Installing), ("B", UpdateState.Scheduled)));
        round.Observe(State(("A", UpdateState.Failed), ("B", UpdateState.Installing)));
        round.Observe(State(("A", UpdateState.Failed)));
        Assert.Equal((0, 0, 0, 0), (round.Finished, round.Total, round.Failed, round.Queued));
        Assert.False(round.ShowCounts);

        round.Observe(State(("A", UpdateState.Failed), ("C", UpdateState.Installing), ("D", UpdateState.Scheduled)));
        Assert.Equal((0, 2, 0, 1), (round.Finished, round.Total, round.Failed, round.Queued));
    }

    [Fact]
    public void The_banner_tail_names_failures_and_queued_updates_only_when_there_are_some()
    {
        Assert.Equal("(1 of 6 finished, 4 queued)", Strings.ProgressCounts(1, 6, 0, 4));
        Assert.Equal("(3 of 6 finished, 1 failed, 2 queued)", Strings.ProgressCounts(3, 6, 1, 2));
        Assert.Equal("(4 of 5 finished, 2 failed)", Strings.ProgressCounts(4, 5, 2, 0));
        Assert.Equal("(2 of 3 finished)", Strings.ProgressCounts(2, 3, 0, 0));
    }
}
