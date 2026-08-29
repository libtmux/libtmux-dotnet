namespace LibTmux.Internal;

/// <summary>Resolves stable entity identifiers without materializing collections.</summary>
/// <remarks>
/// Each lookup asks tmux to resolve one identifier rather than listing the
/// server, so the cost does not grow with what the server holds.
/// </remarks>
internal sealed class TmuxEntityLookup(
    Func<IReadOnlyList<string>, CancellationToken, Task<TmuxCommandResult>> execute)
{
    private const string GenerationFormat = "#{pid}:#{start_time}";

    internal async Task<(ServerGeneration Generation, SessionId Id)?> FindSessionAsync(
        SessionId id,
        CancellationToken cancellationToken)
    {
        (ServerGeneration Generation, string Text)? found = await FindAsync(
            id.ToString(),
            "session_id",
            "session",
            cancellationToken).ConfigureAwait(false);
        if (found is not (ServerGeneration generation, string text))
        {
            return null;
        }

        return SessionId.TryParse(text, out SessionId candidate) && candidate == id
            ? (generation, candidate)
            : throw new InvalidDataException("tmux reported a malformed session identifier.");
    }

    internal async Task<(ServerGeneration Generation, WindowId Id)?> FindWindowAsync(
        WindowId id,
        CancellationToken cancellationToken)
    {
        (ServerGeneration Generation, string Text)? found = await FindAsync(
            id.ToString(),
            "window_id",
            "window",
            cancellationToken).ConfigureAwait(false);
        if (found is not (ServerGeneration generation, string text))
        {
            return null;
        }

        return WindowId.TryParse(text, out WindowId candidate) && candidate == id
            ? (generation, candidate)
            : throw new InvalidDataException("tmux reported a malformed window identifier.");
    }

    internal async Task<(ServerGeneration Generation, PaneId Id)?> FindPaneAsync(
        PaneId id,
        CancellationToken cancellationToken)
    {
        (ServerGeneration Generation, string Text)? found = await FindAsync(
            id.ToString(),
            "pane_id",
            "pane",
            cancellationToken).ConfigureAwait(false);
        if (found is not (ServerGeneration generation, string text))
        {
            return null;
        }

        return PaneId.TryParse(text, out PaneId candidate) && candidate == id
            ? (generation, candidate)
            : throw new InvalidDataException("tmux reported a malformed pane identifier.");
    }

    private async Task<(ServerGeneration Generation, string Text)?> FindAsync(
        string target,
        string idWireName,
        string kind,
        CancellationToken cancellationToken)
    {
        TmuxCommandResult result = await execute(
            [
                "display-message",
                "-p",
                "-t",
                target,
                $"{GenerationFormat}\t#{{{idWireName}}}",
            ],
            cancellationToken).ConfigureAwait(false);
        EnsureSuccessful(result, $"{kind} lookup");
        if (result.StandardOutputLines.Count != 1)
        {
            throw new InvalidDataException($"tmux reported a malformed {kind} identity row.");
        }

        string[] fields = result.StandardOutputLines[0].Split('\t');
        if (fields.Length != 2)
        {
            throw new InvalidDataException($"tmux reported a malformed {kind} identity row.");
        }

        // display-message resolves its target with CMD_FIND_CANFAIL, so a
        // target tmux cannot find leaves the identifier empty and still exits
        // zero. The server's own fields resolve either way.
        ServerGeneration generation = TmuxConnection.ParseGeneration(fields[0]);
        return fields[1].Length == 0 ? null : (generation, fields[1]);
    }

    private static void EnsureSuccessful(TmuxCommandResult result, string operation)
    {
        if (result.ExitCode != 0 || result.StandardErrorLines.Count > 0)
        {
            throw new TmuxCommandException($"{operation} failed.", result);
        }
    }
}
