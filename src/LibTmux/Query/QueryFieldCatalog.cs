using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace LibTmux.Query;

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
            new(static element => ((Pane)element).AtBottom, typeof(bool))),
        new(
            "pane_at_left",
            QueryTarget.Pane,
            QueryValueKind.Boolean,
            typeof(Pane),
            nameof(Pane.AtLeft),
            new(static element => ((Pane)element).AtLeft, typeof(bool))),
        new(
            "pane_at_right",
            QueryTarget.Pane,
            QueryValueKind.Boolean,
            typeof(Pane),
            nameof(Pane.AtRight),
            new(static element => ((Pane)element).AtRight, typeof(bool))),
        new(
            "pane_at_top",
            QueryTarget.Pane,
            QueryValueKind.Boolean,
            typeof(Pane),
            nameof(Pane.AtTop),
            new(static element => ((Pane)element).AtTop, typeof(bool))),
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
            "pane_dead",
            QueryTarget.Pane,
            QueryValueKind.Boolean,
            typeof(Pane),
            nameof(Pane.Dead),
            new(static element => ((Pane)element).Dead, typeof(bool))),
        new(
            "pane_height",
            QueryTarget.Pane,
            QueryValueKind.Int64,
            typeof(Pane),
            nameof(Pane.Height),
            new(static element => (long)((Pane)element).Height, typeof(long))),
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
            new(static element => (long)((Pane)element).Index, typeof(long))),
        new(
            "pane_left",
            QueryTarget.Pane,
            QueryValueKind.Int64,
            typeof(Pane),
            nameof(Pane.Left),
            new(static element => (long)((Pane)element).Left, typeof(long))),
        new(
            "pane_pid",
            QueryTarget.Pane,
            QueryValueKind.Int64,
            typeof(Pane),
            nameof(Pane.ProcessId),
            new(static element => (long)((Pane)element).ProcessId, typeof(long))),
        new(
            "pane_title",
            QueryTarget.Pane,
            QueryValueKind.String,
            typeof(Pane),
            nameof(Pane.Title),
            new(static element => ((Pane)element).Title, typeof(string))),
        new(
            "pane_top",
            QueryTarget.Pane,
            QueryValueKind.Int64,
            typeof(Pane),
            nameof(Pane.Top),
            new(static element => (long)((Pane)element).Top, typeof(long))),
        new(
            "pane_width",
            QueryTarget.Pane,
            QueryValueKind.Int64,
            typeof(Pane),
            nameof(Pane.Width),
            new(static element => (long)((Pane)element).Width, typeof(long))),
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
            "session_windows",
            QueryTarget.Session,
            QueryValueKind.Int64,
            typeof(Session),
            nameof(Session.Windows),
            new(static element => checked((long)((Session)element).Windows.Count), typeof(long)),
            new(
                static element => ((Session)element).Windows,
                typeof(CapturedRelation<Window>))),
        new(
            "window_active",
            QueryTarget.Window,
            QueryValueKind.Boolean,
            typeof(Window),
            nameof(Window.Active),
            new(static element => ((Window)element).Active, typeof(bool))),
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
            "window_index",
            QueryTarget.Window,
            QueryValueKind.Int64,
            typeof(Window),
            nameof(Window.Index),
            new(static element => (long)((Window)element).Index, typeof(long))),
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
            new(static element => ((Window)element).Panes, typeof(CapturedRelation<Pane>))),
        new(
            "window_width",
            QueryTarget.Window,
            QueryValueKind.Int64,
            typeof(Window),
            nameof(Window.Width),
            new(static element => (long)((Window)element).Width, typeof(long))),
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

    internal static IReadOnlyList<string> WireNames { get; } =
        new ReadOnlyCollection<string>([.. Fields.Select(static field => field.WireName)]);

    internal static bool IsRelation(string wireName) =>
        FieldsByWireName.TryGetValue(wireName, out FieldDefinition field)
        && field.Relation is not null;

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
        if (FieldsByWireName.TryGetValue(wireName, out FieldDefinition field))
        {
            kind = field.Kind;
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
        QueryValueKind Kind,
        Type? Owner = null,
        string? Property = null,
        QueryFieldAccessor? Scalar = null,
        QueryFieldAccessor? Relation = null)
    {
        // The tmux format variable a -f filter reads, when tmux has one.
        internal string? TmuxFormat => WireName switch
        {
            "client_id" => null,
            "pane_command" => "pane_current_command",
            _ => WireName,
        };
    }
}
