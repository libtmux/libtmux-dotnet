using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Internal;

namespace LibTmux.IntegrationTests.Parity;

[UnsupportedOSPlatform("windows")]
public sealed class Component04ParityTests
{
    private const string ObjFieldPrefix = "libtmux.neo:Obj.";

    public static TheoryData<string> OwnedRows =>
    [
        "libtmux.common:PaneDict",
        "libtmux.common:SessionDict",
        "libtmux.common:WindowDict",
        "libtmux.formats:FORMAT_SEPARATOR",
        "libtmux.neo:<module>",
        "libtmux.neo:FIELD_VERSION",
        "libtmux.neo:Obj",
        "libtmux.neo:Obj.active_window_index",
        "libtmux.neo:Obj.alternate_saved_x",
        "libtmux.neo:Obj.alternate_saved_y",
        "libtmux.neo:Obj.bracket_paste_flag",
        "libtmux.neo:Obj.buffer_name",
        "libtmux.neo:Obj.buffer_sample",
        "libtmux.neo:Obj.buffer_size",
        "libtmux.neo:Obj.client_activity",
        "libtmux.neo:Obj.client_cell_height",
        "libtmux.neo:Obj.client_cell_width",
        "libtmux.neo:Obj.client_control_mode",
        "libtmux.neo:Obj.client_created",
        "libtmux.neo:Obj.client_discarded",
        "libtmux.neo:Obj.client_flags",
        "libtmux.neo:Obj.client_height",
        "libtmux.neo:Obj.client_key_table",
        "libtmux.neo:Obj.client_last_session",
        "libtmux.neo:Obj.client_mode_format",
        "libtmux.neo:Obj.client_name",
        "libtmux.neo:Obj.client_pid",
        "libtmux.neo:Obj.client_prefix",
        "libtmux.neo:Obj.client_readonly",
        "libtmux.neo:Obj.client_session",
        "libtmux.neo:Obj.client_termfeatures",
        "libtmux.neo:Obj.client_termname",
        "libtmux.neo:Obj.client_termtype",
        "libtmux.neo:Obj.client_tty",
        "libtmux.neo:Obj.client_uid",
        "libtmux.neo:Obj.client_user",
        "libtmux.neo:Obj.client_utf8",
        "libtmux.neo:Obj.client_width",
        "libtmux.neo:Obj.client_written",
        "libtmux.neo:Obj.command_list_alias",
        "libtmux.neo:Obj.command_list_name",
        "libtmux.neo:Obj.command_list_usage",
        "libtmux.neo:Obj.config_files",
        "libtmux.neo:Obj.copy_cursor_line",
        "libtmux.neo:Obj.copy_cursor_word",
        "libtmux.neo:Obj.copy_cursor_x",
        "libtmux.neo:Obj.copy_cursor_y",
        "libtmux.neo:Obj.current_file",
        "libtmux.neo:Obj.cursor_character",
        "libtmux.neo:Obj.cursor_flag",
        "libtmux.neo:Obj.cursor_x",
        "libtmux.neo:Obj.cursor_y",
        "libtmux.neo:Obj.history_bytes",
        "libtmux.neo:Obj.history_limit",
        "libtmux.neo:Obj.history_size",
        "libtmux.neo:Obj.insert_flag",
        "libtmux.neo:Obj.keypad_cursor_flag",
        "libtmux.neo:Obj.keypad_flag",
        "libtmux.neo:Obj.last_window_index",
        "libtmux.neo:Obj.line",
        "libtmux.neo:Obj.mouse_all_flag",
        "libtmux.neo:Obj.mouse_any_flag",
        "libtmux.neo:Obj.mouse_button_flag",
        "libtmux.neo:Obj.mouse_sgr_flag",
        "libtmux.neo:Obj.mouse_standard_flag",
        "libtmux.neo:Obj.next_session_id",
        "libtmux.neo:Obj.origin_flag",
        "libtmux.neo:Obj.pane_active",
        "libtmux.neo:Obj.pane_at_bottom",
        "libtmux.neo:Obj.pane_at_left",
        "libtmux.neo:Obj.pane_at_right",
        "libtmux.neo:Obj.pane_at_top",
        "libtmux.neo:Obj.pane_bg",
        "libtmux.neo:Obj.pane_bottom",
        "libtmux.neo:Obj.pane_current_command",
        "libtmux.neo:Obj.pane_current_path",
        "libtmux.neo:Obj.pane_dead",
        "libtmux.neo:Obj.pane_dead_signal",
        "libtmux.neo:Obj.pane_dead_status",
        "libtmux.neo:Obj.pane_dead_time",
        "libtmux.neo:Obj.pane_fg",
        "libtmux.neo:Obj.pane_flags",
        "libtmux.neo:Obj.pane_floating_flag",
        "libtmux.neo:Obj.pane_format",
        "libtmux.neo:Obj.pane_height",
        "libtmux.neo:Obj.pane_id",
        "libtmux.neo:Obj.pane_in_mode",
        "libtmux.neo:Obj.pane_index",
        "libtmux.neo:Obj.pane_input_off",
        "libtmux.neo:Obj.pane_last",
        "libtmux.neo:Obj.pane_left",
        "libtmux.neo:Obj.pane_marked",
        "libtmux.neo:Obj.pane_marked_set",
        "libtmux.neo:Obj.pane_mode",
        "libtmux.neo:Obj.pane_path",
        "libtmux.neo:Obj.pane_pb_progress",
        "libtmux.neo:Obj.pane_pb_state",
        "libtmux.neo:Obj.pane_pid",
        "libtmux.neo:Obj.pane_pipe",
        "libtmux.neo:Obj.pane_pipe_pid",
        "libtmux.neo:Obj.pane_right",
        "libtmux.neo:Obj.pane_search_string",
        "libtmux.neo:Obj.pane_start_command",
        "libtmux.neo:Obj.pane_start_path",
        "libtmux.neo:Obj.pane_synchronized",
        "libtmux.neo:Obj.pane_tabs",
        "libtmux.neo:Obj.pane_title",
        "libtmux.neo:Obj.pane_top",
        "libtmux.neo:Obj.pane_tty",
        "libtmux.neo:Obj.pane_width",
        "libtmux.neo:Obj.pane_x",
        "libtmux.neo:Obj.pane_y",
        "libtmux.neo:Obj.pane_z",
        "libtmux.neo:Obj.pane_zoomed_flag",
        "libtmux.neo:Obj.pid",
        "libtmux.neo:Obj.scroll_position",
        "libtmux.neo:Obj.scroll_region_lower",
        "libtmux.neo:Obj.scroll_region_upper",
        "libtmux.neo:Obj.search_match",
        "libtmux.neo:Obj.selection_end_x",
        "libtmux.neo:Obj.selection_end_y",
        "libtmux.neo:Obj.selection_start_x",
        "libtmux.neo:Obj.selection_start_y",
        "libtmux.neo:Obj.server",
        "libtmux.neo:Obj.session_activity",
        "libtmux.neo:Obj.session_alerts",
        "libtmux.neo:Obj.session_attached",
        "libtmux.neo:Obj.session_attached_list",
        "libtmux.neo:Obj.session_created",
        "libtmux.neo:Obj.session_format",
        "libtmux.neo:Obj.session_group",
        "libtmux.neo:Obj.session_group_attached",
        "libtmux.neo:Obj.session_group_attached_list",
        "libtmux.neo:Obj.session_group_list",
        "libtmux.neo:Obj.session_group_many_attached",
        "libtmux.neo:Obj.session_group_size",
        "libtmux.neo:Obj.session_grouped",
        "libtmux.neo:Obj.session_id",
        "libtmux.neo:Obj.session_last_attached",
        "libtmux.neo:Obj.session_many_attached",
        "libtmux.neo:Obj.session_marked",
        "libtmux.neo:Obj.session_name",
        "libtmux.neo:Obj.session_path",
        "libtmux.neo:Obj.session_stack",
        "libtmux.neo:Obj.session_windows",
        "libtmux.neo:Obj.socket_path",
        "libtmux.neo:Obj.start_time",
        "libtmux.neo:Obj.synchronized_output_flag",
        "libtmux.neo:Obj.uid",
        "libtmux.neo:Obj.user",
        "libtmux.neo:Obj.version",
        "libtmux.neo:Obj.window_active",
        "libtmux.neo:Obj.window_active_clients",
        "libtmux.neo:Obj.window_active_clients_list",
        "libtmux.neo:Obj.window_active_sessions",
        "libtmux.neo:Obj.window_active_sessions_list",
        "libtmux.neo:Obj.window_activity",
        "libtmux.neo:Obj.window_activity_flag",
        "libtmux.neo:Obj.window_bell_flag",
        "libtmux.neo:Obj.window_bigger",
        "libtmux.neo:Obj.window_cell_height",
        "libtmux.neo:Obj.window_cell_width",
        "libtmux.neo:Obj.window_end_flag",
        "libtmux.neo:Obj.window_flags",
        "libtmux.neo:Obj.window_format",
        "libtmux.neo:Obj.window_height",
        "libtmux.neo:Obj.window_id",
        "libtmux.neo:Obj.window_index",
        "libtmux.neo:Obj.window_last_flag",
        "libtmux.neo:Obj.window_layout",
        "libtmux.neo:Obj.window_linked",
        "libtmux.neo:Obj.window_linked_sessions",
        "libtmux.neo:Obj.window_linked_sessions_list",
        "libtmux.neo:Obj.window_marked_flag",
        "libtmux.neo:Obj.window_name",
        "libtmux.neo:Obj.window_offset_x",
        "libtmux.neo:Obj.window_offset_y",
        "libtmux.neo:Obj.window_panes",
        "libtmux.neo:Obj.window_raw_flags",
        "libtmux.neo:Obj.window_silence_flag",
        "libtmux.neo:Obj.window_stack_index",
        "libtmux.neo:Obj.window_start_flag",
        "libtmux.neo:Obj.window_visible_layout",
        "libtmux.neo:Obj.window_width",
        "libtmux.neo:Obj.window_zoomed_flag",
        "libtmux.neo:Obj.wrap_flag",
        "libtmux.neo:OutputRaw",
        "libtmux.neo:OutputsRaw",
        "libtmux.neo:SCOPES_BY_LIST_CMD",
        "libtmux.neo:fetch_obj",
        "libtmux.neo:fetch_objs",
        "libtmux.neo:get_output_format",
        "libtmux.neo:parse_output",
        "libtmux.pane:Pane.from_pane_id",
        "libtmux.server:Server.child_id_attribute",
        "libtmux.server:Server.formatter_prefix",
        "libtmux.window:Window.from_window_id",
    ];

