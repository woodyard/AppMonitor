using Arkimentum.AppMonitor.Models;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// The logic behind the close-apps dialog's per-process rows (Qualifier / HasQualifier / ServiceCloseHint) and the
/// two inputs its <c>Apply</c> and <c>SetProcesses</c> feed on. It lives in Core precisely so it can be tested here:
/// the dialog runs inside a WPF layout pass, where the tray's unhandled-exception handler swallows the exception and
/// leaves a blank window behind. Every shape the service can hand it - no detail, an empty list, a null entry, an
/// unreadable session or owner - has to come out as an answer rather than a throw.
/// </summary>
public class BlockingProcessSummaryTests
{
    private const int MySession = 1;

    private static BlockingProcessInfo Info(string name = "pwsh", int pid = 100, int session = MySession,
        string? user = null, bool? elevated = null) =>
        new() { ProcessName = name, ProcessId = pid, SessionId = session, UserName = user, Elevated = elevated };

    // ---------------------------------------------------------------- details null / empty

    [Fact]
    public void No_detail_at_all_means_no_markers()
    {
        // An older service sends nothing; the dialog then shows the bare names and never claims the service is needed.
        var summary = BlockingProcessSummary.For("pwsh", null, MySession);

        Assert.False(summary.IsElevated);
        Assert.False(summary.IsInAnotherSession);
        Assert.Null(summary.OtherSessionUser);
        Assert.False(summary.NeedsService);
    }

    [Fact]
    public void An_empty_detail_list_means_no_markers()
    {
        var summary = BlockingProcessSummary.For("pwsh", [], MySession);

        Assert.Same(BlockingProcessSummary.None, summary);
        Assert.False(summary.NeedsService);
    }

    [Fact]
    public void A_null_entry_in_the_list_is_skipped_rather_than_thrown_on()
    {
        var summary = BlockingProcessSummary.For("pwsh", [null, Info(elevated: true)], MySession);

        Assert.True(summary.IsElevated);
        Assert.True(summary.NeedsService);
    }

    [Fact]
    public void A_missing_process_name_is_an_answer_not_an_exception()
    {
        Assert.Same(BlockingProcessSummary.None, BlockingProcessSummary.For(null, [Info()], MySession));
        Assert.Same(BlockingProcessSummary.None, BlockingProcessSummary.For("   ", [Info()], MySession));
    }

    [Fact]
    public void Detail_for_a_different_process_never_marks_this_one()
    {
        var summary = BlockingProcessSummary.For("pwsh", [Info("chrome", elevated: true, session: 7)], MySession);

        Assert.False(summary.NeedsService);
    }

    // ---------------------------------------------------------------- the four qualifier combinations

    [Fact]
    public void Neither_elevated_nor_elsewhere_needs_no_service()
    {
        var summary = BlockingProcessSummary.For("pwsh", [Info(elevated: false)], MySession);

        Assert.False(summary.IsElevated);
        Assert.False(summary.IsInAnotherSession);
        Assert.Null(summary.OtherSessionUser);
        Assert.False(summary.NeedsService);
    }

    [Fact]
    public void Elevated_in_my_own_session_needs_the_service()
    {
        var summary = BlockingProcessSummary.For("pwsh", [Info(elevated: true)], MySession);

        Assert.True(summary.IsElevated);
        Assert.False(summary.IsInAnotherSession);
        Assert.True(summary.NeedsService);
    }

    [Fact]
    public void Another_session_needs_the_service_and_names_the_owner()
    {
        var summary = BlockingProcessSummary.For("pwsh", [Info(session: 3, user: @"H-SURFACELAP5\bob")], MySession);

        Assert.False(summary.IsElevated);
        Assert.True(summary.IsInAnotherSession);
        Assert.Equal(@"H-SURFACELAP5\bob", summary.OtherSessionUser);
        Assert.True(summary.NeedsService);
    }

    [Fact]
    public void Elevated_and_in_another_session_reports_both()
    {
        var summary = BlockingProcessSummary.For("pwsh",
            [Info(pid: 1, elevated: true), Info(pid: 2, session: 3, user: @"H-SURFACELAP5\bob")], MySession);

        Assert.True(summary.IsElevated);
        Assert.True(summary.IsInAnotherSession);
        Assert.Equal(@"H-SURFACELAP5\bob", summary.OtherSessionUser);
    }

