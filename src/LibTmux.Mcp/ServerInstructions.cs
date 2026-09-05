using System.Text;

namespace LibTmux.Mcp;

/// <summary>What the client is told this server is for, before any tool is called.</summary>
/// <remarks>
/// <para>
/// A tool description says what one tool does. These instructions say when this
/// server is the right one at all, which is the question a model answers first
/// and cannot answer from a tool list. They carry the two things a tool
/// description has no room for: what must never route here, and which of two
/// overlapping tools to reach for.
/// </para>
/// <para>
/// The budget is real. Instructions are sent on every connection and sit in
/// context for the whole conversation, so a paragraph added here is paid for by
/// every call that follows.
/// </para>
/// </remarks>
public static class ServerInstructions
{
    /// <summary>The most bytes the instructions may occupy.</summary>
    public const int MaxBytes = 2048;

    /// <summary>Composes the instructions for a running server.</summary>
    /// <param name="policy">What this server will do.</param>
    /// <param name="callerPaneId">The pane this server runs in, when it runs in one.</param>
    /// <returns>The instructions.</returns>
    /// <exception cref="InvalidOperationException">
    /// The fixed guidance no longer fits the budget. That is a bug in this file
    /// rather than a condition to degrade around: silently dropping a segment
    /// would leave a model without the rule it was dropped from.
    /// </exception>
    public static string Compose(ServerPolicy policy, string? callerPaneId)
    {
        ArgumentNullException.ThrowIfNull(policy);

        StringBuilder text = new();
        Append(text, Purpose);
        Append(text, Scope);
        Append(text, MetadataVersusContent);
        Append(text, PaneModes);
        Append(text, WaitDoNotPoll);
        Append(text, Budget);
        Append(text, Gaps);
        Append(text, $"CAPABILITIES: tmux://capabilities reports the startup-frozen socket "
            + $"and effective tools. A missing tool was not selected. Waits are capped at "
            + $"{policy.WaitCeiling.TotalSeconds:0.#}s.");

        if (ExceedsBudget(text.ToString()))
        {
            throw new InvalidOperationException(
                $"The server instructions no longer fit the {MaxBytes}-byte budget. "
                + "Shorten a segment rather than letting one be dropped.");
        }

        // The caller's own pane is the one piece that depends on runtime data,
        // so it is the one piece allowed to be dropped: a hostile TMUX_PANE
        // must not be able to stop the server from starting.
        if (!string.IsNullOrWhiteSpace(callerPaneId))
        {
            StringBuilder withContext = new(text.ToString());
            Append(
                withContext,
                $"YOU ARE HERE: server pane {callerPaneId}. Do not send keys or kill it "
                + "unless asked; get_pane_info confirms it.");
            if (!ExceedsBudget(withContext.ToString()))
            {
                return withContext.ToString();
            }
        }

        return text.ToString();
    }

    private const string Purpose =
        "Drives tmux: terminal sessions, windows and panes on this machine. "
        + "Hierarchy is Server > Session > Window > Pane. Target by id — %1 is a pane, "
        + "@1 a window, $1 a session — because ids survive renames and layout changes. "
        + "Every tool uses the one socket pinned when this MCP process starts.";

    private const string Scope =
        "USE FOR: tmux panes, windows, sessions, splits, scrollback, "
        + "sending keys, 'this terminal', 'the shell'. "
        + "DO NOT USE FOR: browser tabs, editor splits (VS Code, Neovim), desktop "
        + "windows (i3, sway), or login sessions — none of those are tmux. "
        + "If a bare 'window' or 'session' could mean either, ask once.";

    private const string MetadataVersusContent =
        "NAMES VS TEXT: list_sessions, list_windows and list_panes answer names, sizes "
        + "and running commands. They "
        + "cannot see terminal text. For what a pane is SHOWING — an error, a prompt, "
        + "a build log — use search_panes, capture_pane or snapshot_pane.";

    private const string PaneModes =
        "PANE MODES: humans own them. Observe via capture, search, snapshot and "
        + "cursors. Input refuses; wait for exit; never enter, drive or cancel.";

    private const string WaitDoNotPoll =
        "WAIT, NEVER POLL: never loop on capture_pane. For a command you run, "
        + "run_shell_command waits and reports the real exit status. For output you did "
        + "not start, use wait_for_text. To watch across turns, use capture_since and "
        + "pass back its cursor.";

    private const string Budget =
        "COST: terminal text keeps the NEWEST lines. Two losses are reported "
        + "separately and you must check both: truncated with droppedLines counts "
        + "what a budget trimmed, and linesMissed with anchorLost means scrollback "
        + "discarded output before it could be read, which cannot be counted — "
        + "droppedLines reads 0 there because nothing was trimmed, not because "
        + "nothing was lost. Prefer capture_since while watching.";

    private const string Gaps =
        "ABSENT ON PURPOSE: no hook writing (a hook outlives this conversation — put "
        + "it in your tmux config); no buffer reading by default (buffers hold what a "
        + "user copied).";

    private static void Append(StringBuilder text, string segment)
    {
        if (text.Length > 0)
        {
            text.Append("\n\n");
        }

        text.Append(segment);
    }

    private static bool ExceedsBudget(string text) => Encoding.UTF8.GetByteCount(text) > MaxBytes;
}
