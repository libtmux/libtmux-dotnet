namespace LibTmux;

/// <summary>Describes one <c>link-window</c> invocation.</summary>
public sealed record LinkWindowRequest
{
    /// <summary>Initializes a window-link request.</summary>
    /// <param name="targetSession">The session the window is linked into.</param>
    /// <exception cref="ArgumentException"><paramref name="targetSession" /> is blank.</exception>
    public LinkWindowRequest(string targetSession)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSession);
        TargetSession = targetSession;
    }

    /// <summary>Gets the session the window is linked into.</summary>
    public string TargetSession { get; }

    /// <summary>Gets the index to link at, or null for the next free one.</summary>
    public string? TargetIndex { get; init; }

    /// <summary>Gets whether to insert before or after the target.</summary>
    /// <remarks>
    /// Without an index tmux inserts relative to the destination session's
    /// current window, not its first or last.
    /// </remarks>
    public WindowDirection? Direction { get; init; }

    /// <summary>Gets whether a window already at the index is replaced.</summary>
    public bool ReplaceExisting { get; init; }

    /// <summary>Gets whether the linked window is left unselected.</summary>
    public bool Detach { get; init; }
}
