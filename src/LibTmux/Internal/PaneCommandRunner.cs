using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;

namespace LibTmux.Internal;

internal sealed record PaneCommandObservation(
    PaneCommandResult Result,
    PaneCommandRunner.RunToken Token,
    Pane Pane);

internal sealed class PaneCommandStatusUnavailableException()
    : LibTmuxException(
        "The command completed, but tmux did not return its authenticated exit status. "
        + "Do not retry it; inspect the pane instead.",
        TmuxDispatchState.Unknown);

[UnsupportedOSPlatform("windows")]
internal readonly record struct PaneCommandRoute(
    string TmuxBinaryPath,
    string SocketPath,
    PaneId PaneId,
    ServerGeneration Generation)
{
    internal PaneCommandIdentity Identity => new(Generation, PaneId);

    internal static PaneCommandRoute From(Pane pane)
    {
        string binary = ResolveExecutable(pane.Server.ConnectionOptions.TmuxBinaryPath);

        if (!pane.RawFormatFields.TryGetValue("socket_path", out string? socket)
            || string.IsNullOrWhiteSpace(socket)
            || !Path.IsPathFullyQualified(socket))
        {
            throw new LibTmuxException(
                $"Pane {pane.Id} has no absolute tmux socket path.");
        }

        return new PaneCommandRoute(binary, socket, pane.Id, pane.Generation);
    }

    private static string ResolveExecutable(string configured)
    {
        static bool Executable(string candidate)
        {
            if (!File.Exists(candidate))
            {
                return false;
            }

            UnixFileMode mode = File.GetUnixFileMode(candidate);
            return (mode & (UnixFileMode.UserExecute
                    | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherExecute)) != 0;
        }

        if (configured.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            string candidate = Path.GetFullPath(configured);
            if (Executable(candidate))
            {
                return candidate;
            }
        }
        else
        {
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
        }

        throw new LibTmuxException(
            $"Could not resolve an executable tmux binary from '{configured}'.");
    }

    internal void RequireSame(PaneCommandRoute final)
    {
        if (this != final)
        {
            throw new LibTmuxException(
                "The pane or its tmux route changed before dispatch. The command was not sent.");
        }
    }
}

internal readonly record struct PaneCommandIdentity(
    ServerGeneration Generation,
    PaneId PaneId);

[UnsupportedOSPlatform("windows")]
internal static class PaneCommandRunner
{
    private const int MaximumInheritedTrapBytes = 64 * 1024;
    private static readonly TimeSpan RetainedRunProbeInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan StatusCleanupTimeout = TimeSpan.FromSeconds(5);
    private static readonly ConcurrentDictionary<PaneCommandIdentity, Guid> ActiveRuns = new();
    private static readonly HashSet<string> PosixShells = new(StringComparer.Ordinal)
    {
        "sh", "ash", "bash", "dash", "ksh", "ksh93", "mksh", "pdksh", "zsh",
    };

    internal static async Task<PaneCommandObservation> RunAsync(
        Pane pane,
        string command,
        TimeSpan timeout,
        bool suppressHistory,
        int maximumBytes,
        TimeSpan markerLifetime,
        Func<CancellationToken, Task<Pane>>? dispatchPreflight,
        Func<Pane, string, Action<bool>>? noteDispatch,
        Action<RunToken>? onTokenCreated,
        Action? onRetentionStarted,
        Action? onRunReleased,
        Action<TimeSpan>? reportProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pane);
        int externalReleased = 0;
        void ReleaseExternal()
        {
            if (Interlocked.Exchange(ref externalReleased, 1) == 0)
            {
                onRunReleased?.Invoke();
            }
        }

