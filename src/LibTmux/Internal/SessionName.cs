namespace LibTmux.Internal;

/// <summary>Validates tmux session names.</summary>
/// <remarks>
/// Creation and renaming both need this check, and they disagree about what
/// tmux does with a rejected name: 3.2a silently rewrites <c>:</c> to <c>_</c>
/// while 3.7b stores it verbatim. Neither outcome is what the caller asked for,
/// so the name is refused before it reaches tmux.
/// </remarks>
internal static class SessionName
{
    /// <summary>Validates and returns one tmux session name.</summary>
    /// <param name="name">The candidate name.</param>
    /// <returns>The accepted name.</returns>
    /// <exception cref="ArgumentNullException">The name is null.</exception>
    /// <exception cref="ArgumentException">
    /// The name is blank or contains a target separator.
    /// </exception>
    internal static string Validate(string? name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // tmux parses ':' and '.' as the session:window.pane separators, so a
        // name carrying either would address a different object every time it
        // was used as a target.
        if (name.AsSpan().IndexOfAny(':', '.') >= 0)
        {
            throw new ArgumentException(
                "A session name here cannot contain ':' or '.'. tmux accepts them, but "
                + "they separate session, window and pane in a tmux target, so a session "
                + "named with one cannot be reached by name afterwards.",
                nameof(name));
        }

        return name;
    }
}
