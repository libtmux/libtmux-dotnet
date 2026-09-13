using System.Runtime.Versioning;
using LibTmux.Internal;
using LibTmux.UnitTests.Connection;

namespace LibTmux.UnitTests.Entities;

// A row missing its parent's identifier is rare — every live listing carries
// one — but a handle built without it must still fail as a LibTmuxException,
// not a bare System.IO one a broad `catch (LibTmuxException)` cannot see.
[UnsupportedOSPlatform("windows")]
public sealed class HierarchyRelationErrorTests
{
    private static readonly ServerGeneration Generation = new(91, 901);

    [Fact]
    public void Pane_session_reports_incomplete_snapshot_not_invalid_data()
    {
        Pane pane = CreatePane(new Dictionary<string, string?>());

        Assert.Throws<IncompleteSnapshotException>(() => pane.Session);
    }

    [Fact]
    public void Pane_window_reports_incomplete_snapshot_not_invalid_data()
    {
        Pane pane = CreatePane(new Dictionary<string, string?>());

        Assert.Throws<IncompleteSnapshotException>(() => pane.Window);
    }

    [Fact]
    public void Window_session_reports_incomplete_snapshot_not_invalid_data()
    {
        Window window = CreateWindow(new Dictionary<string, string?>());

        Assert.Throws<IncompleteSnapshotException>(() => window.Session);
    }

    private static Pane CreatePane(IReadOnlyDictionary<string, string?> snapshot)
    {
        TmuxConnection connection = CreateConnection();
        return new Pane(
            new Server(connection, Generation, "tmux 3.7"),
            connection,
            Generation,
            new PaneId(1),
            snapshot);
    }

    private static Window CreateWindow(IReadOnlyDictionary<string, string?> snapshot)
    {
        TmuxConnection connection = CreateConnection();
        return new Window(
            new Server(connection, Generation, "tmux 3.7"),
            connection,
            Generation,
            new WindowId(1),
            snapshot);
    }

    private static TmuxConnection CreateConnection() =>
        new(
            new ServerConnectionOptions(socketName: "hierarchy-relation-error-test"),
            FakeMultiplexer.AnsweringVersion(NeverDispatchedAsync));

    private static Task<TmuxCommandResult> NeverDispatchedAsync(
        TmuxCommandRequest request,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("This test never dispatches a command.");
}
