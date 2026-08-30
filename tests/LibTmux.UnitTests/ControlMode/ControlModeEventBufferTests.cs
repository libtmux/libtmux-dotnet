using System.Globalization;
using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux.UnitTests.ControlMode;

[UnsupportedOSPlatform("windows")]
public sealed class ControlModeEventBufferTests
{
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
