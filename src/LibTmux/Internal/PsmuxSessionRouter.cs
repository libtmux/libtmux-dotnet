using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace LibTmux.Internal;

[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The semaphore owns no wait handle unless its AvailableWaitHandle is used.")]
internal sealed class PsmuxSessionRouter
{
    private const string SessionFormat =
        "#{pid}:#{start_time}\t#{session_id}\t#{session_name}";
    private readonly Func<
        IReadOnlyList<string>,
        CancellationToken,
        IReadOnlyList<string>?,
        Task<TmuxCommandResult>> _executeRaw;
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal PsmuxSessionRouter(
        Func<
            IReadOnlyList<string>,
            CancellationToken,
            IReadOnlyList<string>?,
            Task<TmuxCommandResult>> executeRaw) =>
        _executeRaw = executeRaw;

    internal async Task<PsmuxSessionState> DiscoverSessionAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RequireSingleSessionAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<TmuxCommandResult> ExecuteSingleAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PsmuxCommandPolicy.Validate(arguments);
            string command = arguments[0];
            if (PsmuxCommandPolicy.CanRunWithoutSession(command))
            {
                IReadOnlyList<PsmuxSessionState> sessions = await ReadSessionsAsync(
                        cancellationToken)
                    .ConfigureAwait(false);
                EnsureAtMostOneSession(sessions);
                if (sessions.Count == 0)
                {
                    return MissingSessionResult(arguments);
                }

                if (command is "has-session" or "has")
                {
                    IReadOnlyList<string> rewritten =
                        PsmuxTargetGrammar.RewriteSessionTarget(arguments, sessions[0]);
                    return await _executeRaw(rewritten, cancellationToken, arguments)
                        .ConfigureAwait(false);
                }

                return await _executeRaw(arguments, cancellationToken, null).ConfigureAwait(false);
            }

            IReadOnlyList<PsmuxSessionState> available = await ReadSessionsAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            EnsureAtMostOneSession(available);
            if (available.Count == 0)
            {
                return MissingSessionResult(arguments);
            }

            PsmuxSessionState session = available[0];
            IReadOnlyList<string> routed =
                PsmuxTargetGrammar.RewriteSessionTarget(arguments, session);
            await EnsureObjectTargetExistsAsync(routed, session.Name, cancellationToken)
                .ConfigureAwait(false);
            return await _executeRaw(routed, cancellationToken, arguments).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<TmuxCommandResult> ExecuteGuardedAsync(
        ServerGeneration expected,
        IReadOnlyList<IReadOnlyList<string>> commands,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (commands.Count != 1)
            {
                throw new NotSupportedException(
                    "psmux does not preserve tmux grouped-command semantics.");
            }

            IReadOnlyList<string> command = commands[0];
            PsmuxCommandPolicy.Validate(command);
            PsmuxSessionState session = await RequireSingleSessionAsync(cancellationToken)
                .ConfigureAwait(false);
            if (session.Generation != expected)
            {
                ThrowStaleGeneration(expected, session.Generation);
            }

            IReadOnlyList<string> routed =
                PsmuxTargetGrammar.RewriteSessionTarget(command, session);
            await EnsureObjectTargetExistsAsync(routed, session.Name, cancellationToken)
                .ConfigureAwait(false);
            return await _executeRaw(routed, cancellationToken, command).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<PsmuxSessionState>> ReadSessionsAsync(
        CancellationToken cancellationToken)
    {
        TmuxCommandResult result = await _executeRaw(
                ["list-sessions", "-F", SessionFormat],
                cancellationToken,
                null)
            .ConfigureAwait(false);
        EnsureSuccessful(result, "psmux session discovery");

        var sessions = new List<PsmuxSessionState>(result.StandardOutputLines.Count);
        foreach (string line in result.StandardOutputLines)
        {
            PsmuxSessionState session = ParseSessionRow(line, "psmux session");
            PsmuxTargetGrammar.ValidateName(session.Name, "session");
            sessions.Add(session);
        }

        if (sessions.Count == 1)
        {
            await ValidateExactSessionAsync(sessions[0], cancellationToken)
                .ConfigureAwait(false);
        }

        return sessions;
    }

    private async Task ValidateExactSessionAsync(
        PsmuxSessionState session,
        CancellationToken cancellationToken)
    {
        TmuxCommandResult targeted = await _executeRaw(
                ["display-message", "-p", "-t", session.Name, SessionFormat],
                cancellationToken,
                null)
            .ConfigureAwait(false);
        if (targeted.ExitCode != 0 || targeted.StandardErrorLines.Count > 0)
        {
            throw new NotSupportedException(
                "psmux namespace discovery returned a session that cannot be targeted exactly.");
        }

        if (targeted.StandardOutputLines.Count != 1
            || ParseSessionRow(targeted.StandardOutputLines[0], "targeted psmux session")
                != session)
        {
            throw new NotSupportedException(
                "psmux namespace discovery returned an inconsistent session identity.");
        }

        TmuxCommandResult selected = await _executeRaw(
                ["display-message", "-p", SessionFormat],
                cancellationToken,
                null)
            .ConfigureAwait(false);
        if (selected.ExitCode != 0
            || selected.StandardErrorLines.Count > 0
            || selected.StandardOutputLines.Count != 1)
        {
            throw new NotSupportedException(
                "psmux default routing could not be matched to the selected session.");
        }

        PsmuxSessionState selectedSession = ParseSessionRow(
            selected.StandardOutputLines[0],
            "psmux selected session");
        if (selectedSession != session)
        {
            throw new NotSupportedException(
                "psmux default routing does not match the selected session.");
        }
    }

    private async Task<PsmuxSessionState> RequireSingleSessionAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<PsmuxSessionState> sessions = await ReadSessionsAsync(cancellationToken)
            .ConfigureAwait(false);
        EnsureAtMostOneSession(sessions);
        if (sessions.Count == 0)
        {
            throw new InvalidOperationException(
                "The selected psmux namespace has no live session.");
        }

        return sessions[0];
    }

    private async Task EnsureObjectTargetExistsAsync(
        IReadOnlyList<string> arguments,
        string sessionName,
        CancellationToken cancellationToken)
    {
        int operandIndex = PsmuxTargetGrammar.FindOptionOperand(arguments, "-t");
        if (operandIndex < 0)
        {
            return;
        }

        string target = arguments[operandIndex];
        int separator = target.LastIndexOf(':');
        string candidate = separator < 0 ? target : target[(separator + 1)..];
        if (candidate.StartsWith('.') && PaneId.TryParse(candidate[1..], out _))
        {
            candidate = candidate[1..];
        }

        if (candidate.StartsWith('='))
        {
            candidate = candidate[1..];
        }

        IReadOnlyList<string>? probe = null;
        if (PaneId.TryParse(candidate, out _))
        {
            probe = ["list-panes", "-s", "-t", sessionName, "-F", "#{pane_id}"];
        }
        else if (WindowId.TryParse(candidate, out _))
        {
            probe = ["list-windows", "-t", sessionName, "-F", "#{window_id}"];
        }

        if (probe is null)
        {
            return;
        }

        TmuxCommandResult result = await _executeRaw(probe, cancellationToken, null)
            .ConfigureAwait(false);
        EnsureSuccessful(result, "psmux object target validation");
        if (!result.StandardOutputLines.Contains(candidate, StringComparer.Ordinal))
        {
            throw new TmuxObjectNotFoundException(
                $"The psmux target {candidate} is no longer visible in session {sessionName}.",
                candidate);
        }
    }

    private static void EnsureAtMostOneSession(IReadOnlyList<PsmuxSessionState> sessions)
    {
        if (sessions.Count > 1)
        {
            throw new NotSupportedException(
                "LibTmux psmux connections require exactly one session per namespace.");
        }
    }

    private static TmuxCommandResult MissingSessionResult(IReadOnlyList<string> arguments)
    {
        byte[] error = "no server running on selected psmux namespace\n"u8.ToArray();
        return new TmuxCommandResult(
            arguments,
            1,
            ReadOnlyMemory<byte>.Empty,
            error,
            [],
            Utf8BackslashDecoder.ProjectErrorLines(error));
    }

    private static PsmuxSessionState ParseSessionRow(string line, string kind)
    {
        string[] fields = line.Split('\t');
        if (fields.Length != 3
            || !SessionId.TryParse(fields[1], out SessionId id))
        {
            throw new LibTmuxException(
                $"tmux reported a malformed {kind} identity row.",
                TmuxDispatchState.Dispatched);
        }

        return new PsmuxSessionState(
            fields[2],
            id,
            TmuxConnection.ParseGeneration(fields[0]));
    }

    private static void ThrowStaleGeneration(
        ServerGeneration expected,
        ServerGeneration actual)
    {
        string expectedText =
            $"{expected.ProcessId.ToString(CultureInfo.InvariantCulture)}:{expected.StartTime.ToString(CultureInfo.InvariantCulture)}";
        string actualText =
            $"{actual.ProcessId.ToString(CultureInfo.InvariantCulture)}:{actual.StartTime.ToString(CultureInfo.InvariantCulture)}";
        throw new StaleServerGenerationException(
            $"The tmux server generation changed from {expectedText} to {actualText}.",
            expected,
            actual);
    }

    private static void EnsureSuccessful(TmuxCommandResult result, string operation)
    {
        if (result.ExitCode != 0 || result.StandardErrorLines.Count > 0)
        {
            throw new TmuxCommandException($"{operation} failed.", result);
        }
    }
}

internal sealed record PsmuxSessionState(
    string Name,
    SessionId Id,
    ServerGeneration Generation);
