using System.Diagnostics.Tracing;
using System.Text;

namespace LibTmux.Internal;

/// <summary>Carries what one connection's tmux commands are recorded and bounded by.</summary>
/// <remarks>
/// Every tmux command a caller makes passes through one dispatcher, so what it
/// records is decided once here rather than at each of the hundreds of call
/// sites. The socket travels with the sink and is written on every event and
/// span, so two servers in one process stay tellable apart in one log.
/// </remarks>
internal sealed class TmuxCommandContext
{
    internal TmuxCommandContext(
        Action<TmuxLogEntry>? logSink,
        string? socket,
        TimeSpan? commandTimeout = null)
    {
        LogSink = logSink;
        Socket = socket;
        CommandTimeout = commandTimeout;
    }

    /// <summary>Gets the sink tmux commands are recorded through, when one is set.</summary>
    internal Action<TmuxLogEntry>? LogSink { get; }

    /// <summary>Gets the socket the commands are sent to, when one is named.</summary>
    internal string? Socket { get; }

    /// <summary>Gets how long one command may run before it is abandoned.</summary>
    internal TimeSpan? CommandTimeout { get; }
}

/// <summary>Records what tmux was asked and what it answered.</summary>
/// <remarks>
/// The keys are stable scalars so that a log aggregator can filter and group on
/// them. Everything that can carry a payload is truncated, the command line
/// included: a capture runs to megabytes, and setting a buffer puts whatever
/// was copied into the arguments.
/// </remarks>
internal static class TmuxLog
{
    /// <summary>How much of tmux's output is worth keeping in a log line.</summary>
    internal const int OutputLimit = 512;

    internal static void CommandCompleted(
        TmuxCommandContext? context,
        IReadOnlyList<string> arguments,
        TmuxCommandResult result)
    {
        if (context?.LogSink is not { } sink)
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(result);
        string subcommand = arguments.Count > 0 ? arguments[0] : string.Empty;
        if (result.ExitCode != 0)
        {
            // A failure is worth recording whatever the level, and what tmux
            // said about it is the only part of the output that explains it.
            Write(
                sink,
                EventLevel.Error,
                101,
                "tmux {TmuxSubcommand} on {TmuxSocket} failed: exit {TmuxExitCode}: {TmuxStderr}",
                ("TmuxSubcommand", subcommand),
                ("TmuxSocket", context.Socket),
                ("TmuxExitCode", result.ExitCode),
                ("TmuxStderr", JoinTruncated('\n', result.StandardErrorLines)));
            return;
        }

        Write(
            sink,
            EventLevel.Verbose,
            100,
            "tmux {TmuxSubcommand} on {TmuxSocket} completed: exit {TmuxExitCode}, {TmuxStdoutLen} lines from {TmuxCmd}: {TmuxStdout}",
            ("TmuxSubcommand", subcommand),
            ("TmuxSocket", context.Socket),
            ("TmuxExitCode", result.ExitCode),
            ("TmuxStdoutLen", result.StandardOutputLines.Count),
            ("TmuxCmd", JoinTruncated(' ', arguments)),
            ("TmuxStdout", JoinTruncated('\n', result.StandardOutputLines)));
    }

    internal static string Truncate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length <= OutputLimit ? text : text[..OutputLimit];
    }

    /// <summary>Emits one record to <paramref name="sink" />, fields in template order.</summary>
    internal static void Write(
        Action<TmuxLogEntry> sink,
        EventLevel level,
        int eventId,
        string template,
        params (string Name, object? Value)[] fields)
    {
        var copy = new KeyValuePair<string, object?>[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            copy[i] = new KeyValuePair<string, object?>(fields[i].Name, fields[i].Value);
        }

        sink(new TmuxLogEntry(level, eventId, template, copy));
    }

    /// <summary>Joins until the output limit, so a megabyte capture is not copied to be cut.</summary>
    private static string JoinTruncated(char separator, IReadOnlyList<string> parts)
    {
        var text = new StringBuilder();
        for (int i = 0; i < parts.Count; i++)
        {
            if (i > 0)
            {
                text.Append(separator);
            }

            int room = OutputLimit - text.Length;
            if (room <= 0)
            {
                break;
            }

            text.Append(parts[i], 0, Math.Min(room, parts[i].Length));
        }

        return Truncate(text.ToString());
    }
}
