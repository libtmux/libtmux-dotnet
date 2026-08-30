namespace LibTmux.Internal;

internal readonly record struct TmuxTarget(string Value, SessionId? Session = null)
{
    internal static TmuxTarget From(SessionId id) => new(id.ToString());

    internal static TmuxTarget From(WindowId id) => new(id.ToString());

    internal static TmuxTarget From(PaneId id) => new(id.ToString());

    /// <summary>Names a window inside one session.</summary>
    /// <remarks>
    /// A window linked into several sessions resolves from a bare identifier to
    /// whichever session tmux ranks best, which need not be the one a handle
    /// was read in. Naming the session keeps the answer where the caller is.
    /// </remarks>
    internal static TmuxTarget In(SessionId session, WindowId id) =>
        new($"{session}:{id}", session);

    /// <summary>Names a pane inside one session.</summary>
    /// <remarks>
    /// The empty window part asks tmux to resolve the pane identifier globally
    /// and keep the session, which is what a linked window needs.
    /// </remarks>
    internal static TmuxTarget In(SessionId session, PaneId id) =>
        new($"{session}:.{id}", session);
}
