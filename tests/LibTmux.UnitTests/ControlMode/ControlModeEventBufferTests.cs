using System.Globalization;
using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux.UnitTests.ControlMode;

[UnsupportedOSPlatform("windows")]
public sealed class ControlModeEventBufferTests
{
    [Fact]
    public async Task The_byte_ceiling_counts_decoded_utf8_across_notification_fields()
    {
        var buffer = new ControlModeEventBuffer(capacity: 8, maxBytes: 6);
        Assert.True(buffer.TryWrite(new TmuxNotificationEvent("n", ["é"])));
        Assert.True(buffer.TryWrite(new TmuxOutputEvent(new PaneId(1), "\U00010437")));
        Assert.True(buffer.TryWrite(new TmuxExitEvent("x")));
        buffer.Complete();
        List<TmuxEvent> observed = [];
        await foreach (TmuxEvent item in buffer.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            observed.Add(item);
        }

        Assert.Equal(new TmuxEventsDroppedEvent(1, 1), observed[0]);
        Assert.Equal(new TmuxOutputEvent(new PaneId(1), "\U00010437"), observed[1]);
        Assert.Equal(new TmuxExitEvent("x"), observed[2]);
    }

    [Fact]
    public async Task An_oversized_event_wakes_an_empty_reader_with_loss_and_preserves_exit()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var buffer = new ControlModeEventBuffer(capacity: 8, maxBytes: 1);
        await using IAsyncEnumerator<TmuxEvent> reader = buffer.ReadAllAsync(token).GetAsyncEnumerator(token);
        Task<bool> pending = reader.MoveNextAsync().AsTask();
        Assert.False(pending.IsCompleted);

