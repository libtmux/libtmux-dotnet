using System.Collections.Frozen;

namespace LibTmux.Internal;

internal enum TmuxCapabilityState
{
    Unknown,
    Unsupported,
    Supported,
}

internal static class TmuxCapabilities
{
    private static readonly string[] Baseline =
    [
        "attachment_accounting",
        "byte_length_framing",
        "control_notifications",
        "format_fields_and_operators",
        "semicolon_grouping",
        "hook_scope_pane_window_set",
        "hook_scope_pane_window_show",
    ];
    private static readonly string[] Added33 =
    [
        "clear_prompt_history_command",
        "command_prompt_background",
        "confirm_before_background",
        "display_message_client",
        "display_popup_3_3_options",
        "missing_target_format_safety",
        "server_access_command",
        "show_prompt_history_command",
    ];
    private static readonly string[] Added34 =
    [
        "capture_pane_trim_trailing",
        "clear_history_hyperlinks",
        "confirm_before_acceptance",
        "display_menu_styles",
        "display_message_literal",
        "run_shell_working_directory",
        "send_keys_client_keys",
    ];
    private static readonly string[] Added35 =
    [
        "copy_mode_page_down",
        "display_menu_mouse",
    ];
    private static readonly string[] Added36 =
    [
        "capture_pane_mode_screen",
        "command_prompt_literal",
        "display_message_update_pane",
        "display_popup_3_6_key_policy",
        "run_shell_show_stderr",
    ];
    private static readonly string[] Added37 =
    [
        "capture_pane_3_7_metadata",
        "command_prompt_3_7_behavior",
        "kill_session_group",
        "list_keys_format",
        "new_pane_command",
        "paste_buffer_no_vis",
        "refresh_client_clipboard_query",
        "run_shell_arguments",
        "split_window_appearance",
        "split_window_empty",
    ];
    private static readonly FrozenDictionary<string, CapabilityInterval> Intervals =
        CreateIntervals();

    internal static TmuxCapabilityState GetState(
        TmuxVersion version,
        string capability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        if (!Intervals.TryGetValue(capability, out CapabilityInterval interval))
        {
            throw new KeyNotFoundException($"Unknown tmux capability '{capability}'.");
        }

        if (!version.IsStableRelease || version < LibTmuxInfo.MinimumTmuxVersion)
        {
            return TmuxCapabilityState.Unknown;
        }

        return interval.Contains(version)
            ? TmuxCapabilityState.Supported
            : TmuxCapabilityState.Unsupported;
    }

    internal static bool IsSupported(TmuxVersion version, string capability) =>
        GetState(version, capability) is TmuxCapabilityState.Supported;

    private static FrozenDictionary<string, CapabilityInterval> CreateIntervals()
    {
        TmuxVersion minimum = LibTmuxInfo.MinimumTmuxVersion;
        TmuxVersion version33 = TmuxVersion.Parse("3.3");
        TmuxVersion version34 = TmuxVersion.Parse("3.4");
        TmuxVersion version35 = TmuxVersion.Parse("3.5");
        TmuxVersion version36 = TmuxVersion.Parse("3.6");
        TmuxVersion version37 = TmuxVersion.Parse("3.7");
        TmuxVersion version37a = TmuxVersion.Parse("3.7a");
        var intervals = new Dictionary<string, CapabilityInterval>(StringComparer.Ordinal);

        Add(intervals, Baseline, minimum);
        Add(intervals, ["choose_tree_sort_time"], minimum, version37);
        Add(intervals, Added33, version33);
        Add(intervals, Added34, version34);
        Add(intervals, ["option_dollar_double_escape"], version34, version35);
        Add(intervals, Added35, version35);
        Add(intervals, Added36, version36);
        Add(intervals, Added37, version37);
        Add(intervals, ["break_pane_3_7_workaround"], version37, version37a);

        return intervals.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static void Add(
        IDictionary<string, CapabilityInterval> intervals,
        IEnumerable<string> capabilities,
        TmuxVersion supportedFrom,
        TmuxVersion? unsupportedFrom = null)
    {
        var interval = new CapabilityInterval(supportedFrom, unsupportedFrom);
        foreach (string capability in capabilities)
        {
            intervals.Add(capability, interval);
        }
    }

    private readonly struct CapabilityInterval
    {
        internal CapabilityInterval(
            TmuxVersion supportedFrom,
            TmuxVersion? unsupportedFrom)
        {
            if (!supportedFrom.IsStableRelease)
            {
                throw new ArgumentException(
                    "A capability interval requires a stable starting version.",
                    nameof(supportedFrom));
            }

            if (unsupportedFrom is TmuxVersion end
                && (!end.IsStableRelease || end <= supportedFrom))
            {
                throw new ArgumentException(
                    "A capability interval must end at a later stable version.",
                    nameof(unsupportedFrom));
            }

            SupportedFrom = supportedFrom;
            UnsupportedFrom = unsupportedFrom;
        }

        private TmuxVersion SupportedFrom { get; }

        private TmuxVersion? UnsupportedFrom { get; }

        internal bool Contains(TmuxVersion version) =>
            version >= SupportedFrom
            && (UnsupportedFrom is not TmuxVersion end || version < end);
    }
}
