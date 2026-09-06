using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text;
using LibTmux.Internal;
using ModelContextProtocol;

namespace LibTmux.Mcp;

/// <content>Running a command in a pane and knowing when it finished.</content>
[UnsupportedOSPlatform("windows")]
internal sealed partial class WriteTools
{
    private const int MaximumInheritedTrapBytes = 64 * 1024;
    private static readonly TimeSpan StatusCleanupTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StatusCleanupMargin = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan JobStatusMarkerLifetime = TimeSpan.FromMinutes(11);

    /// <summary>Runs a command in a pane and waits for it to finish.</summary>
    /// <param name="command">The shell command.</param>
    /// <param name="paneId">The pane, or null for the active one.</param>
    /// <param name="timeoutSeconds">How long to wait, before the server's ceiling.</param>
    /// <param name="maxLines">The most output lines to answer.</param>
    /// <param name="suppressHistory">Whether to keep the command out of shell history.</param>
    /// <param name="socketName">The tmux socket, or null for the default.</param>
    /// <param name="progress">Reports that the command is still running.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>The exit status and what the command printed.</returns>
    /// <remarks>
    /// Completion is not guessed from the text on screen. The command is
    /// followed by a private tmux rendezvous and a private option carrying
    /// <c>$?</c>, so "it finished" and "it exited 1" are facts rather than
    /// readings of a prompt this tool would have to recognise.
    /// </remarks>
    [Description(
        "Run a shell command in a pane, wait for it to finish, and report its real "
        + "exit status and output. This is the tool for 'run X and tell me if it "
        + "worked'. Do NOT send keys and then poll a capture in a loop — this waits "
        + "deterministically and costs one call. The command runs in a subshell, so "
        + "cd and export do not persist. Output starts at an authenticated position "
        + "captured before dispatch; check linesMissed and anchorLost. If it may "
        + "outlast the timeout, run it through a client-managed MCP task. A timed-out "
        + "command MAY STILL BE RUNNING; inspect it and do not retry it.")]
    public Task<RunResult> RunAsync(
        [Description(
            "The shell command to run, at most LIBTMUX_MCP_MAX_BYTES UTF-8 bytes. "
            + "Put longer scripts in a file and run that file.")]
        string command,
        [Description("The pane id, such as %1. Omit for the active pane.")]
        string? paneId = null,
        [Description(
            "Seconds to wait. Lowered to the server's ceiling; read "
            + "effectiveTimeoutSeconds for the value actually used.")]
        double? timeoutSeconds = null,
        [Description("The most output lines to return, newest kept.")]
        int? maxLines = null,
        [Description(
            "Keep the command out of the shell's history by prefixing a space. "
            + "Works on shells set to ignore space-prefixed commands; it is "
            + "best-effort, not a guarantee.")]
        bool suppressHistory = true,
        [Description("The tmux socket to use. Omit for the default server.")]
        string? socketName = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default) =>
        RunAsyncCore(
            command,
            paneId,
            timeoutSeconds,
            maxLines,
            suppressHistory,
            socketName,
            progress,
            dispatchPreflight: null,
            initialPane: null,
            runLease: null,
            cancellationToken);

    internal Task<RunResult> RunWithDispatchPreflightAsync(
        string command,
        Pane pane,
        double? timeoutSeconds,
        int? maxLines,
        bool suppressHistory,
        string? socketName,
        IProgress<ProgressNotificationValue>? progress,
        PaneRunRegistry.PaneRunLease lease,
        Func<CancellationToken, Task<Pane>> dispatchPreflight,
        CancellationToken cancellationToken) =>
        RunAsyncCore(
            command,
            paneId: null,
            timeoutSeconds,
            maxLines,
            suppressHistory,
            socketName,
            progress,
            dispatchPreflight,
            pane,
            lease,
            cancellationToken);

