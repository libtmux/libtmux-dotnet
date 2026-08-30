using LibTmux.Internal;
using LibTmux.Workspace;

namespace LibTmux.IntegrationTests;

public sealed class WorkspaceResultTests
{
    [Fact]
    public void Build_failure_preserves_the_operation_and_dispatch_state()
    {
        var operation = new LibTmuxException(
            "tmux refused the operation",
            TmuxDispatchState.NotDispatched);

        var failure = new WorkspaceBuildException(null, operation);

        Assert.Same(operation, failure.InnerException);
        Assert.Equal(TmuxDispatchState.NotDispatched, failure.Dispatch);
        Assert.Null(failure.PartialResult);
    }

    [Fact]
    public void Collection_initializers_snapshot_their_inputs()
    {
        (Session session, Window window) = Entities();
        var windows = new List<Window> { window };
        var unsupported = new List<string> { "layout" };
        var result = new WorkspaceResult(session, windows, unsupported);

        windows.Clear();
        unsupported.Clear();
        var replacement = new List<string> { "replacement" };
        WorkspaceResult changed = result with { Unsupported = replacement };
        replacement.Clear();

        Assert.Equal([window], result.Windows);
        Assert.Equal(["layout"], result.Unsupported);
        Assert.Equal(["replacement"], changed.Unsupported);
    }

    [Fact]
    public void Equality_uses_collection_contents()
    {
        (Session session, Window window) = Entities();
        var left = new WorkspaceResult(session, [window], ["layout"]);
        var equal = new WorkspaceResult(session, [window], ["layout"]);
        var different = new WorkspaceResult(session, [window], ["other"]);

        Assert.Equal(left, equal);
        Assert.Equal(left.GetHashCode(), equal.GetHashCode());
        Assert.NotEqual(left, different);
    }

    private static (Session Session, Window Window) Entities()
    {
        var dispatcher = new TmuxCommandDispatcher(
            static (_, _) => throw new InvalidOperationException("No command expected."));
        return (new Session(dispatcher, "$1"), new Window(dispatcher, "@1"));
    }
}
