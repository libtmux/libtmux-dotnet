using System.Diagnostics.Tracing;
using System.Runtime.Versioning;
using LibTmux.Internal;

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
    private static void LogListKeysFormat(Action<TmuxLogEntry> sink, string? tmuxVersion) =>
        TmuxLog.Write(
            sink,
            EventLevel.Warning,
            21,
            "key listing format omitted, tmux {TmuxVersion} does not carry it",
            ("TmuxVersion", tmuxVersion));

    private static void LogPromptLiteral(Action<TmuxLogEntry> sink, string? tmuxVersion) =>
        TmuxLog.Write(
            sink,
            EventLevel.Warning,
            22,
            "prompt literal flag omitted, tmux {TmuxVersion} does not carry it",
            ("TmuxVersion", tmuxVersion));

    private static void LogPrompt37(Action<TmuxLogEntry> sink, string? tmuxVersion) =>
        TmuxLog.Write(
            sink,
            EventLevel.Warning,
            23,
            "prompt exit and redraw flags omitted, tmux {TmuxVersion} does not carry them",
            ("TmuxVersion", tmuxVersion));

    private static void LogConfirmAcceptance(Action<TmuxLogEntry> sink, string? tmuxVersion) =>
        TmuxLog.Write(
            sink,
            EventLevel.Warning,
            24,
            "confirmation key and default omitted, tmux {TmuxVersion} does not carry them",
            ("TmuxVersion", tmuxVersion));

    private static void LogMessageClient(Action<TmuxLogEntry> sink, string? tmuxVersion) =>
        TmuxLog.Write(
            sink,
            EventLevel.Warning,
            41,
            "message target client omitted, tmux {TmuxVersion} does not carry it",
            ("TmuxVersion", tmuxVersion));

    private static void LogMenuMouse(Action<TmuxLogEntry> sink, string? tmuxVersion) =>
        TmuxLog.Write(
            sink,
            EventLevel.Warning,
            25,
            "menu mouse flag omitted, tmux {TmuxVersion} does not carry it",
            ("TmuxVersion", tmuxVersion));

    private static void LogMenuStyles(Action<TmuxLogEntry> sink, string? tmuxVersion) =>
        TmuxLog.Write(
            sink,
            EventLevel.Warning,
            26,
            "menu style flags omitted, tmux {TmuxVersion} does not carry them",
            ("TmuxVersion", tmuxVersion));

    private static void LogMessageLiteral(Action<TmuxLogEntry> sink, string? tmuxVersion) =>
        TmuxLog.Write(
            sink,
            EventLevel.Warning,
            27,
            "message literal flag omitted, tmux {TmuxVersion} does not carry it",
            ("TmuxVersion", tmuxVersion));

    private static void LogRunShellStandardError(Action<TmuxLogEntry> sink, string? tmuxVersion) =>
        TmuxLog.Write(
            sink,
            EventLevel.Warning,
            28,
            "shell error output flag omitted, tmux {TmuxVersion} does not carry it",
            ("TmuxVersion", tmuxVersion));

    private static void LogRunShellWorkingDirectory(Action<TmuxLogEntry> sink, string? tmuxVersion) =>
        TmuxLog.Write(
            sink,
            EventLevel.Warning,
            29,
            "shell working directory omitted, tmux {TmuxVersion} does not carry it",
            ("TmuxVersion", tmuxVersion));

    private static void LogRunShellArguments(Action<TmuxLogEntry> sink, string? tmuxVersion) =>
        TmuxLog.Write(
            sink,
            EventLevel.Warning,
            30,
            "shell arguments omitted, tmux {TmuxVersion} passes them through a shell",
            ("TmuxVersion", tmuxVersion));

    private static void LogDisplayMessageRefused(Action<TmuxLogEntry> sink, string reported) =>
        TmuxLog.Write(
            sink,
            EventLevel.Warning,
            31,
            "tmux refused to render the message: {Reported}",
            ("Reported", reported));

    private bool Supports(string capability) =>
        Version is TmuxVersion version
        && TmuxCapabilities.IsSupported(version, capability);

    private bool SupportsMenuStyles() =>
        RequiresCapability(ServerUtilities.DisplayMenuStylesCapability, LogMenuStyles);

    private bool RequiresCapability(string capability, Action<Action<TmuxLogEntry>, string?> log)
    {
        if (Supports(capability))
        {
            return true;
        }

        if (Connection?.Options.LogSink is { } sink)
        {
            log(sink, RawVersion);
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
            $"The tmux command '{command}' requires tmux 3.3.",
            TmuxVersion.Parse("3.3"),
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
