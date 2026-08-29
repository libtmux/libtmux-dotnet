using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace LibTmux.Internal;

/// <summary>Carries the logger a run of tmux commands is recorded through.</summary>
/// <remarks>
/// Every tmux command a caller makes passes through one dispatcher, so what it
/// records is decided once here rather than at each of the hundreds of call
/// sites. Holding the logger alongside the socket it belongs to also keeps two
/// servers in one process from writing each other's history.
/// </remarks>
internal sealed class TmuxCommandContext
{
    internal TmuxCommandContext(ILogger logger, string? socket)
    {
        ArgumentNullException.ThrowIfNull(logger);
        Logger = logger;
        Socket = socket;
    }

    /// <summary>Gets the logger tmux commands are recorded through.</summary>
    public ILogger Logger { get; }

    /// <summary>Gets the socket the commands are sent to, when one is named.</summary>
    internal string? Socket { get; }
}

/// <summary>Records what tmux was asked and what it answered.</summary>
/// <remarks>
/// The keys are stable scalars so that a log aggregator can filter and group on
/// them. Everything that can carry a payload is truncated, the command line
/// included: a capture runs to megabytes, and setting a buffer puts whatever
/// was copied into the arguments.
/// </remarks>
internal static partial class TmuxLog
{
    /// <summary>How much of tmux's output is worth keeping in a log line.</summary>
    internal const int OutputLimit = 512;

    [SuppressMessage(
        "Performance",
        "CA1873:Avoid potentially expensive logging",
        Justification = "Each call is already behind an explicit level check.")]
    internal static void CommandCompleted(
        TmuxCommandContext? context,
        IReadOnlyList<string> arguments,
        TmuxCommandResult result)
    {
        if (context?.Logger is not ILogger logger)
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(result);
        string subcommand = arguments.Count > 0 ? arguments[0] : string.Empty;
        if (result.ExitCode != 0)
        {
            if (!logger.IsEnabled(LogLevel.Error))
            {
                return;
            }

            // A failure is worth recording whatever the level, and what tmux
            // said about it is the only part of the output that explains it.
            LogCommandFailed(
                logger,
                subcommand,
                result.ExitCode,
                Truncate(string.Join('\n', result.StandardErrorLines)));
            return;
        }

        if (!logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        LogCommandCompleted(
            logger,
            subcommand,
            Truncate(string.Join(' ', arguments)),
            result.ExitCode,
            result.StandardOutputLines.Count,
            Truncate(string.Join('\n', result.StandardOutputLines)));
    }

    internal static string Truncate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length <= OutputLimit ? text : text[..OutputLimit];
    }

    [LoggerMessage(
        EventId = 100,
        Level = LogLevel.Debug,
        Message = "tmux {TmuxSubcommand} completed: exit {TmuxExitCode}, {TmuxStdoutLen} lines from {TmuxCmd}: {TmuxStdout}")]
    private static partial void LogCommandCompleted(
        ILogger logger,
        string tmuxSubcommand,
        string tmuxCmd,
        int tmuxExitCode,
        int tmuxStdoutLen,
        string tmuxStdout);

    [LoggerMessage(
        EventId = 101,
        Level = LogLevel.Error,
        Message = "tmux {TmuxSubcommand} failed: exit {TmuxExitCode}: {TmuxStderr}")]
    private static partial void LogCommandFailed(
        ILogger logger,
        string tmuxSubcommand,
        int tmuxExitCode,
        string tmuxStderr);
}
