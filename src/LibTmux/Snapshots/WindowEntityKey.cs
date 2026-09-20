namespace LibTmux;

/// <summary>Identifies one window linked into one session.</summary>
/// <remarks>
/// tmux can link a window at several indexes in the same session. This key
/// names one placement within a captured server generation; window equality
/// continues to compare the generation and window identifier.
/// </remarks>
/// <param name="SessionId">The session the window is linked into.</param>
/// <param name="WindowId">The linked window.</param>
/// <param name="WindowIndex">The index of this placement in the session.</param>
public readonly record struct WindowEntityKey(SessionId SessionId, WindowId WindowId, int WindowIndex)
{
    /// <inheritdoc />
    public override string ToString() => $"{SessionId}:{WindowIndex}:{WindowId}";
}
