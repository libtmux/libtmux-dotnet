using System.Collections.Frozen;
using System.Collections.ObjectModel;

namespace LibTmux.Internal;

internal static class FormatCatalog
{
    // The first release whose format fields the catalog models, below the
    // support floor so that the psmux preview, which reports 3.3.8, keeps them.
    private static readonly TmuxVersion Minimum = TmuxVersion.Parse("3.3");
    private static readonly TmuxVersion Minimum37 = TmuxVersion.Parse("3.7");
    private static readonly FrozenSet<string> FieldsIntroducedIn37 = new[]
    {
        "pane_flags",
        "pane_floating_flag",
        "pane_x",
        "pane_y",
        "pane_z",
        "pane_zoomed_flag",
        "pane_pb_progress",
        "pane_pb_state",
        "pane_pipe_pid",
        "bracket_paste_flag",
        "synchronized_output_flag",
    }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly string[] UniversalObjFields =
    [
        "config_files",
        "line",
        "next_session_id",
        "pid",
        "socket_path",
        "start_time",
        "uid",
        "user",
        "version",
    ];

    private static readonly string[] SessionObjFields =
    [
        "active_window_index",
        "last_window_index",
        "session_activity",
        "session_alerts",
        "session_attached",
        "session_attached_list",
        "session_created",
        "session_format",
        "session_group",
        "session_group_attached",
        "session_group_attached_list",
        "session_group_list",
        "session_group_many_attached",
        "session_group_size",
        "session_grouped",
        "session_id",
        "session_last_attached",
        "session_many_attached",
        "session_marked",
        "session_name",
        "session_path",
        "session_stack",
        "session_windows",
    ];

    private static readonly string[] WindowObjFields =
    [
        "window_active",
        "window_active_clients",
        "window_active_clients_list",
        "window_active_sessions",
        "window_active_sessions_list",
        "window_activity",
        "window_activity_flag",
        "window_bell_flag",
        "window_bigger",
        "window_cell_height",
        "window_cell_width",
        "window_end_flag",
        "window_flags",
        "window_format",
        "window_height",
        "window_id",
        "window_index",
        "window_last_flag",
        "window_layout",
        "window_linked",
        "window_linked_sessions",
        "window_linked_sessions_list",
        "window_marked_flag",
        "window_name",
        "window_offset_x",
        "window_offset_y",
        "window_panes",
        "window_raw_flags",
        "window_silence_flag",
        "window_stack_index",
        "window_start_flag",
        "window_visible_layout",
        "window_width",
        "window_zoomed_flag",
    ];

    private static readonly string[] PaneObjFields =
    [
        "alternate_saved_x",
        "alternate_saved_y",
        "bracket_paste_flag",
        "cursor_character",
        "cursor_flag",
        "cursor_x",
        "cursor_y",
        "history_bytes",
        "history_limit",
        "history_size",
        "insert_flag",
        "keypad_cursor_flag",
        "keypad_flag",
        "mouse_all_flag",
        "mouse_any_flag",
        "mouse_button_flag",
        "mouse_sgr_flag",
        "mouse_standard_flag",
        "origin_flag",
        "pane_active",
        "pane_at_bottom",
        "pane_at_left",
        "pane_at_right",
        "pane_at_top",
        "pane_bg",
        "pane_bottom",
        "pane_current_command",
        "pane_current_path",
        "pane_dead",
        "pane_dead_signal",
        "pane_dead_status",
        "pane_dead_time",
        "pane_fg",
        "pane_flags",
        "pane_floating_flag",
        "pane_format",
        "pane_height",
        "pane_id",
        "pane_in_mode",
        "pane_index",
        "pane_input_off",
        "pane_last",
        "pane_left",
        "pane_marked",
        "pane_marked_set",
        "pane_mode",
        "pane_path",
        "pane_pb_progress",
        "pane_pb_state",
        "pane_pid",
        "pane_pipe",
        "pane_pipe_pid",
        "pane_right",
        "pane_search_string",
        "pane_start_command",
        "pane_start_path",
        "pane_synchronized",
        "pane_tabs",
        "pane_title",
        "pane_top",
        "pane_tty",
        "pane_width",
        "pane_x",
        "pane_y",
        "pane_z",
        "pane_zoomed_flag",
        "scroll_region_lower",
        "scroll_region_upper",
        "synchronized_output_flag",
        "wrap_flag",
    ];

    private static readonly string[] ClientObjFields =
    [
        "client_activity",
        "client_cell_height",
        "client_cell_width",
        "client_control_mode",
        "client_created",
        "client_discarded",
        "client_flags",
        "client_height",
        "client_key_table",
        "client_last_session",
        "client_mode_format",
        "client_name",
        "client_pid",
        "client_prefix",
        "client_readonly",
        "client_session",
        "client_termfeatures",
        "client_termname",
        "client_termtype",
        "client_tty",
        "client_uid",
        "client_user",
        "client_utf8",
        "client_width",
        "client_written",
    ];

    private static readonly string[] BufferObjFields =
    [
        "buffer_name",
        "buffer_sample",
        "buffer_size",
    ];

    private static readonly string[] EventObjFields =
    [
        "copy_cursor_line",
        "copy_cursor_word",
        "copy_cursor_x",
        "copy_cursor_y",
        "scroll_position",
        "selection_end_x",
        "selection_end_y",
        "selection_start_x",
        "selection_start_y",
    ];