    [Fact]
    public void An_unreadable_owner_still_marks_the_other_session()
    {
        // SYSTEM can normally read the token, but a protected process refuses: the dialog then says "another session".
        var summary = BlockingProcessSummary.For("pwsh", [Info(session: 3, user: null)], MySession);

        Assert.True(summary.IsInAnotherSession);
        Assert.Null(summary.OtherSessionUser);
    }

    [Fact]
    public void An_unreadable_session_is_never_called_another_session()
    {
        // SessionId -1 means "could not be read". Claiming it belongs to someone else would be a lie in the dialog.
        var summary = BlockingProcessSummary.For("pwsh", [Info(session: -1)], MySession);

        Assert.False(summary.IsInAnotherSession);
        Assert.False(summary.NeedsService);
    }

    [Fact]
    public void An_unreadable_elevation_is_not_elevation()
    {
        var summary = BlockingProcessSummary.For("pwsh", [Info(elevated: null)], MySession);

        Assert.False(summary.IsElevated);
    }

    [Fact]
    public void The_exe_suffix_does_not_change_the_match()
    {
        Assert.True(BlockingProcessSummary.For("pwsh.exe", [Info("pwsh", elevated: true)], MySession).IsElevated);
        Assert.True(BlockingProcessSummary.For("pwsh", [Info("PWSH.EXE", elevated: true)], MySession).IsElevated);
        Assert.True(BlockingProcessSummary.NameMatches("pwsh", "pwsh.exe"));
        Assert.False(BlockingProcessSummary.NameMatches("pwsh", "chrome"));
        Assert.False(BlockingProcessSummary.NameMatches(null, "pwsh"));
    }

    // ---------------------------------------------------------------- what Apply feeds SetProcesses

    [Fact]
    public void The_dialog_lists_what_is_running_and_falls_back_to_what_is_configured()
    {
        var configuredOnly = new PendingUpdate { AppId = "powershell", ProcessNames = ["pwsh", "wt"] };
        Assert.Equal(["pwsh", "wt"], BlockingProcessSummary.NamesFor(configuredOnly));

        var running = new PendingUpdate { AppId = "powershell", ProcessNames = ["pwsh", "wt"], BlockingProcesses = ["pwsh"] };
        Assert.Equal(["pwsh"], BlockingProcessSummary.NamesFor(running));
    }

    [Fact]
    public void Blank_and_missing_names_never_become_empty_rows()
    {
        var update = new PendingUpdate { AppId = "powershell", ProcessNames = ["pwsh", "  ", ""] };

        Assert.Equal(["pwsh"], BlockingProcessSummary.NamesFor(update));
        Assert.Empty(BlockingProcessSummary.NamesFor(new PendingUpdate { AppId = "x" }));
        Assert.Empty(BlockingProcessSummary.NamesFor(null));
    }

    [Fact]
    public void The_signature_changes_when_an_instance_appears_elsewhere()
    {
        // The dialog only rebuilds its rows when the signature moves, so it has to cover the markers as well as the pids.
        var before = BlockingProcessSummary.SignatureFor([Info(pid: 1)]);

        Assert.NotEqual(before, BlockingProcessSummary.SignatureFor([Info(pid: 1), Info(pid: 2, session: 3)]));
        Assert.NotEqual(before, BlockingProcessSummary.SignatureFor([Info(pid: 1, elevated: true)]));
        Assert.NotEqual(before, BlockingProcessSummary.SignatureFor([Info(pid: 1, session: 3)]));
        Assert.Equal(before, BlockingProcessSummary.SignatureFor([Info(pid: 1)]));
    }

    [Fact]
    public void The_signature_of_nothing_is_stable()
    {
        Assert.Equal(string.Empty, BlockingProcessSummary.SignatureFor(null));
        Assert.Equal(string.Empty, BlockingProcessSummary.SignatureFor([]));
        Assert.Equal(string.Empty, BlockingProcessSummary.SignatureFor([null]));
    }
}
