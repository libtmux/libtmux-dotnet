namespace LibTmux;

/// <summary>Identifies one window linked into one session.</summary>
/// <remarks>
/// tmux can link a window into several sessions, or at multiple indexes within
/// one session. This key identifies the session and window; use
/// <see cref="Window.Edge"/> for the particular indexed placement.
/// </remarks>
/// <param name="SessionId">The session the window is linked into.</param>
/// <param name="WindowId">The linked window.</param>
public readonly record struct WindowEntityKey(SessionId SessionId, WindowId WindowId)
{
    /// <inheritdoc />
    public override string ToString() => $"{SessionId}:{WindowId}";
}
