using System.Runtime.Versioning;

namespace LibTmux.Internal;

/// <summary>
/// Runs one tmux read and materializes its framed rows.
/// </summary>
/// <remarks>
/// A listing enumerates children; a single-target read asks tmux to resolve
/// one identifier. Both render the same projection, so a row means the same
/// thing whichever produced it.
/// </remarks>
internal sealed class MaterializationQuery
{
    private readonly MaterializationContext _context;

    internal MaterializationQuery(MaterializationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    /// <summary>Fetches every row one tmux list command reports.</summary>
    /// <param name="listCommand">A tmux <c>list-*</c> subcommand.</param>
    /// <param name="extraArguments">Arguments appended before <c>-F</c>.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>One decoded field dictionary per row.</returns>
    [UnsupportedOSPlatform("windows")]
    internal async Task<IReadOnlyList<IReadOnlyDictionary<string, string?>>> FetchAsync(
        string listCommand,
        IEnumerable<string>? extraArguments = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listCommand);
        FormatProjection projection = CreateProjection(listCommand);
        string[] arguments =
        [
            listCommand,
            .. extraArguments ?? [],
            "-F",
            projection.Template,
        ];

        return await ExecuteAsync(listCommand, arguments, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Fetches the row for exactly one tmux entity.</summary>
    /// <param name="listCommand">The <c>list-*</c> subcommand naming the projection.</param>
    /// <param name="idWireName">The format token identifying the entity.</param>
    /// <param name="identifier">The entity's tmux identifier.</param>
    /// <param name="inSession">The entity scoped to one session, tried first.</param>
    /// <param name="cancellationToken">Cancels the tmux commands.</param>
    /// <returns>The row, or null when tmux no longer has the entity.</returns>
    /// <remarks>
    /// This costs one command whatever the server holds, where a listing costs
    /// one row per entity on it. Reading the scoped target first keeps a
    /// refreshed handle in the session its predecessor was read in; the bare
    /// identifier still answers when the entity has left that session.
    /// <para>
    /// The server's own fields resolve whether or not the target does, so a
    /// stale generation is rejected before absence is reported.
    /// </para>
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    internal async Task<IReadOnlyDictionary<string, string?>?> FetchOneAsync(
        string listCommand,
        string idWireName,
        string identifier,
        TmuxTarget? inSession = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listCommand);
        ArgumentException.ThrowIfNullOrWhiteSpace(idWireName);
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        if (inSession is TmuxTarget scoped)
        {
            IReadOnlyDictionary<string, string?>? row = await ReadAsync(
                    listCommand,
                    idWireName,
                    identifier,
                    scoped,
                    cancellationToken)
                .ConfigureAwait(false);
            if (row is not null)
            {
                return row;
            }
        }

        return await ReadAsync(
                listCommand,
                idWireName,
                identifier,
                new TmuxTarget(identifier),
                cancellationToken)
            .ConfigureAwait(false);
    }

    [UnsupportedOSPlatform("windows")]
    private async Task<IReadOnlyDictionary<string, string?>?> ReadAsync(
        string listCommand,
        string idWireName,
        string identifier,
        TmuxTarget target,
        CancellationToken cancellationToken)
    {
        FormatProjection projection = CreateProjection(listCommand);
        string[] arguments = ["display-message", "-p", "-t", target.Value, projection.Template];

        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows =
            await ExecuteAsync(listCommand, arguments, cancellationToken).ConfigureAwait(false);
        if (rows.Count != 1)
        {
            throw new TmuxTransportException(
                $"tmux answered a single-target read with {rows.Count} rows.",
                arguments);
        }

        // display-message declares its target CMD_FIND_CANFAIL and exits zero on
        // one it cannot resolve: an unresolvable target leaves every entity
        // field empty, and one that resolves only in part answers with its
        // session's current window or pane. The identifier separates both.
        return rows[0].TryGetValue(idWireName, out string? id)
            && string.Equals(id, identifier, StringComparison.Ordinal)
                ? rows[0]
                : null;
    }

    private FormatProjection CreateProjection(string listCommand) =>
        FormatProjection.Create(listCommand, _context.TmuxVersion);

    [UnsupportedOSPlatform("windows")]
    private async Task<IReadOnlyList<IReadOnlyDictionary<string, string?>>> ExecuteAsync(
        string listCommand,
        string[] arguments,
        CancellationToken cancellationToken)
    {
        // Reading Generation first rejects an unmaterialized server before a
        // command is ever dispatched.
        ServerGeneration generation = _context.Generation;
        TmuxConnection connection = _context.Server.Connection
            ?? throw new InvalidOperationException(
                "The server has no connection; connect before querying.");
        TmuxCommandResult result = await connection
            .CreateEntityDispatcher(generation)
            .ExecuteAsync(arguments, cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new TmuxCommandException($"{arguments[0]} failed.", result);
        }

        try
        {
            return Materializer.MaterializeFormatFields(
                _context,
                result.StandardOutput.Span,
                listCommand);
        }
        catch (InvalidDataException error)
        {
            throw new TmuxTransportException(
                $"tmux returned an undecodable {listCommand} projection.",
                arguments,
                error);
        }
    }
}
