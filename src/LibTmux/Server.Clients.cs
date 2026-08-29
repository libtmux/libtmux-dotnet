using System.Runtime.Versioning;
using LibTmux.Internal;
using Microsoft.Extensions.Logging;

namespace LibTmux;

// Lists and administers the clients attached to a server.
public sealed partial class Server
{
    private const string ClipboardQueryCapability = "refresh_client_clipboard_query";

    /// <summary>Reads the clients attached to this server.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The clients tmux reports.</returns>
    /// <remarks>
    /// A server with no clients is the ordinary case rather than a failure, so
    /// this answers empty when tmux cannot list them.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public async Task<IReadOnlyList<Client>> GetClientsAsync(
        CancellationToken cancellationToken = default)
    {
        ServerGeneration generation = _generation
            ?? throw new IncompleteSnapshotException("clients", SnapshotDepth.Server);
        TmuxConnection connection = Connection
            ?? throw new InvalidOperationException("The server handle has no connection.");
        try
        {
            IReadOnlyList<IReadOnlyDictionary<string, string?>> rows =
                await new MaterializationQuery(new MaterializationContext(this, ParsedVersion()))
                    .FetchAsync("list-clients", [], cancellationToken)
                    .ConfigureAwait(false);
            return [.. rows.Select(row => new Client(this, connection, generation, row))];
        }
        catch (LibTmuxException)
        {
            return [];
        }
    }

    /// <summary>Detaches one client.</summary>
    /// <param name="targetClient">The client, or null for the caller's own.</param>
    /// <param name="shellCommand">A command the detached client runs.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task DetachClientAsync(
        string? targetClient = null,
        string? shellCommand = null,
        CancellationToken cancellationToken = default) =>
        DetachAsync(targetClient, shellCommand, all: false, cancellationToken);

    /// <summary>Detaches every client except one.</summary>
    /// <param name="keepClient">The client to leave attached.</param>
    /// <param name="shellCommand">A command the detached clients run.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <remarks>
    /// tmux reads the kept client from the same target flag it uses to name a
    /// client, so this always spares one. Naming none does not mean "spare
    /// none": tmux falls back to whichever client it resolves for the caller,
    /// which from a process with no client of its own leaves that client
    /// attached and detaches nothing.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public Task DetachAllClientsAsync(
        string? keepClient = null,
        string? shellCommand = null,
        CancellationToken cancellationToken = default) =>
        DetachAsync(keepClient, shellCommand, all: true, cancellationToken);

    /// <summary>Locks one client.</summary>
    /// <param name="targetClient">The client, or null for the caller's own.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task LockClientAsync(
        string? targetClient = null,
        CancellationToken cancellationToken = default) =>
        RunClientAsync("lock-client", targetClient, cancellationToken);

    /// <summary>Suspends one client.</summary>
    /// <param name="targetClient">The client, or null for the caller's own.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task SuspendClientAsync(
        string? targetClient = null,
        CancellationToken cancellationToken = default) =>
        RunClientAsync("suspend-client", targetClient, cancellationToken);

    /// <summary>Redraws one client.</summary>
    /// <param name="targetClient">The client, or null for the caller's own.</param>
    /// <param name="requestClipboard">Whether the client is asked for its clipboard.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <remarks>
    /// Asking for the clipboard arrived in tmux 3.7. Older servers are asked to
    /// redraw and nothing else.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public Task RefreshClientAsync(
        string? targetClient = null,
        bool requestClipboard = false,
        CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["refresh-client"];
        if (requestClipboard && SupportsClipboardQuery())
        {
            arguments.Add("-l");
        }

        AddTargetClient(arguments, targetClient);
        return RunAsync(arguments, cancellationToken);
    }

    /// <summary>Switches the caller's client to another session.</summary>
    /// <param name="targetSession">The session to switch to.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task SwitchClientAsync(
        string targetSession,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSession);
        return RunAsync(["switch-client", "-t", targetSession], cancellationToken);
    }

    private static void AddTargetClient(List<string> arguments, string? targetClient)
    {
        if (!string.IsNullOrEmpty(targetClient))
        {
            arguments.Add("-t");
            arguments.Add(targetClient);
        }
    }

    [LoggerMessage(
        EventId = 20,
        Level = LogLevel.Warning,
        Message = "clipboard query flag omitted, tmux {TmuxVersion} does not carry it")]
    private static partial void LogClipboardQueryUnsupported(ILogger logger, string? tmuxVersion);

    // The version comes from state captured at connect, so gating costs no
    // extra tmux command and the redraw still dispatches once.
    private bool SupportsClipboardQuery()
    {
        if (Version is TmuxVersion version
            && TmuxCapabilities.IsSupported(version, ClipboardQueryCapability))
        {
            return true;
        }

        if (Connection?.Options.Logger is ILogger logger)
        {
            LogClipboardQueryUnsupported(logger, RawVersion);
        }

        return false;
    }

    private TmuxVersion ParsedVersion()
    {
        string raw = RawVersion
            ?? throw new InvalidOperationException("The server reported no tmux version.");
        return TmuxVersion.Parse(
            raw.StartsWith("tmux ", StringComparison.Ordinal) ? raw[5..] : raw);
    }

    [UnsupportedOSPlatform("windows")]
    private Task DetachAsync(
        string? targetClient,
        string? shellCommand,
        bool all,
        CancellationToken cancellationToken)
    {
        List<string> arguments = ["detach-client"];
        if (all)
        {
            arguments.Add("-a");
        }

        if (shellCommand is not null)
        {
            arguments.Add("-E");
            arguments.Add(shellCommand);
        }

        AddTargetClient(arguments, targetClient);
        return RunAsync(arguments, cancellationToken);
    }

    [UnsupportedOSPlatform("windows")]
    private Task RunClientAsync(
        string subcommand,
        string? targetClient,
        CancellationToken cancellationToken)
    {
        List<string> arguments = [subcommand];
        AddTargetClient(arguments, targetClient);
        return RunAsync(arguments, cancellationToken);
    }

    [UnsupportedOSPlatform("windows")]
    private async Task RunAsync(List<string> arguments, CancellationToken cancellationToken)
    {
        TmuxCommandResult result = await (Connection?.ServerDispatcher
                ?? throw new InvalidOperationException("The server handle has no connection."))
            .ExecuteAsync(arguments, cancellationToken)
            .ConfigureAwait(false);
        TmuxCommandFailure.ThrowIfFailed(result, arguments[0]);
    }
}
