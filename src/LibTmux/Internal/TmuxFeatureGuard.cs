namespace LibTmux.Internal;

/// <summary>Refuses literal commands that a given tmux release mishandles.</summary>
/// <remarks>
/// <para>
/// Typed operations avoid these forms themselves. This sees every command a
/// request carries, including raw commands and batches, after the daemon's
/// version has been checked and before anything is sent. Nothing here reads
/// shell text or the body of a user-authored program.
/// </para>
/// </remarks>
internal static class TmuxFeatureGuard
{
    private static readonly TmuxVersion Version34 = TmuxVersion.Parse("3.4");
    private static readonly TmuxVersion Version35 = TmuxVersion.Parse("3.5");
    private static readonly TmuxVersion Version37 = TmuxVersion.Parse("3.7");
    private static readonly TmuxVersion Version37a = TmuxVersion.Parse("3.7a");

    /// <summary>Gets the first release whose named-buffer delete and rename find their target exactly.</summary>
    internal static TmuxVersion NamedBuffers => Version34;

    /// <summary>Throws when a command is unsafe on the checked release.</summary>
    /// <param name="version">The daemon's version.</param>
    /// <param name="commands">The commands about to be sent.</param>
    /// <exception cref="TmuxVersionTooLowException">A release too old for the command's form.</exception>
    /// <exception cref="ArgumentException">A name or layout the release would mishandle.</exception>
    internal static void Check(TmuxVersion version, IReadOnlyList<IReadOnlyList<string>> commands)
    {
        foreach (IReadOnlyList<string> command in commands)
        {
            if (command.Count == 0)
            {
                continue;
            }

            string word = command[0];
            if (Is(word, "break-pane", "breakp", 2))
            {
                CheckBreakPane(version, command);
                CheckNames(version, Values(command, "Fnst", "n"));
            }
            else if (Is(word, "delete-buffer", "deleteb", 3))
            {
                if (Values(command, "b", "b").Any())
                {
                    RequireNamedBuffers(version, "Deleting a named buffer");
                }
            }
            else if (Is(word, "set-buffer", "setb", 5))
            {
                if (Values(command, "btn", "n").Any())
                {
                    RequireNamedBuffers(version, "Renaming a buffer");
                }
            }
            else if (Is(word, "split-window", "splitw", 2))
            {
                CheckSplit(version, command);
            }
            else if (Is(word, "select-layout", "selectl", 8))
            {
                CheckLayout(version, command);
            }
            else if (Is(word, "new-window", "neww", 5))
            {
                CheckNames(version, Values(command, "cenFt", "n"));
            }
            else if (Is(word, "new-session", "new", 3))
            {
                CheckNames(version, Values(command, "cefFnstxy", "ns"));
            }
            else if (Is(word, "rename-window", "renamew", 8) || Is(word, "rename-session", "rename", 8))
            {
                CheckNames(version, Positionals(command, "t").Take(1));
            }
        }
    }

    /// <summary>Throws unless the release finds a named buffer exactly.</summary>
    /// <param name="version">The daemon's version.</param>
    /// <param name="operation">What is being done, for the message.</param>
    internal static void RequireNamedBuffers(TmuxVersion version, string operation)
    {
        if (version < NamedBuffers)
        {
            throw new TmuxVersionTooLowException(
                $"{operation} needs tmux {NamedBuffers}: earlier releases act on the most recent "
                + $"buffer when the named one is missing; detected {version}.",
                NamedBuffers,
                version,
                TmuxDispatchState.NotDispatched);
        }
    }

    private static void CheckBreakPane(TmuxVersion version, IReadOnlyList<string> command)
    {
        if (version >= Version37 && version < Version37a && !Values(command, "Fnst", "n").Any())
        {
            throw new TmuxVersionTooLowException(
                $"break-pane without -n crashes tmux {version}; name the window or use tmux {Version37a}.",
                Version37a,
                version,
                TmuxDispatchState.NotDispatched);
        }
    }

    private static void CheckSplit(TmuxVersion version, IReadOnlyList<string> command)
    {
        if (version >= Version34 && version < Version35 && Values(command, "cFelpt", "p").Any())
        {
            throw new TmuxVersionTooLowException(
                $"split-window -p is broken on tmux {version}; give the size with -l and a percent sign.",
                Version35,
                version,
                TmuxDispatchState.NotDispatched);
        }
    }

    private static void CheckLayout(TmuxVersion version, IReadOnlyList<string> command)
    {
        if (version >= Version34 || Values(command, "t", "nopE").Any())
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
        if (version != Version37)
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

    private static bool Is(string word, string full, string alias, int shortest) =>
        word == alias || (word.Length >= shortest && full.StartsWith(word, StringComparison.Ordinal));

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
