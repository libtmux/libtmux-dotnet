using LibTmux.Support;

namespace LibTmux.Internal;

/// <summary>Refuses literal commands that a given tmux release mishandles.</summary>
/// <remarks>
/// <para>
/// Typed operations avoid these forms themselves. This sees every command a
/// request carries, including raw commands, batches and the commands of a
/// held control session, after the daemon's version has been checked and
/// before anything is sent. A command word is resolved the way tmux resolves
/// it, by alias, by name or by a unique prefix, and a <c>;</c> list is read
/// one command at a time. Shell text, and a tmux command nested in
/// <c>if-shell</c>, <c>run-shell -C</c> or a brace block, are not read.
/// </para>
/// </remarks>
internal static class TmuxFeatureGuard
{
    /// <summary>Gets the first release whose named-buffer delete and rename find their target exactly.</summary>
    internal static TmuxVersion NamedBuffers => Capabilities.SinceOf("buffer-delete-named");

    /// <summary>Throws when a command is unsafe on the checked release.</summary>
    /// <param name="version">The daemon's version.</param>
    /// <param name="commands">The commands about to be sent.</param>
    /// <exception cref="TmuxVersionTooLowException">A release too old for the command's form.</exception>
    /// <exception cref="ArgumentException">A name or layout the release would mishandle.</exception>
    internal static void Check(TmuxVersion version, IReadOnlyList<IReadOnlyList<string>> commands)
    {
        foreach (IReadOnlyList<string> line in commands)
        {
            foreach (IReadOnlyList<string> command in Split(line))
            {
                CheckOne(version, command);
            }
        }
    }

    private static void CheckOne(TmuxVersion version, IReadOnlyList<string> command)
    {
        switch (TmuxCommandNames.Resolve(version, command[0]))
        {
            case "break-pane":
                CheckBreakPane(version, command);
                CheckNames(version, Values(command, "Fnst", "n"));
                break;
            case "delete-buffer":
                if (Values(command, "b", "b").Any())
                {
                    RequireNamedBuffers(version, "Deleting a named buffer");
                }

                break;
            case "set-buffer":
                if (Values(command, "btn", "n").Any())
                {
                    RequireNamedBuffers(version, "Renaming a buffer", "buffer-rename");
                }

                break;
            case "run-shell":
                CheckRunShell(version, command);
                break;
            case "split-window":
                CheckSplit(version, command);
                break;
            case "select-layout":
                CheckLayout(version, command);
                break;
            case "new-window":
                CheckNames(version, Values(command, "cenFt", "n"));
                break;
            case "new-session":
                CheckNames(version, Values(command, "cefFnstxy", "ns"));
                break;
            case "rename-window":
            case "rename-session":
                CheckNames(version, Positionals(command, "t").Take(1));
                break;
        }
    }

    // tmux reads an argument that is only a semicolon, or that ends in one,
    // as the end of a command.
    private static IEnumerable<IReadOnlyList<string>> Split(IReadOnlyList<string> line)
    {
        List<string> current = [];
        foreach (string word in line)
        {
            bool ends = word.Length > 0 && word[^1] == ';' && !word.EndsWith("\\;", StringComparison.Ordinal);
            string text = ends ? word[..^1] : word;
            if (text.Length > 0)
            {
                current.Add(text);
            }

            if (ends && current.Count > 0)
            {
                yield return current;
                current = [];
            }
        }

        if (current.Count > 0)
        {
            yield return current;
        }
    }

    /// <summary>Throws unless the release finds a named buffer exactly.</summary>
    /// <param name="version">The daemon's version.</param>
    /// <param name="operation">What is being done, for the message.</param>
    /// <param name="capability">The registry id that decides the release.</param>
    internal static void RequireNamedBuffers(
        TmuxVersion version,
        string operation,
        string capability = "buffer-delete-named")
    {
        TmuxVersion required = Capabilities.SinceOf(capability);
        if (version < required)
        {
            throw new TmuxVersionTooLowException(
                $"{operation} needs tmux {required}: earlier releases act on the most recent "
                + $"buffer when the named one is missing; detected {version}.",
                required,
                version,
                TmuxDispatchState.NotDispatched);
        }
    }