    private async Task<RunResult> RunAsyncCore(
        string command,
        string? paneId,
        double? timeoutSeconds,
        int? maxLines,
        bool suppressHistory,
        string? socketName,
        IProgress<ProgressNotificationValue>? progress,
        Func<CancellationToken, Task<Pane>>? dispatchPreflight,
        Pane? initialPane,
        PaneRunRegistry.PaneRunLease? runLease,
        CancellationToken cancellationToken)
    {
        bool completionOwnershipTransferred = false;
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(command);
            ValidateRunCommand(command, _policy.MaxBytes);
            Server server;
            Pane pane;
            if (initialPane is null)
            {
                server = await ServerAsync(socketName, cancellationToken).ConfigureAwait(false);
                pane = await TmuxTargets.PaneAsync(server, paneId, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                server = initialPane.Server;
                pane = initialPane;
            }
            if (dispatchPreflight is null)
            {
                RefuseHumanOwnedMode(pane, "run_shell_command");
            }
            TimeSpan budget = _policy.EffectiveTimeout(
                timeoutSeconds is double seconds ? TimeSpan.FromSeconds(seconds) : null);
            PaneRead baselineRead = await PaneReader
                .ReadVisibleAsync(pane, null, cancellationToken)
                .ConfigureAwait(false);
            string baselineToken = TailCursor
                .Build(pane, baselineRead.State, baselineRead.CursorRows)
                .Encode();
            TailCursor baseline = TailCursor.Decode(baselineToken, pane)!;

            RunToken token = RunToken.Create();
            Stopwatch elapsed = Stopwatch.StartNew();
            var sequence = new TmuxMutationSequence(
                "The command was sent, but observing its result failed. It may still be "
                + "running or may already have finished; do not retry until you inspect the pane.");
            var dispatch = new RunDispatchState(pane);
            TmuxWaitChannel? completionWait = null;
            bool completionObserved = false;
            bool completionWaitFaulted = false;
            try
            {
                await sequence.MutateAsync(
                        () => SendRunPayloadAsync(
                            server,
                            command,
                            token,
                            suppressHistory,
                            _policy.WaitCeiling + StatusCleanupMargin,
                            dispatchPreflight,
                            dispatch,
                            cancellationToken))
                    .ConfigureAwait(false);
                pane = dispatch.Pane;
                completionWait = server.OpenWaitChannel(token.Channel);
                Task<bool> waitAttempt = completionWait.WaitAsync(budget, cancellationToken);
                bool timedOut;
                try
                {
                    timedOut = !await sequence.ObserveAsync(() => TickWhileAsync(
                            waitAttempt,
                            progress,
                            elapsed,
                            budget,
                            $"running in {pane.Id}",
                            cancellationToken))
                        .ConfigureAwait(false);
                }
                catch
                {
                    completionWaitFaulted = waitAttempt.IsFaulted;
                    throw;
                }

                elapsed.Stop();
                completionObserved = !timedOut;
                int? status = timedOut
                    ? null
                    : await sequence
                        .ObserveAsync(() => ReadStatusAsync(pane, token, cancellationToken))
                        .ConfigureAwait(false);
                if (!timedOut && status is null)
                {
                    throw new McpException(
                        "The command completed, but tmux did not return its authenticated "
                        + "exit status. Do not retry it; inspect the pane instead.");
                }

                PaneRead read = await sequence
                    .ObserveAsync(() => PaneReader.ReadSinceAsync(
                        pane,
                        baseline,
                        cancellationToken))
                    .ConfigureAwait(false);

                // The marker is printed by the wrapper itself, so its absence means
                // the shell never ran it. The pane may not have been at an empty,
                // ready shell prompt. Saying so beats the timeout's usual "it may
                // still be running".
                bool started = read.Lines.Any(line =>
                    line.TrimStart().StartsWith(token.BeginMarker, StringComparison.Ordinal));
                string id = pane.Id.ToString();
                double elapsedSeconds = Math.Round(elapsed.Elapsed.TotalSeconds, 3);
                return sequence.Observe(() => StructuredTextResultBudget.Fit(
                    PaneText.Scrub(
                        PaneText.AfterBeginMarker(read.Lines, token.BeginMarker, pane.Width),
                        pane.Width),
                    maxLines ?? _policy.MaxLines,
                    _policy.MaxBytes,
                    content => new RunResult(
                        id,
                        status,
                        timedOut,
                        content,
                        elapsedSeconds,
                        budget.TotalSeconds,
                        read.LinesMissed,
                        read.AnchorLost,
                        started),
                    "command result"));
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
                if (dispatch.PayloadMayHaveReachedTmux && !completionObserved)
                {
                    RetainRunUntilCompletion(
                        server,
                        completionWait,
                        completionWaitFaulted,
                        dispatch.Pane,
                        token,
                        dispatch.Directory,
                        runLease);
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
                        await CleanupStatusMarkerAsync(dispatch.Pane, token)
                            .ConfigureAwait(false);
                    }

                    DeleteRunDirectory(dispatch.Directory);
                }
            }
        }
        finally
        {
            if (!completionOwnershipTransferred)
            {
                runLease?.Release();
            }
        }
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

