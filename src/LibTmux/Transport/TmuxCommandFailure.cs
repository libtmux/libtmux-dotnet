namespace LibTmux.Internal;

internal static class TmuxCommandFailure
{
    // The client reports a missing server as its whole answer: one line of
    // standard error, nothing on standard output, and a failing exit. tmux
    // echoes user text inside its own errors, so the phrase elsewhere names
    // nothing, and output means a server answered part of a group. A socket
    // that cannot be opened is not the same as a server that is gone: "error
    // connecting to" also covers a permission error against a live daemon.
    internal static bool NamesMissingServer(TmuxCommandResult result) =>
        result.ExitCode != 0
        && result.StandardOutput.IsEmpty
        && result.StandardErrorLines is [string line]
        && (line.StartsWith("no server running on ", StringComparison.Ordinal)
            || (line.StartsWith("error connecting to ", StringComparison.Ordinal)
                && line.EndsWith("(No such file or directory)", StringComparison.Ordinal)));

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
