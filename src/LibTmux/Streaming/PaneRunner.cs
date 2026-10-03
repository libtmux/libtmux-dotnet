using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;

namespace LibTmux.Internal;

/// <summary>Names the tmux executable and socket a command run inside a pane must reach.</summary>
/// <param name="TmuxBinaryPath">The absolute tmux executable.</param>
/// <param name="SocketPath">The absolute socket of the server that owns the pane.</param>
/// <param name="PaneId">The pane the route was resolved for.</param>
/// <param name="Generation">The daemon that owns the pane.</param>
/// <remarks>
/// A command run from inside a pane inherits <c>TMUX</c> and would reach the
/// ambient server, which is not necessarily the one being driven, and a bare
/// <c>tmux</c> resolves through the pane shell's own <c>PATH</c>.
/// </remarks>
[UnsupportedOSPlatform("windows")]
internal readonly record struct PaneRunRoute(
    string TmuxBinaryPath,
    string SocketPath,
    PaneId PaneId,
    ServerGeneration Generation)
{
    internal PaneRunIdentity Identity => new(Generation, PaneId);

    /// <summary>Resolves the route from the server's tmux and the pane's socket.</summary>
    /// <exception cref="TmuxPaneException">The executable or socket cannot be named absolutely.</exception>
    internal static PaneRunRoute From(Pane pane)
    {
        ArgumentNullException.ThrowIfNull(pane);
        string binary = pane.Server.ConnectionOptions.TmuxBinaryPath;
        string? resolved = ResolveExecutable(binary);
        if (resolved is null
            || !pane.RawFormatFields.TryGetValue("socket_path", out string? socket)
            || socket is null
            || !Path.IsPathFullyQualified(socket))
        {
            throw new TmuxPaneException(
                $"Pane {pane.Id} cannot run a command: the tmux executable '{binary}' or the pane's socket has no absolute path.",
                pane.Id,
                TmuxDispatchState.NotDispatched);
        }

        return new PaneRunRoute(resolved, socket, pane.Id, pane.Generation);
    }

    private static string? ResolveExecutable(string configured)
    {
        static bool Executable(string candidate)
        {
            if (!File.Exists(candidate))
            {
                return false;
            }

            UnixFileMode mode = File.GetUnixFileMode(candidate);
            return (mode & (UnixFileMode.UserExecute
                | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }

        if (configured.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            string candidate = Path.GetFullPath(configured);
            return Executable(candidate) ? candidate : null;
        }

        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator))
        {
            string candidate = Path.GetFullPath(Path.Combine(
                directory.Length == 0 ? Environment.CurrentDirectory : directory,
                configured));
            if (Executable(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    internal void RequireSame(PaneRunRoute final)
    {
        if (this != final)
        {
            throw new LibTmuxException(
                "The pane or its tmux route changed before dispatch. The command was not sent.");
        }
    }

    /// <summary>Builds a shell command line that reaches this same server.</summary>
    /// <remarks><c>command</c> keeps a shell function from replacing the tmux executable.</remarks>
    internal string CommandLine(params string[] arguments)
    {
        StringBuilder line = new("command ");
        line.Append(PaneRunner.ShellQuote(TmuxBinaryPath));
        line.Append(" -S ").Append(PaneRunner.ShellQuote(SocketPath));
        foreach (string argument in arguments)
        {
            line.Append(' ').Append(PaneRunner.ShellQuote(argument));
        }

        return line.ToString();
    }
}

internal readonly record struct PaneRunIdentity(ServerGeneration Generation, PaneId PaneId);

/// <summary>Lets a caller take part in a run without changing what it does.</summary>
internal sealed record PaneRunHooks
{
    /// <summary>Gets a final check, run just before the payload is pasted, that returns the pane to paste into.</summary>
    internal Func<CancellationToken, Task<Pane>>? DispatchPreflight { get; init; }

    /// <summary>Gets a callback recording the payload about to be pasted, returning how to undo or keep the record.</summary>
    internal Func<Pane, string, (Action Rollback, Action Settle)>? NoteDispatch { get; init; }

    /// <summary>Gets a callback when the run no longer owns the pane, including a refused setup.</summary>
    internal Action? Completed { get; init; }

    /// <summary>Gets a callback told when bounded following stops without terminal proof.</summary>
    internal Action? Unresolved { get; init; }

    /// <summary>
    /// Gets how long to follow a run that outlasts its wait. After that the
    /// reservation remains until the next run authenticates completion.
    /// </summary>
    internal TimeSpan? FollowLimit { get; init; }

    /// <summary>Gets a callback told, about once a second, how long the run has gone on.</summary>
    internal Action<TimeSpan>? Progress { get; init; }

    /// <summary>Gets how to delete the paste buffer after a failed paste; the default ignores failures.</summary>
    internal Func<Server, string, Exception?, Task>? CleanupBuffer { get; init; }
}

/// <summary>Describes how one run ended.</summary>
/// <param name="Pane">The pane the payload went to.</param>
/// <param name="ExitStatus">The command's exit status, or null when it had not finished.</param>
/// <param name="TimedOut">Whether the time allowed ran out first.</param>
/// <param name="Output">What the command printed, without the run's own bookkeeping.</param>
/// <param name="LinesMissed">Whether scrollback dropped rows before they were read.</param>
/// <param name="AnchorLost">Whether the position read from could not be found again.</param>
/// <param name="Started">Whether the wrapper's begin marker was observed.</param>
/// <param name="Elapsed">How long the run took, or how long it was waited for.</param>
/// <param name="PaneExited">Whether the pane's program exited before the command reported its status.</param>
internal sealed record PaneRunOutcome(
    Pane Pane,
    int? ExitStatus,
    bool TimedOut,
    IReadOnlyList<string> Output,
    bool LinesMissed,
    bool AnchorLost,
    bool Started,
    TimeSpan Elapsed,
    bool PaneExited = false);

/// <summary>Runs a shell command in a pane and learns its exit status.</summary>
/// <remarks>
/// Completion is not guessed from the screen. The command is written to a
/// private file the pane's shell sources; a wrapper prints begin and end
/// markers around it, stores <c>$?</c> in a private pane option, and signals a
/// private <c>wait-for</c> channel. A run still going when the time allowed ends
/// is followed for a bounded period. Its reservation remains until completion
/// or pane/server end is authenticated.
/// </remarks>
[UnsupportedOSPlatform("windows")]
internal static class PaneRunner
{
    private const int MaximumInheritedTrapBytes = 64 * 1024;
    private static readonly TimeSpan RetainedRunFirstProbe = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan RetainedRunLongestProbe = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StatusCleanupTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultFollowLimit = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RunningPaneFirstProbe = TimeSpan.FromMilliseconds(250);
    private static readonly Action Nothing = static () => { };
    private static readonly ConcurrentDictionary<PaneRunIdentity, RunReservation> ActiveRuns = new();
    private static readonly HashSet<string> PosixShells =
        new(["sh", "ash", "bash", "dash", "ksh", "ksh93", "mksh", "pdksh", "zsh"],
            StringComparer.Ordinal);

    /// <summary>Runs one command.</summary>
    /// <param name="server">The server that owns the pane.</param>
    /// <param name="pane">The pane whose shell runs the command.</param>
    /// <param name="route">How a command in the pane reaches this server.</param>
    /// <param name="command">The shell command.</param>
    /// <param name="budget">How long to wait for it.</param>
    /// <param name="suppressHistory">Whether the sourcing line starts with a space.</param>
    /// <param name="statusMarkerLifetime">When tmux deletes the status option on its own.</param>
    /// <param name="hooks">What the caller adds.</param>
    /// <param name="fail">Builds the exception for a pane read that cannot be completed.</param>
    /// <param name="cancellationToken">Stops waiting; a dispatched command keeps running.</param>
    /// <returns>How the run ended.</returns>
    internal static async Task<PaneRunOutcome> RunAsync(
        Server server,
        Pane pane,
        PaneRunRoute route,
        string command,
        TimeSpan budget,
        bool suppressHistory,
        TimeSpan statusMarkerLifetime,
        PaneRunHooks hooks,
        Func<PaneReadFailure, Pane, Exception> fail,
        CancellationToken cancellationToken)
    {
        RunReservation? reservation = null;
        bool completionOwnershipTransferred = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateRunCommand(command, maximumBytes: 4_000_000);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(budget, TimeSpan.Zero);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(budget, TimeSpan.FromDays(1));
            if (route.PaneId != pane.Id || route.Generation != pane.Generation)
            {
                throw new TmuxPaneException(
                    $"The run route does not belong to pane {pane.Id} in this server generation.",
                    pane.Id,
                    TmuxDispatchState.NotDispatched);
            }

            reservation = await ReserveAsync(
                    pane, route, hooks.Completed, hooks.Unresolved, cancellationToken)
                .ConfigureAwait(false);
            if (hooks.DispatchPreflight is null)
            {
                Pane current = await RequireSingleWritableShellAsync(pane, cancellationToken)
                    .ConfigureAwait(false);
                route.RequireSame(PaneRunRoute.From(current));
                pane = current;
            }

            PaneRead baselineRead = await PaneReader.ReadVisibleAsync(pane, null, fail, cancellationToken)
                .ConfigureAwait(false);
            PaneCursor baseline = PaneCursor.Build(pane, baselineRead.State, baselineRead.CursorRows);

            RunToken token = RunToken.Create();
            reservation.SetToken(token);
            Stopwatch elapsed = Stopwatch.StartNew();
            var sequence = new TmuxMutationSequence(
                "The command was sent, but observing its result failed. It may still be "
                + "running or may already have finished; do not retry until you inspect the pane.");
            var dispatch = new RunDispatchState(pane);
            TmuxWaitChannel? completionWait = null;
            bool completionAuthenticated = false;
            try
            {
                await sequence.MutateAsync(
                        () => SendRunPayloadAsync(
                            server,
                            route,
                            command,
                            token,
                            suppressHistory,
                            statusMarkerLifetime,
                            hooks,
                            dispatch,
                            cancellationToken))
                    .ConfigureAwait(false);
                pane = dispatch.Pane;
                completionWait = server.OpenWaitChannel(token.Channel);
                TmuxWaitChannel opened = completionWait;
                RunEnd end = await sequence.ObserveAsync(() => AwaitRunEndAsync(
                        server,
                        pane,
                        token,
                        reservation.DaemonProcessStart,
                        opened,
                        budget,
                        hooks.Progress,
                        elapsed,
                        cancellationToken))
                    .ConfigureAwait(false);
                bool timedOut = end == RunEnd.TimedOut;
                bool paneExited = end == RunEnd.PaneExited;

                elapsed.Stop();
                int? status = null;
                if (end == RunEnd.Completed)
                {
                    status = await sequence
                        .ObserveAsync(() => ReadStatusAsync(pane, token, cancellationToken))
                        .ConfigureAwait(false)
                        ?? throw new LibTmuxException(
                            "The command completed, but tmux did not return its authenticated "
                            + "exit status. Do not retry it; inspect the pane instead.",
                            TmuxDispatchState.Dispatched);
                }

                completionAuthenticated = end == RunEnd.Completed;
                PaneRead? read = paneExited
                    ? await ReadAfterExitAsync(pane, baseline, fail, cancellationToken).ConfigureAwait(false)
                    : await sequence
                        .ObserveAsync(() => PaneReader.ReadSinceAsync(pane, baseline, fail, cancellationToken))
                        .ConfigureAwait(false);
                if (read is null)
                {
                    // tmux closed the pane with its program, so nothing is
                    // left to read; the payload was sent, so it may have run.
                    return new PaneRunOutcome(pane, null, false, [], false, false, false, elapsed.Elapsed, PaneExited: true);
                }

                // The wrapper prints the begin marker itself. Its absence
                // leaves startup unobserved; a lost grid anchor cannot prove
                // that the shell never ran the payload.
                bool started = read.Lines.Any(line =>
                    line.TrimStart().StartsWith(token.BeginMarker, StringComparison.Ordinal));
                IReadOnlyList<string> output = sequence.Observe(() => PaneText.Scrub(
                    PaneText.BeforeEndMarker(
                        PaneText.AfterBeginMarker(read.Lines, token.BeginMarker, pane.Width),
                        token.EndMarker,
                        pane.Width),
                    pane.Width));
                return new PaneRunOutcome(
                    pane,
                    status,
                    timedOut,
                    Array.AsReadOnly(output.ToArray()),
                    read.LinesMissed,
                    read.AnchorLost,
                    started,
                    elapsed.Elapsed,
                    paneExited);
            }
            catch (TmuxOperationCanceledException error)
                when (dispatch.PayloadMayHaveReachedTmux && error.CommandMayHaveExecuted)
            {
                // Raised as a failure, not a cancellation: a cancelled task
                // reaches Task.Wait and Async.AwaitTask callers as a bare
                // TaskCanceledException that no longer says the command may run.
                throw new LibTmuxException(
                    "The command may have reached tmux before cancellation. Do not retry "
                    + "until you inspect the pane.",
                    TmuxDispatchState.Unknown,
                    error);
            }
            finally
            {
                elapsed.Stop();
                if (dispatch.PayloadMayHaveReachedTmux && !completionAuthenticated)
                {
                    reservation.SetDirectory(dispatch.Directory);
                    RetainRunUntilCompletion(
                        server,
                        completionWait,
                        dispatch.Pane,
                        token,
                        reservation,
                        hooks.FollowLimit ?? DefaultFollowLimit);
                    completionOwnershipTransferred = true;
                }
                else
                {
                    if (completionWait is not null)
                    {
                        await completionWait.DisposeAsync().ConfigureAwait(false);
                    }

                    if (dispatch.PayloadMayHaveReachedTmux)
                    {
                        await CleanupStatusMarkerAsync(dispatch.Pane, token).ConfigureAwait(false);
                    }

                    DeleteRunDirectory(dispatch.Directory);
                }
            }
        }
        finally
        {
            if (!completionOwnershipTransferred)
            {
                if (reservation is null)
                {
                    hooks.Completed?.Invoke();
                }
                else
                {
                    reservation.Release();
                }
            }
        }
    }

    private static async Task<RunReservation> ReserveAsync(
        Pane pane,
        PaneRunRoute route,
        Action? completed,
        Action? unresolved,
        CancellationToken cancellationToken)
    {
        var next = new RunReservation(pane, route.Identity, completed, unresolved);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (ActiveRuns.TryAdd(route.Identity, next))
            {
                return next;
            }

            if (ActiveRuns.TryGetValue(route.Identity, out RunReservation? previous)
                && !await previous.TryReconcileAsync(cancellationToken).ConfigureAwait(false))
            {
                break;
            }
        }

        throw new LibTmuxException(
            $"Pane {pane.Id} already has a command running or an unverified command in this process. "
            + "Inspect the pane before retrying.");
    }

    /// <summary>Authenticates a retained run before another input tool takes this pane.</summary>
    internal static Task<bool> TryReconcilePendingAsync(Pane pane, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pane);
        cancellationToken.ThrowIfCancellationRequested();
        PaneRunIdentity identity = new(pane.Generation, pane.Id);
        return ActiveRuns.TryGetValue(identity, out RunReservation? pending)
            ? pending.TryReconcileAsync(cancellationToken)
            : Task.FromResult(true);
    }

    private sealed class RunReservation(
        Pane pane,
        PaneRunIdentity identity,
        Action? completed,
        Action? unresolved)
    {
        private readonly object _gate = new();
        private readonly long? _daemonProcessStart = CaptureDaemonProcessStart(pane.Generation);
        private RunToken? _token;
        private string? _directory;
        private int _released;
        private int _reconciliationAllowed;

        internal long? DaemonProcessStart => _daemonProcessStart;

        internal bool IsReleased => Volatile.Read(ref _released) != 0;

        internal void NotifyUnresolved()
        {
            if (Interlocked.Exchange(ref _reconciliationAllowed, 1) == 0)
            {
                unresolved?.Invoke();
            }
        }

        internal void SetToken(RunToken token)
        {
            lock (_gate)
            {
                _token = token;
            }
        }

        internal void SetDirectory(string? directory)
        {
            lock (_gate)
            {
                if (_released == 0)
                {
                    _directory = directory;
                    return;
                }
            }

            DeleteRunDirectory(directory);
        }

        internal async Task<bool> TryReconcileAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _released) != 0)
            {
                return true;
            }

            // A completed option can appear before the current caller has
            // captured output. Input must not be admitted during that window.
            if (Volatile.Read(ref _reconciliationAllowed) == 0)
            {
                return false;
            }

            RunToken? token;
            lock (_gate)
            {
                token = _token;
            }

            if (token is null)
            {
                return false;
            }

            RetainedRunObservation observation = await ObserveRetainedRunAsync(
                    pane.Server, pane, token.Value, _daemonProcessStart, cancellationToken)
                .ConfigureAwait(false);
            return await CompleteAsync(observation).ConfigureAwait(false);
        }

        internal async Task<bool> CompleteAsync(RetainedRunObservation observation)
        {
            if (observation is not (RetainedRunObservation.Completed
                or RetainedRunObservation.PaneEnded
                or RetainedRunObservation.ServerEnded))
            {
                return false;
            }

            RunToken? token;
            lock (_gate)
            {
                token = _token;
            }

            Release();
            if (observation == RetainedRunObservation.Completed && token is RunToken known)
            {
                await CleanupStatusMarkerAsync(pane, known).ConfigureAwait(false);
            }

            return true;
        }

        internal void Release()
        {
            string? directory;
            lock (_gate)
            {
                if (_released != 0)
                {
                    return;
                }

                _released = 1;
                directory = _directory;
            }

            DeleteRunDirectory(directory);
            _ = ActiveRuns.TryRemove(new KeyValuePair<PaneRunIdentity, RunReservation>(identity, this));
            completed?.Invoke();
        }
    }

    /// <summary>Quotes a word so a POSIX shell reads it as exactly that word.</summary>
    /// <remarks>Single quotes end every special meaning except their own.</remarks>
    internal static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    internal static void ValidateRunCommand(string command, int maximumBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        if (command.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("The command cannot contain NUL.", nameof(command));
        }

        if (command.Length > maximumBytes || Encoding.UTF8.GetByteCount(command) > maximumBytes)
        {
            throw new ArgumentException(
                $"The command exceeds {maximumBytes} UTF-8 bytes. Run a script file instead.",
                nameof(command));
        }
    }

    private static async Task<Pane> RequireSingleWritableShellAsync(
        Pane pane,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<Pane> all = await pane.Server.GetPanesAsync(cancellationToken)
            .ConfigureAwait(false);
        Pane[] matches = [.. all.Where(candidate => candidate.Id == pane.Id)];
        if (matches.Length == 0)
        {
            throw new TmuxObjectNotFoundException(
                $"tmux no longer has pane '{pane.Id}'.", pane.Id.ToString());
        }

        Pane current = matches[0];
        if (current.Generation != pane.Generation)
        {
            throw new TmuxPaneException(
                $"Pane {pane.Id} belongs to a different server generation.",
                pane.Id,
                TmuxDispatchState.NotDispatched);
        }

        static string Field(Pane value, string name) =>
            value.RawFormatFields.TryGetValue(name, out string? raw) && raw is not null
                ? raw
                : throw new TmuxPaneException(
                    $"Pane {value.Id} has no authenticated {name} field.",
                    value.Id,
                    TmuxDispatchState.NotDispatched);
        static bool Flag(Pane value, string name) => Field(value, name) switch
        {
            "0" => false,
            "1" => true,
            _ => throw new TmuxPaneException(
                $"Pane {value.Id} has a malformed {name} field.",
                value.Id,
                TmuxDispatchState.NotDispatched),
        };
        if (Flag(current, "pane_dead") || Flag(current, "pane_input_off")
            || Field(current, "pane_in_mode") != "0")
        {
            throw new TmuxPaneException(
                $"Pane {current.Id} is dead, input-disabled, or in a mode that cannot run a shell command.",
                current.Id,
                TmuxDispatchState.NotDispatched);
        }

        string window = Field(current, "window_id");
        if (Flag(current, "pane_synchronized")
            && all.Where(candidate => Field(candidate, "window_id") == window
                    && Flag(candidate, "pane_synchronized"))
                .Select(candidate => candidate.Id)
                .Distinct()
                .Take(2)
                .Count() != 1)
        {
            throw new TmuxPaneException(
                $"Pane {current.Id} belongs to a synchronized window with multiple effective panes.",
                current.Id,
                TmuxDispatchState.NotDispatched);
        }

        string shell = Path.GetFileName(Field(current, "pane_current_command")).TrimStart('-');
        if (!PosixShells.Contains(shell))
        {
            throw new TmuxPaneException(
                $"Pane {current.Id} is running '{shell}', not a POSIX-compatible shell.",
                current.Id,
                TmuxDispatchState.NotDispatched);
        }

        return current;
    }

    /// <summary>Names the private channel and option one run uses.</summary>
    /// <param name="Id">What makes this run's names unique.</param>
    /// <remarks>
    /// Unique per run so two commands in the same pane cannot answer each
    /// other's rendezvous, which would report one command's exit status for
    /// the other's work.
    /// </remarks>
    internal readonly record struct RunToken(string Id)
    {
        /// <summary>Gets the wait-for channel this run signals.</summary>
        internal string Channel => $"lt_r_{Id}";

        /// <summary>Gets the pane option this run leaves its exit status in.</summary>
        internal string StatusOption => $"@lt_s_{Id}";

        /// <summary>Gets the line the command's own output begins after.</summary>
        /// <remarks>
        /// Never written into the payload whole: it is assembled there from two
        /// halves, so no substring or whitespace split of what was pasted can
        /// manufacture it. Only running the payload prints it. A pane held by
        /// awk printing its last field otherwise re-emitted the marker as a
        /// line of its own, and a run that never started reported that it had.
        /// </remarks>
        internal string BeginMarker => $"lt_b_{Id}";

        /// <summary>Gets the marker's first half, as the payload spells it.</summary>
        internal string BeginHead => $"lt_b_{Id[..5]}";

        /// <summary>Gets either marker's second half, as the payload spells it.</summary>
        internal string MarkerTail => Id[5..];

        /// <summary>Gets the line the command's own output ends before.</summary>
        /// <remarks>
        /// The right bound of the output window. Without it a run reported
        /// whatever the shell drew after the command — its next prompt, which
        /// wraps into two rows once it is wider than the pane — as output the
        /// command printed. Assembled from halves for the same reason as
        /// <see cref="BeginMarker" />.
        /// </remarks>
        internal string EndMarker => $"lt_e_{Id}";

        /// <summary>Gets the end marker's first half, as the payload spells it.</summary>
        internal string EndHead => $"lt_e_{Id[..5]}";

        /// <summary>Mints a token nothing else is using.</summary>
        /// <returns>The token.</returns>
        internal static RunToken Create()
        {
            RunToken token = new(Guid.NewGuid().ToString("N"));
            PaneText.Remember(token.Id);
            return token;
        }
    }

    internal sealed class RunDispatchState(Pane pane)
    {
        internal Pane Pane { get; set; } = pane;

        internal string? Directory { get; set; }

        internal bool PayloadMayHaveReachedTmux { get; set; }
    }

    internal static async Task SendRunPayloadAsync(
        Server server,
        PaneRunRoute route,
        string command,
        RunToken token,
        bool suppressHistory,
        TimeSpan statusMarkerLifetime,
        PaneRunHooks hooks,
        RunDispatchState dispatch,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            statusMarkerLifetime,
            TimeSpan.Zero);
        string statusCommand = route.CommandLine(
            "set-option",
            "-p",
            "-t",
            dispatch.Pane.Id.ToString(),
            token.StatusOption);
        string signalCommand = route.CommandLine("wait-for", "-S", token.Channel);
        string unsetStatusCommand = route.CommandLine(
            "set-option",
            "-p",
            "-u",
            "-q",
            "-t",
            dispatch.Pane.Id.ToString(),
            token.StatusOption);
        string cleanupDelay = ((long)Math.Ceiling(statusMarkerLifetime.TotalSeconds))
            .ToString(CultureInfo.InvariantCulture);
        string scheduleCleanupCommand = route.CommandLine(
            "run-shell",
            "-b",
            "-d",
            cleanupDelay,
            unsetStatusCommand);
        string buffer = $"libtmux_run_{Guid.NewGuid():N}"[..24];
        Exception? primaryFailure = null;
        bool bufferMayExist = false;
        try
        {
            dispatch.Directory = Directory
                .CreateTempSubdirectory($"libtmux-run-{token.Id}-")
                .FullName;
            File.SetUnixFileMode(
                dispatch.Directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            string commandPath = Path.Combine(dispatch.Directory, "command");
            string trapPath = Path.Combine(dispatch.Directory, "traps");
            string scriptPath = Path.Combine(dispatch.Directory, "run");
            await WritePrivateFileAsync(commandPath, command + "\n", cancellationToken)
                .ConfigureAwait(false);
            await WritePrivateFileAsync(trapPath, string.Empty, cancellationToken)
                .ConfigureAwait(false);
            string script = BuildRunWrapper(
                token,
                commandPath,
                trapPath,
                statusCommand,
                scheduleCleanupCommand,
                signalCommand);
            await WritePrivateFileAsync(scriptPath, script, cancellationToken)
                .ConfigureAwait(false);
            string payload = string.Concat(
                suppressHistory ? " " : string.Empty,
                ". ",
                ShellQuote(scriptPath),
                "\n");

            try
            {
                await server.Buffers.SetAsync(payload, buffer, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                bufferMayExist = true;
            }
            catch (TmuxOperationCanceledException error)
            {
                bufferMayExist = error.CommandMayHaveExecuted;
                throw;
            }
            catch (LibTmuxException error)
            {
                bufferMayExist = error.Dispatch != TmuxDispatchState.NotDispatched;
                throw;
            }

            dispatch.Pane = hooks.DispatchPreflight is null
                ? await RequireSingleWritableShellAsync(dispatch.Pane, cancellationToken)
                    .ConfigureAwait(false)
                : await hooks.DispatchPreflight(cancellationToken).ConfigureAwait(false);
            route.RequireSame(PaneRunRoute.From(dispatch.Pane));

            // Recorded before dispatch, so a concurrent wait never sees the
            // sourcing line's own echo before a record that discounts it.
            (Action Rollback, Action Settle) note = hooks.NoteDispatch?.Invoke(dispatch.Pane, payload)
                ?? (Nothing, Nothing);
            try
            {
                await dispatch.Pane.PasteBufferAsync(
                        new PasteBufferRequest { Name = buffer, DeleteAfter = true, Bracketed = false },
                        cancellationToken)
                    .ConfigureAwait(false);
                dispatch.PayloadMayHaveReachedTmux = true;
            }
            catch (TmuxOperationCanceledException error)
            {
                dispatch.PayloadMayHaveReachedTmux = error.CommandMayHaveExecuted;
                if (!dispatch.PayloadMayHaveReachedTmux)
                {
                    note.Rollback();
                }

                throw;
            }
            catch (LibTmuxException error)
            {
                dispatch.PayloadMayHaveReachedTmux =
                    error.Dispatch != TmuxDispatchState.NotDispatched;
                if (!dispatch.PayloadMayHaveReachedTmux)
                {
                    note.Rollback();
                }

                throw;
            }

            note.Settle();
            bufferMayExist = false;
        }
        catch (Exception error)
        {
            primaryFailure = error;
            throw;
        }
        finally
        {
            if (bufferMayExist)
            {
                await (hooks.CleanupBuffer ?? DeleteBufferAsync)(server, buffer, primaryFailure)
                    .ConfigureAwait(false);
            }

            if (!dispatch.PayloadMayHaveReachedTmux)
            {
                DeleteRunDirectory(dispatch.Directory);
            }
        }
    }

    private static void RetainRunUntilCompletion(
        Server server,
        TmuxWaitChannel? wait,
        Pane pane,
        RunToken token,
        RunReservation reservation,
        TimeSpan followLimit) =>
        _ = FollowRetainedRunAsync(
            server,
            wait,
            pane,
            token,
            reservation,
            followLimit);

    // Nothing awaits the follow, so nothing it throws may escape it.
    private static async Task FollowRetainedRunAsync(
        Server server,
        TmuxWaitChannel? wait,
        Pane pane,
        RunToken token,
        RunReservation reservation,
        TimeSpan followLimit)
    {
        try
        {
            await CompleteRetainedRunAsync(
                    server,
                    wait,
                    pane,
                    token,
                    reservation,
                    followLimit)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // An observation failure does not authenticate completion. The
            // reservation can be reconciled by the next same-pane run.
            await DisposeRetainedWaitAsync(wait).ConfigureAwait(false);
            reservation.NotifyUnresolved();
        }
    }

    private static async Task CompleteRetainedRunAsync(
        Server server,
        TmuxWaitChannel? wait,
        Pane pane,
        RunToken token,
        RunReservation reservation,
        TimeSpan followLimit)
    {
        Stopwatch followed = Stopwatch.StartNew();
        TimeSpan probe = RetainedRunFirstProbe;
        Task? waitTask = wait?.WaitUntilSignalledAsync(CancellationToken.None);
        try
        {
            while (true)
            {
                if (reservation.IsReleased)
                {
                    await DisposeRetainedWaitAsync(wait).ConfigureAwait(false);
                    return;
                }

                bool intervalElapsed = false;
                if (waitTask is { IsCompleted: false })
                {
                    Task interval = Task.Delay(probe);
                    intervalElapsed = await Task.WhenAny(waitTask, interval).ConfigureAwait(false)
                        == interval;
                }

                if (waitTask is { IsCompleted: true })
                {
                    try
                    {
                        await waitTask.ConfigureAwait(false);
                    }
                    catch (Exception error) when (WaitCanBeReplaced(error))
                    {
                        await DisposeRetainedWaitAsync(wait).ConfigureAwait(false);
                        wait = null;
                    }
                    catch (Exception)
                    {
                        // An uncertain waiter may still own a tmux registration.
                        // Keep it and use authenticated status and pane probes.
                    }

                    waitTask = null;
                }

                using var probeTimeout = new CancellationTokenSource(StatusCleanupTimeout);
                RetainedRunObservation observation = await ObserveRetainedRunAsync(
                        server,
                        pane,
                        token,
                        reservation.DaemonProcessStart,
                        probeTimeout.Token)
                    .ConfigureAwait(false);
                if (observation is RetainedRunObservation.Completed
                    or RetainedRunObservation.PaneEnded
                    or RetainedRunObservation.ServerEnded)
                {
                    await reservation.CompleteAsync(observation).ConfigureAwait(false);
                    await DisposeRetainedWaitAsync(wait).ConfigureAwait(false);
                    return;
                }

                // Stop polling, but do not claim completion or release the lease.
                // A later same-pane run probes the authenticated status and pane.
                if (followed.Elapsed >= followLimit)
                {
                    await DisposeRetainedWaitAsync(wait).ConfigureAwait(false);
                    reservation.NotifyUnresolved();
                    return;
                }

                if (wait is null)
                {
                    try
                    {
                        wait = server.OpenWaitChannel(token.Channel);
                        waitTask = wait.WaitUntilSignalledAsync(CancellationToken.None);
                    }
                    catch (Exception)
                    {
                        wait = null;
                        waitTask = null;
                    }
                }

                if (!intervalElapsed)
                {
                    await Task.Delay(probe).ConfigureAwait(false);
                }

                probe = probe * 2 < RetainedRunLongestProbe ? probe * 2 : RetainedRunLongestProbe;
            }
        }
        finally
        {
            await DisposeRetainedWaitAsync(wait).ConfigureAwait(false);
        }
    }

    private static bool WaitCanBeReplaced(Exception error) =>
        error is TmuxCommandException
        || error is LibTmuxException { Dispatch: TmuxDispatchState.NotDispatched };

    private static async Task DisposeRetainedWaitAsync(TmuxWaitChannel? wait)
    {
        if (wait is null)
        {
            return;
        }

        try
        {
            await wait.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Either no waiter registered, or an authenticated terminal
            // observation owns the release decision.
        }
    }

    private static async Task<RetainedRunObservation> ObserveRetainedRunAsync(
        Server server,
        Pane pane,
        RunToken token,
        long? daemonProcessStart,
        CancellationToken cancellationToken)
    {
        try
        {
            if (await ReadStatusAsync(pane, token, cancellationToken)
                    .ConfigureAwait(false) is not null)
            {
                return RetainedRunObservation.Completed;
            }
        }
        catch (Exception error) when (ProvesServerEnded(
            error,
            pane.Generation,
            daemonProcessStart))
        {
            return RetainedRunObservation.ServerEnded;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
        }

        try
        {
            // Strict materialization authenticates every row against the
            // captured generation before absence or pane_dead is interpreted.
            IReadOnlyList<Pane> panes = await server
                .GetPanesAsync(cancellationToken)
                .ConfigureAwait(false);
            Pane[] matches =
            [
                .. panes.Where(candidate => candidate.Id == pane.Id),
            ];
            if (matches.Length == 0)
            {
                return RetainedRunObservation.PaneEnded;
            }

            bool? dead = null;
            foreach (Pane match in matches)
            {
                if (!match.RawFormatFields.TryGetValue("pane_dead", out string? value)
                    || value is not ("0" or "1"))
                {
                    return RetainedRunObservation.Unknown;
                }

                bool current = value == "1";
                if (dead is bool prior && prior != current)
                {
                    return RetainedRunObservation.Unknown;
                }

                dead = current;
            }

            return dead == true
                ? RetainedRunObservation.PaneEnded
                : RetainedRunObservation.Active;
        }
        catch (Exception error) when (ProvesServerEnded(
            error,
            pane.Generation,
            daemonProcessStart))
        {
            return RetainedRunObservation.ServerEnded;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return RetainedRunObservation.Unknown;
        }
    }

    private static bool ProvesServerEnded(
        Exception error,
        ServerGeneration generation,
        long? daemonProcessStart) =>
        error is StaleServerGenerationException { Actual: not null }
        || error is TmuxCommandException command
            && ReportsMissingDaemon(command)
            && CapturedDaemonEnded(generation, daemonProcessStart);

    private static bool ReportsMissingDaemon(TmuxCommandException error) =>
        error.Result.StandardErrorLines.Any(static line =>
            line.Contains("no server running", StringComparison.Ordinal)
            || line.Contains("error connecting to", StringComparison.Ordinal)
            || line.Contains("No such file or directory", StringComparison.Ordinal));

    private static long? CaptureDaemonProcessStart(ServerGeneration generation)
    {
        try
        {
            using Process process = Process.GetProcessById(generation.ProcessId);
            return process.HasExited ? null : process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception error) when (error is ArgumentException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception
            or NotSupportedException)
        {
            return null;
        }
    }

    private static bool CapturedDaemonEnded(
        ServerGeneration generation,
        long? daemonProcessStart)
    {
        if (daemonProcessStart is not long expected)
        {
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById(generation.ProcessId);
            return process.HasExited || process.StartTime.ToUniversalTime().Ticks != expected;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception
            or NotSupportedException)
        {
            return false;
        }
    }

    private enum RetainedRunObservation
    {
        Unknown,
        Active,
        Completed,
        PaneEnded,
        ServerEnded,
    }

    private static string BuildRunWrapper(
        RunToken token,
        string commandPath,
        string trapPath,
        string statusCommand,
        string scheduleCleanupCommand,
        string signalCommand)
    {
        string flags = $"__lt_flags_{token.Id}";
        string trapStatus = $"__lt_trap_status_{token.Id}";
        string trapBytes = $"__lt_trap_bytes_{token.Id}";
        string status = $"__lt_status_{token.Id}";
        string command = ShellQuote(commandPath);
        string traps = ShellQuote(trapPath);
        string forget = $"\\unset {flags} {trapStatus} {trapBytes} {status}";
        string RunWith(string options) =>
            $"    ( {forget}; {options}; . {traps} )\n";
        return string.Concat(
            "(\n",
            flags,
            "=$-\n",
            "\\set +x\n",
            "\\set +e\n",
            trapStatus,
            "=0\n",
            "\\umask 077\n",
            "if : >| ",
            traps,
            "; then\n",
            "  case \"${BASH_VERSION-}:${ZSH_VERSION-}\" in\n",
            "    ?*:*) if \\trap -p ERR DEBUG >| ",
            traps,
            "; then :; else ",
            trapStatus,
            "=125; fi; \\trap - ERR DEBUG ;;\n",
            "    :?*) if \\trap >| ",
            traps,
            "; then :; else ",
            trapStatus,
            "=125; fi; \\trap - ERR DEBUG ;;\n",
            "  esac\n",
            "else ",
            trapStatus,
            "=125\n",
            "fi\n",
            "if command test \"$",
            trapStatus,
            "\" -eq 0; then\n",
            "  if ",
            trapBytes,
            "=$(command wc -c < ",
            traps,
            ") && command test \"$",
            trapBytes,
            "\" -le ",
            MaximumInheritedTrapBytes.ToString(CultureInfo.InvariantCulture),
            " 2>/dev/null; then :; else ",
            trapStatus,
            "=125; : >| ",
            traps,
            "; fi\n",
            "fi\n",
            "if command test \"$",
            trapStatus,
            "\" -eq 0; then\n",
            "  { command printf '\\n'; command cat ",
            command,
            "; } >> ",
            traps,
            " || ",
            trapStatus,
            "=125\n",
            "fi\n",
            "command printf '%s%s\\n' '",
            token.BeginHead,
            "' '",
            token.MarkerTail,
            "'\n",
            "if command test \"$",
            trapStatus,
            "\" -ne 0; then\n",
            "  ",
            status,
            "=125\n",
            "else\n",
            "  case \"$",
            flags,
            "\" in\n",
            "    *e*x*|*x*e*)\n",
            RunWith("\\set -e; \\set -x"),
            "    ;;\n",
            "    *e*)\n",
            RunWith("\\set -e; \\set +x"),
            "    ;;\n",
            "    *x*)\n",
            RunWith("\\set +e; \\set -x"),
            "    ;;\n",
            "    *)\n",
            RunWith("\\set +e; \\set +x"),
            "    ;;\n",
            "  esac\n",
            "  ",
            status,
            "=$?\n",
            "fi\n",
            "command printf '%s%s\\n' '",
            token.EndHead,
            "' '",
            token.MarkerTail,
            "'\n",
            statusCommand,
            " \"$",
            status,
            "\"\n",
            scheduleCleanupCommand,
            "\n",
            signalCommand,
            "\n\\exit 0\n",
            ")\n");
    }

    private static async Task WritePrivateFileAsync(
        string path,
        string contents,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            new FileStreamOptions
            {
                Access = FileAccess.Write,
                Mode = FileMode.CreateNew,
                Options = FileOptions.Asynchronous,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
        byte[] bytes = Encoding.UTF8.GetBytes(contents);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void DeleteRunDirectory(string? directory)
    {
        if (directory is null)
        {
            return;
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private enum RunEnd
    {
        Completed,
        TimedOut,
        PaneExited,
    }

    /// <summary>Waits for the run's signal, or for the run to be seen to end without one.</summary>
    /// <remarks>
    /// A program that exits mid-run never signals, and a wrapper hung up with
    /// its shell can record the status but die before signalling. So the run
    /// is checked on a lengthening interval rather than waited on to the end
    /// of the budget. The check is the one that follows a timed-out run, and
    /// the open wait stays owned for that follower to withdraw.
    /// </remarks>
    private static async Task<RunEnd> AwaitRunEndAsync(
        Server server,
        Pane pane,
        RunToken token,
        long? daemonProcessStart,
        TmuxWaitChannel completionWait,
        TimeSpan budget,
        Action<TimeSpan>? progress,
        Stopwatch elapsed,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<bool> signalled = TickWhileAsync(
            completionWait.WaitAsync(budget, attempt.Token),
            progress,
            elapsed,
            attempt.Token);
        Task<RunEnd?> watched = WatchRunAsync(server, pane, token, daemonProcessStart, attempt.Token);
        await Task.WhenAny(signalled, watched).ConfigureAwait(false);
        if (!watched.IsCompletedSuccessfully || watched.Result is not RunEnd seen)
        {
            // The signal came first, or the watch gave up: the signal decides.
            try
            {
                return await signalled.ConfigureAwait(false) ? RunEnd.Completed : RunEnd.TimedOut;
            }
            finally
            {
                await attempt.CancelAsync().ConfigureAwait(false);
            }
        }

        await attempt.CancelAsync().ConfigureAwait(false);
        try
        {
            if (await signalled.ConfigureAwait(false))
            {
                return RunEnd.Completed;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The attempt was abandoned above for the end the watch saw; the
            // open wait stays owned, for the follower to withdraw.
        }

        return seen;
    }

    private static async Task<RunEnd?> WatchRunAsync(
        Server server,
        Pane pane,
        RunToken token,
        long? daemonProcessStart,
        CancellationToken cancellationToken)
    {
        TimeSpan probe = RunningPaneFirstProbe;
        while (true)
        {
            await Task.Delay(probe, cancellationToken).ConfigureAwait(false);
            switch (await ObserveRetainedRunAsync(
                server, pane, token, daemonProcessStart, cancellationToken).ConfigureAwait(false))
            {
                case RetainedRunObservation.Completed:
                    return RunEnd.Completed;

                case RetainedRunObservation.PaneEnded:
                    return RunEnd.PaneExited;

                // The run's own wait fails on a server that has gone.
                case RetainedRunObservation.ServerEnded:
                    return null;
            }

            probe = probe * 2 < RetainedRunLongestProbe ? probe * 2 : RetainedRunLongestProbe;
        }
    }

    /// <summary>Reads what a pane whose program exited still shows, or null when tmux closed it.</summary>
    private static async Task<PaneRead?> ReadAfterExitAsync(
        Pane pane,
        PaneCursor baseline,
        Func<PaneReadFailure, Pane, Exception> fail,
        CancellationToken cancellationToken)
    {
        try
        {
            return await PaneReader.ReadSinceAsync(pane, baseline, fail, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<bool> TickWhileAsync(
        Task<bool> waiting,
        Action<TimeSpan>? progress,
        Stopwatch elapsed,
        CancellationToken cancellationToken)
    {
        if (progress is null)
        {
            return await waiting.ConfigureAwait(false);
        }

        while (true)
        {
            Task beat = Task.Delay(ProgressInterval, cancellationToken);
            if (await Task.WhenAny(waiting, beat).ConfigureAwait(false) == waiting || beat.IsCanceled)
            {
                return await waiting.ConfigureAwait(false);
            }

            progress(elapsed.Elapsed);
        }
    }

    private static async Task DeleteBufferAsync(Server server, string buffer, Exception? primaryFailure)
    {
        using var cleanup = new CancellationTokenSource(StatusCleanupTimeout);
        try
        {
            await server.Buffers.DeleteAsync(buffer, cleanup.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The buffer holds only the sourcing line, not the command.
        }
    }

    internal static async Task<int?> ReadStatusAsync(
        Pane pane,
        RunToken token,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TmuxOption> options = await pane.Options
            .GetAsync(new GetOptionRequest(token.StatusOption) { Quiet = true }, cancellationToken)
            .ConfigureAwait(false);

        return options.Count > 0
            && int.TryParse(
                options[0].Value.Raw,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int parsed)
                ? parsed
                : null;
    }

    internal static async Task CleanupStatusMarkerAsync(Pane pane, RunToken token)
    {
        using var cleanup = new CancellationTokenSource(StatusCleanupTimeout);
        try
        {
            await pane.Options
                .UnsetAsync(
                    new UnsetOptionRequest(token.StatusOption) { Quiet = true },
                    cleanup.Token)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The payload also schedules a bounded cleanup inside tmux.
        }
    }
}
