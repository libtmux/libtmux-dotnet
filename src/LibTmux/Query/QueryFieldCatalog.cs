using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace LibTmux.Query;

[SuppressMessage("Interoperability", "CA1416:Validate platform compatibility", Justification = "Catalog accessors only read captured state; relation getters do not invoke platform APIs.")]
internal static class QueryFieldCatalog
{
    private static readonly FieldDefinition[] Fields =
    [
        new(
            "client_control_mode",
            QueryTarget.Client,
            QueryValueKind.Boolean,
            typeof(Client),
            nameof(Client.IsControlClient),
            new(static element => ((Client)element).IsControlClient, typeof(bool))),
        new("client_id", QueryTarget.Client, QueryValueKind.TypedId),
        new(
            "client_name",
            QueryTarget.Client,
            QueryValueKind.String,
            typeof(Client),
            nameof(Client.Name),
            new(static element => ((Client)element).Name, typeof(string))),
        new(
            "pane_command",
            QueryTarget.Pane,
            QueryValueKind.String,
            typeof(Pane),
            nameof(Pane.CurrentCommand),
            new(static element => ((Pane)element).CurrentCommand, typeof(string))),
        new(
            "pane_current_path",
            QueryTarget.Pane,
            QueryValueKind.String,
            typeof(Pane),
            nameof(Pane.CurrentPath),
            new(static element => ((Pane)element).CurrentPath, typeof(string))),
        new(
            "pane_id",
            QueryTarget.Pane,
            QueryValueKind.TypedId,
            typeof(Pane),
            nameof(Pane.Id),
            new(static element => ((Pane)element).Id, typeof(PaneId))),
        new(
            "pane_session", QueryTarget.Pane, null, typeof(Pane), nameof(Pane.Session),
            Relation: new(static element => ((Pane)element).Session, typeof(Session)),
            RelationShape: new(QueryRelationCardinality.One, QueryTarget.Session, SnapshotDepth.Panes)),
        new(
            "pane_window", QueryTarget.Pane, null, typeof(Pane), nameof(Pane.Window),
            Relation: new(static element => ((Pane)element).Window, typeof(Window)),
            RelationShape: new(QueryRelationCardinality.One, QueryTarget.Window, SnapshotDepth.Panes)),
        new(
            "session_active_pane", QueryTarget.Session, null, typeof(Session), nameof(Session.ActivePane),
            Relation: new(static element => ((Session)element).ActivePane.Value, typeof(Pane)),
            RelationShape: new(QueryRelationCardinality.One, QueryTarget.Pane, SnapshotDepth.Panes)),
        new(
            "session_active_window", QueryTarget.Session, null, typeof(Session), nameof(Session.ActiveWindow),
            Relation: new(static element => ((Session)element).ActiveWindow.Value, typeof(Window)),
            RelationShape: new(QueryRelationCardinality.One, QueryTarget.Window, SnapshotDepth.Windows)),
        new(
            "session_attached",
            QueryTarget.Session,
            QueryValueKind.Boolean,
            typeof(Session),
            nameof(Session.Attached),
            new(static element => ((Session)element).Attached, typeof(bool))),
        new(
            "session_id",
            QueryTarget.Session,
            QueryValueKind.TypedId,
            typeof(Session),
            nameof(Session.Id),
            new(static element => ((Session)element).Id, typeof(SessionId))),
        new(
            "session_name",
            QueryTarget.Session,
            QueryValueKind.String,
            typeof(Session),
            nameof(Session.Name),
            new(static element => ((Session)element).Name, typeof(string))),
        new(
            "session_panes", QueryTarget.Session, QueryValueKind.Int64, typeof(Session), nameof(Session.Panes),
            new(static element => checked((long)((Session)element).Panes.Count), typeof(long)),
            new(static element => ((Session)element).Panes, typeof(CapturedRelation<Pane>)),
            RelationShape: new(QueryRelationCardinality.Many, QueryTarget.Pane, SnapshotDepth.Panes)),
        new(
            "session_windows",
            QueryTarget.Session,
            QueryValueKind.Int64,
            typeof(Session),
            nameof(Session.Windows),
            new(static element => checked((long)((Session)element).Windows.Count), typeof(long)),
            new(
                static element => ((Session)element).Windows,
                typeof(CapturedRelation<Window>)),
            RelationShape: new(QueryRelationCardinality.Many, QueryTarget.Window, SnapshotDepth.Windows)),
        new(
            "window_active", QueryTarget.Window, QueryValueKind.Boolean, typeof(Window), nameof(Window.IsActive),
            new(static element => ((Window)element).IsActive, typeof(bool))),
        new(
            "window_active_pane", QueryTarget.Window, null, typeof(Window), nameof(Window.ActivePane),
            Relation: new(static element => ((Window)element).ActivePane.Value, typeof(Pane)),
            RelationShape: new(QueryRelationCardinality.One, QueryTarget.Pane, SnapshotDepth.Panes)),
        new(
            "window_id",
            QueryTarget.Window,
            QueryValueKind.TypedId,
            typeof(Window),
            nameof(Window.Id),
            new(static element => ((Window)element).Id, typeof(WindowId))),
        new(
            "window_index", QueryTarget.Window, QueryValueKind.Int64, typeof(Window), nameof(Window.Index),
            new(static element => ((Window)element).Index, typeof(int))),
        new(
            "window_linked_sessions", QueryTarget.Window, QueryValueKind.Int64, typeof(Window), nameof(Window.LinkedSessions),
            new(static element => checked((long)((Window)element).LinkedSessions.Count), typeof(long)),
            new(static element => ((Window)element).LinkedSessions, typeof(CapturedRelation<Session>)),
            RelationShape: new(QueryRelationCardinality.Many, QueryTarget.Session, SnapshotDepth.Windows)),
        new(
            "window_name",
            QueryTarget.Window,
            QueryValueKind.String,
            typeof(Window),
            nameof(Window.Name),
            new(static element => ((Window)element).Name, typeof(string))),
        new(
            "window_panes",
            QueryTarget.Window,
            QueryValueKind.Int64,
            typeof(Window),
            nameof(Window.Panes),
            new(static element => checked((long)((Window)element).Panes.Count), typeof(long)),
            new(static element => ((Window)element).Panes, typeof(CapturedRelation<Pane>)),
            RelationShape: new(QueryRelationCardinality.Many, QueryTarget.Pane, SnapshotDepth.Panes)),
        new(
            "window_session", QueryTarget.Window, null, typeof(Window), nameof(Window.Session),
            Relation: new(static element => ((Window)element).Session, typeof(Session)),
            RelationShape: new(QueryRelationCardinality.One, QueryTarget.Session, SnapshotDepth.Windows)),
    ];