    private static void CheckBreakPane(TmuxVersion version, IReadOnlyList<string> command)
    {
        WorkaroundClaim claim = Capabilities.Workaround("break-pane-misnames-windows");
        if (claim.Affects(version) && !Values(command, "Fnst", "n").Any())
        {
            throw new TmuxVersionTooLowException(
                $"break-pane without -n crashes tmux {version}; name the window or use tmux {claim.Until}.",
                claim.Until,
                version,
                TmuxDispatchState.NotDispatched);
        }
    }

    private static void CheckRunShell(TmuxVersion version, IReadOnlyList<string> command)
    {
        // A foreground job that names no pane has its output shown in a
        // view-mode pane on these releases, so there is nothing to return.
        WorkaroundClaim claim = Capabilities.Workaround("run-shell-output-in-copy-mode");
        if (claim.Affects(version) && !Values(command, "cdt", "bCt").Any())
        {
            throw new TmuxVersionTooLowException(
                $"Foreground run-shell output needs tmux {claim.Until} or a named pane: earlier releases "
                + $"show it in a pane instead of returning it; detected {version}.",
                claim.Until,
                version,
                TmuxDispatchState.NotDispatched);
        }
    }

    private static void CheckSplit(TmuxVersion version, IReadOnlyList<string> command)
    {
        WorkaroundClaim claim = Capabilities.Workaround("split-window-percent-size");
        if (claim.Affects(version) && Values(command, "cFelpt", "p").Any())
        {
            throw new TmuxVersionTooLowException(
                $"split-window -p is broken on tmux {version}; give the size with -l and a percent sign.",
                claim.Until,
                version,
                TmuxDispatchState.NotDispatched);
        }
    }

    private static void CheckLayout(TmuxVersion version, IReadOnlyList<string> command)
    {
        if (version >= Capabilities.SinceOf("layout-without-checksum") || Values(command, "t", "nopE").Any())
        {
            return;
        }

        if (Positionals(command, "t") is not [string layout, ..])
        {
            return;
        }

        if (!TmuxLayoutSyntax.IsValidCandidate(layout, 1))
        {
            throw new ArgumentException(
                $"tmux {version} crashes on the layout '{layout}'; it is not a checksummed "
                + "layout or an unambiguous preset name.");
        }
    }

    private static void CheckNames(TmuxVersion version, IEnumerable<string> names)
    {
        if (!Capabilities.Workaround("name-separators-rejected").Affects(version))
        {
            return;
        }

        foreach (string name in names)
        {
            if (name.AsSpan().IndexOfAny(':', '.') >= 0)
            {
                throw new ArgumentException(
                    $"tmux {version} rejects ':' and '.' in the name '{name}'.");
            }
        }
    }

    /// <summary>Reads the words after a command's leading flags.</summary>
    private static string[] Positionals(IReadOnlyList<string> command, string valueFlags)
    {
        int index = 1;
        for (; index < command.Count; index++)
        {
            string word = command[index];
            if (word == "--")
            {
                index++;
                break;
            }

            if (word.Length < 2 || word[0] != '-')
            {
                break;
            }

            for (int at = 1; at < word.Length; at++)
            {
                if (valueFlags.Contains(word[at], StringComparison.Ordinal))
                {
                    if (at == word.Length - 1)
                    {
                        index++;
                    }

                    break;
                }
            }
        }

        return [.. command.Skip(index)];
    }

    /// <summary>Reads the values of the wanted flags among a command's leading flags.</summary>
    /// <param name="command">The command and its words.</param>
    /// <param name="valueFlags">The flags whose next word is their value.</param>
    /// <param name="wanted">The flags to report; a flag with no value reports an empty one.</param>
    private static IEnumerable<string> Values(IReadOnlyList<string> command, string valueFlags, string wanted)
    {
        for (int index = 1; index < command.Count; index++)
        {
            string word = command[index];
            if (word == "--" || word.Length < 2 || word[0] != '-')
            {
                yield break;
            }

            for (int at = 1; at < word.Length; at++)
            {
                char flag = word[at];
                bool takesValue = valueFlags.Contains(flag, StringComparison.Ordinal);
                string value = !takesValue
                    ? string.Empty
                    : at < word.Length - 1
                        ? word[(at + 1)..]
                        : index + 1 < command.Count ? command[index + 1] : string.Empty;
                if (wanted.Contains(flag, StringComparison.Ordinal))
                {
                    yield return value;
                }

                if (takesValue)
                {
                    if (at == word.Length - 1)
                    {
                        index++;
                    }

                    break;
                }
            }
        }
    }
}
