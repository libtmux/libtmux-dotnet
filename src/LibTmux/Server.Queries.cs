using System.Runtime.Versioning;

using LibTmux.Internal;
using LibTmux.Query;

namespace LibTmux;

// Reads listings that tmux narrows with -f before each row is rechecked.
public sealed partial class Server
{
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
        string? narrowed = And(
            request.Screen?.Render(),
            And(request.Unsafe?.Value, request.Filter is null ? null : TmuxFilterRenderer.Superset(request.Filter)));
        Server owner = await ListingOwnerAsync(cancellationToken).ConfigureAwait(false);
        SnapshotDepth required = request.Filter?.RequiredSnapshotDepth ?? SnapshotDepth.Server;
        if (required > ListedDepth(request.Target))
        {
            string? scope = request.Session is { } session ? $"#{{==:#{{session_id}},{session}}}" : null;
            Server captured = await owner
                .CaptureSnapshotAsync(required, TimeProvider.System, And(scope, Lift(narrowed, request.Target)), cancellationToken)
                .ConfigureAwait(false);
            IEnumerable<T> candidates = request.Target switch
            {
                QueryTarget.Session => captured.Sessions.Cast<T>(),
                _ => captured.Windows.Where(window => request.Session is null || window.Edge.SessionId == request.Session).Cast<T>(),
            };
            return [.. candidates.Where(keep)];
        }

        (string command, string[] arguments) = ListCommand(request);
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows = await RelationReader
            .ListAsync(owner, command, narrowed is null ? arguments : [.. arguments, "-f", narrowed], cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Select(row => Materialize<T>(owner, request.Target, row)).Where(keep)];
    }

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

    // A session keeps its subtree when one of its windows, or panes, matches.
    private static string? Lift(string? filter, QueryTarget target) => filter is null
        ? null
        : target switch
        {
            QueryTarget.Window => $"#{{W:#{{?{filter},1,}}}}",
            QueryTarget.Pane => $"#{{W:#{{?#{{P:#{{?{filter},1,}}}},1,}}}}",
            _ => filter,
        };

    private static string? And(string? left, string? right) =>
        left is null ? right : right is null ? left : $"#{{&&:{left},{right}}}";
}
