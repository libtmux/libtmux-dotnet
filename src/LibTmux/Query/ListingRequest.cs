namespace LibTmux.Query;

/// <summary>Describes one tmux listing narrowed by tmux and rechecked locally.</summary>
/// <param name="Target">The kind of object listed.</param>
/// <param name="Session">The session the listing is confined to, if any.</param>
/// <param name="Window">The window the listing is confined to, if any.</param>
/// <param name="Filter">The portable predicate every result satisfies.</param>
/// <param name="Unsafe">A raw tmux filter, trusted without a recheck.</param>
/// <param name="Screen">Text a listed pane's visible rows must show.</param>
internal sealed record ListingRequest(
    QueryTarget Target,
    SessionId? Session = null,
    WindowId? Window = null,
    QueryDocument? Filter = null,
    UnsafeTmuxFilter? Unsafe = null,
    PaneScreenSearch? Screen = null)
{
    /// <summary>Rejects a request that names objects its target cannot hold.</summary>
    /// <exception cref="ArgumentException">The request mixes incompatible parts.</exception>
    internal void Validate()
    {
        if (Filter is not null && Filter.Target != Target)
        {
            throw new ArgumentException(
                $"The filter selects {Filter.Target} objects, but the listing reads {Target} objects.");
        }

        if (Screen is not null && Target != QueryTarget.Pane)
        {
            throw new ArgumentException($"Only panes show screen text; the listing reads {Target} objects.");
        }

        bool scoped = (Session, Window, Target) switch
        {
            (null, null, _) => true,
            (_, _, QueryTarget.Client) => false,
            (_, null, QueryTarget.Window or QueryTarget.Pane) => true,
            (null, _, QueryTarget.Pane) => true,
            _ => false,
        };
        if (!scoped)
        {
            throw new ArgumentException($"A {Target} listing cannot be confined to that scope.");
        }
    }
}
