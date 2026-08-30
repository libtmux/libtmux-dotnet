using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using LibTmux.Internal;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace LibTmux.Mcp;

/// <content>Running a command in a pane and knowing when it finished.</content>
[UnsupportedOSPlatform("windows")]
public sealed partial class WriteTools
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
    [McpServerTool(Name = "tmux_run", Destructive = true, OpenWorld = true, UseStructuredContent = true)]
    [Description(
        "Run a shell command in a pane, wait for it to finish, and report its real "
        + "exit status and output. This is the tool for 'run X and tell me if it "
        + "worked'. Do NOT send keys and then poll a capture in a loop — this waits "
        + "deterministically and costs one call. The command runs in a subshell, so "
        + "cd and export do not persist. Output starts at an authenticated position "
        + "captured before dispatch; check linesMissed and anchorLost. If it may "
        + "outlast the timeout, use tmux_start_job instead and collect it later. A "
        + "timed-out command MAY STILL BE RUNNING; inspect it and do not retry it.")]
    public async Task<RunResult> RunAsync(
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
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ValidateRunCommand(command, _policy.MaxBytes);
        Server server = await ServerAsync(socketName, cancellationToken).ConfigureAwait(false);
        Pane pane = await TmuxTargets.PaneAsync(server, paneId, cancellationToken)
            .ConfigureAwait(false);
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
                PaneText.Scrub(read.Lines, pane.Width),
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

        // The subshell isolates command syntax from status capture and rendezvous;
        // otherwise a trailing operator can swallow both and leave the wait hanging.
        string payload = string.Concat(
            suppressHistory ? " " : string.Empty,
            "(\n",
            command.TrimEnd(),
            "\n); __lt=$?; ",
            statusCommand,
            " \"$__lt\"; ",
            scheduleCleanupCommand,
            "; ",
            signalCommand);

        await pane.SendKeysAsync(
                new SendKeysRequest(text: payload, enter: true, literal: true),
                cancellationToken)
            .ConfigureAwait(false);
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
