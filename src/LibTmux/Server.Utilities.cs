using System.Runtime.Versioning;
using LibTmux.Internal;
using Microsoft.Extensions.Logging;

namespace LibTmux;

/// <summary>What <c>show-messages</c> should list.</summary>
public enum ShowMessagesMode
{
    /// <summary>The server's own message log.</summary>
    Messages,

    /// <summary>The jobs the server is running.</summary>
    Jobs,

    /// <summary>What the server knows about attached terminals.</summary>
    Terminals,
}
// Server utilities omit unsupported commands and warn when optional flags must
// be downgraded.
public sealed partial class Server
{
    [LoggerMessage(
        EventId = 21,
        Level = LogLevel.Warning,
        Message = "key listing format omitted, tmux {TmuxVersion} does not carry it")]
    private static partial void LogListKeysFormat(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 22,
        Level = LogLevel.Warning,
        Message = "prompt literal flag omitted, tmux {TmuxVersion} does not carry it")]
    private static partial void LogPromptLiteral(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 23,
        Level = LogLevel.Warning,
        Message = "prompt exit and redraw flags omitted, tmux {TmuxVersion} does not carry them")]
    private static partial void LogPrompt37(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 24,
        Level = LogLevel.Warning,
        Message = "confirmation key and default omitted, tmux {TmuxVersion} does not carry them")]
    private static partial void LogConfirmAcceptance(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 41,
        Level = LogLevel.Warning,
        Message = "message target client omitted, tmux {TmuxVersion} does not carry it")]
    private static partial void LogMessageClient(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 25,
        Level = LogLevel.Warning,
        Message = "menu mouse flag omitted, tmux {TmuxVersion} does not carry it")]
    private static partial void LogMenuMouse(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 26,
        Level = LogLevel.Warning,
        Message = "menu style flags omitted, tmux {TmuxVersion} does not carry them")]
    private static partial void LogMenuStyles(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 27,
        Level = LogLevel.Warning,
        Message = "message literal flag omitted, tmux {TmuxVersion} does not carry it")]
    private static partial void LogMessageLiteral(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 28,
        Level = LogLevel.Warning,
        Message = "shell error output flag omitted, tmux {TmuxVersion} does not carry it")]
    private static partial void LogRunShellStandardError(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 29,
        Level = LogLevel.Warning,
        Message = "shell working directory omitted, tmux {TmuxVersion} does not carry it")]
    private static partial void LogRunShellWorkingDirectory(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 30,
        Level = LogLevel.Warning,
        Message = "shell arguments omitted, tmux {TmuxVersion} passes them through a shell")]
    private static partial void LogRunShellArguments(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 31,
        Level = LogLevel.Warning,
        Message = "tmux refused to render the message: {Reported}")]
    private static partial void LogDisplayMessageRefused(ILogger logger, string reported);

    private bool Supports(string capability) =>
        Version is TmuxVersion version
        && TmuxCapabilities.IsSupported(version, capability);

    private bool SupportsMenuStyles() =>
        RequiresCapability(ServerUtilities.DisplayMenuStylesCapability, LogMenuStyles);

    private bool RequiresCapability(string capability, Action<ILogger, string?> log)
    {
        if (Supports(capability))
        {
            return true;
        }

        if (Connection?.Options.Logger is ILogger logger)
        {
            log(logger, RawVersion);
        }

        return false;
    }

    private void RequireCommand(string capability, string command)
    {
        if (Supports(capability))
        {
            return;
        }

        // The whole command is missing rather than one of its flags, so there
        // is nothing to send that would mean the same thing.
        throw new TmuxVersionTooLowException(
            $"The tmux command '{command}' requires tmux 3.3a.",
            TmuxVersion.Parse("3.3a"),
            Version ?? default);
    }

    [UnsupportedOSPlatform("windows")]
    private async Task RunUtilityAsync(
        List<string> arguments,
        CancellationToken cancellationToken)
    {
        TmuxCommandResult result = await _commandDispatcher
            .ExecuteAsync(arguments, cancellationToken)
            .ConfigureAwait(false);
        TmuxCommandFailure.ThrowIfFailed(result, arguments[0]);
    }

    [UnsupportedOSPlatform("windows")]
    private async Task<IReadOnlyList<string>> ReadUtilityAsync(
        List<string> arguments,
        CancellationToken cancellationToken)
    {
        TmuxCommandResult result = await _commandDispatcher
            .ExecuteAsync(arguments, cancellationToken)
            .ConfigureAwait(false);
        TmuxCommandFailure.ThrowIfFailed(result, arguments[0]);
        return result.StandardOutputLines;
    }
}
