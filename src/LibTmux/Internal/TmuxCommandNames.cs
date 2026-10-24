namespace LibTmux.Internal;

/// <summary>Resolves a command word the way tmux does.</summary>
/// <remarks>
/// <para>
/// tmux takes a word that is a command's alias, then a command's name, then
/// a prefix of exactly one name; a prefix of several is ambiguous and runs
/// nothing. The table is in tmux's own order, which decides a tie between an
/// alias and a prefix, and lists a command only from the release that added
/// it, since a new command can make a prefix ambiguous.
/// </para>
/// </remarks>
internal static class TmuxCommandNames
{
    private static readonly (string Name, string? Alias, TmuxVersion? Since)[] Table =
    [
        ("attach-session", "attach", null),
        ("bind-key", "bind", null),
        ("break-pane", "breakp", null),
        ("capture-pane", "capturep", null),
        ("choose-buffer", null, null),
        ("choose-client", null, null),
        ("choose-tree", null, null),
        ("clear-history", "clearhist", null),
        ("clear-prompt-history", "clearphist", null),
        ("clock-mode", null, null),
        ("command-prompt", null, null),
        ("confirm-before", "confirm", null),
        ("copy-mode", null, null),
        ("customize-mode", null, null),
        ("delete-buffer", "deleteb", null),
        ("detach-client", "detach", null),
        ("display-menu", "menu", null),
        ("display-message", "display", null),
        ("display-popup", "popup", null),
        ("display-panes", "displayp", null),
        ("find-window", "findw", null),
        ("has-session", "has", null),
        ("if-shell", "if", null),
        ("join-pane", "joinp", null),
        ("kill-pane", "killp", null),
        ("kill-server", null, null),
        ("kill-session", null, null),
        ("kill-window", "killw", null),
        ("last-pane", "lastp", null),
        ("last-window", "last", null),
        ("link-window", "linkw", null),
        ("list-buffers", "lsb", null),
        ("list-clients", "lsc", null),
        ("list-commands", "lscm", null),
        ("list-keys", "lsk", null),
        ("list-panes", "lsp", null),
        ("list-sessions", "ls", null),
        ("list-windows", "lsw", null),
        ("load-buffer", "loadb", null),
        ("lock-client", "lockc", null),
        ("lock-server", "lock", null),
        ("lock-session", "locks", null),
        ("move-pane", "movep", null),
        ("move-window", "movew", null),
        ("new-pane", "newp", TmuxVersion.Parse("3.7")),
        ("new-session", "new", null),
        ("new-window", "neww", null),
        ("next-layout", "nextl", null),
        ("next-window", "next", null),
        ("paste-buffer", "pasteb", null),
        ("pipe-pane", "pipep", null),
        ("previous-layout", "prevl", null),
        ("previous-window", "prev", null),
        ("refresh-client", "refresh", null),
        ("rename-session", "rename", null),
        ("rename-window", "renamew", null),
        ("resize-pane", "resizep", null),
        ("resize-window", "resizew", null),
        ("respawn-pane", "respawnp", null),
        ("respawn-window", "respawnw", null),
        ("rotate-window", "rotatew", null),
        ("run-shell", "run", null),
        ("save-buffer", "saveb", null),
        ("select-layout", "selectl", null),
        ("select-pane", "selectp", null),
        ("select-window", "selectw", null),
        ("send-keys", "send", null),
        ("send-prefix", null, null),
        ("server-access", null, null),
        ("set-buffer", "setb", null),
        ("set-environment", "setenv", null),
        ("set-hook", null, null),
        ("set-option", "set", null),
        ("set-window-option", "setw", null),
        ("show-buffer", "showb", null),
        ("show-environment", "showenv", null),
        ("show-hooks", null, null),
        ("show-messages", "showmsgs", null),
        ("show-options", "show", null),
        ("show-prompt-history", "showphist", null),
        ("show-window-options", "showw", null),
        ("source-file", "source", null),
        ("split-window", "splitw", null),
        ("start-server", "start", null),
        ("suspend-client", "suspendc", null),
        ("swap-pane", "swapp", null),
        ("swap-window", "swapw", null),
        ("switch-client", "switchc", null),
        ("switch-mode", null, TmuxVersion.Parse("3.8")),
        ("unbind-key", "unbind", null),
        ("unlink-window", "unlinkw", null),
        ("wait-for", "wait", null),
    ];

    /// <summary>Gets every command name and alias a release lists, for checking against <c>list-commands</c>.</summary>
    /// <param name="version">The release.</param>
    internal static IEnumerable<(string Name, string? Alias)> Listed(TmuxVersion version) =>
        Table.Where(entry => entry.Since is null || version >= entry.Since)
            .Select(static entry => (entry.Name, entry.Alias));

    /// <summary>Finds the command a word runs on a release.</summary>
    /// <param name="version">The daemon's version.</param>
    /// <param name="word">The first word of a command.</param>
    /// <returns>The command's full name, or null when the word is unknown or ambiguous.</returns>
    internal static string? Resolve(TmuxVersion version, string word)
    {
        if (word.Length == 0)
        {
            return null;
        }

        string? found = null;
        bool ambiguous = false;
        foreach ((string name, string? alias, TmuxVersion? since) in Table)
        {
            if (since is not null && version < since)
            {
                continue;
            }

            if (alias is not null && string.Equals(alias, word, StringComparison.Ordinal))
            {
                // An alias is as exact as a name, so it wins over a prefix
                // an earlier command already shared, as `set` does.
                found = name;
                ambiguous = false;
                break;
            }

            if (!name.StartsWith(word, StringComparison.Ordinal))
            {
                continue;
            }

            if (found is not null)
            {
                ambiguous = true;
            }

            found = name;
            if (word.Length == name.Length)
            {
                ambiguous = false;
                break;
            }
        }

        return ambiguous ? null : found;
    }
}
