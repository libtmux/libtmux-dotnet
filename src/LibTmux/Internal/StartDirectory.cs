namespace LibTmux.Internal;

/// <summary>Prepares a tmux <c>-c</c> start-directory value.</summary>
/// <remarks>
/// tmux hands the value straight to <c>chdir</c>, so a leading <c>~</c> is not
/// a home directory to it: the call fails and tmux silently falls back to its
/// own default, leaving the pane somewhere the caller never asked for. Only a
/// shell expands the tilde, and there is no shell in this path.
/// <para>
/// tmux also expands the value as a format, in the spawn path every command
/// that takes <c>-c</c> shares, so a <c>#</c> in it does not reach chdir.
/// </para>
/// </remarks>
internal static class StartDirectory
{
    /// <summary>Resolves one start directory, or null when there is nothing to send.</summary>
    /// <param name="value">The requested directory.</param>
    /// <returns>The directory to send, or null to omit the flag.</returns>
    internal static string? Resolve(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (value[0] != '~')
        {
            return value;
        }

        if (value.Length > 1 && value[1] != Path.DirectorySeparatorChar)
        {
            // "~user" names somebody else's home, which only a shell can
            // resolve, so it is passed through rather than guessed at.
            return value;
        }

        string home = System.Environment.GetFolderPath(
            System.Environment.SpecialFolder.UserProfile);
        return home.Length == 0
            ? value
            : value.Length == 1 ? home : Path.Join(home, value.AsSpan(2));
    }
}
