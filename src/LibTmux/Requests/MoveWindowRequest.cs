namespace LibTmux;

/// <summary>Describes one <c>move-window</c> invocation.</summary>
public sealed record MoveWindowRequest : ITmuxRequest<Window>
{
    /// <summary>Gets the window part of the target, empty for the next free index.</summary>
    public string Destination { get; init; } = "";

    /// <summary>Gets the destination session, or null for the window's own.</summary>
    public string? Session { get; init; }

    /// <summary>Gets whether to insert before or after the destination.</summary>
    public WindowDirection? Direction { get; init; }

    /// <summary>Gets whether the moved window is left unselected.</summary>
    public bool NoSelect { get; init; }

    /// <summary>Gets whether a window already at the index is replaced.</summary>
    public bool ReplaceExisting { get; init; }

    /// <summary>Gets whether the destination session's windows are renumbered.</summary>
    /// <remarks>
    /// tmux renumbers and returns without moving anything, ignoring every other
    /// flag on the request, so this is a renumber request rather than a move
    /// that also renumbers.
    /// </remarks>
    public bool Renumber { get; init; }

    /// <summary>Returns a window-move request as one tmux command.</summary>
    /// <param name="window">The window being moved.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="window" /> is null.</exception>
    public TmuxCommand ToCommand(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return TmuxChaining.Command([.. window.BuildMoveWindowArguments(this)]) with
        {
            RequiredGeneration = window.Generation,
        };
    }
}
