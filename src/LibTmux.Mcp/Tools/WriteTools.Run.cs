using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using LibTmux.Internal;
using ModelContextProtocol;

namespace LibTmux.Mcp;

/// <content>Running a command in a pane and knowing when it finished.</content>
[UnsupportedOSPlatform("windows")]
internal sealed partial class WriteTools
{
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
            cancellationToken);

    internal Task<RunResult> RunWithDispatchPreflightAsync(
        string command,
        Pane pane,
        double? timeoutSeconds,
        int? maxLines,
        bool suppressHistory,
        string? socketName,
        IProgress<ProgressNotificationValue>? progress,
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
        CancellationToken cancellationToken)
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
        bool payloadMayHaveReachedTmux = false;
        try
        {
            try
            {
                if (dispatchPreflight is not null)
                {
                    pane = await dispatchPreflight(cancellationToken).ConfigureAwait(false);
                }

                await sequence.MutateAsync(
                        () => SendRunPayloadAsync(
                            server,
                            pane,
                            command,
                            token,
                            suppressHistory,
                            _policy.WaitCeiling + StatusCleanupMargin,
                            cancellationToken))
                    .ConfigureAwait(false);
                payloadMayHaveReachedTmux = true;
            }
            catch (TmuxOperationCanceledException error) when (error.CommandMayHaveExecuted)
            {
                payloadMayHaveReachedTmux = true;
                throw new LibTmuxException(
                    "The command may have reached tmux before cancellation. Do not retry "
                    + "until you inspect the pane.",
                    TmuxDispatchState.Unknown,
                    error);
            }
            catch (LibTmuxException error)
                when (error.Dispatch != TmuxDispatchState.NotDispatched)
            {
                payloadMayHaveReachedTmux = true;
                throw;
            }

            bool timedOut = !await sequence.ObserveAsync(() => TickWhileAsync(
                    AwaitChannelAsync(server, token.Channel, budget, cancellationToken),
                    progress,
                    elapsed,
                    budget,
                    $"running in {pane.Id}",
                    cancellationToken))
                .ConfigureAwait(false);
            elapsed.Stop();

            int? status = timedOut
                ? null
                : await sequence
                    .ObserveAsync(() => ReadStatusAsync(pane, token, cancellationToken))
                    .ConfigureAwait(false);
            PaneRead read = await sequence
                .ObserveAsync(() => PaneReader.ReadSinceAsync(
                    pane,
                    baseline,
                    cancellationToken))
                .ConfigureAwait(false);

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
                    read.AnchorLost),
                "command result"));
        }
        finally
        {
            elapsed.Stop();
            if (payloadMayHaveReachedTmux)
            {
                await CleanupStatusMarkerAsync(pane, token).ConfigureAwait(false);
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
        internal string BeginMarker => $"lt_b_{Id}";

        /// <summary>Mints a token nothing else is using.</summary>
        /// <returns>The token.</returns>
        internal static RunToken Create() => new(Guid.NewGuid().ToString("N")[..10]);
    }

    internal static async Task SendRunPayloadAsync(
        Server server,
        Pane pane,
        string command,
        RunToken token,
        bool suppressHistory,
        TimeSpan statusMarkerLifetime,
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
            pane.Id.ToString(),
            token.StatusOption);
        string signalCommand = TmuxCommandLine(server, "wait-for", "-S", token.Channel);
        string unsetStatusCommand = TmuxCommandLine(
            server,
            "set-option",
            "-p",
            "-u",
            "-q",
            "-t",
            pane.Id.ToString(),
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

        // The subshell isolates user syntax from the rendezvous. The marker
        // separates the shell's echoed payload from the command's output.
        string payload = string.Concat(
            suppressHistory ? " " : string.Empty,
            "(\nprintf '%s\\n' ",
            token.BeginMarker,
            "\n",
            command.TrimEnd(),
            "\n); __lt=$?; ",
            statusCommand,
            " \"$__lt\"; ",
            scheduleCleanupCommand,
            "; ",
            signalCommand,

            // The submitting newline is part of the payload here. send-keys
            // carried Enter as a separate key; a buffer has to hold it.
            "\n");

        // Clear whatever is already typed at the prompt. A caller's own
        // send_keys with enter false, or a human sharing the pane, leaves the
        // line editor holding text, and the payload lands after it: the shell
        // then reads "echo LEFTOVER(" and parses the subshell paren as a glob
        // qualifier, so the command never runs and the wait burns its whole
        // budget. A pasted 0x15 is not read as kill-line — measured — and
        // tmux's key path is, but that path fans out to a synchronized cohort,
        // so it is only taken when the pane is alone.
        if (!SynchronizesInput(pane))
        {
            await pane.SendKeysAsync(
                    new SendKeysRequest(text: "C-u", enter: false, literal: false),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // Through a buffer rather than as keys. tmux fans send-keys out to the
        // synchronized cohort (window.c:1381), so a tool promising one exit
        // status could not keep that promise while synchronize-panes was on,
        // and no check-then-send closes the window between them. paste-buffer
        // writes straight to this pane's event (cmd-paste-buffer.c:53) and
        // never reaches window_pane_paste, so the outcome is singular by
        // construction. Not bracketed: a bracketed paste tells the shell the
        // newline was pasted rather than typed, and nothing would run.
        string buffer = $"libtmux_run_{Guid.NewGuid():N}"[..24];
        bool bufferMayExist = false;
        try
        {
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

            await pane.PasteBufferAsync(
                    new PasteBufferRequest(name: buffer, deleteAfter: true, bracketed: false),
                    cancellationToken)
                .ConfigureAwait(false);
            bufferMayExist = false;
        }
        finally
        {
            if (bufferMayExist)
            {
                _ = await CleanupPasteBufferAsync(server, buffer, null).ConfigureAwait(false);
            }
        }
    }

    private static bool SynchronizesInput(Pane pane) =>
        pane.RawFormatFields.TryGetValue("pane_synchronized", out string? value)
        && string.Equals(value, "1", StringComparison.Ordinal);

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