    [Theory(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    [MemberData(nameof(OwnedRows))]
    public async Task Owned_parity_row_has_materialization_behavior(string pythonSymbolId)
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        var options = new ServerConnectionOptions(
            tmuxBinaryPath: raw.TmuxBinaryPath,
            socketPath: raw.SocketPath,
            configurationFile: "/dev/null");
        Server server = await Server.ConnectAsync(
            options,
            TestContext.Current.CancellationToken);
        TmuxVersion version = await TmuxVersion.DetectAsync(
            raw.TmuxBinaryPath,
            TestContext.Current.CancellationToken);
        var context = new MaterializationContext(server, version);
        var query = new MaterializationQuery(context);

        bool proved = pythonSymbolId switch
        {
            "libtmux.neo:Obj.server" => ReferenceEquals(context.Server, server),
            "libtmux.formats:FORMAT_SEPARATOR" => SeparatorIsExcluded(version),
            "libtmux.neo:FIELD_VERSION" =>
                FormatCatalog.GetMinimumTmuxVersion("pane_dead_signal")
                    == TmuxVersion.Parse("3.3"),
            "libtmux.neo:SCOPES_BY_LIST_CMD" =>
                FormatCatalog.GetScopesForListCommand("list-clients").Contains("client")
                && !FormatCatalog.GetScopesForListCommand("list-panes").Contains("client"),
            "libtmux.server:Server.child_id_attribute" =>
                ServerProjection.Descriptor.ChildIdAttribute == "session_id",
            "libtmux.server:Server.formatter_prefix" =>
                ServerProjection.Descriptor.FormatterPrefix == "server_",
            "libtmux.common:SessionDict" or "libtmux.neo:fetch_obj" =>
                await ProvesSessionAsync(query, context),
            "libtmux.common:WindowDict" or "libtmux.window:Window.from_window_id" =>
                await ProvesWindowAsync(query, context, server),
            "libtmux.common:PaneDict" or "libtmux.pane:Pane.from_pane_id" =>
                await ProvesPaneAsync(query, context, server),
            "libtmux.neo:<module>"
                or "libtmux.neo:Obj"
                or "libtmux.neo:OutputRaw"
                or "libtmux.neo:OutputsRaw"
                or "libtmux.neo:fetch_objs"
                or "libtmux.neo:get_output_format"
                or "libtmux.neo:parse_output" => await ProvesRowsAsync(query),
            _ => pythonSymbolId.StartsWith(ObjFieldPrefix, StringComparison.Ordinal)
                && ProjectsField(pythonSymbolId[ObjFieldPrefix.Length..]),
        };

        Assert.True(proved, $"Parity behavior was not proved for {pythonSymbolId}.");
    }

    private static bool SeparatorIsExcluded(TmuxVersion version)
    {
        // The separator is randomized per process and carries no format
        // punctuation, so no captured value can collide with it or expand.
        FormatProjection projection = FormatProjection.Create("list-sessions", version);
        return projection.FramedFieldCount == projection.Fields.Count
            && !projection.Template.Contains('\t', StringComparison.Ordinal)
            && !FormatProjection.RowSeparator.Contains('#', StringComparison.Ordinal)
            && FormatProjection.RowSeparator.Length >= 16;
    }

    private static bool ProjectsField(string wireName) =>
        FormatCatalog.ObjProjection.Any(
            field => string.Equals(field.WireName, wireName, StringComparison.Ordinal));

    private static async Task<bool> ProvesRowsAsync(MaterializationQuery query)
    {
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows = await query.FetchAsync(
            "list-sessions",
            cancellationToken: TestContext.Current.CancellationToken);
        return rows.Count == 1 && rows[0]["session_id"] is not null;
    }

    private static async Task<bool> ProvesSessionAsync(
        MaterializationQuery query,
        MaterializationContext context)
    {
        IReadOnlyDictionary<string, string?>? row = await query.FetchOneAsync(
            "list-sessions",
            "session_id",
            await FirstIdAsync(query, "list-sessions", "session_id"),
            cancellationToken: TestContext.Current.CancellationToken);
        return row is not null
            && Materializer.MaterializeSession(context, row).Id.Value >= 0;
    }

    private static async Task<bool> ProvesWindowAsync(
        MaterializationQuery query,
        MaterializationContext context,
        Server server)
    {
        string id = await FirstIdAsync(query, "list-windows", "window_id");
        IReadOnlyDictionary<string, string?>? row = await query.FetchOneAsync(
            "list-windows",
            "window_id",
            id,
            cancellationToken: TestContext.Current.CancellationToken);
        if (row is null)
        {
            return false;
        }

        Window materialized = Materializer.MaterializeWindow(context, row);
        Window resolved = await server.GetWindowAsync(
            materialized.Id,
            TestContext.Current.CancellationToken);
        return materialized.Id == resolved.Id;
    }

    private static async Task<bool> ProvesPaneAsync(
        MaterializationQuery query,
        MaterializationContext context,
        Server server)
    {
        string id = await FirstIdAsync(query, "list-panes", "pane_id");
        IReadOnlyDictionary<string, string?>? row = await query.FetchOneAsync(
            "list-panes",
            "pane_id",
            id,
            cancellationToken: TestContext.Current.CancellationToken);
        if (row is null)
        {
            return false;
        }

        Pane materialized = Materializer.MaterializePane(context, row);
        Pane resolved = await server.GetPaneAsync(
            materialized.Id,
            TestContext.Current.CancellationToken);
        return materialized.Id == resolved.Id;
    }

    private static async Task<string> FirstIdAsync(
        MaterializationQuery query,
        string listCommand,
        string idWireName)
    {
        string[] extra = listCommand is "list-windows" or "list-panes" ? ["-a"] : [];
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows = await query.FetchAsync(
            listCommand,
            extra,
            TestContext.Current.CancellationToken);
        return rows[0][idWireName]!;
    }
}
