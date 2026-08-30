using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace LibTmux;

// Displays messages through this window.
public sealed partial class Window
{
    private const string DisplayMessageLiteralCapability = "display_message_literal";

    /// <summary>Shows a message on the client viewing this window.</summary>
    /// <param name="request">The message to show.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The printed lines when the request asked for them, else null.</returns>
    /// <exception cref="ArgumentException">
    /// The request asks to redraw the pane, which only a pane can honour.
    /// </exception>
    /// <remarks>
    /// A message with no client to show it on is not a failure, so tmux's
    /// complaint is logged rather than raised.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public async Task<IReadOnlyList<string>?> DisplayMessageAsync(
        DisplayMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.UpdatePane)
        {
            throw new ArgumentException(
                "Redrawing while a message is shown is pane-scoped.",
                nameof(request));
        }

        Server owner = RequireOwner("display");
        if (request.TargetClient is not null
            && owner.Version is TmuxVersion version
            && version < TmuxVersion.Parse("3.3a"))
        {
            // tmux 3.2a declares the flag without a value, so naming a client
            // there would silently address a different one.
            throw new TmuxVersionTooLowException(
                "Naming a display-message client requires tmux 3.3a.",
                TmuxVersion.Parse("3.3a"),
                owner.Version ?? default);
        }

        List<string> arguments = ["display-message", "-t", Target];
        if (request.ReturnText)
        {
            arguments.Add("-p");
        }

        if (request.AllFormats)
        {
            arguments.Add("-a");
        }

        if (request.Verbose)
        {
            arguments.Add("-v");
        }

        if (request.NoExpand && RequireLiteralMessages(owner))
        {
            arguments.Add("-l");
        }

        if (request.Notify)
        {
            arguments.Add("-N");
        }

        AddValue(arguments, "-c", request.TargetClient);
        AddValue(
            arguments,
            "-d",
            request.Delay is TimeSpan delay
                ? ((long)delay.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)
                : null);
        AddValue(arguments, "-F", request.Format);
        if (request.Message.Length > 0)
        {
            arguments.Add(request.Message);
        }

        TmuxCommandResult result = await _commandDispatcher
            .ExecuteAsync(arguments, cancellationToken)
            .ConfigureAwait(false);
        if (result.StandardErrorLines.Count > 0
            && owner.Connection?.Options.Logger is ILogger logger)
        {
            LogDisplayMessageRefused(logger, string.Join('\n', result.StandardErrorLines));
        }

        return request.ReturnText ? result.StandardOutputLines : null;
    }

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "literal message flag omitted, tmux {TmuxVersion} does not carry it")]
    private static partial void LogLiteralUnsupported(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Warning,
        Message = "tmux refused to display the message: {TmuxError}")]
    private static partial void LogDisplayMessageRefused(ILogger logger, string tmuxError);

    // The version comes from state captured when the handle materialized, so
    // gating costs no extra tmux command and the call still dispatches once.
    private static bool RequireLiteralMessages(Server owner)
    {
        if (Supports(owner, DisplayMessageLiteralCapability))
        {
            return true;
        }

        Warn(owner, LogLiteralUnsupported);
        return false;
    }
}
