using System.Globalization;
using System.Text;

namespace LibTmux.Internal;

/// <summary>Reads what <c>show-options</c> printed.</summary>
/// <remarks>
/// tmux escapes an option value the way its own parser reads it back, not
/// the way a shell quotes -- a tab returns as literal <c>\t</c> -- so
/// splitting on whitespace would lose it. Each line is cut once, at the
/// first space, and the remainder unescaped.
/// </remarks>
internal static class OptionParser
{
    /// <summary>Reads one option value.</summary>
    /// <param name="value">The unescaped text, or null when tmux reported none.</param>
    /// <returns>The value with whatever typed readings it supports.</returns>
    internal static TmuxOptionValue ParseValue(string? value)
    {
        if (value is null)
        {
            return new TmuxOptionValue(null, TmuxOptionState.Absent, null, null);
        }

        if (string.Equals(value, "on", StringComparison.Ordinal))
        {
            return new TmuxOptionValue(value, TmuxOptionState.On, true, null);
        }

        if (string.Equals(value, "off", StringComparison.Ordinal))
        {
            return new TmuxOptionValue(value, TmuxOptionState.Off, false, null);
        }

        // Only digits count as a number, matching how tmux itself reads
        // option integers.
        bool numeric = value.Length > 0;
        foreach (char character in value)
        {
            numeric &= char.IsAsciiDigit(character);
        }

        return numeric
            && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long number)
            ? new TmuxOptionValue(value, TmuxOptionState.Value, null, number)
            : new TmuxOptionValue(value, TmuxOptionState.Value, null, null);
    }

    /// <summary>Reads a run of option values.</summary>
    /// <param name="values">The unescaped texts, each possibly null.</param>
    /// <returns>One value per input, in order.</returns>
    internal static IReadOnlyList<TmuxOptionValue> ParseValues(IReadOnlyList<string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        TmuxOptionValue[] parsed = new TmuxOptionValue[values.Count];
        for (int index = 0; index < values.Count; index++)
        {
            parsed[index] = ParseValue(values[index]);
        }

        return parsed;
    }

    /// <summary>Reads the lines <c>show-options</c> printed.</summary>
    /// <param name="lines">The output lines.</param>
    /// <param name="doubleEscapedDollar">
    /// Whether this tmux escapes a dollar sign a second time on top of its own
    /// escaping, which tmux 3.4 does and its neighbours do not.
    /// </param>
    /// <returns>One option per non-empty line, in the order tmux printed them.</returns>
    internal static IReadOnlyList<TmuxOption> ParseRows(
        IReadOnlyList<string> lines,
        bool doubleEscapedDollar = false)
    {
        ArgumentNullException.ThrowIfNull(lines);
        List<TmuxOption> options = [];
        foreach (string line in lines)
        {
            if (ParseRow(line, forceIndex: false, doubleEscapedDollar) is TmuxOption option)
            {
                options.Add(option);
            }
        }

        return options;
    }

    /// <summary>Reads lines whose options are arrays even when tmux omits the index.</summary>
    /// <param name="rows">The output lines.</param>
    /// <returns>One option per non-empty line, each carrying an index.</returns>
    /// <remarks>
    /// Hooks are always arrays, and a hook with one entry prints without the
    /// <c>[0]</c> that a second entry would give it. Reading those lines as
    /// arrays keeps a hook the same shape however many entries it has.
    /// </remarks>
    internal static IReadOnlyList<TmuxOption> ParseSparse(IReadOnlyList<string> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        List<TmuxOption> options = [];
        foreach (string row in rows)
        {
            if (ParseRow(row, forceIndex: true) is TmuxOption option)
            {
                options.Add(option);
            }
        }

        return options;
    }

    /// <summary>Groups options into the structures tmux packs into their text.</summary>
    /// <param name="options">The options to group.</param>
    /// <returns>Each option name mapped to its structured value.</returns>
    /// <remarks>
    /// Three tmux options carry a table: <c>terminal-features</c> lists
    /// features per terminal; <c>terminal-overrides</c> maps capabilities
    /// per terminal; <c>command-alias</c> maps alias to command. Anything
    /// else is a lone value, or the sparse index map an array option is.
    /// </remarks>
    internal static IReadOnlyDictionary<string, object?> ParseComplex(
        IReadOnlyList<TmuxOption> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Dictionary<string, List<TmuxOption>> grouped = [];
        List<string> order = [];
        foreach (TmuxOption option in options)
        {
            if (!grouped.TryGetValue(option.Name, out List<TmuxOption>? entries))
            {
                entries = [];
                grouped[option.Name] = entries;
                order.Add(option.Name);
            }

            entries.Add(option);
        }

        Dictionary<string, object?> complex = [];
        foreach (string name in order)
        {
            List<TmuxOption> entries = grouped[name];
            complex[name] = name switch
            {
                "terminal-features" => BuildTerminalFeatures(entries),
                "terminal-overrides" => BuildTerminalOverrides(entries),
                "command-alias" => BuildCommandAliases(entries),
                _ => BuildPlain(entries),
            };
        }

        return complex;
    }

    /// <summary>Undoes the escaping tmux applies when it prints a value.</summary>
    /// <param name="value">The escaped text.</param>
    /// <returns>The text tmux was asked to store.</returns>
    /// <remarks>
    /// tmux quotes a value only when it has to, and never nests quotes, so one
    /// matching outer pair can be stripped without ambiguity: a value holding a
    /// double quote is single-quoted and the other way round.
    /// </remarks>
    internal static string Unescape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length >= 2
            && (value[0] == '"' || value[0] == '\'')
            && value[^1] == value[0])
        {
            value = value[1..^1];
        }

        return DecodeEscapes(value);
    }

    /// <summary>Decodes tmux's escaping without touching surrounding quotes.</summary>
    /// <remarks>
    /// tmux escapes option values and control-mode payloads the same way, but
    /// only an option value can be quoted. A payload that happens to start and
    /// end with a quote is data, so stripping stays with the caller that knows
    /// which of the two it is holding.
    /// </remarks>
    internal static string DecodeEscapes(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.Contains('\\', StringComparison.Ordinal))
        {
            return value;
        }

        StringBuilder text = new(value.Length);
        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] != '\\')
            {
                text.Append(value[index]);
                continue;
            }

            if (++index == value.Length)
            {
                text.Append('\\');
                break;
            }

            char escaped = value[index];
            if (escaped is >= '0' and <= '7')
            {
                int code = 0;
                int digits = 0;
                while (digits < 3 && index < value.Length && value[index] is >= '0' and <= '7')
                {
                    code = (code * 8) + (value[index] - '0');
                    index++;
                    digits++;
                }

                index--;
                text.Append((char)code);
                continue;
            }

            text.Append(escaped switch
            {
                'a' => '\a',
                'b' => '\b',
                'f' => '\f',
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                'v' => '\v',

                // A backslash before anything else is tmux protecting a
                // character it would otherwise have had to quote for.
                _ => escaped,
            });
        }

        return text.ToString();
    }

    private static TmuxOption? ParseRow(
        string line,
        bool forceIndex,
        bool doubleEscapedDollar = false)
    {
        if (string.IsNullOrEmpty(line))
        {
            return null;
        }

        int separator = line.IndexOf(' ', StringComparison.Ordinal);
        string name = separator < 0 ? line : line[..separator];
        string? value = separator < 0 ? null : Unescape(line[(separator + 1)..]);
        if (doubleEscapedDollar && value is not null)
        {
            // One decode leaves the backslash this tmux added on top of its
            // own escaping. Collapsing the survivor restores both an ordinary
            // dollar and a dollar the caller really did escape.
            value = value.Replace("\\$", "$", StringComparison.Ordinal);
        }

        if (name.Length == 0)
        {
            return null;
        }

        // An option read with the inherited flag is marked where it came from a
        // parent scope. The name is what a caller would set, so the marker is
        // not part of it.
        bool inherited = name[^1] == '*';
        if (inherited)
        {
            name = name[..^1];
        }

        int? index = null;
        if (name.Length > 2 && name[^1] == ']')
        {
            int open = name.LastIndexOf('[');
            if (open > 0
                && int.TryParse(
                    name.AsSpan(open + 1, name.Length - open - 2),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int parsed))
            {
                index = parsed;
                name = name[..open];
            }
        }

        return name.Length == 0
            ? null
            : new TmuxOption(name, ParseValue(value), index ?? (forceIndex ? 0 : null), inherited);
    }

    private static Dictionary<string, IReadOnlyList<string>> BuildTerminalFeatures(
        List<TmuxOption> entries)
    {
        Dictionary<string, IReadOnlyList<string>> features = [];
        foreach (string item in RawValues(entries))
        {
            int separator = item.IndexOf(':', StringComparison.Ordinal);
            if (separator > 0)
            {
                features[item[..separator]] = item[(separator + 1)..].Split(':');
            }
        }

        return features;
    }

    private static Dictionary<string, IReadOnlyDictionary<string, object?>> BuildTerminalOverrides(
        List<TmuxOption> entries)
    {
        Dictionary<string, Dictionary<string, object?>> overrides = [];
        foreach (string item in RawValues(entries))
        {
            string[] parts = item.Split(':');
            if (parts.Length == 0 || parts[0].Length == 0)
            {
                continue;
            }

            if (!overrides.TryGetValue(parts[0], out Dictionary<string, object?>? capabilities))
            {
                capabilities = [];
                overrides[parts[0]] = capabilities;
            }

            foreach (string capability in parts[1..])
            {
                if (capability.Length == 0)
                {
                    continue;
                }

                int assignment = capability.IndexOf('=', StringComparison.Ordinal);
                if (assignment < 0)
                {
                    capabilities[capability] = null;
                    continue;
                }

                string key = capability[..assignment];
                string text = capability[(assignment + 1)..];
                capabilities[key] = ParseValue(text).Integer ?? (object)text;
            }
        }

        return overrides.ToDictionary(
            static entry => entry.Key,
            static entry => (IReadOnlyDictionary<string, object?>)entry.Value,
            StringComparer.Ordinal);
    }

    private static Dictionary<string, string> BuildCommandAliases(List<TmuxOption> entries)
    {
        Dictionary<string, string> aliases = [];
        foreach (string item in RawValues(entries))
        {
            int assignment = item.IndexOf('=', StringComparison.Ordinal);
            if (assignment > 0)
            {
                aliases[item[..assignment]] = item[(assignment + 1)..];
            }
        }

        return aliases;
    }

    private static object? BuildPlain(List<TmuxOption> entries) =>
        entries.Count == 1 && entries[0].Index is null
            ? entries[0].Value
            : entries.ToDictionary(
                static entry => entry.Index ?? 0,
                static entry => entry.Value);

    private static IEnumerable<string> RawValues(List<TmuxOption> entries)
    {
        foreach (TmuxOption entry in entries)
        {
            if (entry.Value.Raw is string raw && raw.Length > 0)
            {
                yield return raw;
            }
        }
    }
}
