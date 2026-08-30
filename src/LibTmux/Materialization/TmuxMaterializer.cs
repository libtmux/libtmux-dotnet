using System.Globalization;
using System.Runtime.Versioning;

namespace LibTmux.Internal;

/// <summary>
/// Turns framed tmux rows into decoded fields and owned entity handles.
/// </summary>
/// <remarks>
/// Every row is checked against the owning server's generation before a handle
/// is produced, so a handle can never outlive the server it was read from.
/// </remarks>
internal static class Materializer
{
    /// <summary>Decodes every framed row into tmux fields.</summary>
    /// <param name="context">The owning server and gating tmux version.</param>
    /// <param name="payload">Raw framed bytes from a tmux list command.</param>
    /// <returns>One decoded field dictionary per row.</returns>
    internal static IReadOnlyList<IReadOnlyDictionary<string, string?>> MaterializeFormatFields(
        MaterializationContext context,
        ReadOnlySpan<byte> payload) =>
        MaterializeFormatFields(context, payload, ServerProjection.Descriptor.ListCommand);

    /// <summary>Decodes framed rows produced by one tmux list command.</summary>
    /// <param name="context">The owning server and gating tmux version.</param>
    /// <param name="payload">Raw framed bytes from a tmux list command.</param>
    /// <param name="listCommand">The tmux list command that produced them.</param>
    /// <returns>One decoded field dictionary per row.</returns>
    internal static IReadOnlyList<IReadOnlyDictionary<string, string?>> MaterializeFormatFields(
        MaterializationContext context,
        ReadOnlySpan<byte> payload,
        string listCommand)
    {
        ArgumentNullException.ThrowIfNull(context);
        FormatProjection projection = FormatProjection.Create(listCommand, context.TmuxVersion);
        IReadOnlyList<IReadOnlyDictionary<string, ReadOnlyMemory<byte>?>> rows =
            SeparatedRowFramer.Decode(payload, projection, new TmuxTransportLimits());
        var decoded = new List<IReadOnlyDictionary<string, string?>>(rows.Count);
        foreach (IReadOnlyDictionary<string, ReadOnlyMemory<byte>?> row in rows)
        {
            var fields = new Dictionary<string, string?>(row.Count, StringComparer.Ordinal);
            foreach ((string wireName, ReadOnlyMemory<byte>? value) in row)
            {
                fields[wireName] = value is null
                    ? null
                    : Utf8BackslashDecoder.ProjectValue(value.Value.Span);
            }

            context.EnsureOwns(ReadGeneration(fields));
            decoded.Add(fields);
        }

        return decoded;
    }

    /// <summary>Materializes one session from framed bytes.</summary>
    /// <param name="context">The owning server and gating tmux version.</param>
    /// <param name="payload">Raw framed bytes for exactly one session row.</param>
    /// <returns>The owned session handle.</returns>
    [UnsupportedOSPlatform("windows")]
    internal static Session MaterializeSession(
        MaterializationContext context,
        ReadOnlySpan<byte> payload) =>
        MaterializeSession(context, Single(context, payload, "list-sessions"));

    /// <summary>Materializes one session from decoded fields.</summary>
    /// <param name="context">The owning server and gating tmux version.</param>
    /// <param name="fields">Decoded fields for exactly one session row.</param>
    /// <returns>The owned session handle.</returns>
    [UnsupportedOSPlatform("windows")]
    internal static Session MaterializeSession(
        MaterializationContext context,
        IReadOnlyDictionary<string, string?> fields)
    {
        EntityMaterializationState state = CreateState(context, fields);
        SessionId id = state.SessionId
            ?? throw new InvalidDataException("tmux row carries no session identifier.");
        return new Session(context.Server, RequireConnection(context), state.Generation, id, state.RawFields);
    }

    /// <summary>Materializes one window from framed bytes.</summary>
    /// <param name="context">The owning server and gating tmux version.</param>
    /// <param name="payload">Raw framed bytes for exactly one window row.</param>
    /// <returns>The owned window handle.</returns>
    [UnsupportedOSPlatform("windows")]
    internal static Window MaterializeWindow(
        MaterializationContext context,
        ReadOnlySpan<byte> payload) =>
        MaterializeWindow(context, Single(context, payload, "list-windows"));

    /// <summary>Materializes one window from decoded fields.</summary>
    /// <param name="context">The owning server and gating tmux version.</param>
    /// <param name="fields">Decoded fields for exactly one window row.</param>
    /// <returns>The owned window handle.</returns>
    [UnsupportedOSPlatform("windows")]
    internal static Window MaterializeWindow(
        MaterializationContext context,
        IReadOnlyDictionary<string, string?> fields)
    {
        EntityMaterializationState state = CreateState(context, fields);
        WindowId id = state.WindowId
            ?? throw new InvalidDataException("tmux row carries no window identifier.");
        return new Window(context.Server, RequireConnection(context), state.Generation, id, state.RawFields);
    }

