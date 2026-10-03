using System.Runtime.Versioning;
using LibTmux.Internal;
using LibTmux.UnitTests.Connection;

namespace LibTmux.UnitTests.ControlMode;

[UnsupportedOSPlatform("windows")]
public sealed class PaneObservationTests
{
    [Fact]
    public async Task Notification_loss_is_forwarded_and_rechecks_the_pane()
    {
        var connection = new TmuxConnection(
            new ServerConnectionOptions { SocketName = "pane-loss" },
            FakeMultiplexer.AnsweringVersion(static (_, _) =>
                throw new InvalidOperationException("The borrowed control client owns the query.")));
        ServerGeneration generation = new(17, 9001);
        Pane pane = new(
            new Server(connection, generation, "tmux 3.7"), connection, generation,
            new PaneId(1), new Dictionary<string, string?>());
        LossSession session = new();
        List<TmuxEvent> observed = [];

        await foreach (TmuxEvent item in session.WatchAsync(pane, TestContext.Current.CancellationToken))
        {
            observed.Add(item);
        }

        Assert.Equal(new TmuxEventsDroppedEvent(3, 3), observed[0]);
        Assert.Equal(2, session.Probes);
        Assert.False(session.Disposed);
        Assert.IsType<TmuxExitEvent>(observed[1]);
    }

    private sealed class LossSession : IControlModeSession
    {
        internal int Probes { get; private set; }

        internal bool Disposed { get; private set; }

        public bool IsRunning => !Disposed;

        public IAsyncEnumerable<TmuxEvent> Events => ReadEvents();

        public Task<IReadOnlyList<string>> SendAsync(
            TmuxCommand command, CancellationToken cancellationToken = default)
        {
            Assert.Equal(["display-message", "-p", "-t", "%1", "#{pane_id}"], command.ToArguments());
            Probes++;
            return Task.FromResult<IReadOnlyList<string>>(["%1"]);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        private static async IAsyncEnumerable<TmuxEvent> ReadEvents()
        {
            yield return new TmuxEventsDroppedEvent(3, 3);
            yield return new TmuxExitEvent(null);
            await Task.CompletedTask;
        }
    }
}
