using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Internal;

namespace LibTmux.IntegrationTests.ControlMode;

[UnsupportedOSPlatform("windows")]
public sealed class PaneObservationTests
{
    [UnixFact]
    public async Task An_already_gone_pane_ends_without_a_new_arrangement_event()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);
        Pane pane = await server.GetPaneAsync(new PaneId(0), token);
        await pane.SplitAsync(cancellationToken: token);
        await pane.KillAsync(cancellationToken: token);
        await using IControlModeSession control = await server.EnterControlModeAsync(cancellationToken: token);
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(token);
        watchdog.CancelAfter(TimeSpan.FromMilliseconds(750));
        await using IAsyncEnumerator<TmuxEvent> reader = control.WatchAsync(pane, watchdog.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(pane.Id, Assert.IsType<TmuxPaneGoneEvent>(reader.Current).PaneId);
        Assert.False(await reader.MoveNextAsync());
        Assert.True(control.IsRunning);
    }

    // tmux sends a control client output only from its own session, so a
    // watch of a pane elsewhere would wait in silence.
    [UnixFact]
    public async Task A_pane_in_another_session_is_refused_rather_than_watched_in_silence()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);
        RawTmuxResult other = await raw.ExecuteAsync(
            ["new-session", "-d", "-P", "-F", "#{pane_id}", "-s", raw.SessionName + "-other"],
            token);
        Pane elsewhere = await server.GetPaneAsync(PaneId.Parse(other.StandardOutputText.Trim()), token);
        await using IControlModeSession control = await server.EnterControlModeAsync(raw.SessionName, token);

        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(token);
        watchdog.CancelAfter(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<TmuxEvent> reader = control.WatchAsync(elsewhere, watchdog.Token).GetAsyncEnumerator();
        ArgumentException refused = await Assert.ThrowsAsync<ArgumentException>(async () => await reader.MoveNextAsync());

        Assert.Contains(elsewhere.Id.ToString(), refused.Message, StringComparison.Ordinal);
        Assert.True(control.IsRunning);
    }

    // A watched pane's window can also leave after reading starts, and tmux
    // then stops sending its output just as silently.
    [UnixFact]
    public async Task A_pane_moved_to_another_session_ends_the_watch_rather_than_silencing_it()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);
        _ = await raw.ExecuteAsync(["new-session", "-d", "-s", raw.SessionName + "-other"], token);
        RawTmuxResult created = await raw.ExecuteAsync(
            ["new-window", "-d", "-P", "-F", "#{window_id} #{pane_id}", "-t", raw.SessionName, "cat"],
            token);
        string[] ids = created.StandardOutputText.Trim().Split(' ');
        Pane moving = await server.GetPaneAsync(PaneId.Parse(ids[1]), token);
        await using IControlModeSession control = await server.EnterControlModeAsync(raw.SessionName, token);

        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(token);
        watchdog.CancelAfter(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<TmuxEvent> reader = control.WatchAsync(moving, watchdog.Token).GetAsyncEnumerator();

        // Output read through the watch shows its start-time check has passed.
        Task<bool> first = reader.MoveNextAsync().AsTask();
        _ = await raw.ExecuteAsync(["send-keys", "-t", ids[1], "seen", "Enter"], token);
        Assert.True(await first);
        Assert.IsType<TmuxOutputEvent>(reader.Current);

        _ = await raw.ExecuteAsync(["move-window", "-s", ids[0], "-t", raw.SessionName + "-other:"], token);
        InvalidOperationException moved = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            while (await reader.MoveNextAsync())
            {
                // Read on until the watch fails on the pane that moved away.
            }
        });

        Assert.Contains(moving.Id.ToString(), moved.Message, StringComparison.Ordinal);
        Assert.True(control.IsRunning);
    }

    [UnixFact]
    public async Task A_generic_async_event_source_cannot_silently_discard_buffered_output()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);
        Pane pane = await server.GetPaneAsync(new PaneId(0), token);
        await pane.SplitAsync(cancellationToken: token);
        await pane.KillAsync(cancellationToken: token);
        await using IControlModeSession control = await server.EnterControlModeAsync(cancellationToken: token);
        var delivery = new AsynchronouslyBufferedSession(control);
        Assert.True(delivery.Buffer.TryWrite(new TmuxOutputEvent(pane.Id, "last")));

        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(token);
        watchdog.CancelAfter(TimeSpan.FromMilliseconds(750));
        await using IAsyncEnumerator<TmuxEvent> watched = delivery.WatchAsync(pane, watchdog.Token).GetAsyncEnumerator();
        await Assert.ThrowsAsync<NotSupportedException>(async () => await watched.MoveNextAsync());

        delivery.Release();
        await using IAsyncEnumerator<TmuxEvent> retained = delivery.Events.GetAsyncEnumerator(token);
        Assert.True(await retained.MoveNextAsync());
        Assert.Equal("last", Assert.IsType<TmuxOutputEvent>(retained.Current).Data);
        Assert.True(control.IsRunning);
    }

    [UnixFact]
    public async Task A_generic_reader_ignoring_cancellation_cannot_hold_a_gone_pane_watch_open()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);
        Pane pane = await server.GetPaneAsync(new PaneId(0), token);
        await pane.SplitAsync(cancellationToken: token);
        await pane.KillAsync(cancellationToken: token);
        await using IControlModeSession control = await server.EnterControlModeAsync(cancellationToken: token);
        var delivery = new AsynchronouslyBufferedSession(control, ignoreCancellation: true);
        Assert.True(delivery.Buffer.TryWrite(new TmuxOutputEvent(pane.Id, "last")));

        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(token);
        watchdog.CancelAfter(TimeSpan.FromMilliseconds(750));
        await using IAsyncEnumerator<TmuxEvent> watched = delivery.WatchAsync(pane, token).GetAsyncEnumerator();
        Task<bool> pending = watched.MoveNextAsync().AsTask();
        try
        {
            await Assert.ThrowsAsync<NotSupportedException>(async () => await pending.WaitAsync(watchdog.Token));
        }
        finally
        {
            delivery.Release();
            if (!pending.IsCompleted)
            {
                await Assert.ThrowsAsync<NotSupportedException>(async () =>
                    await pending.WaitAsync(TimeSpan.FromMilliseconds(750)));
            }
        }

        Assert.False(delivery.ReaderStarted);
        await using IAsyncEnumerator<TmuxEvent> retained = delivery.Events.GetAsyncEnumerator(token);
        Assert.True(await retained.MoveNextAsync());
        Assert.Equal("last", Assert.IsType<TmuxOutputEvent>(retained.Current).Data);
        Assert.True(control.IsRunning);
    }

    [UnixFact]
    public async Task A_generic_continuous_source_fails_after_losing_the_arrangement_event()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);
        Pane pane = await server.GetPaneAsync(new PaneId(0), token);
        await pane.SplitAsync(cancellationToken: token);
        await using IControlModeSession control = await server.EnterControlModeAsync(cancellationToken: token);
        await using var delivery = new BufferedSession(control, capacity: 2);
        delivery.Buffer.TryWrite(new TmuxOutputEvent(pane.Id, "ready"));
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(token);
        watchdog.CancelAfter(TimeSpan.FromMilliseconds(750));
        await using IAsyncEnumerator<TmuxEvent> reader = delivery.WatchAsync(pane, watchdog.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal("ready", Assert.IsType<TmuxOutputEvent>(reader.Current).Data);

        await pane.KillAsync(cancellationToken: token);
        delivery.Buffer.TryWrite(new TmuxNotificationEvent("layout-change", []));
        delivery.Buffer.TryWrite(new TmuxOutputEvent(pane.Id, "last"));
        delivery.Buffer.TryWrite(new TmuxNotificationEvent("unrelated", []));
        using var stopProducer = new CancellationTokenSource();
        var producerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task ProduceAsync()
        {
            while (!stopProducer.IsCancellationRequested)
            {
                if (!delivery.Buffer.TryWrite(new TmuxNotificationEvent("unrelated", [])))
                {
                    throw new InvalidOperationException("The event producer stopped accepting notifications.");
                }

                producerStarted.TrySetResult();
                await Task.Yield();
            }
        }

        Task producer = ProduceAsync();
        try
        {
            await producerStarted.Task.WaitAsync(watchdog.Token);
            Assert.True(await reader.MoveNextAsync());
            Assert.True(Assert.IsType<TmuxEventsDroppedEvent>(reader.Current).Count > 0);
            await Assert.ThrowsAsync<NotSupportedException>(async () => await reader.MoveNextAsync());
            Assert.True(delivery.ReaderDisposed);
            Assert.False(producer.IsCompleted);
        }
        finally
        {
            stopProducer.Cancel();
            await producer;
        }

        Assert.True(control.IsRunning);
    }

    [UnixFact]
    public async Task A_restarted_server_cannot_watch_a_reused_pane_id()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server original = await ConnectAsync(raw, token);
        Pane pane = await original.GetPaneAsync(new PaneId(0), token);
        using Process daemon = Process.GetProcessById(pane.Generation.ProcessId);
        Assert.Equal(0, (await raw.ExecuteAsync(["kill-server"], token)).ExitCode);
        await daemon.WaitForExitAsync(token);
        Assert.Equal(0, (await raw.ExecuteAsync(["new-session", "-d", "-s", raw.SessionName], token)).ExitCode);
        Server replacement = await ConnectAsync(raw, token);
        Pane reused = await replacement.GetPaneAsync(new PaneId(0), token);
        Assert.Equal(pane.Id, reused.Id);
        Assert.NotEqual(pane.Generation, reused.Generation);
        await using IControlModeSession control = await replacement.EnterControlModeAsync(cancellationToken: token);
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(token);
        watchdog.CancelAfter(TimeSpan.FromMilliseconds(750));
        await using IAsyncEnumerator<TmuxEvent> reader = control.WatchAsync(pane, watchdog.Token).GetAsyncEnumerator();
        await Assert.ThrowsAsync<StaleServerGenerationException>(async () => await reader.MoveNextAsync());
        Assert.True(control.IsRunning);
        await control.DisposeAsync();
        await using IAsyncEnumerator<TmuxEvent> stopped = control.WatchAsync(pane, token).GetAsyncEnumerator();
        await Assert.ThrowsAsync<StaleServerGenerationException>(async () => await stopped.MoveNextAsync());
    }

    [UnixFact]
    public async Task A_real_control_buffer_overflow_reaches_the_pane_watcher()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);
        Pane pane = await server.GetPaneAsync(new PaneId(0), token);
        await using IControlModeSession control = await server.EnterControlModeAsync(cancellationToken: token);
        var commands = new List<string>();
        for (int index = 0; index < ControlModeSession.EventBufferCapacity + 2; index++)
        {
            if (commands.Count > 0)
            {
                commands.Add(";");
            }

            commands.AddRange(["rename-session", "-t", "$0", index % 2 == 0 ? "watch-even" : "watch-odd"]);
            if (index % 32 == 31)
            {
                RawTmuxResult batch = await raw.ExecuteAsync(commands, token);
                Assert.True(batch.ExitCode == 0, batch.StandardErrorText);
                commands.Clear();
            }
        }

        RawTmuxResult renamed = await raw.ExecuteAsync(commands, token);
        Assert.True(renamed.ExitCode == 0, renamed.StandardErrorText);
        await control.SendAsync(TmuxCommand.Create("display-message", "-p", "barrier"), token);
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(token);
        watchdog.CancelAfter(TimeSpan.FromMilliseconds(750));
        await using (IAsyncEnumerator<TmuxEvent> reader = control.WatchAsync(pane, watchdog.Token).GetAsyncEnumerator())
        {
            Assert.True(await reader.MoveNextAsync());
            Assert.True(Assert.IsType<TmuxEventsDroppedEvent>(reader.Current).Count >= 2);
        }

        Assert.Equal(["alive"], await control.SendAsync(TmuxCommand.Create("display-message", "-p", "alive"), token));
    }

    [UnixFact]
    public async Task Loss_and_buffered_output_precede_the_gone_event()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);
        Pane pane = await server.GetPaneAsync(new PaneId(0), token);
        await pane.SplitAsync(cancellationToken: token);
        await using IControlModeSession control = await server.EnterControlModeAsync(cancellationToken: token);
        await using var delivery = new WatermarkedSession(control, capacity: 2);
        delivery.Buffer.TryWrite(new TmuxOutputEvent(new PaneId(999), "discarded"));
        delivery.Buffer.TryWrite(new TmuxOutputEvent(pane.Id, "first"));
        delivery.Buffer.TryWrite(new TmuxOutputEvent(pane.Id, "last"));
        await pane.KillAsync(cancellationToken: token);
        var observed = new List<TmuxEvent>();
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(token);
        watchdog.CancelAfter(TimeSpan.FromMilliseconds(750));
        await foreach (TmuxEvent item in delivery.WatchAsync(pane, watchdog.Token))
        {
            observed.Add(item);
        }
        Assert.Collection(observed,
            item => Assert.Equal(1, Assert.IsType<TmuxEventsDroppedEvent>(item).Count),
            item => Assert.Equal("first", Assert.IsType<TmuxOutputEvent>(item).Data),
            item => Assert.Equal("last", Assert.IsType<TmuxOutputEvent>(item).Data),
            item => Assert.Equal(pane.Id, Assert.IsType<TmuxPaneGoneEvent>(item).PaneId));
        Assert.True(control.IsRunning);
    }

    [UnixFact]
    public async Task One_client_watches_several_panes_and_ends_each_after_its_output()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);
        Pane first = await server.GetPaneAsync(new PaneId(0), token);
        Pane second = await first.SplitAsync(cancellationToken: token);
        await first.SplitAsync(cancellationToken: token);
        await using IControlModeSession control = await server.EnterControlModeAsync(cancellationToken: token);
        await using var delivery = new WatermarkedSession(control, capacity: 16);
        delivery.Buffer.TryWrite(new TmuxOutputEvent(first.Id, "first"));
        delivery.Buffer.TryWrite(new TmuxOutputEvent(new PaneId(999), "unwatched"));
        delivery.Buffer.TryWrite(new TmuxOutputEvent(second.Id, "second"));
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(token);
        watchdog.CancelAfter(TimeSpan.FromMilliseconds(750));
        await using IAsyncEnumerator<TmuxEvent> reader =
            delivery.WatchAsync([first, second], watchdog.Token).GetAsyncEnumerator();

        Assert.Equal("first", await NextOutputAsync(reader));
        Assert.Equal("second", await NextOutputAsync(reader));

        await first.KillAsync(cancellationToken: token);
        delivery.Buffer.TryWrite(new TmuxOutputEvent(first.Id, "first-last"));
        delivery.Buffer.TryWrite(new TmuxNotificationEvent("layout-change", []));
        Assert.Equal("first-last", await NextOutputAsync(reader));
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(first.Id, Assert.IsType<TmuxPaneGoneEvent>(reader.Current).PaneId);

        // The other pane is still watched after the first one ends.
        delivery.Buffer.TryWrite(new TmuxOutputEvent(second.Id, "second-still"));
        Assert.Equal("second-still", await NextOutputAsync(reader));

        await second.KillAsync(cancellationToken: token);
        delivery.Buffer.TryWrite(new TmuxNotificationEvent("layout-change", []));
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(second.Id, Assert.IsType<TmuxPaneGoneEvent>(reader.Current).PaneId);
        Assert.False(await reader.MoveNextAsync());
        Assert.True(control.IsRunning);

        static async Task<string> NextOutputAsync(IAsyncEnumerator<TmuxEvent> events)
        {
            Assert.True(await events.MoveNextAsync());
            return Assert.IsType<TmuxOutputEvent>(events.Current).Data;
        }
    }

    [UnixFact]
    public async Task Panes_gone_together_each_end_once_in_the_order_given()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);
        Pane first = await server.GetPaneAsync(new PaneId(0), token);
        Pane second = await first.SplitAsync(cancellationToken: token);
        await first.SplitAsync(cancellationToken: token);
        await using IControlModeSession control = await server.EnterControlModeAsync(cancellationToken: token);
        await using var delivery = new WatermarkedSession(control, capacity: 16);
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(token);
        watchdog.CancelAfter(TimeSpan.FromMilliseconds(750));
        IAsyncEnumerable<TmuxEvent> watch = delivery.WatchAsync([second, first], watchdog.Token);

        await second.KillAsync(cancellationToken: token);
        await first.KillAsync(cancellationToken: token);
        delivery.Buffer.TryWrite(new TmuxOutputEvent(first.Id, "first-last"));
        delivery.Buffer.TryWrite(new TmuxOutputEvent(second.Id, "second-last"));
        delivery.Buffer.TryWrite(new TmuxNotificationEvent("layout-change", []));
        var observed = new List<string>();
        await foreach (TmuxEvent item in watch)
        {
            observed.Add(item switch
            {
                TmuxOutputEvent output => output.Data,
                TmuxPaneGoneEvent gone => $"gone {gone.PaneId}",
                _ => item.GetType().Name,
            });
        }

        Assert.Equal(["first-last", "second-last", $"gone {second.Id}", $"gone {first.Id}"], observed);

        // Enumerating the same stream again watches both panes again.
        var again = new List<PaneId>();
        await foreach (TmuxEvent item in watch)
        {
            again.Add(Assert.IsType<TmuxPaneGoneEvent>(item).PaneId);
        }

        Assert.Equal([second.Id, first.Id], again);
    }

    [UnixFact]
    public async Task A_lost_arrangement_notification_still_terminates_the_watch()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);
        Pane pane = await server.GetPaneAsync(new PaneId(0), token);
        await pane.SplitAsync(cancellationToken: token);
        await using IControlModeSession control = await server.EnterControlModeAsync(cancellationToken: token);
        await using var delivery = new WatermarkedSession(control, capacity: 2);
        delivery.Buffer.TryWrite(new TmuxOutputEvent(pane.Id, "ready"));
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(token);
        watchdog.CancelAfter(TimeSpan.FromMilliseconds(750));
        await using IAsyncEnumerator<TmuxEvent> reader = delivery.WatchAsync(pane, watchdog.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal("ready", Assert.IsType<TmuxOutputEvent>(reader.Current).Data);
        await pane.KillAsync(cancellationToken: token);

        // Output is discarded first, so a notification is lost only from a
        // buffer holding none.
        delivery.Buffer.TryWrite(new TmuxNotificationEvent("layout-change", []));
        delivery.Buffer.TryWrite(new TmuxNotificationEvent("window-renamed", []));
        delivery.Buffer.TryWrite(new TmuxNotificationEvent("session-changed", []));
        Assert.True(await reader.MoveNextAsync());
        Assert.False(Assert.IsType<TmuxEventsDroppedEvent>(reader.Current).OnlyOutput);
        Assert.True(await reader.MoveNextAsync());
        Assert.IsType<TmuxPaneGoneEvent>(reader.Current);
        Assert.False(await reader.MoveNextAsync());
    }

    [UnixFact]
    public async Task Post_check_unrelated_events_do_not_delay_pane_termination()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);
        Pane pane = await server.GetPaneAsync(new PaneId(0), token);
        await pane.SplitAsync(cancellationToken: token);
        await using IControlModeSession control = await server.EnterControlModeAsync(cancellationToken: token);
        await using var delivery = new WatermarkedSession(control, capacity: 16);
        delivery.Buffer.TryWrite(new TmuxOutputEvent(pane.Id, "ready"));
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(token);
        watchdog.CancelAfter(TimeSpan.FromMilliseconds(750));
        await using IAsyncEnumerator<TmuxEvent> reader = delivery.WatchAsync(pane, watchdog.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal("ready", Assert.IsType<TmuxOutputEvent>(reader.Current).Data);

        await pane.KillAsync(cancellationToken: token);
        delivery.Buffer.TryWrite(new TmuxNotificationEvent("layout-change", []));
        delivery.Buffer.TryWrite(new TmuxOutputEvent(pane.Id, "before-one"));
        delivery.Buffer.TryWrite(new TmuxOutputEvent(pane.Id, "before-two"));

        Assert.True(await reader.MoveNextAsync());
        Assert.Equal("before-one", Assert.IsType<TmuxOutputEvent>(reader.Current).Data);

        for (int index = 0; index < 4; index++)
        {
            delivery.Buffer.TryWrite(new TmuxNotificationEvent($"after-{index}", []));
        }

        Assert.True(await reader.MoveNextAsync());
        Assert.Equal("before-two", Assert.IsType<TmuxOutputEvent>(reader.Current).Data);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(pane.Id, Assert.IsType<TmuxPaneGoneEvent>(reader.Current).PaneId);
        Assert.False(await reader.MoveNextAsync());

        await using IAsyncEnumerator<TmuxEvent> following = delivery.Events.GetAsyncEnumerator(token);
        Assert.True(await following.MoveNextAsync());
        Assert.Equal("after-0", Assert.IsType<TmuxNotificationEvent>(following.Current).Name);
    }

    [UnixFact]
    public async Task Continuous_unrelated_events_do_not_delay_pane_termination()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);
        Pane pane = await server.GetPaneAsync(new PaneId(0), token);
        await pane.SplitAsync(cancellationToken: token);
        await using IControlModeSession control = await server.EnterControlModeAsync(cancellationToken: token);
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(token);
        watchdog.CancelAfter(TimeSpan.FromMilliseconds(750));
        using var watermarkCaptured = new ManualResetEventSlim();
        using var producedAfterWatermark = new ManualResetEventSlim();
        await using var delivery = new WatermarkedSession(
            control,
            capacity: 16,
            afterWatermark: () =>
            {
                watermarkCaptured.Set();
                producedAfterWatermark.Wait(watchdog.Token);
            });
        delivery.Buffer.TryWrite(new TmuxOutputEvent(pane.Id, "ready"));
        await using IAsyncEnumerator<TmuxEvent> reader = delivery.WatchAsync(pane, watchdog.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal("ready", Assert.IsType<TmuxOutputEvent>(reader.Current).Data);

        await pane.KillAsync(cancellationToken: token);
        delivery.Buffer.TryWrite(new TmuxNotificationEvent("layout-change", []));
        using var stopProducer = new CancellationTokenSource();
        var producerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var producedAfterTerminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int terminalObserved = 0;
        int produced = 0;
        int writesAfterWatermark = 0;

        async Task ProduceAsync()
        {
            while (!stopProducer.IsCancellationRequested)
            {
                if (!delivery.Buffer.TryWrite(new TmuxNotificationEvent("unrelated", [])))
                {
                    throw new InvalidOperationException("The event producer stopped accepting notifications.");
                }

                Interlocked.Increment(ref produced);
                producerStarted.TrySetResult();
                if (watermarkCaptured.IsSet)
                {
                    Interlocked.Increment(ref writesAfterWatermark);
                    producedAfterWatermark.Set();
                }

                if (Volatile.Read(ref terminalObserved) != 0)
                {
                    producedAfterTerminal.TrySetResult();
                }

                await Task.Yield();
            }
        }

        Task producer = ProduceAsync();
        try
        {
            await producerStarted.Task.WaitAsync(watchdog.Token);
            int beforeRead = Volatile.Read(ref produced);
            bool sawGone = false;
            while (await reader.MoveNextAsync())
            {
                if (reader.Current is TmuxPaneGoneEvent)
                {
                    sawGone = true;
                    break;
                }
            }

            Assert.True(sawGone);
            Assert.False(await reader.MoveNextAsync());
            Assert.True(Volatile.Read(ref produced) > beforeRead);
            Assert.True(Volatile.Read(ref writesAfterWatermark) > 0);
            Volatile.Write(ref terminalObserved, 1);
            await producedAfterTerminal.Task.WaitAsync(watchdog.Token);
            Assert.False(producer.IsCompleted);
        }
        finally
        {
            stopProducer.Cancel();
            await producer;
        }

        Assert.True(control.IsRunning);
    }

    [UnixFact]
    public async Task Real_continuous_unrelated_notifications_do_not_delay_pane_termination()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);
        Pane pane = await server.GetPaneAsync(new PaneId(0), token);
        await pane.SplitAsync(cancellationToken: token);
        await using IControlModeSession control = await server.EnterControlModeAsync(cancellationToken: token);
        var source = Assert.IsAssignableFrom<IControlModeEventWatermarkSource>(control);
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(token);
        watchdog.CancelAfter(TimeSpan.FromMilliseconds(750));
        using var releaseWatermark = new ManualResetEventSlim();
        var delivery = new ObservedWatermarkSession(control, source, releaseWatermark, watchdog.Token);
        using var stopProducer = new CancellationTokenSource();
        var firstRename = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var producedAfterTerminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int terminalObserved = 0;

        async Task ProduceAsync()
        {
            try
            {
                for (int index = 0; !stopProducer.IsCancellationRequested; index++)
                {
                    RawTmuxResult renamed = await raw.ExecuteAsync(
                        ["rename-session", "-t", "$0", index % 2 == 0 ? "watch-even" : "watch-odd"],
                        stopProducer.Token);
                    Assert.True(renamed.ExitCode == 0, renamed.StandardErrorText);
                    firstRename.TrySetResult();
                    if (Volatile.Read(ref terminalObserved) != 0)
                    {
                        producedAfterTerminal.TrySetResult();
                    }
                }
            }
            catch (OperationCanceledException) when (stopProducer.IsCancellationRequested)
            {
            }
        }

        Task<TmuxEvent[]> watch = Task.Run(async () =>
        {
            var observed = new List<TmuxEvent>();
            await foreach (TmuxEvent item in delivery.WatchAsync(pane, watchdog.Token))
            {
                observed.Add(item);
            }

            return observed.ToArray();
        }, watchdog.Token);
        Task? producer = null;
        try
        {
            await delivery.InitialCheck.WaitAsync(watchdog.Token);
            long beforeRenames = source.CaptureEventWatermark();
            producer = ProduceAsync();
            await firstRename.Task.WaitAsync(watchdog.Token);
            await control.SendAsync(TmuxCommand.Create("display-message", "-p", "before-kill"), watchdog.Token);
            Assert.True(source.CaptureEventWatermark() > beforeRenames);

            await pane.KillAsync(cancellationToken: watchdog.Token);
            long boundary = await delivery.CapturedWatermark.WaitAsync(watchdog.Token);
            RawTmuxResult afterBoundary = await raw.ExecuteAsync(
                ["rename-session", "-t", "$0", $"watch-after-{Guid.NewGuid():N}"],
                watchdog.Token);
            Assert.True(afterBoundary.ExitCode == 0, afterBoundary.StandardErrorText);
            await control.SendAsync(TmuxCommand.Create("display-message", "-p", "after-watermark"), watchdog.Token);
            Assert.True(source.CaptureEventWatermark() > boundary);
            releaseWatermark.Set();

            TmuxEvent[] observed = await watch.WaitAsync(watchdog.Token);
            Assert.NotEmpty(observed);
            Assert.Equal(pane.Id, Assert.IsType<TmuxPaneGoneEvent>(observed[^1]).PaneId);
            Assert.DoesNotContain(observed, static item => item is TmuxNotificationEvent);
            Assert.False(producer.IsCompleted);
            Volatile.Write(ref terminalObserved, 1);
            await producedAfterTerminal.Task.WaitAsync(watchdog.Token);
        }
        finally
        {
            releaseWatermark.Set();
            stopProducer.Cancel();
            if (producer is not null)
            {
                await producer;
            }
        }

        Assert.Equal(["alive"], await control.SendAsync(TmuxCommand.Create("display-message", "-p", "alive"), token));
    }

    [UnixFact]
    public async Task Early_stop_and_cancellation_dispose_only_the_reader()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);
        Pane pane = await server.GetPaneAsync(new PaneId(0), token);
        await using IControlModeSession control = await server.EnterControlModeAsync(cancellationToken: token);
        await using var delivery = new BufferedSession(control);
        delivery.Buffer.TryWrite(new TmuxOutputEvent(pane.Id, "ready"));
        await using (IAsyncEnumerator<TmuxEvent> early = delivery.WatchAsync(pane, token).GetAsyncEnumerator())
        {
            Assert.True(await early.MoveNextAsync());
        }

        Assert.True(delivery.ReaderDisposed);
        delivery.Buffer.TryWrite(new TmuxOutputEvent(pane.Id, "ready"));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        await using IAsyncEnumerator<TmuxEvent> reader = delivery.WatchAsync(pane, cancel.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Task<bool> pending = reader.MoveNextAsync().AsTask();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        Assert.True(delivery.ReaderDisposed);
        Assert.Equal(["alive"], await control.SendAsync(TmuxCommand.Create("display-message", "-p", "alive"), token));
    }

    [UnixFact]
    public async Task Buffered_output_precedes_a_stream_fault_and_disposes_the_reader()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);
        Pane pane = await server.GetPaneAsync(new PaneId(0), token);
        await using IControlModeSession control = await server.EnterControlModeAsync(cancellationToken: token);
        await using var delivery = new BufferedSession(control);
        delivery.Buffer.TryWrite(new TmuxOutputEvent(pane.Id, "last"));
        var fault = new IOException("Control stream failed.");
        delivery.Buffer.Complete(fault);
        var cleanup = new IOException("Reader disposal failed.");
        delivery.CleanupFailure = cleanup;
        await using IAsyncEnumerator<TmuxEvent> reader = delivery.WatchAsync(pane, token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal("last", Assert.IsType<TmuxOutputEvent>(reader.Current).Data);
        Assert.Same(fault, await Assert.ThrowsAsync<IOException>(async () => await reader.MoveNextAsync()));
        Assert.Same(cleanup, fault.Data["LibTmux.ControlModeCleanupFailure"]);
        Assert.True(delivery.ReaderDisposed);
        Assert.True(control.IsRunning);
    }

    [UnixFact]
    public Task A_stopped_client_drains_buffered_output_before_exit() => CheckStoppedClientAsync(fail: false);

    [UnixFact]
    public Task A_stopped_client_drains_buffered_output_before_failure() => CheckStoppedClientAsync(fail: true);

    private static async Task CheckStoppedClientAsync(bool fail)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);
        Pane pane = await server.GetPaneAsync(new PaneId(0), token);
        await using IControlModeSession control = await server.EnterControlModeAsync(cancellationToken: token);
        await using var delivery = new BufferedSession(control);
        delivery.Buffer.TryWrite(new TmuxOutputEvent(pane.Id, "last"));
        var fault = new IOException("Control stream failed.");
        if (!fail)
        {
            delivery.Buffer.TryWrite(new TmuxExitEvent(null));
        }

        delivery.Buffer.Complete(fail ? fault : null);
        await control.DisposeAsync();
        Assert.False(control.IsRunning);
        await using IAsyncEnumerator<TmuxEvent> reader = delivery.WatchAsync(pane, token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal("last", Assert.IsType<TmuxOutputEvent>(reader.Current).Data);
        if (fail)
        {
            Assert.Same(fault, await Assert.ThrowsAsync<IOException>(async () => await reader.MoveNextAsync()));
        }
        else
        {
            Assert.True(await reader.MoveNextAsync());
            Assert.IsType<TmuxExitEvent>(reader.Current);
            Assert.False(await reader.MoveNextAsync());
        }

        Assert.True(delivery.ReaderDisposed);
    }

    private static Task<Server> ConnectAsync(RawTmuxTestContext raw, CancellationToken token) =>
        Server.ConnectAsync(new ServerConnectionOptions
        {
            TmuxBinaryPath = raw.TmuxBinaryPath,
            SocketPath = raw.SocketPath,
            ConfigurationFile = "/dev/null",
        }, token);

    private sealed class AsynchronouslyBufferedSession(
        IControlModeSession inner,
        bool ignoreCancellation = false) : IControlModeSession
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ControlModeEventBuffer Buffer { get; } = new(capacity: 2);
        internal bool ReaderStarted { get; private set; }
        public bool IsRunning => inner.IsRunning;
        public IAsyncEnumerable<TmuxEvent> Events => ReadAsync();
        public Task<IReadOnlyList<string>> SendAsync(TmuxCommand command, CancellationToken cancellationToken = default) =>
            inner.SendAsync(command, cancellationToken);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        internal void Release() => _released.TrySetResult();

        private async IAsyncEnumerable<TmuxEvent> ReadAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ReaderStarted = true;
            if (ignoreCancellation)
            {
                await _released.Task;
            }
            else
            {
                await _released.Task.WaitAsync(cancellationToken);
            }

            await foreach (TmuxEvent item in Buffer.ReadAllAsync(cancellationToken))
            {
                yield return item;
            }
        }
    }

    private class BufferedSession(
        IControlModeSession inner,
        int capacity = 2) : IControlModeSession, IAsyncEnumerable<TmuxEvent>
    {
        internal ControlModeEventBuffer Buffer { get; } = new(capacity);
        internal bool ReaderDisposed { get; private set; }
        internal Exception? CleanupFailure { get; set; }
        public bool IsRunning => inner.IsRunning;
        public IAsyncEnumerable<TmuxEvent> Events => this;
        public Task<IReadOnlyList<string>> SendAsync(TmuxCommand command, CancellationToken cancellationToken = default) =>
            inner.SendAsync(command, cancellationToken);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public IAsyncEnumerator<TmuxEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            ReaderDisposed = false;
            return new Reader(this, Buffer.ReadAllAsync(cancellationToken).GetAsyncEnumerator(cancellationToken));
        }

        private sealed class Reader(BufferedSession owner, IAsyncEnumerator<TmuxEvent> inner) : IAsyncEnumerator<TmuxEvent>
        {
            public TmuxEvent Current => inner.Current;
            public ValueTask<bool> MoveNextAsync() => inner.MoveNextAsync();
            public async ValueTask DisposeAsync()
            {
                await inner.DisposeAsync();
                owner.ReaderDisposed = true;
                if (owner.CleanupFailure is not null)
                {
                    throw owner.CleanupFailure;
                }
            }
        }
    }

    private sealed class WatermarkedSession(IControlModeSession inner, int capacity, Action? afterWatermark = null) :
        BufferedSession(inner, capacity), IControlModeEventWatermarkSource
    {
        long IControlModeEventWatermarkSource.CaptureEventWatermark()
        {
            long watermark = Buffer.CaptureWatermark();
            afterWatermark?.Invoke();
            return watermark;
        }

        ControlModeEventBuffer.Reader IControlModeEventWatermarkSource.CreateEventReader(
            CancellationToken cancellationToken) => Buffer.CreateReader(cancellationToken);
    }

    private sealed class ObservedWatermarkSession(
        IControlModeSession inner,
        IControlModeEventWatermarkSource source,
        ManualResetEventSlim releaseWatermark,
        CancellationToken cancellationToken) : IControlModeSession, IControlModeEventWatermarkSource
    {
        private readonly TaskCompletionSource initialCheck =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<long> capturedWatermark =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IAsyncEnumerable<TmuxEvent> Events => inner.Events;
        public bool IsRunning => inner.IsRunning;
        internal Task InitialCheck => initialCheck.Task;
        internal Task<long> CapturedWatermark => capturedWatermark.Task;

        public async Task<IReadOnlyList<string>> SendAsync(
            TmuxCommand command,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<string> result = await inner.SendAsync(command, cancellationToken);
            initialCheck.TrySetResult();
            return result;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        long IControlModeEventWatermarkSource.CaptureEventWatermark()
        {
            long watermark = source.CaptureEventWatermark();
            capturedWatermark.TrySetResult(watermark);
            releaseWatermark.Wait(cancellationToken);
            return watermark;
        }

        ControlModeEventBuffer.Reader IControlModeEventWatermarkSource.CreateEventReader(
            CancellationToken readerCancellationToken) => source.CreateEventReader(readerCancellationToken);
    }
}
