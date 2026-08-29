using System.Runtime.Versioning;

namespace LibTmux.Internal;

/// <summary>Reads one live relation and rebuilds owned entity handles.</summary>
/// <remarks>
/// Relation reads go through the same projection and materializer as a
/// snapshot, so a live child carries the same fields a captured one does.
/// </remarks>
internal static class RelationReader
{
    [UnsupportedOSPlatform("windows")]
    internal static Task<IReadOnlyList<IReadOnlyDictionary<string, string?>>> ListAsync(
        Server owner,
        string listCommand,
        IReadOnlyList<string> extraArguments,
        CancellationToken cancellationToken)
    {
        var context = new MaterializationContext(owner, ParseVersion(owner));
        return new MaterializationQuery(context)
            .FetchAsync(listCommand, extraArguments, cancellationToken);
    }

    /// <summary>Reads the session a handle was materialized in.</summary>
    /// <param name="snapshot">The fields captured with the handle, or null.</param>
    /// <returns>The captured session, or null when none was captured.</returns>
    internal static SessionId? CapturedSession(IReadOnlyDictionary<string, string?>? snapshot) =>
        snapshot is not null
        && snapshot.TryGetValue("session_id", out string? text)
        && SessionId.TryParse(text, out SessionId id)
            ? id
            : null;

    /// <summary>Reads the one entity a tmux identifier resolves to.</summary>
    /// <param name="owner">The server that owns the entity.</param>
    /// <param name="listCommand">The <c>list-*</c> subcommand naming the projection.</param>
    /// <param name="idWireName">The format token identifying the entity.</param>
    /// <param name="identifier">The entity's tmux identifier.</param>
    /// <param name="inSession">The entity scoped to one session, tried first.</param>
    /// <param name="cancellationToken">Cancels the tmux commands.</param>
    /// <returns>The row, or null when tmux no longer has the entity.</returns>
    [UnsupportedOSPlatform("windows")]
    internal static Task<IReadOnlyDictionary<string, string?>?> FindAsync(
        Server owner,
        string listCommand,
        string idWireName,
        string identifier,
        TmuxTarget? inSession,
        CancellationToken cancellationToken)
    {
        var context = new MaterializationContext(owner, ParseVersion(owner));
        return new MaterializationQuery(context)
            .FetchOneAsync(listCommand, idWireName, identifier, inSession, cancellationToken);
    }

    [UnsupportedOSPlatform("windows")]
    internal static Window ToWindow(Server owner, IReadOnlyDictionary<string, string?> row)
    {
        EntityMaterializationState state = Capture(owner, row);
        return new Window(
            owner,
            Connection(owner),
            state.Generation,
            state.WindowId ?? throw new InvalidDataException("tmux row carries no window."),
            state.RawFields);
    }

    [UnsupportedOSPlatform("windows")]
    internal static Pane ToPane(Server owner, IReadOnlyDictionary<string, string?> row)
    {
        EntityMaterializationState state = Capture(owner, row);
        if (!PaneId.TryParse(
                state.RawFields.TryGetValue("pane_id", out string? text) ? text : null,
                out PaneId id))
        {
            throw new InvalidDataException("tmux row carries no pane.");
        }

        return new Pane(owner, Connection(owner), state.Generation, id, state.RawFields);
    }

    [UnsupportedOSPlatform("windows")]
    internal static Session ToSession(Server owner, IReadOnlyDictionary<string, string?> row)
    {
        EntityMaterializationState state = Capture(owner, row);
        return new Session(
            owner,
            Connection(owner),
            state.Generation,
            state.SessionId ?? throw new InvalidDataException("tmux row carries no session."),
            state.RawFields);
    }

    private static EntityMaterializationState Capture(
        Server owner,
        IReadOnlyDictionary<string, string?> row) =>
        Materializer.CreateState(new MaterializationContext(owner, ParseVersion(owner)), row);

    private static TmuxConnection Connection(Server owner) =>
        owner.Connection
        ?? throw new InvalidOperationException("The server has no connection.");

    private static TmuxVersion ParseVersion(Server owner)
    {
        string raw = owner.RawVersion
            ?? throw new InvalidOperationException("The server reported no tmux version.");
        return TmuxVersion.Parse(
            raw.StartsWith("tmux ", StringComparison.Ordinal) ? raw[5..] : raw);
    }
}
