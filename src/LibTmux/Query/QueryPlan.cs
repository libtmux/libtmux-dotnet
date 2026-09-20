using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace LibTmux.Query;

/// <summary>Controls predicate evaluation inside tmux.</summary>
public enum QueryPushdown
{
    /// <summary>Evaluate the whole predicate locally.</summary>
    Never = 0,
    /// <summary>Evaluate an exact leading portion in tmux and the remainder locally.</summary>
    Auto = 1,
    /// <summary>Reject any predicate that cannot execute entirely in tmux.</summary>
    Require = 2,
}

/// <summary>An immutable query plan; inspecting it never reads tmux.</summary>
/// <typeparam name="T">The native entity selected by this plan.</typeparam>
public sealed class QueryPlan<T>
{
    internal QueryPlan(QueryDocument document, TmuxVersion daemonVersion, QueryPushdown pushdown)
    {
        QuerySourceParts parts = QuerySourcePlanner.Prepare<T>(document, daemonVersion, pushdown);
        Document = document;
        DaemonVersion = daemonVersion;
        Pushdown = pushdown;
        PushedPredicate = Wrap(parts.Pushed);
        ResidualPredicate = Wrap(parts.Residual);
        RequiredFields = parts.Fields;
        FallbackReasons = parts.Reasons;
        PredicateFormat = parts.Format;
        RequiredSnapshotDepth = document.RequiredSnapshotDepth;

        QueryDocument? Wrap(QueryNode? node) => node is null ? null
            : node == document.Predicate ? document
            : new QueryDocument(document.Schema, document.Version, document.Target, node);
    }

    internal string? PredicateFormat { get; }

    /// <summary>Gets the complete portable predicate.</summary>
    public QueryDocument Document { get; }
    /// <summary>Gets the daemon version against which this plan was prepared.</summary>
    public TmuxVersion DaemonVersion { get; }
    /// <summary>Gets the requested evaluation mode.</summary>
    public QueryPushdown Pushdown { get; }
    /// <summary>Gets the predicate evaluated in tmux, or null when evaluation is local.</summary>
    public QueryDocument? PushedPredicate { get; }
    /// <summary>Gets the predicate evaluated locally, or null when evaluation is entirely in tmux.</summary>
    public QueryDocument? ResidualPredicate { get; }
    /// <summary>Gets every scalar or relation field referenced by this query.</summary>
    public IReadOnlyList<QueryFieldDescriptor> RequiredFields { get; }
    /// <summary>Gets the complete hierarchy depth required for local evaluation.</summary>
    public SnapshotDepth RequiredSnapshotDepth { get; }
    /// <summary>Gets reasons why some or all predicate evaluation remains local.</summary>
    public IReadOnlyList<string> FallbackReasons { get; }

    /// <summary>Acquires a fresh snapshot and evaluates this plan.</summary>
    /// <param name="server">The endpoint to inspect without initializing it.</param>
    /// <param name="cancellationToken">Stops acquisition, evaluation, and publication.</param>
    /// <returns>Ordered matches retaining the complete captured graph.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<QueryResult<T>> ExecuteAsync(Server server, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        cancellationToken.ThrowIfCancellationRequested();
        Server live = await server.InspectAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The tmux daemon is absent.");
        if (live.DaemonVersion != DaemonVersion)
        {
            throw new InvalidOperationException(
                $"Query daemon version '{DaemonVersion}' does not match observed version '{live.DaemonVersion}'.");
        }
        (Server snapshot, IReadOnlyList<bool>? matches) = await live.CaptureQuerySnapshotAsync(
            RequiredSnapshotDepth, DaemonVersion, Document.Target, PredicateFormat, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<T> entities = Document.Target switch
        {
            QueryTarget.Session => (IReadOnlyList<T>)(object)snapshot.Sessions,
            QueryTarget.Window => (IReadOnlyList<T>)(object)snapshot.Windows,
            QueryTarget.Pane => (IReadOnlyList<T>)(object)snapshot.Panes,
            _ => throw new InvalidOperationException("This target has no native source."),
        };
        if (PredicateFormat is not null && (matches is null || matches.Count != entities.Count))
        {
            throw new TmuxProtocolException("Query marker count does not match the captured entity rows.", TmuxDispatchState.Dispatched);
        }
        Func<T, bool>? predicate = ResidualPredicate is null ? null
            : QueryInterpreter.CompileNative<T>(ResidualPredicate, cancellationToken);
        List<T> selected = [];
        for (int index = 0; index < entities.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool accepted;
            try
            {
                accepted = (matches is null || matches[index]) && (predicate is null || predicate(entities[index]));
            }
            catch (RegexMatchTimeoutException) when (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (accepted)
            {
                selected.Add(entities[index]);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new QueryResult<T>(snapshot, selected);
    }
}
