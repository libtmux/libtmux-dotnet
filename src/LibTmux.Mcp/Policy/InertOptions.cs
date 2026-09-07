using System.Collections.Frozen;
using System.Text.RegularExpressions;
using ModelContextProtocol;

namespace LibTmux.Mcp;

/// <summary>The tmux options a generic setter may write, and the values it may write.</summary>
internal static partial class InertOptions
{
    /// <summary>
    /// Options tmux types as flag, number, choice or colour. Its string and
    /// command types are absent because their values are formats and commands:
    /// a format runs <c>#(...)</c>, a command option runs on tmux's own events,
    /// and either would turn one setter into arbitrary execution on every
    /// future pane. Array options are absent because an index is a second name.
    /// </summary>
    internal static readonly FrozenSet<string> Names = FrozenSet.ToFrozenSet(
    [
        "activity-action", "aggressive-resize", "allow-passthrough", "allow-rename",
        "allow-set-title", "alternate-screen", "assume-paste-time", "automatic-rename",
        "base-index", "bell-action", "buffer-limit", "clock-mode-colour",
        "clock-mode-style", "copy-mode-line-numbers", "cursor-colour", "cursor-style",
        "destroy-unattached", "detach-on-destroy", "display-panes-active-colour",
        "display-panes-colour", "display-panes-time", "display-time", "escape-time",
        "extended-keys", "extended-keys-format", "focus-events", "focus-follows-mouse",
        "get-clipboard", "initial-repeat-time", "input-buffer-size", "lock-after-time",
        "menu-border-lines", "message-limit", "message-line", "mode-keys",
        "monitor-activity", "monitor-bell", "monitor-silence", "pane-base-index",
        "pane-border-indicators", "pane-border-lines", "pane-border-status",
        "pane-scrollbars", "pane-scrollbars-position", "popup-border-lines",
        "prefix-timeout", "prompt-command-cursor-style", "prompt-cursor-colour",
        "prompt-cursor-style", "prompt-history-limit", "remain-on-exit", "renumber-windows",
        "repeat-time", "scroll-on-clear", "set-clipboard", "set-titles", "silence-action",
        "status", "status-bg", "status-fg", "status-interval", "status-justify",
        "status-keys", "status-left-length", "status-position", "status-right-length",
        "tiled-layout-max-columns", "variation-selector-always-wide", "visual-activity",
        "visual-bell", "visual-silence", "window-size", "wrap-search", "xterm-keys",
    ],
        StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, string> Dedicated =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["history-limit"] = "set_history_limit",
            ["mouse"] = "set_mouse_enabled",
            ["synchronize-panes"] = "set_synchronize_panes",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// The three inert options that end sessions or the server. Excluding
    /// tmux's string and command types keeps a value from carrying code, and
    /// that filter is blind to this: these are flags, and tmux acts on them
    /// itself — server.c:281 and :284 exit the server, server-fn.c:493
    /// destroys every unattached session. So they answer to the teardown gate
    /// rather than to the toolset this tool sits in.
    /// </summary>
    private static readonly FrozenDictionary<string, string> Teardown =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["destroy-unattached"] = "kill_session",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    // No tool here can end the tmux server, so nothing here may arm tmux to
    // end it. These two are absent from the enum for that reason rather than
    // for the code-execution reason every other absent option has.
    private static readonly FrozenSet<string> ServerLethal = FrozenSet.ToFrozenSet(
        ["exit-empty", "exit-unattached"],
        StringComparer.Ordinal);

    /// <summary>Refuses a name or value outside what an inert option accepts.</summary>
    /// <param name="name">The option name a caller asked for.</param>
    /// <param name="value">The value a caller asked for.</param>
    /// <param name="published">Answers whether one named tool is published here.</param>
    internal static void Require(string name, string value, Func<string, bool> published)
    {
        ArgumentNullException.ThrowIfNull(published);
        if (Dedicated.TryGetValue(name, out string? tool))
        {
            throw new McpException($"Use {tool} to set {name}.");
        }

        if (ServerLethal.Contains(name))
        {
            throw new McpException(
                $"Setting {name} would let tmux exit the whole server on its own, "
                + "ending every session on it. No tool here can end the server, so "
                + "none may arm it to end itself. Put it in your tmux config file if "
                + "you want it.");
        }

        if (!Names.Contains(name))
        {
            throw new McpException(
                $"'{name}' is not a settable option. This tool writes the options tmux "
                + "types as flag, number, choice or colour; its string and command options "
                + "hold formats and commands, so setting them would run code. Read any "
                + "option with show_option.");
        }

        if (Teardown.TryGetValue(name, out string? gate) && !published(gate))
        {
            throw new McpException(
                $"Setting {name} would let tmux destroy sessions on its own, which "
                + $"needs {gate}. Enable the teardown toolset, or read the option "
                + "with show_option.");
        }

        if (!InertValue().IsMatch(value))
        {
            throw new McpException(
                $"'{value}' is not an inert option value. Give a whole number, a word such "
                + "as on or bottom, or a colour such as red, colour12 or #00ff00.");
        }
    }

    // Admits no tmux format and no shell syntax: a '#' only ever opens a
    // six-digit colour, so '#(' and '#{' cannot be written at all.
    [GeneratedRegex(
        @"\A(?:-?[0-9]{1,10}|[A-Za-z][A-Za-z0-9-]{0,31}|[#][0-9A-Fa-f]{6})\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex InertValue();
}
