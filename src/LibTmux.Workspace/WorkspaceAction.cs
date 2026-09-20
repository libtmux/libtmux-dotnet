namespace LibTmux.Workspace;

/// <summary>Names one ordered workspace operation.</summary>
public enum WorkspaceActionKind
{
    /// <summary>Creates a session with a temporary bootstrap window.</summary>
    CreateSession,
    /// <summary>Reads the sole bootstrap window created with a session.</summary>
    CaptureBootstrap,
    /// <summary>Removes the exact session selected during planning.</summary>
    RemoveSession,
    /// <summary>Creates a window in the selected session.</summary>
    CreateWindow,
    /// <summary>Reads the first pane of a newly created window.</summary>
    CaptureFirstPane,
    /// <summary>Creates another pane by splitting a known pane.</summary>
    SplitPane,
    /// <summary>Sets a session, window or pane option.</summary>
    SetOption,
    /// <summary>Removes a captured placement, destroying the window only when no other links remain.</summary>
    UnlinkWindow,
    /// <summary>Moves the first workspace window to the session's effective base index.</summary>
    MoveToBaseIndex,
    /// <summary>Sends literal command text followed by one Enter key.</summary>
    SendText,
    /// <summary>Applies a layout to the created panes.</summary>
    SelectLayout,
    /// <summary>Selects a pane by its created identity.</summary>
    SelectPane,
    /// <summary>Selects a window by its created placement.</summary>
    SelectWindow,
    /// <summary>Opens a unique cooperative readiness channel before pane startup.</summary>
    OpenReadinessChannel,
    /// <summary>Waits for the pane's startup program to signal its channel.</summary>
    WaitForReadiness,
    /// <summary>Closes the owned readiness channel.</summary>
    CloseReadinessChannel,
    /// <summary>Returns the exact existing session without mutation.</summary>
    ReuseSession,
    /// <summary>Runs the explicitly allowed host script in its resolved document directory.</summary>
    RunHostScript,
    /// <summary>Captures the final session and created placements in a server-wide pane snapshot.</summary>
    CaptureResult,
    /// <summary>Arranges existing panes to make space for the next split; failure stops application.</summary>
    ArrangePanes,
}

/// <summary>Describes one immutable operation over plan-local targets.</summary>
/// <remarks>
/// Targets are symbols resolved to native identities during application. Display and
/// enumeration perform no I/O. Derived generic actions expose the actual request.
/// </remarks>
public class WorkspaceAction
{
    internal WorkspaceAction(WorkspaceActionKind kind, string target, string? sourceTarget = null)
    {
        Kind = kind;
        Target = target;
        SourceTarget = sourceTarget;
    }

    /// <summary>Gets the operation to perform.</summary>
    public WorkspaceActionKind Kind { get; }

    /// <summary>Gets the symbolic target created or used by this action.</summary>
    public string Target { get; }

    /// <summary>Gets the parent or source target, when the action needs one.</summary>
    public string? SourceTarget { get; }

    /// <inheritdoc />
    public override string ToString() => $"{Kind} {Target}";
}

/// <summary>Describes an operation with a frozen native request or scalar argument.</summary>
/// <typeparam name="TRequest">The immutable argument type.</typeparam>
public sealed class WorkspaceAction<TRequest> : WorkspaceAction where TRequest : notnull
{
    internal WorkspaceAction(WorkspaceActionKind kind, string target, TRequest request, string? sourceTarget = null)
        : base(kind, target, sourceTarget) => Request = request;

    /// <summary>Gets the request reviewed by the caller before application.</summary>
    public TRequest Request { get; }
}
