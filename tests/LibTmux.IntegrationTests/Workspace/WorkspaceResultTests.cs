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
    public void Cancellation_preserves_host_evidence_and_cleanup_failures()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        WorkspaceHostResult host = new(true, 137, "partial", "diagnostic", 17);
        var operation = new WorkspaceHostCanceledException(host,
            new OperationCanceledException(cancellation.Token), cancellation.Token);
        var cleanupFailure = new IOException("Owned cleanup failed.");
        WorkspaceActionOutcome interrupted = new(new(WorkspaceActionKind.RunHostScript, "host"),
            WorkspaceActionState.Unknown, TmuxDispatchState.Unknown, host, operation);
        WorkspaceActionOutcome compensation = new(new(WorkspaceActionKind.UnlinkWindow, "window:0"),
            WorkspaceActionState.Failed, TmuxDispatchState.NotDispatched, failure: cleanupFailure);
        List<WorkspaceActionOutcome> journal = [interrupted];
        List<WorkspaceActionOutcome> cleanup = [compensation];
        (Session session, Window window) = Entities();
        WorkspaceResult partial = new(session, [window], []);

        var failure = new WorkspaceOperationCanceledException(partial, operation, journal, cleanup, cancellation.Token);
        journal.Clear();
        cleanup.Clear();

        Assert.Same(operation, failure.InnerException);
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Same(partial, failure.PartialResult);
        Assert.Equal(TmuxDispatchState.Unknown, failure.Dispatch);
        Assert.Same(interrupted, Assert.Single(failure.Journal));
        Assert.Same(host, Assert.Single(failure.Journal).Result);
        Assert.Same(cleanupFailure, Assert.Single(failure.CompensationJournal).Failure);
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
