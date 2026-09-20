namespace LibTmux.Internal;

/// <summary>Turns a failed option command into a typed failure.</summary>
internal static class OptionFailure
{
    /// <summary>Throws when tmux rejected an option command.</summary>
    /// <param name="result">What the command returned.</param>
    /// <param name="optionName">The option the command named.</param>
    /// <exception cref="TmuxOptionException">tmux reported a failure.</exception>
    internal static void ThrowIfFailed(TmuxCommandResult result, string optionName)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.ExitCode == 0)
        {
            return;
        }

        string reported = string.Join('\n', result.StandardErrorLines).Trim();
        throw new TmuxOptionException(
            reported.Length == 0
                ? $"tmux rejected the option '{optionName}'."
                : reported,
            optionName,
            TmuxDispatchState.Dispatched);
    }
}