    private static readonly string[] ContextObjFields =
    [
        "command_list_alias",
        "command_list_name",
        "command_list_usage",
        "current_file",
        "search_match",
    ];

    private static readonly ReadOnlyCollection<FormatFieldDescriptor> ObjFields =
        Array.AsReadOnly(
            [
                .. CreateObjProjection(UniversalObjFields, "universal"),
                .. CreateObjProjection(SessionObjFields, "session"),
                .. CreateObjProjection(WindowObjFields, "window"),
                .. CreateObjProjection(PaneObjFields, "pane"),
                .. CreateObjProjection(ClientObjFields, "client"),
                .. CreateObjProjection(BufferObjFields, "buffer"),
                .. CreateObjProjection(EventObjFields, "event"),
                .. CreateObjProjection(ContextObjFields, "context"),
            ]);
    private static readonly ReadOnlyCollection<FormatFieldDescriptor> Clients =
        CreateProjection(TmuxFormats.Client, "client");
    private static readonly ReadOnlyCollection<FormatFieldDescriptor> Panes =
        CreateProjection(TmuxFormats.Pane, "pane");
    private static readonly ReadOnlyCollection<FormatFieldDescriptor> Sessions =
        CreateProjection(TmuxFormats.Session, "session");
    private static readonly ReadOnlyCollection<FormatFieldDescriptor> Windows =
        CreateProjection(TmuxFormats.Window, "window");
    // Per-entity descriptors stay canonical so a projection and a lookup
    // return the same instance; Obj contributes the fields no per-entity
    // projection carries, such as the tmux 3.3 additions.
    private static readonly FrozenDictionary<string, FormatFieldDescriptor> Fields =
        Clients.Concat(Panes)
            .Concat(Sessions)
            .Concat(Windows)
            .Concat(ObjFields)
            .GroupBy(static field => field.WireName, StringComparer.Ordinal)
            .ToFrozenDictionary(
                static group => group.Key,
                static group => group.First(),
                StringComparer.Ordinal);
    private static readonly FrozenDictionary<string, IReadOnlySet<string>> ScopesByListCommand =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["list-sessions"] = CreateScopeSet("universal", "session", "window", "pane"),
            ["list-windows"] = CreateScopeSet("universal", "session", "window", "pane"),
            ["list-panes"] = CreateScopeSet("universal", "session", "window", "pane"),
            ["list-clients"] = CreateScopeSet(
                "universal",
                "session",
                "window",
                "pane",
                "client"),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Gets every projected tmux format field.</summary>
    internal static IReadOnlyList<FormatFieldDescriptor> ObjProjection => ObjFields;

    internal static IReadOnlyList<FormatFieldDescriptor> ClientProjection => Clients;

    internal static IReadOnlyList<FormatFieldDescriptor> PaneProjection => Panes;

    internal static IReadOnlyList<FormatFieldDescriptor> SessionProjection => Sessions;

    internal static IReadOnlyList<FormatFieldDescriptor> WindowProjection => Windows;

    internal static FormatFieldDescriptor Resolve(string wireName)
    {
        ArgumentNullException.ThrowIfNull(wireName);
        return Fields.TryGetValue(wireName, out FormatFieldDescriptor? descriptor)
            ? descriptor
            : throw new KeyNotFoundException($"Unknown tmux format field '{wireName}'.");
    }

    internal static TmuxVersion GetMinimumTmuxVersion(string wireName) =>
        Resolve(wireName).MinimumTmuxVersion;

    internal static IReadOnlySet<string> GetScopesForListCommand(string listCommand)
    {
        ArgumentNullException.ThrowIfNull(listCommand);
        return ScopesByListCommand.TryGetValue(listCommand, out IReadOnlySet<string>? scopes)
            ? scopes
            : throw new KeyNotFoundException($"Unknown tmux list command '{listCommand}'.");
    }

    private static ReadOnlyCollection<FormatFieldDescriptor> CreateProjection(
        IEnumerable<string> wireNames,
        string scope)
    {
        IReadOnlySet<string> scopes = CreateScopeSet(scope);
        return Array.AsReadOnly(
            wireNames.Select(
                    wireName => new FormatFieldDescriptor(
                        wireName,
                        ToPascalCase(wireName),
                        FieldsIntroducedIn37.Contains(wireName) ? Minimum37 : Minimum,
                        scopes))
                .ToArray());
    }

    private static FormatFieldDescriptor[] CreateObjProjection(
        string[] wireNames,
        string scope)
    {
        IReadOnlySet<string> scopes = CreateScopeSet(scope);
        return [.. wireNames.Select(
            wireName => new FormatFieldDescriptor(
                wireName,
                ToPascalCase(wireName),
                MinimumVersionFor(wireName),
                scopes))];
    }

    private static TmuxVersion MinimumVersionFor(string wireName) =>
        FieldsIntroducedIn37.Contains(wireName) ? Minimum37 : Minimum;

    private static FrozenSet<string> CreateScopeSet(params string[] scopes) =>
        scopes.ToFrozenSet(StringComparer.Ordinal);

    private static string ToPascalCase(string wireName) =>
        string.Concat(
            wireName.Split('_', StringSplitOptions.RemoveEmptyEntries)
                .Select(static part => char.ToUpperInvariant(part[0]) + part[1..]));
}