        Pane initial;
        PaneCommandRoute route = default;
        Guid reservation = Guid.NewGuid();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(markerLifetime, timeout);
            ValidateRunCommand(command, maximumBytes);
            initial = pane;
            route = PaneCommandRoute.From(initial);
            if (!ActiveRuns.TryAdd(route.Identity, reservation))
            {
                throw new LibTmuxException(
                    $"Pane {initial.Id} already has a command running in this process.");
            }
            if (dispatchPreflight is null)
            {
                Pane current = await RequireSingleWritableShellAsync(pane, cancellationToken)
                    .ConfigureAwait(false);
                route.RequireSame(PaneCommandRoute.From(current));
            }
        }
        catch
        {
            if (route != default)
            {
                _ = ActiveRuns.TryRemove(new KeyValuePair<PaneCommandIdentity, Guid>(
                    route.Identity,
                    reservation));
            }
            ReleaseExternal();
            throw;
        }

        void Release()
        {
            _ = ActiveRuns.TryRemove(new KeyValuePair<PaneCommandIdentity, Guid>(
                route.Identity,
                reservation));
            ReleaseExternal();
        }

        bool ownershipTransferred = false;
        try
        {
            long? daemonProcessStart = CaptureDaemonProcessStart(initial.Generation);
            RunToken token = RunToken.Create();
            onTokenCreated?.Invoke(token);
            Stopwatch elapsed = Stopwatch.StartNew();
            var sequence = new TmuxMutationSequence(
                "The command was sent, but observing its result failed. It may still be "
                + "running or may already have finished; do not retry until you inspect the pane.");
            var dispatch = new RunDispatchState(initial);
            TmuxWaitChannel? completionWait = null;
            bool completionAuthenticated = false;
            try
            {
                await sequence.MutateAsync(() => SendRunPayloadAsync(
                        initial.Server,
                        route,
                        command,
                        token,
                        suppressHistory,
                        markerLifetime,
                        dispatchPreflight ?? (ct => RequireSingleWritableShellAsync(initial, ct)),
                        noteDispatch,
                        dispatch,
                        cancellationToken))
                    .ConfigureAwait(false);
                completionWait = initial.Server.OpenWaitChannel(token.Channel);
                Task<bool> waitAttempt = completionWait.WaitAsync(timeout, cancellationToken);
                bool timedOut = !await sequence.ObserveAsync(() => AwaitWithProgressAsync(
                        waitAttempt,
                        elapsed,
                        timeout,
                        reportProgress,
                        cancellationToken))
                    .ConfigureAwait(false);
                elapsed.Stop();
                int? status = timedOut
                    ? null
                    : await sequence.ObserveAsync(() => ReadStatusAsync(
                            dispatch.Pane,
                            token,
                            cancellationToken))
                        .ConfigureAwait(false);
                if (!timedOut && status is null)
                {
                    throw new PaneCommandStatusUnavailableException();
                }

                completionAuthenticated = !timedOut;
                return new PaneCommandObservation(
                    new PaneCommandResult(
                        dispatch.Pane.Id,
                        status,
                        timedOut,
                        elapsed.Elapsed,
                        timeout),
                    token,
                    dispatch.Pane);
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
                    onRetentionStarted?.Invoke();
                    RetainRunUntilCompletion(
                        initial.Server,
                        completionWait,
                        dispatch.Pane,
                        token,
                        dispatch.Directory,
                        daemonProcessStart,
                        Release);
                    ownershipTransferred = true;
                }
                else
                {
                    if (completionWait is not null)
                    {
                        await completionWait.DisposeAsync().ConfigureAwait(false);
                    }

                    if (dispatch.PayloadMayHaveReachedTmux)
                    {
                        await CleanupStatusMarkerAsync(dispatch.Pane, token)
                            .ConfigureAwait(false);
                    }

                    DeleteRunDirectory(dispatch.Directory);
                }
            }
        }
        finally
        {
            if (!ownershipTransferred)
            {
                Release();
            }
        }
    }

    private static async Task<bool> AwaitWithProgressAsync(
        Task<bool> waiting,
        Stopwatch elapsed,
        TimeSpan timeout,
        Action<TimeSpan>? reportProgress,
        CancellationToken cancellationToken)
    {
        if (reportProgress is null)
        {
            return await waiting.ConfigureAwait(false);
        }

        while (true)
        {
            Task beat = Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            if (await Task.WhenAny(waiting, beat).ConfigureAwait(false) == waiting)
            {
                return await waiting.ConfigureAwait(false);
            }

            reportProgress(elapsed.Elapsed);
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
            throw new LibTmuxException($"Pane {pane.Id} no longer exists.");
        }

        Pane current = matches[0];
        if (current.Generation != pane.Generation)
        {
            throw new LibTmuxException($"Pane {pane.Id} belongs to a different server generation.");
        }

        static string Field(Pane value, string name) =>
            value.RawFormatFields.TryGetValue(name, out string? raw) && raw is not null
                ? raw
                : throw new LibTmuxException(
                    $"Pane {value.Id} has no authenticated {name} field.");
        static bool Flag(Pane value, string name) => Field(value, name) switch
        {
            "0" => false,
            "1" => true,
            _ => throw new LibTmuxException(
                $"Pane {value.Id} has a malformed {name} field."),
        };
        if (Flag(current, "pane_dead") || Flag(current, "pane_input_off")
            || Field(current, "pane_in_mode") != "0")
        {
            throw new LibTmuxException(
                $"Pane {current.Id} is dead, input-disabled, or in a mode that cannot run a shell command.");
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
            throw new LibTmuxException(
                $"Pane {current.Id} belongs to a synchronized window with multiple effective panes.");
        }

        string currentCommand = Field(current, "pane_current_command");
        string shell = Path.GetFileName(currentCommand).TrimStart('-');
        if (!PosixShells.Contains(shell))
        {
            throw new LibTmuxException(
                $"Pane {current.Id} is running '{shell}', not a POSIX-compatible shell.");
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
        internal static RunToken Create() => new(Guid.NewGuid().ToString("N"));
    }

    internal sealed class RunDispatchState(Pane pane)
    {
        internal Pane Pane { get; set; } = pane;

        internal string? Directory { get; set; }

        internal bool PayloadMayHaveReachedTmux { get; set; }
    }

    internal static async Task SendRunPayloadAsync(
        Server server,
        PaneCommandRoute route,
        string command,
        RunToken token,
        bool suppressHistory,
        TimeSpan statusMarkerLifetime,
        Func<CancellationToken, Task<Pane>>? dispatchPreflight,
        Func<Pane, string, Action<bool>>? noteDispatch,
        RunDispatchState dispatch,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            statusMarkerLifetime,
            TimeSpan.Zero);
        string statusCommand = TmuxCommandLine(
            route,
            "set-option",
            "-p",
            "-t",
            dispatch.Pane.Id.ToString(),
            token.StatusOption);
        string signalCommand = TmuxCommandLine(route, "wait-for", "-S", token.Channel);
        string unsetStatusCommand = TmuxCommandLine(
            route,
            "set-option",
            "-p",
            "-u",
            "-q",
            "-t",
            dispatch.Pane.Id.ToString(),
            token.StatusOption);
        string cleanupDelay = ((long)Math.Ceiling(statusMarkerLifetime.TotalSeconds))
            .ToString(CultureInfo.InvariantCulture);
        string scheduleCleanupCommand = TmuxCommandLine(
            route,
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

            if (dispatchPreflight is not null)
            {
                dispatch.Pane = await dispatchPreflight(cancellationToken).ConfigureAwait(false);
            }
            route.RequireSame(PaneCommandRoute.From(dispatch.Pane));

            // Recorded before dispatch, for the same reason as send_keys: a
            // concurrent wait_for_text must never see the sourcing line's own
            // echo before the record that discounts it exists. The payload
            // already ends with a newline in this one paste, so there is no
            // separate Enter dispatch to wait on before settling.
            Action<bool>? note = noteDispatch?.Invoke(dispatch.Pane, payload);
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
                    note?.Invoke(false);
                }

                throw;
            }
            catch (LibTmuxException error)
            {
                dispatch.PayloadMayHaveReachedTmux =
                    error.Dispatch != TmuxDispatchState.NotDispatched;
                if (!dispatch.PayloadMayHaveReachedTmux)
                {
                    note?.Invoke(false);
                }

                throw;
            }

            note?.Invoke(true);
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
                await CleanupPasteBufferAsync(server, buffer, primaryFailure)
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
        Action? onRunReleased) =>
        _ = CompleteRetainedRunAsync(
            server,
            wait,
            pane,
            token,
            directory,
            daemonProcessStart,
            onRunReleased);

    private static async Task CompleteRetainedRunAsync(
        Server server,
        TmuxWaitChannel? wait,
        Pane pane,
        RunToken token,
        string? directory,
        long? daemonProcessStart,
        Action? onRunReleased)
    {
        Task? waitTask = wait?.WaitUntilSignalledAsync(CancellationToken.None);
        while (true)
        {
            bool intervalElapsed = false;
            if (waitTask is { IsCompleted: false })
            {
                Task interval = Task.Delay(RetainedRunProbeInterval);
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
                onRunReleased?.Invoke();
                await DisposeRetainedWaitAsync(wait).ConfigureAwait(false);
                await CleanupStatusMarkerAsync(pane, token).ConfigureAwait(false);
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
                await Task.Delay(RetainedRunProbeInterval).ConfigureAwait(false);
            }
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

    private static async Task CleanupPasteBufferAsync(
        Server server,
        string buffer,
        Exception? primaryFailure)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await server.Buffers.DeleteAsync(buffer, cleanup.Token).ConfigureAwait(false);
        }
        catch (Exception cleanupFailure)
        {
            if (primaryFailure is null)
            {
                throw;
            }

            primaryFailure.Data["LibTmux.PaneCommand.PasteBufferCleanupFailure"] = cleanupFailure;
        }
    }

    internal static void ValidateRunCommand(string command, int maximumBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        if (command.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Shell commands cannot contain NUL.", nameof(command));
        }

        if (command.Length > maximumBytes)
        {
            throw RunCommandTooLarge(
                $"The command is more than {maximumBytes} UTF-8 bytes");
        }

        int commandBytes = System.Text.Encoding.UTF8.GetByteCount(command);
        if (commandBytes > maximumBytes)
        {
            throw RunCommandTooLarge(
                $"The command is {commandBytes} UTF-8 bytes; the input ceiling is "
                + maximumBytes.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static ArgumentException RunCommandTooLarge(string size) =>
        new(size + ". Put a longer script in a file and run that file instead.");

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

    private static async Task CleanupStatusMarkerAsync(Pane pane, RunToken token)
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
    /// <summary>Quotes a word so a POSIX shell reads it as exactly that word.</summary>
    /// <param name="value">The word.</param>
    /// <returns>The quoted word.</returns>
    /// <remarks>
    /// Single quotes end every special meaning a shell has except their own, so
    /// the only thing to handle is a single quote in the input.
    /// </remarks>
    internal static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>Builds the tmux command line that reaches this same server.</summary>
    /// <param name="route">The startup and preflight-authenticated route.</param>
    /// <param name="arguments">The tmux command and its arguments.</param>
    /// <returns>A shell-safe command line.</returns>
    /// <remarks>
    /// A command run from inside a pane inherits <c>TMUX</c> and would reach the
    /// ambient server, which is not necessarily the one this tool is driving.
    /// Naming the absolute socket is what makes the two the same server, and
    /// <c>command</c> keeps a shell function from replacing the tmux executable.
    /// </remarks>
    internal static string TmuxCommandLine(
        PaneCommandRoute route,
        params string[] arguments)
    {
        StringBuilder line = new("command ");
        line.Append(ShellQuote(route.TmuxBinaryPath));
        line.Append(" -S ").Append(ShellQuote(route.SocketPath));

        foreach (string argument in arguments)
        {
            line.Append(' ').Append(ShellQuote(argument));
        }

        return line.ToString();
    }
}
