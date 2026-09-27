using System.ComponentModel;
using System.Globalization;
using System.Runtime.Versioning;
using LibTmux.Internal;
using ModelContextProtocol;

namespace LibTmux.Mcp;

[UnsupportedOSPlatform("windows")]
internal readonly record struct RunCommandRoute(
    string TmuxBinaryPath,
    string SocketPath,
    PaneId PaneId)
{
    internal static RunCommandRoute From(Pane pane, string toolName)
    {
        ArgumentNullException.ThrowIfNull(pane);
        string binary = pane.Server.ConnectionOptions.TmuxBinaryPath;
        McpStartup.RequireSafeRouteValue(binary, "tmux executable route");
        if (!Path.IsPathFullyQualified(binary) || !McpStartup.IsExecutableFile(binary))
        {
            throw new McpException(
                $"{toolName} refuses an unresolved or unusable tmux executable route.");
        }

        if (!pane.RawFormatFields.TryGetValue("socket_path", out string? socketPath)
            || string.IsNullOrWhiteSpace(socketPath))
        {
            throw new McpException(
                $"{toolName} refuses a missing or non-absolute pane socket route.");
        }

        McpStartup.RequireSafeRouteValue(socketPath, "pane socket route");
        if (!Path.IsPathFullyQualified(socketPath))
        {
            throw new McpException(
                $"{toolName} refuses a missing or non-absolute pane socket route.");
        }

        return new RunCommandRoute(binary, socketPath, pane.Id);
    }

    internal PaneRunRoute ForRunner() => new(TmuxBinaryPath, SocketPath, PaneId);

    internal void RequireSame(RunCommandRoute final, string toolName)
    {
        if (this != final)
        {
            throw new McpException(
                $"{toolName} refuses because the pane or its tmux route changed before "
                + "dispatch. The command was not sent.");
        }
    }
}

/// <content>Running a command in a pane and knowing when it finished.</content>
[UnsupportedOSPlatform("windows")]
internal sealed partial class WriteTools
{
    private static readonly TimeSpan StatusCleanupMargin = TimeSpan.FromMinutes(1);

    /// <summary>Runs a command in a pane and waits for it to finish.</summary>
    /// <param name="command">The shell command.</param>
    /// <param name="paneId">The pane, or null for the caller's pane or the one the first session shows.</param>
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
        + "command MAY STILL BE RUNNING; inspect it and do not retry it. paneExited "
        + "means the pane's shell exited first, ending the call early.")]
    public Task<RunResult> RunAsync(
        [Description(
            "The shell command to run, at most LIBTMUX_MCP_MAX_BYTES UTF-8 bytes. "
            + "Put longer scripts in a file and run that file.")]
        string command,
        [Description(
            "The pane id, such as %1. Omit for this server's own pane, or else the one the "
            + "first session shows.")]
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
            initialRoute: null,
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
        RunCommandRoute route,
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
            route,
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
        RunCommandRoute? initialRoute,
        PaneRunRegistry.PaneRunLease? runLease,
        CancellationToken cancellationToken)
    {
        bool leaseHandedOff = false;
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
            RunCommandRoute route = initialRoute
                ?? RunCommandRoute.From(pane, "run_shell_command");
            if (route.PaneId != pane.Id)
            {
                throw new McpException(
                    "run_shell_command lost its authenticated pane route before setup.");
            }

            TimeSpan budget = _policy.EffectiveTimeout(
                timeoutSeconds is double seconds ? TimeSpan.FromSeconds(seconds) : null);
            PaneRunHooks hooks = new()
            {
                DispatchPreflight = dispatchPreflight,

                // Recorded before dispatch, for the same reason as send_keys: a
                // concurrent wait_for_text must never see the sourcing line's
                // own echo before the record that discounts it exists.
                NoteDispatch = (target, payload) =>
                {
                    PaneEchoRegistry.PaneEchoNote note = PaneEchoRegistry.NoteLiteralWrite(target, payload, enter: false);
                    return (note.Rollback, note.Settle);
                },
                Completed = () => runLease?.Release(),
                Progress = spent => ReadTools.Report(progress, spent, budget, $"running in {pane.Id}"),
                CleanupBuffer = async (owner, buffer, failure) =>
                    _ = await CleanupPasteBufferAsync(owner, buffer, failure).ConfigureAwait(false),
            };
            leaseHandedOff = true;
            PaneRunOutcome outcome = await PaneRunner
                .RunAsync(
                    server,
                    pane,
                    route.ForRunner(),
                    command,
                    budget,
                    suppressHistory,
                    _policy.WaitCeiling + StatusCleanupMargin,
                    progress is null ? hooks with { Progress = null } : hooks,
                    McpPaneReader.Failure,
                    cancellationToken)
                .ConfigureAwait(false);
            string id = outcome.Pane.Id.ToString();
            double elapsedSeconds = Math.Round(outcome.Elapsed.TotalSeconds, 3);
            return StructuredTextResultBudget.Fit(
                outcome.Output,
                maxLines ?? _policy.MaxLines,
                _policy.MaxBytes,
                content => new RunResult(
                    id,
                    outcome.ExitStatus,
                    outcome.TimedOut,
                    content,
                    elapsedSeconds,
                    budget.TotalSeconds,
                    outcome.LinesMissed,
                    outcome.AnchorLost,
                    outcome.Started,
                    outcome.PaneExited),
                "command result");
        }
        finally
        {
            if (!leaseHandedOff)
            {
                runLease?.Release();
            }
        }
    }

    internal static void ValidateRunCommand(string command, int maximumBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        if (command.Contains('\0', StringComparison.Ordinal))
        {
            throw new McpException("The command cannot contain NUL.");
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

    private static McpException RunCommandTooLarge(string size) =>
        new(size + ". Put a longer script in a file and run that file instead.");
}
