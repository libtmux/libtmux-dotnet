namespace LibTmux.Workspace;

/// <summary>Reports a workspace failure and the tmux state created before it.</summary>
public sealed class WorkspaceBuildException : LibTmuxException
{
    /// <summary>Initializes a workspace build exception.</summary>
    /// <param name="partialResult">The materialized state, or null when none was read.</param>
    /// <param name="failure">The operation that failed.</param>
    public WorkspaceBuildException(WorkspaceResult? partialResult, Exception failure)
        : this(partialResult, failure, partialResult?.Journal ?? [], partialResult?.CompensationJournal ?? [])
    {
    }

    internal WorkspaceBuildException(WorkspaceResult? partialResult, Exception failure,
        IReadOnlyList<WorkspaceActionOutcome> journal, IReadOnlyList<WorkspaceActionOutcome> cleanup)
        : base(
            partialResult is null
                ? "Workspace construction failed before tmux state was materialized."
                : "Workspace construction failed; PartialResult reports materialized tmux state.",
            DispatchFor(failure, journal),
            failure)
    {
        PartialResult = partialResult;
        Journal = WorkspaceCollections.Copy(journal, nameof(journal));
        CompensationJournal = WorkspaceCollections.Copy(cleanup, nameof(cleanup));
    }

    /// <summary>Gets the session and windows materialized before failure, when known.</summary>
    public WorkspaceResult? PartialResult { get; }

    /// <summary>Gets all planned action outcomes, even when no session was materialized.</summary>
    public IReadOnlyList<WorkspaceActionOutcome> Journal { get; }

    /// <summary>Gets attempted cleanup outcomes and their failures without replacing the original cause.</summary>
    public IReadOnlyList<WorkspaceActionOutcome> CompensationJournal { get; }

    private static TmuxDispatchState DispatchFor(Exception failure, IReadOnlyList<WorkspaceActionOutcome> journal)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (journal.Any(outcome => outcome.Dispatch == TmuxDispatchState.Unknown || outcome.Result is WorkspaceHostResult { Started: true }))
            return TmuxDispatchState.Unknown;
        if (journal.Any(outcome => outcome.Dispatch == TmuxDispatchState.Dispatched))
            return TmuxDispatchState.Dispatched;
        return failure switch
        {
            LibTmuxException tmux => tmux.Dispatch,
            StaleServerGenerationException => TmuxDispatchState.NotDispatched,
            TmuxOperationCanceledException tmux => tmux.CommandMayHaveExecuted ? TmuxDispatchState.Unknown : TmuxDispatchState.NotDispatched,
            OperationCanceledException => TmuxDispatchState.NotDispatched,
            _ => TmuxDispatchState.Unknown,
        };
    }
}
