namespace LibTmux;

/// <summary>A created session and the initial child identities reported by its creation command.</summary>
/// <remarks>
/// The identifiers belong to <see cref="Session" />'s endpoint and generation.
/// They describe creation time, not captured relationships or current membership.
/// Resolve those exact identifiers before acting on the children; another client
/// or hook may already have moved or removed them.
/// </remarks>
public sealed class SessionCreationResult
{
    internal SessionCreationResult(Session session, WindowId initialWindowId, int initialWindowIndex, PaneId initialPaneId)
    {
        Session = session;
        InitialWindowId = initialWindowId;
        InitialWindowIndex = initialWindowIndex;
        InitialPaneId = initialPaneId;
    }

    /// <summary>Gets the created session, bound to the daemon that acknowledged creation.</summary>
    public Session Session { get; }

    /// <summary>Gets the initial window identifier from the creation reply.</summary>
    public WindowId InitialWindowId { get; }

    /// <summary>Gets the initial window's session-relative index from the creation reply.</summary>
    public int InitialWindowIndex { get; }

    /// <summary>Gets the initial pane identifier from the creation reply.</summary>
    public PaneId InitialPaneId { get; }
}
