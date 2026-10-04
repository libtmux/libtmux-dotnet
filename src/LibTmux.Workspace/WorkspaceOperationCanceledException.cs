namespace LibTmux.Workspace;

/// <summary>Reports workspace cancellation and the tmux state materialized before it.</summary>
public sealed class WorkspaceOperationCanceledException : OperationCanceledException
{
    /// <summary>Initializes a workspace cancellation exception.</summary>
    /// <param name="partialResult">The materialized state, or null when none was read.</param>
    /// <param name="failure">The original cancellation.</param>
    /// <param name="cancellationToken">The caller token that canceled application.</param>
    public WorkspaceOperationCanceledException(WorkspaceResult? partialResult,
        OperationCanceledException failure, CancellationToken cancellationToken)
        : this(partialResult, failure, partialResult?.Journal ?? [],
            partialResult?.CompensationJournal ?? [], cancellationToken)
    {
    }

    internal WorkspaceOperationCanceledException(WorkspaceResult? partialResult,
        OperationCanceledException failure, IReadOnlyList<WorkspaceActionOutcome> journal,
        IReadOnlyList<WorkspaceActionOutcome> cleanup, CancellationToken cancellationToken)
        : base(
            partialResult is null
                ? "Workspace application was canceled before tmux state was materialized."
                : "Workspace application was canceled; PartialResult reports materialized tmux state.",
            failure, cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(failure);
        PartialResult = partialResult;
        Journal = WorkspaceCollections.Copy(journal, nameof(journal));
        CompensationJournal = WorkspaceCollections.Copy(cleanup, nameof(cleanup));
        Dispatch = WorkspaceBuildException.DispatchFor(failure, Journal);
    }

    /// <summary>Gets the session and windows materialized before cancellation, when known.</summary>
    public WorkspaceResult? PartialResult { get; }

    /// <summary>Gets all planned action outcomes, even when no session was materialized.</summary>
    public IReadOnlyList<WorkspaceActionOutcome> Journal { get; }

    /// <summary>Gets attempted cleanup outcomes and their failures without replacing the original cancellation.</summary>
    public IReadOnlyList<WorkspaceActionOutcome> CompensationJournal { get; }

    /// <summary>Gets the dispatch evidence from application.</summary>
    public TmuxDispatchState Dispatch { get; }
}
