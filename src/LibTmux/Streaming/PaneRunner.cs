using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;

namespace LibTmux.Internal;

/// <summary>Names the tmux executable and socket a command run inside a pane must reach.</summary>
/// <param name="TmuxBinaryPath">The absolute tmux executable.</param>
/// <param name="SocketPath">The absolute socket of the server that owns the pane.</param>
/// <param name="PaneId">The pane the route was resolved for.</param>
/// <remarks>
/// A command run from inside a pane inherits <c>TMUX</c> and would reach the
/// ambient server, which is not necessarily the one being driven, and a bare
/// <c>tmux</c> resolves through the pane shell's own <c>PATH</c>.
/// </remarks>
[UnsupportedOSPlatform("windows")]
internal readonly record struct PaneRunRoute(string TmuxBinaryPath, string SocketPath, PaneId PaneId)
{
    /// <summary>Resolves the route from the server's tmux and the pane's socket.</summary>
    /// <exception cref="TmuxPaneException">The executable or socket cannot be named absolutely.</exception>
    internal static PaneRunRoute From(Pane pane)
    {
        ArgumentNullException.ThrowIfNull(pane);
        string binary = pane.Server.ConnectionOptions.TmuxBinaryPath;
        string? resolved = Path.IsPathFullyQualified(binary)
            ? binary
            : (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(directory => Path.Combine(directory, binary))
                .FirstOrDefault(candidate => Path.IsPathFullyQualified(candidate) && File.Exists(candidate));
        if (resolved is null
            || !pane.RawFormatFields.TryGetValue("socket_path", out string? socket)
            || socket is null
            || !Path.IsPathFullyQualified(socket))
        {
            throw new TmuxPaneException(
                $"Pane {pane.Id} cannot run a command: the tmux executable '{binary}' or the pane's socket has no absolute path.",
                pane.Id);
        }

        return new PaneRunRoute(resolved, socket, pane.Id);
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

/// <summary>Lets a caller take part in a run without changing what it does.</summary>
internal sealed record PaneRunHooks
{
    /// <summary>Gets a final check, run just before the payload is pasted, that returns the pane to paste into.</summary>
    internal Func<CancellationToken, Task<Pane>>? DispatchPreflight { get; init; }

    /// <summary>Gets a callback recording the payload about to be pasted, returning how to undo or keep the record.</summary>
    internal Func<Pane, string, (Action Rollback, Action Settle)>? NoteDispatch { get; init; }

    /// <summary>Gets a callback told once the run is known to have finished, however long after the call that is.</summary>
    internal Action? Completed { get; init; }

    /// <summary>
    /// Gets how long to follow a run that outlasts its wait; after that it is
    /// treated as finished. Null follows it until the run, pane or server ends.
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
/// <param name="Started">Whether the shell ran the payload at all.</param>
/// <param name="Elapsed">How long the run took, or how long it was waited for.</param>
internal sealed record PaneRunOutcome(
    Pane Pane,
    int? ExitStatus,
    bool TimedOut,
    IReadOnlyList<string> Output,
    bool LinesMissed,
    bool AnchorLost,
    bool Started,
    TimeSpan Elapsed);

/// <summary>Runs a shell command in a pane and learns its exit status.</summary>
/// <remarks>
/// Completion is not guessed from the screen. The command is written to a
/// private file the pane's shell sources; a wrapper prints begin and end
/// markers around it, stores <c>$?</c> in a private pane option, and signals a
/// private <c>wait-for</c> channel. A run still going when the time allowed ends
/// is followed in the background until it finishes, so its option and files
/// are cleaned up.
/// </remarks>
[UnsupportedOSPlatform("windows")]
internal static class PaneRunner
{
    private const int MaximumInheritedTrapBytes = 64 * 1024;
    private static readonly TimeSpan RetainedRunFirstProbe = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan RetainedRunLongestProbe = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StatusCleanupTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(1);
    private static readonly Action Nothing = static () => { };

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
        bool completionOwnershipTransferred = false;
        try
        {
            if (route.PaneId != pane.Id)
            {
                throw new TmuxPaneException($"The run route belongs to pane {route.PaneId}, not {pane.Id}.", pane.Id);
            }

            long? daemonProcessStart = CaptureDaemonProcessStart(pane.Generation);
            PaneRead baselineRead = await PaneReader.ReadVisibleAsync(pane, null, fail, cancellationToken)
                .ConfigureAwait(false);
            PaneCursor baseline = PaneCursor.Build(pane, baselineRead.State, baselineRead.CursorRows);

            RunToken token = RunToken.Create();
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
                Task<bool> waitAttempt = completionWait.WaitAsync(budget, cancellationToken);
                bool timedOut = !await sequence.ObserveAsync(() => TickWhileAsync(
                        waitAttempt,
                        hooks.Progress,
                        elapsed,
                        cancellationToken))
                    .ConfigureAwait(false);

                elapsed.Stop();
                int? status = timedOut
                    ? null
                    : await sequence
                        .ObserveAsync(() => ReadStatusAsync(pane, token, cancellationToken))
                        .ConfigureAwait(false);
                if (!timedOut && status is null)
                {
                    throw new LibTmuxException(
                        "The command completed, but tmux did not return its authenticated "
                        + "exit status. Do not retry it; inspect the pane instead.",
                        TmuxDispatchState.Dispatched);
                }

                completionAuthenticated = !timedOut;
                PaneRead read = await sequence
                    .ObserveAsync(() => PaneReader.ReadSinceAsync(pane, baseline, fail, cancellationToken))
                    .ConfigureAwait(false);

                // The wrapper prints the begin marker itself, so its absence
                // means the shell never ran the payload: the pane was not at an
                // empty, ready shell prompt.
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
                    output,
                    read.LinesMissed,
                    read.AnchorLost,
                    started,
                    elapsed.Elapsed);
            }
            catch (TmuxOperationCanceledException error)
                when (dispatch.PayloadMayHaveReachedTmux && error.CommandMayHaveExecuted)
            {
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
                    RetainRunUntilCompletion(
                        server,
                        completionWait,
                        dispatch.Pane,
                        token,
                        dispatch.Directory,
                        daemonProcessStart,
                        hooks.Completed,
                        hooks.FollowLimit);
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
                hooks.Completed?.Invoke();
            }
        }
    }

    /// <summary>Quotes a word so a POSIX shell reads it as exactly that word.</summary>
    /// <remarks>Single quotes end every special meaning except their own.</remarks>
    internal static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

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
            RunToken token = new(Guid.NewGuid().ToString("N")[..10]);
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

            if (hooks.DispatchPreflight is not null)
            {
                dispatch.Pane = await hooks.DispatchPreflight(cancellationToken).ConfigureAwait(false);
            }

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
        string? directory,
        long? daemonProcessStart,
        Action? completed,
        TimeSpan? followLimit) =>
        _ = FollowRetainedRunAsync(
            server,
            wait,
            pane,
            token,
            directory,
            daemonProcessStart,
            completed,
            followLimit);

    // Nothing awaits the follow, so nothing it throws may escape it.
    private static async Task FollowRetainedRunAsync(
        Server server,
        TmuxWaitChannel? wait,
        Pane pane,
        RunToken token,
        string? directory,
        long? daemonProcessStart,
        Action? completed,
        TimeSpan? followLimit)
    {
        try
        {
            await CompleteRetainedRunAsync(
                    server,
                    wait,
                    pane,
                    token,
                    directory,
                    daemonProcessStart,
                    completed,
                    followLimit)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            DeleteRunDirectory(directory);
        }
    }

    private static async Task CompleteRetainedRunAsync(
        Server server,
        TmuxWaitChannel? wait,
        Pane pane,
        RunToken token,
        string? directory,
        long? daemonProcessStart,
        Action? completed,
        TimeSpan? followLimit)
    {
        Stopwatch followed = Stopwatch.StartNew();
        TimeSpan probe = RetainedRunFirstProbe;
        Task? waitTask = wait?.WaitUntilSignalledAsync(CancellationToken.None);
        while (true)
        {
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

            RetainedRunObservation observation = await ObserveRetainedRunAsync(
                    server,
                    pane,
                    token,
                    daemonProcessStart)
                .ConfigureAwait(false);
            if (observation is RetainedRunObservation.Completed
                or RetainedRunObservation.PaneEnded
                or RetainedRunObservation.ServerEnded)
            {
                completed?.Invoke();
                await DisposeRetainedWaitAsync(wait).ConfigureAwait(false);
                await CleanupStatusMarkerAsync(pane, token).ConfigureAwait(false);
                DeleteRunDirectory(directory);
                return;
            }

            // A command that never reaches the wrapper's tail, interrupted or
            // endless, sets no status. Past the limit it is left to run, and
            // tmux removes a status it sets later on its own schedule.
            if (followed.Elapsed >= followLimit)
            {
                completed?.Invoke();
                await DisposeRetainedWaitAsync(wait).ConfigureAwait(false);
                DeleteRunDirectory(directory);
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
        long? daemonProcessStart)
    {
        try
        {
            if (await ReadStatusAsync(pane, token, CancellationToken.None)
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
        catch (Exception)
        {
        }

        try
        {
            // Strict materialization authenticates every row against the
            // captured generation before absence or pane_dead is interpreted.
            IReadOnlyList<Pane> panes = await server
                .GetPanesAsync(CancellationToken.None)
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