    private static readonly FrozenDictionary<string, FieldDefinition> FieldsByWireName =
        Fields.ToFrozenDictionary(static field => field.WireName, StringComparer.Ordinal);

    internal static IReadOnlyList<string> WireNames { get; } =
        new ReadOnlyCollection<string>([.. Fields.Select(static field => field.WireName)]);

    internal static bool IsRelation(string wireName) =>
        FieldsByWireName.TryGetValue(wireName, out FieldDefinition field)
        && field.Relation is not null;

    internal static bool TryGetRelation(string wireName, out QueryRelationDefinition relation)
    {
        if (FieldsByWireName.TryGetValue(wireName, out FieldDefinition field)
            && field.RelationShape is { } shape)
        {
            relation = shape;
            return true;
        }

        relation = default;
        return false;
    }

    internal static bool TryGetTarget(string wireName, out QueryTarget target)
    {
        if (FieldsByWireName.TryGetValue(wireName, out FieldDefinition field))
        {
            target = field.Target;
            return true;
        }

        target = default;
        return false;
    }

    internal static bool TryGetKind(string wireName, out QueryValueKind kind)
    {
        if (FieldsByWireName.TryGetValue(wireName, out FieldDefinition field)
            && field.Kind is { } scalarKind)
        {
            kind = scalarKind;
            return true;
        }

        kind = default;
        return false;
    }

    internal static bool TryGetWireName(Type owner, string property, out string wireName)
    {
        foreach (FieldDefinition field in Fields)
        {
            if (field.Owner == owner
                && string.Equals(field.Property, property, StringComparison.Ordinal))
            {
                wireName = field.WireName;
                return true;
            }
        }

        wireName = string.Empty;
        return false;
    }

    internal static bool TryGetProperty(Type owner, string wireName, out string property)
    {
        if (FieldsByWireName.TryGetValue(wireName, out FieldDefinition field)
            && field.Owner == owner
            && field.Property is not null)
        {
            property = field.Property;
            return true;
        }

        property = string.Empty;
        return false;
    }

    internal static bool TryBindEntityScalar(
        Type owner,
        string wireName,
        out QueryFieldAccessor accessor) =>
        TryBind(owner, wireName, relation: false, out accessor);

    internal static bool TryBindEntityRelation(
        Type owner,
        string wireName,
        out QueryFieldAccessor accessor) =>
        TryBind(owner, wireName, relation: true, out accessor);

    private static bool TryBind(
        Type owner,
        string wireName,
        bool relation,
        out QueryFieldAccessor accessor)
    {
        if (FieldsByWireName.TryGetValue(wireName, out FieldDefinition field)
            && field.Owner == owner
            && (relation ? field.Relation : field.Scalar) is { } bound)
        {
            accessor = bound;
            return true;
        }

        accessor = null!;
        return false;
    }

    private readonly record struct FieldDefinition(
        string WireName,
        QueryTarget Target,
        QueryValueKind? Kind,
        Type? Owner = null,
        string? Property = null,
        QueryFieldAccessor? Scalar = null,
        QueryFieldAccessor? Relation = null,
        QueryRelationDefinition? RelationShape = null);
}

internal enum QueryRelationCardinality
{
    One,
    Many,
}

internal readonly record struct QueryRelationDefinition(
    QueryRelationCardinality Cardinality,
    QueryTarget Target,
    SnapshotDepth Depth);
