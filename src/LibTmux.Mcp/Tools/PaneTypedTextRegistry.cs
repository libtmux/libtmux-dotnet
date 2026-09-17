using System.Collections.Concurrent;
using System.Runtime.Versioning;
using ModelContextProtocol;

namespace LibTmux.Mcp;

/// <summary>Remembers the literal text this process most recently typed into a pane.</summary>
/// <remarks>
/// A wait must never count the pane echoing back what this server itself
/// typed as a fresh match: the kernel echoes keystrokes immediately, and a
/// shell's line editor can re-print an unsubmitted buffer or redraw it later
/// (a readline redraw, a still-starting shell), both genuinely new bytes but
/// not output the pane produced. The remembered text is replaced by the next
/// literal send to the same pane, submitted or not, and submitting it does
/// not clear it.
/// </remarks>
[UnsupportedOSPlatform("windows")]
internal static class PaneTypedTextRegistry
{
    private static readonly ConcurrentDictionary<PaneRunRegistry.PaneRunIdentity, string>
        LastSentByPane = [];

    /// <summary>Records the literal text just sent to a pane.</summary>
    /// <param name="pane">The pane the text was sent to.</param>
    /// <param name="text">The literal text tmux typed.</param>
    /// <remarks>
    /// Silently does nothing when the pane's endpoint cannot be identified.
    /// This is a best-effort exclusion for a wait, not a safety guard, so a
    /// pane an identity cannot be read from degrades to the old behaviour
    /// rather than failing the send that is otherwise complete.
    /// </remarks>
    internal static void Record(Pane pane, string text)
    {
        ArgumentNullException.ThrowIfNull(pane);
        if (string.IsNullOrEmpty(text) || TryIdentify(pane) is not { } identity)
        {
            return;
        }

        LastSentByPane[identity] = text;
    }

    /// <summary>Gets the literal text this process most recently sent to a pane.</summary>
    /// <param name="pane">The pane to check.</param>
    /// <returns>The text, or null when nothing has been recorded for it.</returns>
    internal static string? LastSent(Pane pane)
    {
        ArgumentNullException.ThrowIfNull(pane);
        return TryIdentify(pane) is { } identity
            && LastSentByPane.TryGetValue(identity, out string? text)
            ? text
            : null;
    }

    private static PaneRunRegistry.PaneRunIdentity? TryIdentify(Pane pane)
    {
        try
        {
            return PaneRunRegistry.PaneRunIdentity.For(pane);
        }
        catch (McpException)
        {
            return null;
        }
    }
}
