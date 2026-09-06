using LibTmux.Internal;
using ModelContextProtocol;

namespace LibTmux.Mcp;

/// <summary>Coordinates pane input across MCP tool instances in this process.</summary>
internal static class PaneRunRegistry
{
    private static readonly object Gate = new();
    private static readonly Dictionary<PaneRunIdentity, Reservation> Active = [];

    internal static PaneRunLease Acquire(Pane pane)
    {
        PaneRunIdentity identity = PaneRunIdentity.For(pane);
        Guid token = Guid.NewGuid();
        lock (Gate)
        {
            if (Active.TryGetValue(identity, out Reservation occupied))
            {
                throw Conflict(identity.PaneId.ToString(), "run_shell_command", occupied.Kind);
            }

            Active.Add(identity, new Reservation(token, ReservationKind.Run));
        }

        return new PaneRunLease(identity, token);
    }

    internal static PaneInputLease? Authorize(
        IReadOnlyList<Pane> panes,
        PaneRunLease? owner,
        string toolName,
        bool reserveDispatch)
    {
        PaneRunIdentity[] identities = [.. panes.Select(PaneRunIdentity.For)];
        lock (Gate)
        {
            if (owner is not null)
            {
                if (identities.Length != 1 || !owner.OwnsLocked(identities[0]))
                {
                    throw new McpException(
                        "run_shell_command lost its pane reservation or route before "
                        + "dispatch. The command was not sent.");
                }

                return null;
            }

            foreach (PaneRunIdentity identity in identities)
            {
                if (Active.TryGetValue(identity, out Reservation occupied))
                {
                    throw Conflict(identity.PaneId.ToString(), toolName, occupied.Kind);
                }
            }

            if (!reserveDispatch)
            {
                return null;
            }

            Guid token = Guid.NewGuid();
            foreach (PaneRunIdentity identity in identities)
            {
                Active.Add(identity, new Reservation(token, ReservationKind.Input));
            }

            return new PaneInputLease(identities, token);
        }
    }

    private static McpException Conflict(
        string paneId,
        string toolName,
        ReservationKind kind) =>
        kind == ReservationKind.Run
            ? new McpException(
                $"{toolName} refuses pane {paneId} because a command started by this MCP "
                + "process is still active. Wait for that run to finish before sending "
                + "more input.")
            : new McpException(
                $"{toolName} refuses pane {paneId} because another pane-input dispatch "
                + "is in progress. Wait for it to finish and retry.");

    private static void Release(
        IReadOnlyList<PaneRunIdentity> identities,
        Guid token,
        ReservationKind kind)
    {
        lock (Gate)
        {
            foreach (PaneRunIdentity identity in identities)
            {
                if (Active.TryGetValue(identity, out Reservation reservation)
                    && reservation.Token == token
                    && reservation.Kind == kind)
                {
                    _ = Active.Remove(identity);
                }
            }
        }
    }

    internal sealed class PaneRunLease
    {
        private readonly PaneRunIdentity _identity;
        private readonly Guid _token;
        private int _released;

        internal PaneRunLease(PaneRunIdentity identity, Guid token)
        {
            _identity = identity;
            _token = token;
        }

        internal bool OwnsLocked(PaneRunIdentity identity) =>
            Volatile.Read(ref _released) == 0
            && _identity == identity
            && Active.TryGetValue(identity, out Reservation reservation)
            && reservation.Token == _token
            && reservation.Kind == ReservationKind.Run;

        internal void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                PaneRunRegistry.Release([_identity], _token, ReservationKind.Run);
            }
        }
    }

    internal sealed class PaneInputLease
    {
        private readonly PaneRunIdentity[] _identities;
        private readonly Guid _token;
        private int _released;

        internal PaneInputLease(PaneRunIdentity[] identities, Guid token)
        {
            _identities = identities;
            _token = token;
        }

        internal void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                PaneRunRegistry.Release(_identities, _token, ReservationKind.Input);
            }
        }
    }

    internal readonly record struct PaneRunIdentity(
        string Endpoint,
        ServerGeneration Generation,
        PaneId PaneId,
        string TmuxBinary)
    {
        internal static PaneRunIdentity For(Pane pane)
        {
            TmuxConnection connection = pane.Server.Connection
                ?? throw new InvalidOperationException(
                    "Pane input requires a materialized tmux connection.");
            return new PaneRunIdentity(
                connection.GetEndpointFingerprint(),
                pane.Generation,
                pane.Id,
                pane.Server.ConnectionOptions.TmuxBinaryPath);
        }
    }

    private readonly record struct Reservation(Guid Token, ReservationKind Kind);

    private enum ReservationKind
    {
        Run,
        Input,
    }
}
