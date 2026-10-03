namespace LibTmux;

/// <summary>A created window and the initial pane identity reported by its creation command.</summary>
/// <remarks>
/// The pane identifier belongs to <see cref="Window" />'s endpoint and generation.
/// It describes creation time, not a captured relationship or current membership.
/// Resolve that exact identifier before acting on the pane; another client or
/// hook may already have moved or removed it.
/// </remarks>
public sealed class WindowCreationResult
{
    internal WindowCreationResult(Window window, PaneId initialPaneId)
    {
        Window = window;
        InitialPaneId = initialPaneId;
    }

    /// <summary>Gets the created window in the creating session's placement.</summary>
    public Window Window { get; }

    /// <summary>Gets the initial pane identifier from the creation reply.</summary>
    public PaneId InitialPaneId { get; }
}