    /// <summary>Materializes one pane from framed bytes.</summary>
    /// <param name="context">The owning server and gating tmux version.</param>
    /// <param name="payload">Raw framed bytes for exactly one pane row.</param>
    /// <returns>The owned pane handle.</returns>
    [UnsupportedOSPlatform("windows")]
    internal static Pane MaterializePane(
        MaterializationContext context,
        ReadOnlySpan<byte> payload) =>
        MaterializePane(context, Single(context, payload, "list-panes"));

    /// <summary>Materializes one pane from decoded fields.</summary>
    /// <param name="context">The owning server and gating tmux version.</param>
    /// <param name="fields">Decoded fields for exactly one pane row.</param>
    /// <returns>The owned pane handle.</returns>
    [UnsupportedOSPlatform("windows")]
    internal static Pane MaterializePane(
        MaterializationContext context,
        IReadOnlyDictionary<string, string?> fields)
    {
        EntityMaterializationState state = CreateState(context, fields);
        if (!PaneId.TryParse(Require(fields, "pane_id"), out PaneId id))
        {
            throw new InvalidDataException("tmux row carries a malformed pane identifier.");
        }

        return new Pane(context.Server, RequireConnection(context), state.Generation, id, state.RawFields);
    }

    /// <summary>Builds the hierarchy state one materialized row carries.</summary>
    /// <param name="context">The owning server and gating tmux version.</param>
    /// <param name="fields">Decoded fields for exactly one row.</param>
    /// <returns>The state, with relation slots left uncaptured.</returns>
    internal static EntityMaterializationState CreateState(
        MaterializationContext context,
        IReadOnlyDictionary<string, string?> fields)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(fields);
        ServerGeneration generation = ReadGeneration(fields);
        context.EnsureOwns(generation);
        SessionId? sessionId = ReadSessionId(fields);
        WindowId? windowId = ReadWindowId(fields);
        return new EntityMaterializationState
        {
            RawFields = fields,
            Server = context.Server,
            Generation = generation,
            SessionId = sessionId,
            WindowId = windowId,
            WindowEdge = CreateEdge(fields, sessionId, windowId),
        };
    }

    private static SessionWindowEdge? CreateEdge(
        IReadOnlyDictionary<string, string?> fields,
        SessionId? sessionId,
        WindowId? windowId)
    {
        if (sessionId is null
            || windowId is null
            || !fields.TryGetValue("window_index", out string? index)
            || !int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
        {
            return null;
        }

        return new SessionWindowEdge
        {
            SessionId = sessionId.Value,
            WindowId = windowId.Value,
            WindowIndex = parsed,
        };
    }

    private static SessionId? ReadSessionId(IReadOnlyDictionary<string, string?> fields)
    {
        if (!fields.TryGetValue("session_id", out string? text) || string.IsNullOrEmpty(text))
        {
            return null;
        }

        return SessionId.TryParse(text, out SessionId id)
            ? id
            : throw new InvalidDataException("tmux row carries a malformed session identifier.");
    }

    private static WindowId? ReadWindowId(IReadOnlyDictionary<string, string?> fields)
    {
        if (!fields.TryGetValue("window_id", out string? text) || string.IsNullOrEmpty(text))
        {
            return null;
        }

        return WindowId.TryParse(text, out WindowId id)
            ? id
            : throw new InvalidDataException("tmux row carries a malformed window identifier.");
    }

    private static ServerGeneration ReadGeneration(IReadOnlyDictionary<string, string?> fields) =>
        TmuxConnection.ParseGeneration(
            $"{Require(fields, "pid")}:{Require(fields, "start_time")}");

    private static string Require(IReadOnlyDictionary<string, string?> fields, string wireName) =>
        fields.TryGetValue(wireName, out string? value) && !string.IsNullOrEmpty(value)
            ? value
            : throw new InvalidDataException($"tmux row is missing required field '{wireName}'.");

    private static IReadOnlyDictionary<string, string?> Single(
        MaterializationContext context,
        ReadOnlySpan<byte> payload,
        string listCommand)
    {
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows =
            MaterializeFormatFields(context, payload, listCommand);
        return rows.Count == 1
            ? rows[0]
            : throw new InvalidDataException(
                $"tmux returned {rows.Count.ToString(CultureInfo.InvariantCulture)} rows where one was required.");
    }

    private static TmuxConnection RequireConnection(MaterializationContext context) =>
        context.Server.Connection
        ?? throw new InvalidOperationException(
            "The server has no connection; connect before materializing.");
}