        Assert.True(buffer.TryWrite(new TmuxOutputEvent(new PaneId(1), "é")));
        Assert.True(await pending.WaitAsync(TimeSpan.FromSeconds(1), token));
        Assert.Equal(new TmuxEventsDroppedEvent(1, 1), reader.Current);
        Assert.True(buffer.TryWrite(new TmuxExitEvent("too large")));
        buffer.Complete();

        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(new TmuxEventsDroppedEvent(1, 2), reader.Current);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(new TmuxExitEvent(null), reader.Current);
        Assert.False(await reader.MoveNextAsync());
    }

    [Fact]
    public async Task An_oversized_event_reports_loss_before_its_watermark_boundary()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var buffer = new ControlModeEventBuffer(capacity: 4, maxBytes: 1);
        Assert.True(buffer.TryWrite(new TmuxOutputEvent(new PaneId(1), "é")));
        long watermark = buffer.CaptureWatermark();

        await using ControlModeEventBuffer.Reader reader = buffer.CreateReader(token);
        Assert.Equal(ControlModeEventRead.Item, await reader.MoveNextThroughAsync(watermark));
        Assert.Equal(new TmuxEventsDroppedEvent(1, 1), reader.Current);
        Assert.Equal(ControlModeEventRead.Boundary, await reader.MoveNextThroughAsync(watermark));
    }

    [Fact]
    public async Task Byte_eviction_preserves_later_events_beyond_a_watermark()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var buffer = new ControlModeEventBuffer(capacity: 4, maxBytes: 2);
        Assert.True(buffer.TryWrite(new TmuxOutputEvent(new PaneId(1), "a")));
        long watermark = buffer.CaptureWatermark();
        Assert.True(buffer.TryWrite(new TmuxOutputEvent(new PaneId(1), "bb")));

        await using (ControlModeEventBuffer.Reader reader = buffer.CreateReader(token))
        {
            Assert.Equal(ControlModeEventRead.Item, await reader.MoveNextThroughAsync(watermark));
            Assert.Equal(new TmuxEventsDroppedEvent(1, 1), reader.Current);
            Assert.Equal(ControlModeEventRead.Boundary, await reader.MoveNextThroughAsync(watermark));
        }

        await using ControlModeEventBuffer.Reader following = buffer.CreateReader(token);
        Assert.Equal(ControlModeEventRead.Item, await following.MoveNextAsync());
        Assert.Equal("bb", Assert.IsType<TmuxOutputEvent>(following.Current).Data);
    }

    [Fact]
    public async Task Payload_bytes_bound_the_queue_before_its_event_count_limit()
    {
        var buffer = new ControlModeEventBuffer(capacity: 128);
        string payload = new('x', 64 * 1024);
        for (int index = 0; index < 65; index++)
        {
            Assert.True(buffer.TryWrite(new TmuxOutputEvent(new PaneId(1), payload)));
        }
        buffer.Complete();

        List<TmuxEvent> observed = [];
        await foreach (TmuxEvent item in buffer.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            observed.Add(item);
        }

        TmuxEventsDroppedEvent loss = Assert.IsType<TmuxEventsDroppedEvent>(observed[0]);
        Assert.Equal(1, loss.Count);
        Assert.Equal(64, observed.OfType<TmuxOutputEvent>().Count());
    }

    [Fact]
    public async Task Overflow_is_reported_without_blocking_the_writer()
    {
        const int ExtraEvents = 11;
        var buffer = new ControlModeEventBuffer(ControlModeSession.EventBufferCapacity);

        for (int index = 0;
            index < ControlModeSession.EventBufferCapacity + ExtraEvents;
            index++)
        {
            Assert.True(buffer.TryWrite(new TmuxNotificationEvent(
                index.ToString(CultureInfo.InvariantCulture),
                [])));
        }
        buffer.Complete();

        var observed = new List<TmuxEvent>();
        await foreach (TmuxEvent item in buffer.ReadAllAsync(
            TestContext.Current.CancellationToken))
        {
            observed.Add(item);
        }

        TmuxEventsDroppedEvent loss = Assert.IsType<TmuxEventsDroppedEvent>(observed[0]);
        Assert.Equal(ExtraEvents, loss.Count);
        Assert.Equal(ExtraEvents, loss.TotalDropped);
        Assert.False(loss.OnlyOutput);
        TmuxNotificationEvent firstRetained = Assert.IsType<TmuxNotificationEvent>(observed[1]);
        Assert.Equal(ExtraEvents.ToString(CultureInfo.InvariantCulture), firstRetained.Name);
        Assert.Equal(ControlModeSession.EventBufferCapacity + 1, observed.Count);
    }

    [Fact]
    public async Task A_full_buffer_discards_pane_output_before_any_notification()
    {
        var discarded = new List<PaneId>();
        var buffer = new ControlModeEventBuffer(capacity: 3, outputDiscarded: discarded.Add);
        Assert.True(buffer.TryWrite(Notification("window-add")));
        Assert.True(buffer.TryWrite(new TmuxOutputEvent(new PaneId(1), "first")));
        Assert.True(buffer.TryWrite(new TmuxOutputEvent(new PaneId(1), "second")));
        Assert.True(buffer.TryWrite(Notification("layout-change")));
        buffer.Complete();

        var observed = new List<TmuxEvent>();
        await foreach (TmuxEvent item in buffer.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            observed.Add(item);
        }

        Assert.Equal([new PaneId(1)], discarded);
        Assert.Equal(
            ["dropped 1, only output", "window-add", "%1: second", "layout-change"],
            observed.Select(item => item switch
            {
                TmuxEventsDroppedEvent loss => $"dropped {loss.Count}{(loss.OnlyOutput ? ", only output" : "")}",
                TmuxOutputEvent output => $"{output.PaneId}: {output.Data}",
                TmuxNotificationEvent notification => notification.Name,
                _ => item.ToString(),
            }));
    }

    [Fact]
    public async Task A_flooding_pane_loses_its_own_output_before_a_quieter_pane_does()
    {
        var discarded = new List<PaneId>();
        var buffer = new ControlModeEventBuffer(capacity: 3, outputDiscarded: discarded.Add);
        Assert.True(buffer.TryWrite(new TmuxOutputEvent(new PaneId(1), "quiet")));
        Assert.True(buffer.TryWrite(new TmuxOutputEvent(new PaneId(2), "flood-1")));
        Assert.True(buffer.TryWrite(new TmuxOutputEvent(new PaneId(2), "flood-2")));
        Assert.True(buffer.TryWrite(new TmuxOutputEvent(new PaneId(2), "flood-3")));
        buffer.Complete();

        var observed = new List<string>();
        await foreach (TmuxEvent item in buffer.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            observed.Add(item is TmuxOutputEvent output ? $"{output.PaneId}: {output.Data}" : "dropped");
        }

        Assert.Equal([new PaneId(2)], discarded);
        Assert.Equal(["dropped", "%1: quiet", "%2: flood-2", "%2: flood-3"], observed);
    }

    [Fact]
    public async Task Panes_with_equal_output_lose_the_oldest_first()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var discarded = new List<PaneId>();
        var buffer = new ControlModeEventBuffer(capacity: 3, outputDiscarded: discarded.Add);
        Assert.True(buffer.TryWrite(new TmuxOutputEvent(new PaneId(1), "a")));
        Assert.True(buffer.TryWrite(new TmuxOutputEvent(new PaneId(2), "b")));
        await using IAsyncEnumerator<TmuxEvent> reader = buffer.ReadAllAsync(token).GetAsyncEnumerator(token);
        Assert.True(await reader.MoveNextAsync());

        // Pane 1 returns to the index after pane 2, holding newer output.
        Assert.True(buffer.TryWrite(new TmuxOutputEvent(new PaneId(1), "c")));
        Assert.True(buffer.TryWrite(Notification("window-add")));
        Assert.True(buffer.TryWrite(Notification("layout-change")));
        buffer.Complete();

        Assert.Equal([new PaneId(2)], discarded);
    }

    [Fact]
    public async Task Stopping_after_loss_leaves_the_first_retained_event_for_the_next_reader()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var buffer = new ControlModeEventBuffer(capacity: 1);
        Assert.True(buffer.TryWrite(Notification("dropped")));
        Assert.True(buffer.TryWrite(Notification("retained")));
        buffer.Complete();

        await using (IAsyncEnumerator<TmuxEvent> first =
            buffer.ReadAllAsync(token).GetAsyncEnumerator(token))
        {
            Assert.True(await first.MoveNextAsync());
            Assert.Equal(1, Assert.IsType<TmuxEventsDroppedEvent>(first.Current).Count);
        }

        await using IAsyncEnumerator<TmuxEvent> second =
            buffer.ReadAllAsync(token).GetAsyncEnumerator(token);
        Assert.True(await second.MoveNextAsync());
        Assert.Equal("retained", Assert.IsType<TmuxNotificationEvent>(second.Current).Name);
        Assert.False(await second.MoveNextAsync());
    }

    [Fact]
    public async Task A_second_reader_is_refused_while_the_first_is_reading()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var buffer = new ControlModeEventBuffer(capacity: 4);
        Assert.True(buffer.TryWrite(Notification("first")));
        Assert.True(buffer.TryWrite(Notification("second")));
        buffer.Complete();

        await using (IAsyncEnumerator<TmuxEvent> reading =
            buffer.ReadAllAsync(token).GetAsyncEnumerator(token))
        {
            Assert.True(await reading.MoveNextAsync());
            await using IAsyncEnumerator<TmuxEvent> competing =
                buffer.ReadAllAsync(token).GetAsyncEnumerator(token);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await competing.MoveNextAsync());
        }

        await using IAsyncEnumerator<TmuxEvent> after =
            buffer.ReadAllAsync(token).GetAsyncEnumerator(token);
        Assert.True(await after.MoveNextAsync());
        Assert.Equal("second", Assert.IsType<TmuxNotificationEvent>(after.Current).Name);
    }

    [Fact]
    public async Task A_drop_after_dequeue_is_reported_after_the_held_event()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        using var consumerDequeued = new ManualResetEventSlim();
        using var producerAttempted = new ManualResetEventSlim();
        var buffer = new ControlModeEventBuffer(
            capacity: 2,
            afterDequeue: _ =>
            {
                consumerDequeued.Set();
                producerAttempted.Wait(token);
            });
        Assert.True(buffer.TryWrite(Notification("before")));
        Assert.True(buffer.TryWrite(Notification("will-drop")));

        Task producer = Task.Run(
            () =>
            {
                consumerDequeued.Wait(token);
                producerAttempted.Set();
                Assert.True(buffer.TryWrite(Notification("retained-1")));
                Assert.True(buffer.TryWrite(Notification("retained-2")));
            },
            token);

        await using IAsyncEnumerator<TmuxEvent> reader =
            buffer.ReadAllAsync(token).GetAsyncEnumerator(token);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal("before", Assert.IsType<TmuxNotificationEvent>(reader.Current).Name);

        await producer.WaitAsync(token);
        buffer.Complete();

        Assert.True(await reader.MoveNextAsync());
        TmuxEventsDroppedEvent loss = Assert.IsType<TmuxEventsDroppedEvent>(reader.Current);
        Assert.Equal(1, loss.Count);
        Assert.Equal(1, loss.TotalDropped);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal("retained-1", Assert.IsType<TmuxNotificationEvent>(reader.Current).Name);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal("retained-2", Assert.IsType<TmuxNotificationEvent>(reader.Current).Name);
        Assert.False(await reader.MoveNextAsync());
    }

    [Fact]
    public async Task Cancellation_stops_a_reader_before_it_drains_buffered_events()
    {
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var buffer = new ControlModeEventBuffer(capacity: 2);
        Assert.True(buffer.TryWrite(Notification("first")));
        Assert.True(buffer.TryWrite(Notification("second")));

        await using IAsyncEnumerator<TmuxEvent> reader =
            buffer.ReadAllAsync(canceled.Token).GetAsyncEnumerator(canceled.Token);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal("first", Assert.IsType<TmuxNotificationEvent>(reader.Current).Name);

        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await reader.MoveNextAsync());
    }

    [Fact]
    public async Task A_watermark_reader_leaves_later_events_for_the_next_consumer()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var buffer = new ControlModeEventBuffer(capacity: 4);
        Assert.True(buffer.TryWrite(Notification("before")));
        long watermark = buffer.CaptureWatermark();
        Assert.True(buffer.TryWrite(Notification("after")));

        await using (ControlModeEventBuffer.Reader reader = buffer.CreateReader(token))
        {
            Assert.Equal(ControlModeEventRead.Item, await reader.MoveNextThroughAsync(watermark));
            Assert.Equal("before", Assert.IsType<TmuxNotificationEvent>(reader.Current).Name);
            Assert.Equal(ControlModeEventRead.Boundary, await reader.MoveNextThroughAsync(watermark));
        }

        await using ControlModeEventBuffer.Reader following = buffer.CreateReader(token);
        Assert.Equal(ControlModeEventRead.Item, await following.MoveNextAsync());
        Assert.Equal("after", Assert.IsType<TmuxNotificationEvent>(following.Current).Name);
    }

    [Fact]
    public async Task A_watermark_reader_finishes_when_an_earlier_reader_drained_the_boundary()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var buffer = new ControlModeEventBuffer(capacity: 2);
        Assert.True(buffer.TryWrite(Notification("already-read")));

        await using (ControlModeEventBuffer.Reader earlier = buffer.CreateReader(token))
        {
            Assert.Equal(ControlModeEventRead.Item, await earlier.MoveNextAsync());
        }

        long watermark = buffer.CaptureWatermark();
        await using ControlModeEventBuffer.Reader reader = buffer.CreateReader(token);
        ValueTask<ControlModeEventRead> next = reader.MoveNextThroughAsync(watermark);

        Assert.True(next.IsCompletedSuccessfully);
        Assert.Equal(ControlModeEventRead.Boundary, await next);
    }

    [Fact]
    public async Task A_watermark_reader_reports_later_loss_once_and_then_reaches_its_boundary()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var buffer = new ControlModeEventBuffer(capacity: 1);
        Assert.True(buffer.TryWrite(Notification("before")));
        long watermark = buffer.CaptureWatermark();
        Assert.True(buffer.TryWrite(Notification("after-first")));

        await using (ControlModeEventBuffer.Reader reader = buffer.CreateReader(token))
        {
            Assert.Equal(ControlModeEventRead.Item, await reader.MoveNextThroughAsync(watermark));
            TmuxEventsDroppedEvent firstLoss = Assert.IsType<TmuxEventsDroppedEvent>(reader.Current);
            Assert.Equal(1, firstLoss.Count);
            Assert.Equal(1, firstLoss.TotalDropped);

            Assert.True(buffer.TryWrite(Notification("after-second")));
            Assert.Equal(ControlModeEventRead.Boundary, await reader.MoveNextThroughAsync(watermark));
        }

        buffer.Complete();
        await using ControlModeEventBuffer.Reader following = buffer.CreateReader(token);
        Assert.Equal(ControlModeEventRead.Item, await following.MoveNextAsync());
        TmuxEventsDroppedEvent laterLoss = Assert.IsType<TmuxEventsDroppedEvent>(following.Current);
        Assert.Equal(1, laterLoss.Count);
        Assert.Equal(2, laterLoss.TotalDropped);
        Assert.Equal(ControlModeEventRead.Item, await following.MoveNextAsync());
        Assert.Equal("after-second", Assert.IsType<TmuxNotificationEvent>(following.Current).Name);
        Assert.Equal(ControlModeEventRead.Completed, await following.MoveNextAsync());
    }

    private static TmuxNotificationEvent Notification(string name) => new(name, []);
}
