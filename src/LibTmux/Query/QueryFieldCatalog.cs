using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace LibTmux.Query;

/// <summary>Discovers the closed query vocabulary and its built-in entity bindings.</summary>
[SuppressMessage("Interoperability", "CA1416:Validate platform compatibility", Justification = "Catalog accessors only read captured state; relation getters do not invoke platform APIs.")]
public static class QueryFieldCatalog
{
    /// <summary>Reads the supported fields for one portable query target.</summary>
    /// <param name="target">The target whose vocabulary is requested.</param>
    /// <returns>Immutable descriptors in stable wire-name order.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The target is undefined.</exception>
    public static IReadOnlyList<QueryFieldDescriptor> GetFields(QueryTarget target) =>
        Descriptors.TryGetValue(target, out ReadOnlyCollection<QueryFieldDescriptor>? fields)
            ? fields
            : throw new ArgumentOutOfRangeException(nameof(target), target, "The query target is undefined.");

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
            "history_size",
            QueryTarget.Pane,
            QueryValueKind.Int64,
            typeof(Pane),
            nameof(Pane.HistorySize),
            new(static element => (long)((Pane)element).HistorySize, typeof(long))),
        new(
            "pane_active",
            QueryTarget.Pane,
            QueryValueKind.Boolean,
            typeof(Pane),
            nameof(Pane.Active),
            new(static element => ((Pane)element).Active, typeof(bool))),
        new(
            "pane_at_bottom",
            QueryTarget.Pane,
            QueryValueKind.Boolean,
            typeof(Pane),
            nameof(Pane.AtBottom),
            new(static element => ReadPaneFlag((Pane)element, "pane_at_bottom"), typeof(bool))),
        new(
            "pane_at_left",
            QueryTarget.Pane,
            QueryValueKind.Boolean,
            typeof(Pane),
            nameof(Pane.AtLeft),
            new(static element => ReadPaneFlag((Pane)element, "pane_at_left"), typeof(bool))),
        new(
            "pane_at_right",
            QueryTarget.Pane,
            QueryValueKind.Boolean,
            typeof(Pane),
            nameof(Pane.AtRight),
            new(static element => ReadPaneFlag((Pane)element, "pane_at_right"), typeof(bool))),
        new(
            "pane_at_top",
            QueryTarget.Pane,
            QueryValueKind.Boolean,
            typeof(Pane),
            nameof(Pane.AtTop),
            new(static element => ReadPaneFlag((Pane)element, "pane_at_top"), typeof(bool))),
        new(
            "pane_command",
            QueryTarget.Pane,
            QueryValueKind.String,
            typeof(Pane),
            nameof(Pane.CurrentCommand),
            new(static element => ((Pane)element).CurrentCommand, typeof(string)),
            Nullable: true),
        new(
            "pane_current_path",
            QueryTarget.Pane,
            QueryValueKind.String,
            typeof(Pane),
            nameof(Pane.CurrentPath),
            new(static element => ((Pane)element).CurrentPath, typeof(string)),
            Nullable: true),
        new(
            "pane_dead",
            QueryTarget.Pane,
            QueryValueKind.Boolean,
            typeof(Pane),
            nameof(Pane.Dead),
            new(static element => ((Pane)element).Dead, typeof(bool))),
        new(
            "pane_dead_status",
            QueryTarget.Pane,
            QueryValueKind.Int64,
            typeof(Pane),
            nameof(Pane.DeadStatus),
            new(static element => (long?)((Pane)element).DeadStatus, typeof(long?)),
            Nullable: true),
        new(
            "pane_height", QueryTarget.Pane, QueryValueKind.Int64, typeof(Pane), nameof(Pane.Height),
            new(static element => ((Pane)element).Height, typeof(int))),
        new(
            "pane_id",
            QueryTarget.Pane,
            QueryValueKind.TypedId,
            typeof(Pane),
            nameof(Pane.Id),
            new(static element => ((Pane)element).Id, typeof(PaneId))),
        new(
            "pane_in_mode",
            QueryTarget.Pane,
            QueryValueKind.Boolean,
            typeof(Pane),
            nameof(Pane.InMode),
            new(static element => ((Pane)element).InMode, typeof(bool))),
        new(
            "pane_index",
            QueryTarget.Pane,
            QueryValueKind.Int64,
            typeof(Pane),
            nameof(Pane.Index),
            new(static element => ((Pane)element).Index, typeof(int))),
        new(
            "pane_left",
            QueryTarget.Pane,
            QueryValueKind.Int64,
            typeof(Pane),
            nameof(Pane.Left),
            new(static element => ((Pane)element).Left, typeof(int))),
        new(
            "pane_pid",
            QueryTarget.Pane,
            QueryValueKind.Int64,
            typeof(Pane),
            nameof(Pane.ProcessId),
            new(static element => (long)((Pane)element).ProcessId, typeof(long))),
        new(
            "pane_session", QueryTarget.Pane, null, typeof(Pane), nameof(Pane.Session),
            Relation: new(static element => ((Pane)element).Session, typeof(Session)),
            RelationShape: new(QueryRelationCardinality.One, QueryTarget.Session, SnapshotDepth.Panes)),
        new(
            "pane_start_command",
            QueryTarget.Pane,
            QueryValueKind.String,
            typeof(Pane),
            nameof(Pane.StartCommand),
            new(static element => ((Pane)element).StartCommand, typeof(string)),
            Nullable: true),
        new(
            "pane_synchronized",
            QueryTarget.Pane,
            QueryValueKind.Boolean,
            typeof(Pane),
            nameof(Pane.Synchronized),
            new(static element => ((Pane)element).Synchronized, typeof(bool))),
        new(
            "pane_title",
            QueryTarget.Pane,
            QueryValueKind.String,
            typeof(Pane),
            nameof(Pane.Title),
            new(static element => ReadPaneText((Pane)element, "pane_title"), typeof(string)),
            Nullable: true),
        new(
            "pane_top",
            QueryTarget.Pane,
            QueryValueKind.Int64,
            typeof(Pane),
            nameof(Pane.Top),
            new(static element => ((Pane)element).Top, typeof(int))),
        new(
            "pane_tty",
            QueryTarget.Pane,
            QueryValueKind.String,
            typeof(Pane),
            nameof(Pane.Tty),
            new(static element => ((Pane)element).Tty, typeof(string)),
            Nullable: true),
        new(
            "pane_width", QueryTarget.Pane, QueryValueKind.Int64, typeof(Pane), nameof(Pane.Width),
            new(static element => ((Pane)element).Width, typeof(int))),
        new(
            "pane_window", QueryTarget.Pane, null, typeof(Pane), nameof(Pane.Window),
            Relation: new(static element => ((Pane)element).Window, typeof(Window)),
            RelationShape: new(QueryRelationCardinality.One, QueryTarget.Window, SnapshotDepth.Panes)),
        new(
            "session_active_pane", QueryTarget.Session, null, typeof(Session), nameof(Session.ActivePane),
            Relation: new(static element => ((Session)element).ActivePane.Value, typeof(Pane)),
            RelationShape: new(QueryRelationCardinality.One, QueryTarget.Pane, SnapshotDepth.Panes),
            UnwrapCapturedValue: true),
        new(
            "session_active_window", QueryTarget.Session, null, typeof(Session), nameof(Session.ActiveWindow),
            Relation: new(static element => ((Session)element).ActiveWindow.Value, typeof(Window)),
            RelationShape: new(QueryRelationCardinality.One, QueryTarget.Window, SnapshotDepth.Windows),
            UnwrapCapturedValue: true),
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
            RelationShape: new(QueryRelationCardinality.One, QueryTarget.Pane, SnapshotDepth.Panes),
            UnwrapCapturedValue: true),
        new(
            "window_activity_flag",
            QueryTarget.Window,
            QueryValueKind.Boolean,
            typeof(Window),
            nameof(Window.ActivityAlert),
            new(static element => ((Window)element).ActivityAlert, typeof(bool))),
        new(
            "window_bell_flag",
            QueryTarget.Window,
            QueryValueKind.Boolean,
            typeof(Window),
            nameof(Window.BellAlert),
            new(static element => ((Window)element).BellAlert, typeof(bool))),
        new(
            "window_flags",
            QueryTarget.Window,
            QueryValueKind.String,
            typeof(Window),
            nameof(Window.Flags),
            new(static element => ((Window)element).Flags, typeof(string))),
        new(
            "window_height",
            QueryTarget.Window,
            QueryValueKind.Int64,
            typeof(Window),
            nameof(Window.Height),
            new(static element => (long)((Window)element).Height, typeof(long))),
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
            "window_layout",
            QueryTarget.Window,
            QueryValueKind.String,
            typeof(Window),
            nameof(Window.Layout),
            new(static element => ((Window)element).Layout, typeof(string))),
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
        new(
            "window_silence_flag",
            QueryTarget.Window,
            QueryValueKind.Boolean,
            typeof(Window),
            nameof(Window.SilenceAlert),
            new(static element => ((Window)element).SilenceAlert, typeof(bool))),
        new(
            "window_width",
            QueryTarget.Window,
            QueryValueKind.Int64,
            typeof(Window),
            nameof(Window.Width),
            new(static element => ((Window)element).Width, typeof(int))),
        new(
            "window_zoomed_flag",
            QueryTarget.Window,
            QueryValueKind.Boolean,
            typeof(Window),
            nameof(Window.Zoomed),
            new(static element => ((Window)element).Zoomed, typeof(bool))),
    ];

    private static readonly FrozenDictionary<string, FieldDefinition> FieldsByWireName =
        Fields.ToFrozenDictionary(static field => field.WireName, StringComparer.Ordinal);

    private static readonly FrozenDictionary<QueryTarget, ReadOnlyCollection<QueryFieldDescriptor>> Descriptors =
        Fields.GroupBy(static field => field.Target).ToFrozenDictionary(
            static group => group.Key,
            static group => Array.AsReadOnly(group.Select(Describe).ToArray()));

    internal static IReadOnlyList<string> WireNames { get; } =
        new ReadOnlyCollection<string>([.. Fields.Select(static field => field.WireName)]);

    private static QueryFieldDescriptor Describe(FieldDefinition field)
    {
        List<string> operations = field.Kind is null ? [] : ["equal", "notEqual"];
        if (field.Kind is QueryValueKind.Int64)
        {
            operations.AddRange(["lessThan", "lessThanOrEqual", "greaterThan", "greaterThanOrEqual"]);
        }
        else if (field.Kind is QueryValueKind.String)
        {
            operations.AddRange(["stringEqualOrdinal", "stringEqualOrdinalIgnoreCase",
                "startsWithOrdinal", "startsWithOrdinalIgnoreCase",
                "endsWithOrdinal", "endsWithOrdinalIgnoreCase",
                "containsOrdinal", "containsOrdinalIgnoreCase", "regex"]);
        }
        if (field.RelationShape?.Cardinality is QueryRelationCardinality.Many)
        {
            operations.AddRange(["any", "all"]);
        }
        else if (field.RelationShape?.Cardinality is QueryRelationCardinality.One)
        {
            operations.Add("related");
        }

        SnapshotDepth? depth = field.Owner is null || field.Target == QueryTarget.Client
            ? null
            : field.RelationShape?.Depth ?? field.Target switch
            {
                QueryTarget.Session => SnapshotDepth.Sessions,
                QueryTarget.Window => SnapshotDepth.Windows,
                _ => SnapshotDepth.Panes,
            };
        return new QueryFieldDescriptor(
            field.WireName,
            field.Target,
            field.Kind,
            field.Scalar is null ? null : field.Property
                + (field.RelationShape?.Cardinality is QueryRelationCardinality.Many ? ".Count" : string.Empty),
            field.Relation is null ? null : field.Property + (field.UnwrapCapturedValue ? ".Value" : string.Empty),
            field.RelationShape?.Target,
            field.RelationShape?.Cardinality,
            depth,
            field.Scalar is null ? null : field.Nullable,
            operations);
    }

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

    internal static bool TryGetTmuxFormat(string wireName, [NotNullWhen(true)] out string? format)
    {
        format = FieldsByWireName.TryGetValue(wireName, out FieldDefinition field) ? field.TmuxFormat : null;
        return format is not null;
    }

    internal static bool CanBeAbsent(string wireName) =>
        FieldsByWireName.TryGetValue(wireName, out FieldDefinition field) && field.Nullable;

    internal static bool TryGetWireName(Type owner, string property, out string wireName)
    {
        if (owner == typeof(Window) && property == nameof(Window.Active))
        {
            wireName = "window_active";
            return true;
        }

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

    private static string? ReadPaneText(Pane pane, string wireName) =>
        pane.Snapshot is { } fields && fields.TryGetValue(wireName, out string? value)
            ? value
            : throw new IncompleteSnapshotException(wireName, SnapshotDepth.Panes);

    private static bool ReadPaneFlag(Pane pane, string wireName) => ReadPaneText(pane, wireName) switch
    {
        "1" => true,
        "0" => false,
        null => throw new IncompleteSnapshotException(wireName, SnapshotDepth.Panes),
        string value => throw new TmuxProtocolException(
            $"Captured {wireName} value '{value}' is not zero or one.",
            value,
            TmuxDispatchState.NotDispatched),
    };

    private readonly record struct FieldDefinition(
        string WireName,
        QueryTarget Target,
        QueryValueKind? Kind,
        Type? Owner = null,
        string? Property = null,
        QueryFieldAccessor? Scalar = null,
        QueryFieldAccessor? Relation = null,
        QueryRelationDefinition? RelationShape = null,
        bool Nullable = false,
        bool UnwrapCapturedValue = false)
    {
        // The tmux format variable a -f filter reads, when tmux has one.
        internal string? TmuxFormat => WireName switch
        {
            // tmux has no session pane total, and its linked-session count
            // collapses session groups rather than counting captured sessions.
            "client_id" or "session_panes" or "window_linked_sessions" => null,
            "pane_command" => "pane_current_command",
            _ => WireName,
        };
    }
}

/// <summary>Names whether a captured query relation has one child or a collection.</summary>
public enum QueryRelationCardinality
{
    /// <summary>A captured to-one relation.</summary>
    One,

    /// <summary>A captured collection relation.</summary>
    Many,
}

internal readonly record struct QueryRelationDefinition(
    QueryRelationCardinality Cardinality,
    QueryTarget Target,
    SnapshotDepth Depth);
