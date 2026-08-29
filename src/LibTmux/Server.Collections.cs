using System.Runtime.Versioning;

using LibTmux.Internal;

namespace LibTmux;

// Session listings preserve historical any-failure leniency; window and pane
// listings tolerate only a missing daemon or socket.
public sealed partial class Server
{
    /// <summary>Reads every session on this server.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The sessions, empty when the listing fails.</returns>
    [UnsupportedOSPlatform("windows")]
    public Task<IReadOnlyList<Session>> GetSessionsAsync(
        CancellationToken cancellationToken = default) =>
        ListAsync(
            "list-sessions",
            [],
            static (owner, row) => RelationReader.ToSession(owner, row),
            LenientListPolicy.AnyFailure,
            cancellationToken);

    [UnsupportedOSPlatform("windows")]
    internal Task<IReadOnlyList<Session>> GetSessionsStrictAsync(
        CancellationToken cancellationToken = default) =>
        ListAsync(
            "list-sessions",
            [],
            static (owner, row) => RelationReader.ToSession(owner, row),
            LenientListPolicy.None,
            cancellationToken);

    /// <summary>Reads every session with at least one attached client.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The attached sessions, empty when the listing fails.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<IReadOnlyList<Session>> GetAttachedSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows = await ListRowsAsync(
                "list-sessions",
                [],
                LenientListPolicy.AnyFailure,
                cancellationToken)
            .ConfigureAwait(false);
        return
        [
            .. rows
                .Where(static row => row.TryGetValue("session_attached", out string? value)
                    && value is not null
                    && value != "0")
                .Select(row => RelationReader.ToSession(this, row)),
        ];
    }

    /// <summary>Reads every window on this server.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The windows, empty when no daemon or socket is present.</returns>
    [UnsupportedOSPlatform("windows")]
    public Task<IReadOnlyList<Window>> GetWindowsAsync(
        CancellationToken cancellationToken = default) =>
        ListAsync(
            "list-windows",
            ["-a"],
            static (owner, row) => RelationReader.ToWindow(owner, row),
            LenientListPolicy.MissingDaemonOrSocket,
            cancellationToken);

    [UnsupportedOSPlatform("windows")]
    internal Task<IReadOnlyList<Window>> GetWindowsStrictAsync(
        CancellationToken cancellationToken = default) =>
        ListAsync(
            "list-windows",
            ["-a"],
            static (owner, row) => RelationReader.ToWindow(owner, row),
            LenientListPolicy.None,
            cancellationToken);

    /// <summary>Reads every pane on this server.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The panes, empty when no daemon or socket is present.</returns>
    [UnsupportedOSPlatform("windows")]
    public Task<IReadOnlyList<Pane>> GetPanesAsync(
        CancellationToken cancellationToken = default) =>
        ListAsync(
            "list-panes",
            ["-a"],
            static (owner, row) => RelationReader.ToPane(owner, row),
            LenientListPolicy.MissingDaemonOrSocket,
            cancellationToken);

    [UnsupportedOSPlatform("windows")]
    internal Task<IReadOnlyList<Pane>> GetPanesStrictAsync(
        CancellationToken cancellationToken = default) =>
        ListAsync(
            "list-panes",
            ["-a"],
            static (owner, row) => RelationReader.ToPane(owner, row),
            LenientListPolicy.None,
            cancellationToken);

    [UnsupportedOSPlatform("windows")]
    private async Task<IReadOnlyList<T>> ListAsync<T>(
        string listCommand,
        IReadOnlyList<string> extraArguments,
        Func<Server, IReadOnlyDictionary<string, string?>, T> project,
        LenientListPolicy policy,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows =
            await ListRowsAsync(listCommand, extraArguments, policy, cancellationToken)
                .ConfigureAwait(false);
        return [.. rows.Select(row => project(this, row))];
    }

    [UnsupportedOSPlatform("windows")]
    private async Task<IReadOnlyList<IReadOnlyDictionary<string, string?>>> ListRowsAsync(
        string listCommand,
        IReadOnlyList<string> extraArguments,
        LenientListPolicy policy,
        CancellationToken cancellationToken)
    {
        try
        {
            return await RelationReader
                .ListAsync(this, listCommand, extraArguments, cancellationToken)
                .ConfigureAwait(false);
        }
        // Matches Python libtmux's leniency for a missing daemon or socket. A
        // handle that never captured a version throws InvalidOperationException,
        // which this catch does not see, so it always propagates.
        catch (LibTmuxException error) when (policy.Tolerates(error))
        {
            return [];
        }
    }

    private sealed class LenientListPolicy
    {
        private readonly bool _anyFailure;
        private readonly bool _missingDaemonOrSocket;

        private LenientListPolicy(bool anyFailure, bool missingDaemonOrSocket)
        {
            _anyFailure = anyFailure;
            _missingDaemonOrSocket = missingDaemonOrSocket;
        }

        internal static LenientListPolicy AnyFailure { get; } =
            new(anyFailure: true, missingDaemonOrSocket: true);

        internal static LenientListPolicy MissingDaemonOrSocket { get; } =
            new(anyFailure: false, missingDaemonOrSocket: true);

        internal static LenientListPolicy None { get; } =
            new(anyFailure: false, missingDaemonOrSocket: false);

        internal bool Tolerates(LibTmuxException error) =>
            _anyFailure || (_missingDaemonOrSocket && IsMissingDaemonOrSocket(error));

        private static bool IsMissingDaemonOrSocket(LibTmuxException error) =>
            error is TmuxCommandNotFoundException
            || (error is TmuxCommandException command
                && command.Result.StandardErrorLines.Any(static line =>
                    line.Contains("no server running", StringComparison.Ordinal)
                    || line.Contains("error connecting to", StringComparison.Ordinal)
                    || line.Contains("No such file or directory", StringComparison.Ordinal)));
    }
}
