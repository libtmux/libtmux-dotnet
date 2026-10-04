namespace LibTmux.Internal;

internal static class TmuxCommandFailure
{
    // A socket that cannot be opened is not the same as a server that is
    // already gone: "error connecting to" also covers a permission error
    // against a live daemon, so on its own it must not read as absence.
    internal static bool NamesMissingServer(TmuxCommandResult result)
    {
        string standardError = string.Join('\n', result.StandardErrorLines);
        return standardError.Contains("no server running", StringComparison.Ordinal)
            || (standardError.Contains("error connecting to", StringComparison.Ordinal)
                && standardError.Contains("No such file or directory", StringComparison.Ordinal));
    }

    // The tmux client ran, but no server heard the command.
    internal static TmuxDispatchState DispatchOf(TmuxCommandResult result) =>
        NamesMissingServer(result) ? TmuxDispatchState.NotDispatched : TmuxDispatchState.Dispatched;

    internal static void ThrowIfFailed(TmuxCommandResult result, string operation)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        if (result.StandardErrorLines.Count > 0)
        {
            throw new TmuxCommandException(
                $"{operation} failed: {string.Join('\n', result.StandardErrorLines)}",
                result);
        }

        if (result.ExitCode == 0)
        {
            return;
        }

        // Not every failure writes to standard error: checking a config file
        // reports the problem on standard output but still exits non-zero.
        string reported = string.Join('\n', result.StandardOutputLines).Trim();
        throw new TmuxCommandException(
            reported.Length == 0
                ? $"{operation} failed with exit code {result.ExitCode}."
                : $"{operation} failed: {reported}",
            result);
    }
}
