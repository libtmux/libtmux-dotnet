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
        TmuxNotificationEvent firstRetained = Assert.IsType<TmuxNotificationEvent>(observed[1]);
        Assert.Equal(ExtraEvents.ToString(CultureInfo.InvariantCulture), firstRetained.Name);
        Assert.Equal(ControlModeSession.EventBufferCapacity + 1, observed.Count);
    }

    [Fact]
    public async Task A_drop_after_dequeue_is_reported_after_the_held_event()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        using var consumerDequeued = new ManualResetEventSlim();
        using var producerAttempted = new ManualResetEventSlim();
        var buffer = new ControlModeEventBuffer(
            capacity: 2,
            afterDequeue: () =>
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

    private static TmuxNotificationEvent Notification(string name) => new(name, []);
}
