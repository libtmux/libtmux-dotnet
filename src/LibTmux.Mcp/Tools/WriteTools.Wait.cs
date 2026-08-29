using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace LibTmux.Mcp;

/// <content>Waiting on a tmux rendezvous channel.</content>
/// <remarks>
/// Waiting takes the channel's one pending signal, which another process can
/// be relying on, so this sits with the tools that change tmux rather than
/// with the ones that only read it.
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed partial class WriteTools
{
    /// <summary>Waits on a tmux wait-for channel.</summary>
    /// <param name="channel">The channel name.</param>
    /// <param name="timeoutSeconds">How long to wait, before the server's ceiling.</param>
    /// <param name="socketName">The tmux socket, or null for the default.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>What happened.</returns>
    /// <remarks>
    /// tmux's own rendezvous, exposed for a shell command a caller composed
    /// themselves. <c>tmux_run</c> uses this internally, so reach for this only
    /// when the command's shape does not fit that tool.
    /// </remarks>
    [McpServerTool(Name = "tmux_wait_for_channel", Destructive = true, OpenWorld = false, UseStructuredContent = true)]
    [Description(
        "Block until something signals a tmux wait-for channel with "
        + "'tmux wait-for -S <channel>'. Use when you composed a shell command that "
        + "signals it. For an ordinary command whose completion you want, tmux_run "
        + "already does this and also reports the exit status.")]
    public async Task<ActionResult> WaitForChannelAsync(
        [Description("The channel name to wait on, at most 4096 UTF-8 bytes.")] string channel,
        [Description("Seconds to wait. Lowered to the server's ceiling.")]
        double? timeoutSeconds = null,
        [Description("The tmux socket to read. Omit for the default server.")]
        string? socketName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ValidateChannel(channel, _policy.MaxBytes);
        Server server = await ServerAsync(socketName, cancellationToken).ConfigureAwait(false);
        TimeSpan budget = _policy.EffectiveTimeout(
            timeoutSeconds is double seconds ? TimeSpan.FromSeconds(seconds) : null);

        await using TmuxWaitChannel wait = server.OpenWaitChannel(channel);
        if (!await wait.WaitAsync(budget, cancellationToken).ConfigureAwait(false))
        {
            // Withdraw before answering. A signal landing as the attempt ended
            // was taken by this waiter, and only withdrawing settles whether
            // that happened.
            await wait.DisposeAsync().ConfigureAwait(false);
        }

        return wait.Signalled
            ? new ActionResult($"Channel '{channel}' was signalled.")
            : new ActionResult(NotSignalled(channel, budget));
    }

    /// <summary>Says a wait ran out without claiming the channel is untouched.</summary>
    private static string NotSignalled(string channel, TimeSpan budget) =>
        $"Channel '{channel}' was not signalled within {budget.TotalSeconds:0.#}s. "
        + "The wait was withdrawn, so a signal arriving now still counts; call again.";

    internal static void ValidateChannel(string channel, int resultMaxBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resultMaxBytes);
        if (channel.Length > MaximumChannelBytes
            || Encoding.UTF8.GetByteCount(channel) > MaximumChannelBytes)
        {
            throw new McpException(
                $"A wait channel may use at most {MaximumChannelBytes} UTF-8 bytes.");
        }

        ActionResult success = new($"Channel '{channel}' was signalled.");
        ActionResult timeout = new(NotSignalled(channel, TimeSpan.FromSeconds(600)));
        if (Utf8JsonBudget.GetStructuredToolResultByteCount(success, ToolJson.Options)
                > resultMaxBytes
            || Utf8JsonBudget.GetStructuredToolResultByteCount(timeout, ToolJson.Options)
                > resultMaxBytes)
        {
            throw new McpException(
                "The wait channel cannot fit in the configured result byte ceiling. "
                + $"Use a shorter channel or raise {ServerPolicy.MaxBytesVariable}.");
        }
    }

    private const int MaximumChannelBytes = 4_096;
}
