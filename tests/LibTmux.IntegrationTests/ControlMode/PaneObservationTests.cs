using System.Diagnostics;
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
        await using var delivery = new BufferedSession(control);
        delivery.Buffer.TryWrite(new TmuxNotificationEvent("discarded", []));
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
        Assert.True(delivery.ReaderDisposed);
        Assert.True(control.IsRunning);
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
        await using var delivery = new BufferedSession(control);
        delivery.Buffer.TryWrite(new TmuxOutputEvent(pane.Id, "ready"));
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(token);
        watchdog.CancelAfter(TimeSpan.FromMilliseconds(750));
        await using IAsyncEnumerator<TmuxEvent> reader = delivery.WatchAsync(pane, watchdog.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal("ready", Assert.IsType<TmuxOutputEvent>(reader.Current).Data);
        await pane.KillAsync(cancellationToken: token);
        delivery.Buffer.TryWrite(new TmuxNotificationEvent("layout-change", []));
        delivery.Buffer.TryWrite(new TmuxOutputEvent(pane.Id, "first"));
        delivery.Buffer.TryWrite(new TmuxOutputEvent(pane.Id, "last"));
        Assert.True(await reader.MoveNextAsync());
        Assert.IsType<TmuxEventsDroppedEvent>(reader.Current);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal("first", Assert.IsType<TmuxOutputEvent>(reader.Current).Data);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal("last", Assert.IsType<TmuxOutputEvent>(reader.Current).Data);
        Assert.True(await reader.MoveNextAsync());
        Assert.IsType<TmuxPaneGoneEvent>(reader.Current);
        Assert.False(await reader.MoveNextAsync());
        Assert.True(delivery.ReaderDisposed);
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

    private sealed class WatermarkedSession(IControlModeSession inner, int capacity) :
        BufferedSession(inner, capacity), IControlModeEventWatermarkSource
    {
        long IControlModeEventWatermarkSource.CaptureEventWatermark() => Buffer.CaptureWatermark();

        ControlModeEventBuffer.Reader IControlModeEventWatermarkSource.CreateEventReader(
            CancellationToken cancellationToken) => Buffer.CreateReader(cancellationToken);
    }
}
