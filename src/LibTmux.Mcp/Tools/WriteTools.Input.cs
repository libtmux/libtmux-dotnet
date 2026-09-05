using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text;
using LibTmux.Internal;
using ModelContextProtocol;

namespace LibTmux.Mcp;

/// <content>Putting keystrokes and text into a pane.</content>
[UnsupportedOSPlatform("windows")]
internal sealed partial class WriteTools
{
    internal const string PasteBufferCleanupFailureDataKey =
        "LibTmux.Mcp.PasteBufferCleanupFailure";
    internal const string PasteBufferCleanupBufferDataKey =
        "LibTmux.Mcp.PasteBufferCleanupBuffer";

    private static readonly TimeSpan PasteBufferCleanupTimeout = TimeSpan.FromSeconds(5);
    private const int MaximumBatchSteps = 64;
    private const int MaximumBatchKeyBytes = 65_536;
    internal const int MaximumStepDelayMilliseconds = 2_000;

    /// <summary>Sends keys to a pane.</summary>
    /// <param name="keys">The text or key name to send.</param>
    /// <param name="paneId">The pane, or null for the active one.</param>
    /// <param name="enter">Whether Enter follows.</param>
    /// <param name="literal">Whether the text is sent as typed rather than read as key names.</param>
    /// <param name="suppressHistory">Whether to keep the text out of shell history.</param>
    /// <param name="socketName">The tmux socket, or null for the default.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What was sent.</returns>
    /// <remarks>
    /// This does not wait and reports nothing about what the keys did, which is
    /// the point: it is for driving a program's interface. A shell command
    /// whose result matters belongs in <c>run_shell_command</c>.
    /// </remarks>
    [Description(
        "Send raw keystrokes to a pane and return immediately. Use for driving an "
        + "interactive program — a key in vim, a menu choice, Ctrl-C. Set literal=false "
        + "to send named keys such as C-c, Escape or F5. For running a shell command "
        + "and learning whether it worked, use run_shell_command instead; this tool tells you "
        + "nothing about what happened next.")]
    public async Task<ActionResult> SendKeysAsync(
        [Description("The text to type, or a key name such as C-c when literal is false.")]
        string keys,
        [Description("The pane id, such as %1. Omit for the active pane.")]
        string? paneId = null,
        [Description("Press Enter after the keys.")] bool enter = false,
        [Description(
            "Send the text exactly as written. Turn this off to send tmux key names "
            + "such as C-c, Escape, Up or F5.")]
        bool literal = true,
        [Description("Keep the text out of the shell's history. Best-effort.")]
        bool suppressHistory = false,
        [Description("The tmux socket to use. Omit for the default server.")]
        string? socketName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        Server server = await ServerAsync(socketName, cancellationToken).ConfigureAwait(false);
        Pane pane = await TmuxTargets.PaneAsync(server, paneId, cancellationToken)
            .ConfigureAwait(false);
        return await SendKeysToPaneAsync(
                pane,
                keys,
                enter,
                literal,
                suppressHistory,
                "send_keys",
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<ActionResult> SendKeysToPaneAsync(
        Pane pane,
        string keys,
        bool enter,
        bool literal,
        bool suppressHistory,
        string toolName,
        CancellationToken cancellationToken)
    {
        Pane fresh = await TmuxTargets.PaneAsync(
                pane.Server,
                pane.Id.ToString(),
                cancellationToken)
            .ConfigureAwait(false);
        return await SendKeysToPreflightedPaneAsync(
                fresh,
                keys,
                enter,
                literal,
                suppressHistory,
                toolName,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<ActionResult> SendKeysToPreflightedPaneAsync(
        Pane pane,
        string keys,
        bool enter,
        bool literal,
        bool suppressHistory,
        string toolName,
        CancellationToken cancellationToken)
    {
        RefuseHumanOwnedMode(pane, toolName);
        await pane.SendKeysAsync(
                new SendKeysRequest(
                    text: keys,
                    enter: enter,
                    literal: literal,
                    suppressHistory: suppressHistory),
                cancellationToken)
            .ConfigureAwait(false);

        // Only literal keys are characters. With literal false the argument is
        // a key name, so counting it counted the name rather than the key —
        // and tmux types a name it does not recognize as text, which the
        // caller is worse placed than this server to notice.
        return new ActionResult(
            (literal
                ? $"Sent {keys.Length} characters to {pane.Id}."
                : $"Sent key '{keys}' to {pane.Id}; tmux types a key name it does not "
                    + "know as literal text.")
            + " Read the pane, or use wait_for_text, to see what they did.",
            PaneId: pane.Id.ToString());
    }

    /// <summary>Sends several keystrokes in order.</summary>
    /// <param name="steps">What to send, in order.</param>
    /// <param name="paneId">The pane, or null for the active one.</param>
    /// <param name="socketName">The tmux socket, or null for the default.</param>
    /// <param name="cancellationToken">Cancels the tmux commands.</param>
    /// <returns>What was sent.</returns>
    [Description(
        "Send several keystrokes to one pane in order, in a single call. Use for a "
        + "short interactive sequence — open a file, move, type, save — instead of "
        + "one call per key. A batch has at most 64 steps and 64 KiB of UTF-8 text "
        + "(or the lower server byte limit). Each delay is 0–2000 ms and all delays "
        + "together must fit the server wait ceiling.")]
    public async Task<ActionResult> SendKeysBatchAsync(
        [Description(
            "The keystrokes to send, in order: 1–64 steps, with no null step or keys. "
            + "Combined text is limited to min(LIBTMUX_MCP_MAX_BYTES, 65536) UTF-8 bytes.")]
        IReadOnlyList<KeyStep> steps,
        [Description("The pane id, such as %1. Omit for the active pane.")]
        string? paneId = null,
        [Description("The tmux socket to use. Omit for the default server.")]
        string? socketName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ValidateBatch(steps, _policy);
        Server server = await ServerAsync(socketName, cancellationToken).ConfigureAwait(false);
        Pane pane = await TmuxTargets.PaneAsync(server, paneId, cancellationToken)
            .ConfigureAwait(false);

        var sequence = new TmuxMutationSequence(
            "An earlier key batch step succeeded, but a later step failed. The pane "
            + "may already have acted on those keys; do not retry the whole batch.");
        for (int index = 0; index < steps.Count; index++)
        {
            KeyStep step = steps[index];
            pane = await TmuxTargets.PaneAsync(server, pane.Id.ToString(), cancellationToken)
                .ConfigureAwait(false);
            RefuseHumanOwnedMode(pane, "send_keys_batch");
            await MutateAsync(
                    sequence,
                    () => pane.SendKeysAsync(
                        new SendKeysRequest(
                            text: step.Keys,
                            enter: step.Enter,
                            literal: step.Literal),
                        cancellationToken),
                    $"Key batch step {index + 1} may have reached tmux. The pane may "
                    + "already have acted on it; do not retry the whole batch.")
                .ConfigureAwait(false);

            if (step.DelayMilliseconds is int delay and > 0)
            {
                await sequence.ObserveAsync(() => Task.Delay(delay, cancellationToken))
                    .ConfigureAwait(false);
            }
        }

        return new ActionResult(
            $"Sent {steps.Count} steps to {pane.Id}.",
            PaneId: pane.Id.ToString());
    }

    internal static void ValidateBatch(IReadOnlyList<KeyStep> steps, ServerPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(policy);
        if (steps.Count is 0 or > MaximumBatchSteps)
        {
            throw new McpException(
                $"A key batch must contain between 1 and {MaximumBatchSteps} steps.");
        }

        int maximumBytes = Math.Min(policy.MaxBytes, MaximumBatchKeyBytes);
        int totalBytes = 0;
        long totalDelay = 0;
        for (int index = 0; index < steps.Count; index++)
        {
            KeyStep? step = steps[index];
            if (step is null)
            {
                throw new McpException($"Key batch step {index + 1} is null.");
            }

            if (step.Keys is null)
            {
                throw new McpException($"Key batch step {index + 1} has null keys.");
            }

            if (step.Keys.Length > maximumBytes - totalBytes)
            {
                throw BatchKeysTooLarge(maximumBytes);
            }

            int bytes = Encoding.UTF8.GetByteCount(step.Keys);
            if (bytes > maximumBytes - totalBytes)
            {
                throw BatchKeysTooLarge(maximumBytes);
            }

            totalBytes += bytes;
            int delay = step.DelayMilliseconds ?? 0;
            if (delay is < 0 or > MaximumStepDelayMilliseconds)
            {
                throw new McpException(
                    $"Key batch step {index + 1} delay must be between 0 and "
                    + $"{MaximumStepDelayMilliseconds} milliseconds.");
            }

            totalDelay += delay;
        }

        long maximumDelay = checked((long)Math.Floor(policy.WaitCeiling.TotalMilliseconds));
        if (totalDelay > maximumDelay)
        {
            throw new McpException(
                $"Key batch delays total {totalDelay} milliseconds; this server allows "
                + $"at most {maximumDelay} milliseconds per call.");
        }
    }

    private static McpException BatchKeysTooLarge(int maximumBytes) =>
        new($"Key batch text may use at most {maximumBytes} UTF-8 bytes in one call.");

    /// <summary>Pastes text into a pane without the shell reading it as keys.</summary>
    /// <param name="text">The text to paste.</param>
    /// <param name="paneId">The pane, or null for the active one.</param>
    /// <param name="bracketed">Whether to use bracketed paste.</param>
    /// <param name="socketName">The tmux socket, or null for the default.</param>
    /// <param name="cancellationToken">Cancels the tmux commands.</param>
    /// <returns>What was pasted.</returns>
    /// <remarks>
    /// Bracketed paste tells the program the text was pasted rather than typed,
    /// which is what stops an editor auto-indenting every line of it.
    /// </remarks>
    [Description(
        "Paste a block of text into a pane through a tmux buffer. Use for multi-line "
        + "text, or anything an editor would mangle if typed — bracketed paste stops "
        + "auto-indent. The tool attempts to delete its temporary buffer afterwards; "
        + "if cleanup fails, the completed-paste result identifies what remains.")]
    public async Task<ActionResult> PasteTextAsync(
        [Description("The text to paste.")] string text,
        [Description("The pane id, such as %1. Omit for the active pane.")]
        string? paneId = null,
        [Description(
            "Tell the program the text was pasted, not typed. Keep this on for "
            + "editors, which otherwise re-indent every line.")]
        bool bracketed = true,
        [Description("The tmux socket to use. Omit for the default server.")]
        string? socketName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        Server server = await ServerAsync(socketName, cancellationToken).ConfigureAwait(false);
        Pane pane = await TmuxTargets.PaneAsync(server, paneId, cancellationToken)
            .ConfigureAwait(false);
        RefuseHumanOwnedMode(pane, "paste_text");

        string buffer = $"libtmux_mcp_{Guid.NewGuid():N}"[..24];
        Exception? primaryFailure = null;
        Exception? cleanupFailure = null;
        bool bufferMayExist = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await server.SetBufferAsync(text, buffer, cancellationToken: cancellationToken)
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
                    new PasteBufferRequest(name: buffer, bracketed: bracketed),
                    cancellationToken)
                .ConfigureAwait(false);
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
                cleanupFailure = await CleanupPasteBufferAsync(server, buffer, primaryFailure)
                    .ConfigureAwait(false);
            }
        }

        if (cleanupFailure is not null)
        {
            return new ActionResult(
                $"Pasted {text.Length} characters into {pane.Id}, but cleanup failed and "
                + $"temporary buffer {buffer} may remain. Do not retry the paste. Inspect "
                + "and remove it manually with "
                + $"tmux delete-buffer -b {buffer}.",
                PaneId: pane.Id.ToString());
        }

        return new ActionResult(
            $"Pasted {text.Length} characters into {pane.Id}.",
            PaneId: pane.Id.ToString());
    }

    internal static void RefuseHumanOwnedMode(Pane pane, string toolName)
    {
        bool acceptsInput = pane.RawFormatFields.TryGetValue(
            "pane_in_mode", out string? value) && value == "0";
        if (!acceptsInput)
        {
            throw new McpException(
                $"{toolName} refuses input to {pane.Id} because its pane mode is human-owned. "
                + "Use capture_pane or snapshot_pane to observe it, then wait for the mode "
                + "to end before retrying.");
        }
    }

    private static async Task<Exception?> CleanupPasteBufferAsync(
        Server server,
        string buffer,
        Exception? primaryFailure)
    {
        using var cleanup = new CancellationTokenSource(PasteBufferCleanupTimeout);
        try
        {
            await server.DeleteBufferAsync(buffer, cleanup.Token).ConfigureAwait(false);
            return null;
        }
        catch (Exception cleanupFailure)
        {
            if (primaryFailure is null)
            {
                return cleanupFailure;
            }

            primaryFailure.Data[PasteBufferCleanupFailureDataKey] = cleanupFailure;
            primaryFailure.Data[PasteBufferCleanupBufferDataKey] = buffer;
            return null;
        }
    }

}

/// <summary>One keystroke in a batch.</summary>
/// <param name="Keys">The text to type, or a key name.</param>
/// <param name="Enter">Whether Enter follows.</param>
/// <param name="Literal">Whether the text is sent as typed rather than read as a key name.</param>
/// <param name="DelayMilliseconds">
/// How long to pause afterwards, for a program that needs a moment to redraw
/// before it will accept the next key. Capped at two seconds.
/// </param>
public sealed record KeyStep(
    string Keys,
    bool Enter = false,
    bool Literal = true,
    int? DelayMilliseconds = null);
