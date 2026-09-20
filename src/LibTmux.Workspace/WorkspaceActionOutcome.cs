namespace LibTmux.Workspace;

/// <summary>Describes how one reviewed workspace operation ended.</summary>
public enum WorkspaceActionState
{
    /// <summary>The operation was not attempted.</summary>
    NotStarted,
    /// <summary>The operation completed; sent input does not imply program completion.</summary>
    Completed,
    /// <summary>tmux rejected an optional layout and application continued.</summary>
    Rejected,
    /// <summary>The operation failed with known dispatch information.</summary>
    Failed,
    /// <summary>The operation failed without establishing its final effect.</summary>
    Unknown,
}

/// <summary>Records one plan action, its dispatch and any materialized result or failure.</summary>
public sealed class WorkspaceActionOutcome
{
    internal WorkspaceActionOutcome(WorkspaceAction action, WorkspaceActionState state,
        TmuxDispatchState dispatch, object? result = null, Exception? failure = null)
    {
        Action = action;
        State = state;
        Dispatch = dispatch;
        Result = result;
        Failure = failure;
    }

    /// <summary>Gets the same immutable action instance held by the reviewed plan.</summary>
    public WorkspaceAction Action { get; }
    /// <summary>Gets whether the action completed, failed or was never attempted.</summary>
    public WorkspaceActionState State { get; }
    /// <summary>Gets whether dispatch was observed; unknown effects must not be retried blindly.</summary>
    public TmuxDispatchState Dispatch { get; }
    /// <summary>Gets a materialized entity, readiness channel name or host result when available.</summary>
    public object? Result { get; }
    /// <summary>Gets the original action failure, including an optional layout rejection.</summary>
    public Exception? Failure { get; }
}