        /// <summary>Gets the marker's second half, as the payload spells it.</summary>
        internal string BeginTail => Id[5..];

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
        string command,
        RunToken token,
        bool suppressHistory,
        TimeSpan statusMarkerLifetime,
        Func<CancellationToken, Task<Pane>>? dispatchPreflight,
        RunDispatchState dispatch,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            statusMarkerLifetime,
            TimeSpan.Zero);
        string statusCommand = TmuxCommandLine(
            server,
            "set-option",
            "-p",
            "-t",
            dispatch.Pane.Id.ToString(),
            token.StatusOption);
        string signalCommand = TmuxCommandLine(server, "wait-for", "-S", token.Channel);
        string unsetStatusCommand = TmuxCommandLine(
            server,
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
            server,
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
                await server.SetBufferAsync(payload, buffer, cancellationToken: cancellationToken)
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

            try
            {
                await dispatch.Pane.PasteBufferAsync(
                        new PasteBufferRequest(name: buffer, deleteAfter: true, bracketed: false),
                        cancellationToken)
                    .ConfigureAwait(false);
                dispatch.PayloadMayHaveReachedTmux = true;
            }
            catch (TmuxOperationCanceledException error)
            {
                dispatch.PayloadMayHaveReachedTmux = error.CommandMayHaveExecuted;
                throw;
            }
            catch (LibTmuxException error)
            {
                dispatch.PayloadMayHaveReachedTmux =
                    error.Dispatch != TmuxDispatchState.NotDispatched;
                throw;
            }

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
                _ = await CleanupPasteBufferAsync(server, buffer, primaryFailure)
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
        bool replaceFaultedWait,
        Pane pane,
        RunToken token,
        string? directory,
        PaneRunRegistry.PaneRunLease? runLease) =>
        _ = CompleteRetainedRunAsync(
            server,
            wait,
            replaceFaultedWait,
            pane,
            token,
            directory,
            runLease);

    private static async Task CompleteRetainedRunAsync(
        Server server,
        TmuxWaitChannel? wait,
        bool replaceFaultedWait,
        Pane pane,
        RunToken token,
        string? directory,
        PaneRunRegistry.PaneRunLease? runLease)
    {
        try
        {
            if (replaceFaultedWait && wait is not null)
            {
                try
                {
                    await wait.DisposeAsync().ConfigureAwait(false);
                }
                catch (LibTmuxException error)
                    when (error.Dispatch == TmuxDispatchState.NotDispatched)
                {
                    // This waiter never reached tmux, so a fresh one cannot
                    // overlap a live registration.
                }

                wait = null;
            }

            wait ??= server.OpenWaitChannel(token.Channel);
            await wait.WaitUntilSignalledAsync(CancellationToken.None).ConfigureAwait(false);
            await wait.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Completion is unproven. Keep the lease and private files rather
            // than letting a retry overlap a command that may still be active.
            return;
        }

        await CleanupStatusMarkerAsync(pane, token).ConfigureAwait(false);
        DeleteRunDirectory(directory);
        runLease?.Release();
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
            token.BeginTail,
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

    internal static void ValidateRunCommand(string command, int maximumBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
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

    private static McpException RunCommandTooLarge(string size) =>
        new(size + ". Put a longer script in a file and run that file instead.");

    /// <summary>Reports progress on a beat while one wait runs.</summary>
    /// <param name="waiting">The wait to watch.</param>
    /// <param name="progress">Where to report, or null when the client asked for none.</param>
    /// <param name="elapsed">How long the wait has run.</param>
    /// <param name="budget">How long it may run.</param>
    /// <param name="message">What to say it is doing.</param>
    /// <param name="cancellationToken">Stops the beat.</param>
    /// <returns>Whatever the wait answered.</returns>
    /// <remarks>
    /// The wait itself is one call with nothing to iterate, so the beat comes
    /// from a timer rather than from the work. It costs nothing when the
    /// client asked for no progress, which is the common case.
    /// </remarks>
    internal static async Task<bool> TickWhileAsync(
        Task<bool> waiting,
        IProgress<ProgressNotificationValue>? progress,
        Stopwatch elapsed,
        TimeSpan budget,
        string message,
        CancellationToken cancellationToken)
    {
        if (progress is null)
        {
            return await waiting.ConfigureAwait(false);
        }

        while (true)
        {
            Task beat = Task.Delay(ProgressInterval, cancellationToken);
            if (await Task.WhenAny(waiting, beat).ConfigureAwait(false) == waiting)
            {
                return await waiting.ConfigureAwait(false);
            }

            ReadTools.Report(progress, elapsed.Elapsed, budget, message);
        }
    }

    /// <summary>How often a caller is told a wait is still running.</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(1);

    internal static async Task<bool> AwaitChannelAsync(
        Server server,
        string channel,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        // A command signals its channel once. Cancelling a waiting client to
        // enforce the budget leaves tmux holding the registration, and that
        // registration takes the signal instead of the next caller.
        TmuxWaitChannel wait = server.OpenWaitChannel(channel);
        await using ConfiguredAsyncDisposable _ = wait.ConfigureAwait(false);
        if (!await wait.WaitAsync(budget, cancellationToken).ConfigureAwait(false))
        {
            await wait.DisposeAsync().ConfigureAwait(false);
        }

        return wait.Signalled;
    }

    internal static async Task<int?> ReadStatusAsync(
        Pane pane,
        RunToken token,
        CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<TmuxOption> options = await pane.Options
                .GetAsync(new GetOptionRequest(token.StatusOption, quiet: true), cancellationToken)
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
        finally
        {
            await CleanupStatusMarkerAsync(pane, token).ConfigureAwait(false);
        }
    }

    private static async Task CleanupStatusMarkerAsync(Pane pane, RunToken token)
    {
        using var cleanup = new CancellationTokenSource(StatusCleanupTimeout);
        try
        {
            await pane.Options
                .UnsetAsync(
                    new UnsetOptionRequest(token.StatusOption, quiet: true),
                    cleanup.Token)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The payload also schedules a bounded cleanup inside tmux.
        }
    }
}
