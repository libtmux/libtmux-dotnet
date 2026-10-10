using System.Runtime.Versioning;

namespace LibTmux.Internal;

internal static class TmuxOwnershipIdentity
{
    internal const string Option = "@libtmux_owner_generation";
    internal const string Format = "#{" + Option + "}";

    internal static IReadOnlyList<IReadOnlyList<string>> InitializeCommands()
    {
        string token = Guid.NewGuid().ToString("N");
        // Braces in a regex quantifier are interpreted by tmux's format parser.
        string pattern = "^" + string.Concat(Enumerable.Repeat("[0-9a-fA-F]", 32)) + "$";
        return [
            ["set-option", "-soq", Option, token],
            ["if-shell", "-F", "#{m/r:" + pattern + "," + Format + "}", string.Empty, "libtmux_invalid_owner_generation"]];
    }

    internal static void Validate(string value)
    {
        if (value.Length != 32 || value.Any(static c => !char.IsAsciiHexDigit(c)))
        {
            throw new TmuxProtocolException("The reserved server option @libtmux_owner_generation must contain exactly 32 ASCII hexadecimal characters.", TmuxDispatchState.Dispatched);
        }
    }

    [UnsupportedOSPlatform("windows")]
    internal static async Task<Server> CaptureAsync(Server server, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TmuxConnection connection = server.Connection
            ?? throw new InvalidOperationException("The server has no connection identity.");
        if (connection.OwnershipToken is not null)
        {
            return server;
        }
        TmuxCommandDispatcher dispatcher = server.Generation is { } expected
            ? connection.CreateEntityDispatcher(expected) : connection.CreateInspectionDispatcher();
        TmuxCommandResult result = await dispatcher.ExecuteGroupAsync([
            .. InitializeCommands(),
            ["display-message", "-p", TmuxConnection.GenerationFormat + "\t#{version}\t" + Format]], cancellationToken).ConfigureAwait(false);
        if (TmuxCommandFailure.NamesMissingServer(result))
        {
            throw new TmuxObjectNotFoundException("No daemon is listening at the endpoint.", connection.SocketPath);
        }
        TmuxCommandFailure.ThrowIfFailed(result, "ownership identity capture");
        if (result.StandardOutputLines.Count != 1)
        {
            throw new TmuxCommandException("tmux did not report exactly one ownership identity.", result);
        }
        string[] fields = result.StandardOutputLines[0].Split('\t');
        if (fields.Length != 3 || !TmuxVersion.TryParse(fields[1], out TmuxVersion version))
        {
            throw new TmuxCommandException("tmux reported a malformed ownership identity.", result);
        }
        Validate(fields[2]);
        cancellationToken.ThrowIfCancellationRequested();
        return new Server(connection.WithOwnershipToken(fields[2]), TmuxConnection.ParseGeneration(fields[0]), connection.VerifiedRawVersion, version);
    }
}
