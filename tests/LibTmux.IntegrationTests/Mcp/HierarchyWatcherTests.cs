using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Mcp;
using LibTmux.Testing;

namespace LibTmux.IntegrationTests;

/// <summary>Serializes the tests that hold a tmux control client.</summary>
/// <remarks>
/// A control client is a tmux process attached for as long as a test runs, and
/// several at once slow the machine enough to change what unrelated tests see.
/// The first symptom is always a test waiting on a shell to redraw, because
/// those carry the tightest budgets.
///
/// Measured after the harness stopped leaking servers, which was the larger
/// half of the same problem: serialized, four runs of the suite were clean;
/// parallel, one run in three failed.
/// </remarks>
[CollectionDefinition("tmux control clients", DisableParallelization = true)]
public sealed class ControlClientCollectionDefinition;

/// <summary>Telling a subscriber that the hierarchy is not what it was.</summary>
/// <remarks>
/// Driven directly rather than through a client. How a change reaches a client
/// is the protocol's business and it moves — the 2026-07-28 revision replaced
/// <c>resources/subscribe</c> with <c>subscriptions/listen</c>. What must keep
/// working across that is the part below: tmux says a window appeared, and a
/// subscriber is told.
/// </remarks>
[Collection("tmux control clients")]
[UnsupportedOSPlatform("windows")]
public sealed class HierarchyWatcherTests
{
    [UnixFact]
    public async Task A_window_appearing_reaches_a_subscriber()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TmuxTestFactory factory = new();
        TmuxTestOptions options = new(new ServerConnectionOptions(
            tmuxBinaryPath: System.Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux",
            socketName: $"ltw-{Guid.NewGuid():N}"[..20],
            configurationFile: "/dev/null"));
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            options,
            token);

        await using HierarchyWatcher watcher = new();
        TaskCompletionSource<IReadOnlyList<string>> told = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await watcher.SubscribeAsync(
            "tmux://hierarchy",
            changed =>
            {
                told.TrySetResult(changed);
                return Task.CompletedTask;
            },
            scope.Session.Server,
            token);

        await scope.Session.CreateWindowAsync(
            new NewWindowRequest(name: "appeared"),
            token);

        Task finished = await Task.WhenAny(
            told.Task,
            Task.Delay(TimeSpan.FromSeconds(20), token));
        Assert.True(finished == told.Task, "the watcher never reported the new window");
        Assert.Contains("tmux://hierarchy", await told.Task);
    }

    [UnixFact]
    public async Task Selecting_a_window_reaches_a_subscriber()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TmuxTestFactory factory = new();
        TmuxTestOptions options = new(new ServerConnectionOptions(
            tmuxBinaryPath: System.Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux",
            socketName: $"ltw-{Guid.NewGuid():N}"[..20],
            configurationFile: "/dev/null"));
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            options,
            token);
        _ = await scope.Session.CreateWindowAsync(
            new NewWindowRequest(name: "selected", attach: true),
            token);

        await using HierarchyWatcher watcher = new();
        TaskCompletionSource<IReadOnlyList<string>> told = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int armed = 0;
        await watcher.SubscribeAsync(
            "tmux://hierarchy",
            changed =>
            {
                if (Volatile.Read(ref armed) != 0)
                {
                    told.TrySetResult(changed);
                }

                return Task.CompletedTask;
            },
            scope.Session.Server,
            token);
        Volatile.Write(ref armed, 1);

        _ = await scope.Window.SelectAsync(token);

        Task finished = await Task.WhenAny(
            told.Task,
            Task.Delay(TimeSpan.FromSeconds(20), token));
        Assert.True(finished == told.Task, "the watcher missed the active-window change");
        Assert.Contains("tmux://hierarchy", await told.Task);
    }

    [UnixFact]
    public async Task Moving_a_client_to_another_session_reaches_a_subscriber()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TmuxTestFactory factory = new();
        TmuxTestOptions options = new(new ServerConnectionOptions(
            tmuxBinaryPath: System.Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux",
            socketName: $"ltw-{Guid.NewGuid():N}"[..20],
            configurationFile: "/dev/null"));
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            options,
            token);
        Session other = await scope.Session.Server.CreateSessionAsync(
            new NewSessionRequest(name: "other"),
            token);
        await using IControlModeSession moving = await scope.Session.Server.EnterControlModeAsync(
            scope.Session.Id.ToString(),
            token);

        await using HierarchyWatcher watcher = new();
        TaskCompletionSource<IReadOnlyList<string>> told = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int armed = 0;
        await watcher.SubscribeAsync(
            "tmux://sessions",
            changed =>
            {
                if (Volatile.Read(ref armed) != 0)
                {
                    told.TrySetResult(changed);
                }

                return Task.CompletedTask;
            },
            scope.Session.Server,
            token);
        Volatile.Write(ref armed, 1);

        _ = await moving.SendAsync(
            TmuxCommand.Create("switch-client", "-t", other.Id.ToString()),
            token);

        Task finished = await Task.WhenAny(
            told.Task,
            Task.Delay(TimeSpan.FromSeconds(20), token));
        Assert.True(finished == told.Task, "the watcher missed the client-session change");
        Assert.Contains("tmux://sessions", await told.Task);
    }

    [UnixFact]
    public async Task Dropping_the_last_subscriber_stops_the_control_client()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TmuxTestFactory factory = new();
        TmuxTestOptions options = new(new ServerConnectionOptions(
            tmuxBinaryPath: System.Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux",
            socketName: $"ltw-{Guid.NewGuid():N}"[..20],
            configurationFile: "/dev/null"));
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            options,
            token);

        await using HierarchyWatcher watcher = new();
        await watcher.SubscribeAsync(
            "tmux://hierarchy",
            _ => Task.CompletedTask,
            scope.Session.Server,
            token);

        // A control client is a real attached client and shows up in the
        // user's own list-clients, so it must not outlive the subscription
        // that needed it.
        await watcher.UnsubscribeAsync("tmux://hierarchy");

        // Polled gently: every probe is a tmux process, and a tight interval
        // here spawns hundreds of them beside the rest of the suite.
        IReadOnlyList<Client> clients = await TmuxWait.UntilAsync(
            cancellation => scope.Session.Server.GetClientsAsync(cancellation),
            current => current.Count == 0,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMilliseconds(250),
            token);
        Assert.Empty(clients);
    }

    [UnixFact]
    public async Task Overlapping_subscribers_are_distinct_and_duplicates_are_idempotent()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TmuxTestFactory factory = new();
        TmuxTestOptions options = new(new ServerConnectionOptions(
            tmuxBinaryPath: System.Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux",
            socketName: $"ltw-{Guid.NewGuid():N}"[..20],
            configurationFile: "/dev/null"));
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            options,
            token);

        await using HierarchyWatcher watcher = new();
        object firstKey = new();
        object secondKey = new();
        TaskCompletionSource firstTold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondTold = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await watcher.SubscribeAsync(
            "tmux://hierarchy",
            firstKey,
            changed =>
            {
                if (changed.Contains("tmux://hierarchy"))
                {
                    firstTold.TrySetResult();
                }

                return Task.CompletedTask;
            },
            scope.Session.Server,
            token);
        await watcher.SubscribeAsync(
            "tmux://hierarchy",
            secondKey,
            changed =>
            {
                if (changed.Contains("tmux://hierarchy"))
                {
                    secondTold.TrySetResult();
                }

                return Task.CompletedTask;
            },
            scope.Session.Server,
            token);
        await watcher.SubscribeAsync(
            "tmux://hierarchy",
            secondKey,
            _ => Task.CompletedTask,
            scope.Session.Server,
            token);

        await scope.Session.CreateWindowAsync(new NewWindowRequest(name: "appeared"), token);

        Task bothTold = Task.WhenAll(firstTold.Task, secondTold.Task);
        Assert.True(
            await Task.WhenAny(bothTold, Task.Delay(TimeSpan.FromSeconds(20), token)) == bothTold,
            "one overlapping subscriber did not receive the hierarchy change");

        await watcher.UnsubscribeAsync("tmux://hierarchy", firstKey);
        IReadOnlyList<Client> oneReference = await TmuxWait.UntilAsync(
            cancellation => scope.Session.Server.GetClientsAsync(cancellation),
            current => current.Count == 1,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMilliseconds(250),
            token);
        Assert.Single(oneReference);

        await watcher.UnsubscribeAsync("tmux://hierarchy", secondKey);
        IReadOnlyList<Client> noReferences = await TmuxWait.UntilAsync(
            cancellation => scope.Session.Server.GetClientsAsync(cancellation),
            current => current.Count == 0,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMilliseconds(250),
            token);
        Assert.Empty(noReferences);
    }

    [Theory]
    [InlineData("window-add", true)]
    [InlineData("layout-change", true)]
    [InlineData("session-renamed", true)]
    [InlineData("session-window-changed", true)]
    [InlineData("client-session-changed", true)]
    [InlineData("output", false)]
    [InlineData("continue", false)]
    // A bell or a byte of pane output is not a change to the hierarchy. Waking
    // every subscriber for one would cost more than the polling the
    // subscription replaces.
    public void Only_a_change_to_what_exists_wakes_a_subscriber(string name, bool expected) =>
        Assert.Equal(expected, HierarchyWatcher.IsStructural(name));

    [Fact]
    public void Lost_control_events_invalidate_the_hierarchy() =>
        Assert.True(HierarchyWatcher.InvalidatesHierarchy(new TmuxEventsDroppedEvent(1, 1)));
}
