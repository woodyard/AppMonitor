using Arkimentum.AppMonitor.Ipc;
using Arkimentum.AppMonitor.Models;
using Arkimentum.AppMonitor.Service.State;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

public class InstallHistoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 22, 10, 0, 0, TimeSpan.Zero);
    private const string Alice = "S-1-5-21-1-1001";
    private const string Bob = "S-1-5-21-1-1002";

    private static InstallHistoryEntry Entry(string appId, DateTimeOffset at, InstallContext context = InstallContext.System, string? sid = null, bool ok = true) => new()
    {
        AppId = appId,
        DisplayName = appId,
        ToVersion = "1.0",
        Succeeded = ok,
        CompletedUtc = at,
        Context = context,
        UserSid = sid,
    };

    [Fact]
    public void Append_puts_the_new_entry_first()
    {
        var history = InstallHistory.Append([], Entry("a", T0));
        history = InstallHistory.Append(history, Entry("b", T0.AddMinutes(1)));
        history = InstallHistory.Append(history, Entry("c", T0.AddMinutes(2)));

        Assert.Equal(["c", "b", "a"], history.Select(e => e.AppId));
    }

    [Fact]
    public void Append_keeps_a_new_entry_ahead_of_one_with_the_same_time()
    {
        var history = InstallHistory.Append([Entry("old", T0)], Entry("new", T0));

        Assert.Equal(["new", "old"], history.Select(e => e.AppId));
    }

    [Fact]
    public void Append_caps_the_history_at_50_and_drops_the_oldest()
    {
        List<InstallHistoryEntry> history = [];
        for (var i = 0; i < 60; i++) history = InstallHistory.Append(history, Entry($"app{i}", T0.AddMinutes(i)));

        Assert.Equal(InstallHistory.MaxEntries, history.Count);
        Assert.Equal("app59", history[0].AppId);
        Assert.Equal("app10", history[^1].AppId);
    }

    [Fact]
    public void Append_does_not_edit_the_list_it_was_given()
    {
        List<InstallHistoryEntry> before = [Entry("a", T0)];

        var after = InstallHistory.Append(before, Entry("b", T0.AddMinutes(1)));

        Assert.Single(before);
        Assert.NotSame(before, after);
    }

    [Fact]
    public void Machine_entries_are_visible_to_everyone_and_user_entries_only_to_their_user()
    {
        List<InstallHistoryEntry> history =
        [
            Entry("machine", T0.AddMinutes(3)),
            Entry("alice-app", T0.AddMinutes(2), InstallContext.User, Alice),
            Entry("bob-app", T0.AddMinutes(1), InstallContext.User, Bob),
        ];

        Assert.Equal(["machine", "alice-app"], InstallHistory.VisibleTo(history, Alice).Select(e => e.AppId));
        Assert.Equal(["machine", "bob-app"], InstallHistory.VisibleTo(history, Bob.ToLowerInvariant()).Select(e => e.AppId));
        Assert.Equal(["machine"], InstallHistory.VisibleTo(history, null).Select(e => e.AppId));
    }

    [Fact]
    public void A_client_gets_at_most_the_10_newest_entries_it_may_see()
    {
        List<InstallHistoryEntry> history = [];
        for (var i = 0; i < 20; i++)
            history = InstallHistory.Append(history, i % 2 == 0
                ? Entry($"machine{i}", T0.AddMinutes(i))
                : Entry($"bob{i}", T0.AddMinutes(i), InstallContext.User, Bob));

        var visible = InstallHistory.VisibleTo(history, Alice);

        Assert.Equal(10, visible.Count);
        Assert.All(visible, e => Assert.Equal(InstallContext.System, e.Context));
        Assert.Equal("machine18", visible[0].AppId);

        var forBob = InstallHistory.VisibleTo(history, Bob);
        Assert.Equal(InstallHistory.MaxSentToClient, forBob.Count);
        Assert.Equal("bob19", forBob[0].AppId);
        Assert.Equal("machine10", forBob[^1].AppId);
    }

    [Fact]
    public void A_success_records_the_version_read_back_and_the_previous_version()
    {
        var u = new PendingUpdate { AppId = "firefox", DisplayName = "Firefox", InstalledVersion = "155.0", AvailableVersion = "156.0", Context = InstallContext.User, UserSid = Alice };

        var entry = InstallHistory.Succeeded(u, InstallResult.Ok() with { InstalledVersion = "156.0.1" }, T0);

        Assert.True(entry.Succeeded);
        Assert.Equal("155.0", entry.FromVersion);
        Assert.Equal("156.0.1", entry.ToVersion);
        Assert.Equal(Alice, entry.UserSid);
        Assert.Null(entry.Message);
        Assert.Equal("156.0", InstallHistory.Succeeded(u, InstallResult.Ok(), T0).ToVersion);
    }

    [Fact]
    public void A_failure_records_the_target_version_and_a_short_single_line_message()
    {
        var u = new PendingUpdate { AppId = "7zip", DisplayName = "7-Zip", InstalledVersion = "24.08", AvailableVersion = "25.01", Context = InstallContext.System, UserSid = "ignored" };

        var entry = InstallHistory.Failed(u, "line one\r\nline two " + new string('x', 400), T0);

        Assert.False(entry.Succeeded);
        Assert.Equal("25.01", entry.ToVersion);
        Assert.Null(entry.UserSid);
        Assert.Equal(InstallHistory.MaxMessageLength, entry.Message!.Length);
        Assert.StartsWith("line one line two", entry.Message);
        Assert.EndsWith("…", entry.Message);
        Assert.Null(InstallHistory.Failed(u, "  ", T0).Message);
    }

    [Fact]
    public void State_file_round_trips_the_history()
    {
        using var dir = new TempDir();
        var settings = new AgentSettings { StateDirectory = dir.Path };
        var store = new StateStore(NullLogger<StateStore>.Instance);
        var state = new ServiceState();
        state.InstallHistory = InstallHistory.Append(state.InstallHistory, Entry("a", T0));
        state.InstallHistory = InstallHistory.Append(state.InstallHistory, Entry("b", T0.AddMinutes(5), InstallContext.User, Alice, ok: false));

        store.Save(settings, state);
        var back = store.Load(settings);

        Assert.Equal(["b", "a"], back.InstallHistory.Select(e => e.AppId));
        Assert.False(back.InstallHistory[0].Succeeded);
        Assert.Equal(InstallContext.User, back.InstallHistory[0].Context);
        Assert.Equal(Alice, back.InstallHistory[0].UserSid);
        Assert.Equal(T0.AddMinutes(5), back.InstallHistory[0].CompletedUtc);
    }

    [Fact]
    public void An_older_state_file_without_a_history_loads_with_an_empty_one()
    {
        using var dir = new TempDir();
        var settings = new AgentSettings { StateDirectory = dir.Path };
        File.WriteAllText(StateStore.PathFor(settings),
            "{ \"Updates\": {}, \"LastScanUtc\": \"2026-09-20T10:00:00+00:00\", \"AppPresence\": {} }");

        var back = new StateStore(NullLogger<StateStore>.Instance).Load(settings);

        Assert.NotNull(back.LastScanUtc);
        Assert.NotNull(back.InstallHistory);
        Assert.Empty(back.InstallHistory);
    }

    [Fact]
    public void A_state_file_with_a_null_or_oversized_history_is_normalised_on_load()
    {
        using var dir = new TempDir();
        var settings = new AgentSettings { StateDirectory = dir.Path };
        var store = new StateStore(NullLogger<StateStore>.Instance);

        File.WriteAllText(StateStore.PathFor(settings), "{ \"InstallHistory\": null }");
        Assert.Empty(store.Load(settings).InstallHistory);

        // Written out of order and too long (by hand, or by a future version with a larger cap).
        var state = new ServiceState { InstallHistory = Enumerable.Range(0, 70).Select(i => Entry($"app{i}", T0.AddMinutes(i))).ToList() };
        store.Save(settings, state);
        var back = store.Load(settings);
        Assert.Equal(InstallHistory.MaxEntries, back.InstallHistory.Count);
        Assert.Equal("app69", back.InstallHistory[0].AppId);
    }

    [Fact]
    public void State_message_round_trips_the_recent_installs()
    {
        var message = new StateMessage { RecentInstalls = [Entry("firefox", T0, InstallContext.User, Alice, ok: false)] };

        var back = Assert.IsType<StateMessage>(IpcJson.Deserialize(IpcJson.Serialize(message)));

        var entry = Assert.Single(back.RecentInstalls!);
        Assert.Equal("firefox", entry.AppId);
        Assert.False(entry.Succeeded);
        Assert.Equal(InstallContext.User, entry.Context);
        Assert.Equal(T0, entry.CompletedUtc);
    }

    [Fact]
    public void State_message_from_an_older_service_has_no_recent_installs()
    {
        const string line = "{\"$type\":\"state\",\"updates\":[],\"scanInProgress\":false,\"serviceVersion\":\"1.1.20\",\"settings\":{\"scanIntervalMinutes\":240,\"monitoredApps\":[]}}";

        var back = Assert.IsType<StateMessage>(IpcJson.Deserialize(line));

        Assert.Null(back.RecentInstalls);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "appmon-history-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }
}
