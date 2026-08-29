using System.Globalization;
using System.Runtime.Versioning;
using LibTmux.Internal;
using Microsoft.Extensions.Logging;

namespace LibTmux;

public sealed partial class Server
{
    /// <summary>Builds the arguments a message request sends.</summary>
    /// <remarks>
    /// This stays on the server because two of the flags depend on which tmux
    /// is answering: literal expansion arrived in 3.4, and 3.2a refuses the
    /// target-client flag outright. A chained message has to be built the same
    /// way a direct one is.
    /// </remarks>
    internal List<string> BuildDisplayMessageArguments(DisplayMessageRequest request)
    {
        List<string> arguments = ["display-message"];
        ServerUtilities.AddFlag(arguments, request.ReturnText, "-p");
        ServerUtilities.AddFlag(arguments, request.AllFormats, "-a");
        ServerUtilities.AddFlag(arguments, request.Verbose, "-v");
        if (request.NoExpand
            && RequiresCapability(
                ServerUtilities.DisplayMessageLiteralCapability,
                LogMessageLiteral))
        {
            arguments.Add("-l");
        }

        ServerUtilities.AddFlag(arguments, request.Notify, "-N");
        if (request.TargetClient is not null
            && RequiresCapability(
                ServerUtilities.DisplayMessageClientCapability,
                LogMessageClient))
        {
            // tmux 3.2a prints its usage and refuses the command, even for a
            // client that is really attached. Its usage text advertises the
            // flag anyway, so only running it tells the truth.
            ServerUtilities.AddValue(arguments, "-c", request.TargetClient);
        }
        ServerUtilities.AddValue(
            arguments,
            "-d",
            request.Delay is TimeSpan delay
                ? ((long)delay.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)
                : null);
        ServerUtilities.AddValue(arguments, "-F", request.Format);
        if (request.Message.Length > 0)
        {
            arguments.Add(request.Message);
        }

        return arguments;
    }

    /// <summary>Shows a message on a client.</summary>
    /// <param name="request">What to show, and how.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The rendered text when it was asked for, and null otherwise.</returns>
    /// <remarks>
    /// tmux reports a bad format on its error stream rather than by failing, so
    /// a message it would not render is logged and answered with nothing.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public async Task<IReadOnlyList<string>?> DisplayMessageAsync(
        DisplayMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<string> arguments = BuildDisplayMessageArguments(request);

        TmuxCommandResult result = await _commandDispatcher
            .ExecuteAsync(arguments, cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode == 0)
        {
            return request.ReturnText ? result.StandardOutputLines : null;
        }

        if (Connection?.Options.Logger is ILogger logger)
        {
            LogDisplayMessageRefused(logger, string.Join('\n', result.StandardErrorLines));
        }

        return null;
    }
}
