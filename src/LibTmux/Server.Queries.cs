using System.Runtime.Versioning;

using LibTmux.Internal;
using LibTmux.Query;

namespace LibTmux;

// Reads listings that tmux narrows with -f before each row is rechecked.
public sealed partial class Server
{
    private const string ListClientsFilterCapability = "list_clients_filter";

    /// <summary>Lists the objects a request describes.</summary>
    /// <remarks>
    /// A filter that needs a relation reads a snapshot of only the session
    /// subtrees tmux keeps, at the depth the recheck reads.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    internal async Task<IReadOnlyList<T>> QueryAsync<T>(
        ListingRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        Func<T, bool> keep = request.Filter is null
            ? static _ => true
            : QueryInterpreter.CompileEntity<T>(request.Filter, cancellationToken);
        string? typed = request.Filter is null ? null : TmuxFilterRenderer.Superset(request.Filter);
        string? screen = request.Screen?.Render();
        string? raw = request.Unsafe?.Value;
        if (raw is not null && (typed is not null || screen is not null))
        {
            TmuxFilterRenderer.RequireSingleExpression(raw);
        }

        Server owner = await ListingOwnerAsync(cancellationToken).ConfigureAwait(false);
        string? narrowed = And(screen, And(raw, typed));
        if (request.Target == QueryTarget.Client)
        {
            IReadOnlyList<Client> clients = await owner
                .ReadClientsAsync(owner.ClientFilterArguments(narrowed, request.Unsafe), cancellationToken)
                .ConfigureAwait(false);
            return [.. clients.Cast<T>().Where(keep)];
        }

        SnapshotDepth required = request.Filter?.RequiredSnapshotDepth ?? SnapshotDepth.Server;
        if (required <= ListedDepth(request.Target))
        {
            IReadOnlyList<T> listed = await owner.ListAsync<T>(request, narrowed, cancellationToken)
                .ConfigureAwait(false);
            return [.. listed.Where(keep)];
        }

        // Only sessions and windows hold relations. The snapshot keeps whole
        // session subtrees, so a raw filter, written for the target's own
        // rows, is answered by a listing of its own and intersected.
        string? scope = request.Session is { } session ? $"#{{==:#{{session_id}},{session}}}" : null;
        string? lifted = Lift(typed, request.Target);
        if (lifted is not null)
        {
            // tmux runs a relation filter's window and pane loops for every row
            // it filters. Run them once per session, then capture only the
            // sessions kept, by an identifier test each row answers at once.
            IReadOnlyList<Session> matched = await owner
                .ListAsync<Session>(new ListingRequest(QueryTarget.Session), And(scope, lifted), cancellationToken)
                .ConfigureAwait(false);
            if (matched.Count == 0)
            {
                return [];
            }

            scope = TmuxFilterRenderer.AnyOf("session_id", [.. matched.Select(found => found.Id.ToString())]);
        }

        // Scoped by identifiers that never change, so the capture's separate
        // list commands can disagree only if a session ends between them.
        Server captured = await owner
            .CaptureSnapshotAsync(required, TimeProvider.System, scope, cancellationToken)
            .ConfigureAwait(false);
        HashSet<string>? kept = raw is null
            ? null
            : [.. (await owner.ListAsync<T>(request, raw, cancellationToken).ConfigureAwait(false)).Select(Key)];
        IEnumerable<T> candidates = request.Target switch
        {
            QueryTarget.Session => captured.Sessions.Cast<T>(),
            QueryTarget.Window => captured.Windows
                .Where(window => request.Session is null || window.Edge.SessionId == request.Session)
                .Cast<T>(),
            _ => throw new InvalidOperationException($"A {request.Target} listing has no relations to capture."),
        };
        return [.. candidates.Where(candidate => kept is null || kept.Contains(Key(candidate))).Where(keep)];
    }

    [UnsupportedOSPlatform("windows")]
    private async Task<IReadOnlyList<T>> ListAsync<T>(
        ListingRequest request,
        string? filter,
        CancellationToken cancellationToken)
    {
        (string command, string[] arguments) = ListCommand(request);
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows = await RelationReader
            .ListAsync(this, command, filter is null ? arguments : [.. arguments, "-f", filter], cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Select(row => Materialize<T>(this, request.Target, row))];
    }

    private static string Key<T>(T item) => item switch
    {
        Session session => session.Id.ToString(),
        Window window => $"{window.Edge.SessionId}:{window.Edge.WindowIndex}:{window.Id}",
        _ => throw new InvalidOperationException($"A {typeof(T).Name} has no listing key."),
    };

    private static (string Command, string[] Arguments) ListCommand(ListingRequest request) =>
        (request.Target, request.Session, request.Window) switch
        {
            (QueryTarget.Session, _, _) => ("list-sessions", []),
            (QueryTarget.Window, { } session, _) => ("list-windows", ["-t", session.ToString()]),
            (QueryTarget.Window, _, _) => ("list-windows", ["-a"]),
            (_, _, { } window) => ("list-panes", ["-t", window.ToString()]),
            (_, { } session, _) => ("list-panes", ["-s", "-t", session.ToString()]),
            _ => ("list-panes", ["-a"]),
        };

    // list-clients gained -f in tmux 3.4. Older tmux leaves a typed filter to
    // the recheck; a raw filter has no local meaning, so nothing is sent.
    private string[] ClientFilterArguments(string? filter, UnsafeTmuxFilter? unsafeFilter)
    {
        if (filter is null)
        {
            return [];
        }

        if (Supports(ListClientsFilterCapability))
        {
            return ["-f", filter];
        }

        return unsafeFilter is null
            ? []
            : throw new TmuxVersionTooLowException(
                "Filtering clients in tmux requires tmux 3.4.",
                TmuxVersion.Parse("3.4"),
                Version ?? default);
    }

    [UnsupportedOSPlatform("windows")]
    private static T Materialize<T>(Server owner, QueryTarget target, IReadOnlyDictionary<string, string?> row) =>
        target switch
        {
            QueryTarget.Session => (T)(object)RelationReader.ToSession(owner, row),
            QueryTarget.Window => (T)(object)RelationReader.ToWindow(owner, row),
            _ => (T)(object)RelationReader.ToPane(owner, row),
        };

    private static SnapshotDepth ListedDepth(QueryTarget target) => target switch
    {
        QueryTarget.Window => SnapshotDepth.Windows,
        QueryTarget.Pane => SnapshotDepth.Panes,
        _ => SnapshotDepth.Sessions,
    };

    // A session keeps its subtree when one of its windows matches.
    private static string? Lift(string? filter, QueryTarget target) =>
        filter is not null && target == QueryTarget.Window ? $"#{{W:#{{?{filter},1,}}}}" : filter;

    private static string? And(string? left, string? right) =>
        left is null ? right : right is null ? left : $"#{{&&:{left},{right}}}";
}
