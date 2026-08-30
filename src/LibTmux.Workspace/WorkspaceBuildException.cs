namespace LibTmux.Workspace;

/// <summary>Reports a workspace failure and the tmux state created before it.</summary>
public sealed class WorkspaceBuildException : LibTmuxException
{
    /// <summary>Initializes a workspace build exception.</summary>
    /// <param name="partialResult">The materialized state, or null when none was read.</param>
    /// <param name="failure">The operation that failed.</param>
    public WorkspaceBuildException(WorkspaceResult? partialResult, Exception failure)
        : base(
            partialResult is null
                ? "Workspace construction failed before tmux state was materialized."
                : "Workspace construction failed; PartialResult reports materialized tmux state.",
            DispatchFor(failure),
            failure)
        => PartialResult = partialResult;

    /// <summary>Gets the session and windows materialized before failure, when known.</summary>
    public WorkspaceResult? PartialResult { get; }

    private static TmuxDispatchState DispatchFor(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return failure is LibTmuxException tmuxFailure
            ? tmuxFailure.Dispatch
            : TmuxDispatchState.Unknown;
    }
}
