using System.Diagnostics.Tracing;
using System.Globalization;
using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

// Pane mutations return replacements when a truthful handle remains; destructive
// or re-homing operations do not.
public sealed partial class Pane
{
    private const string CaptureTrimCapability = "capture_pane_trim_trailing";
    private const string ChooseTreeSortTimeCapability = "choose_tree_sort_time";
    private const string CaptureModeScreenCapability = "capture_pane_mode_screen";
    private const string CaptureMetadataCapability = "capture_pane_3_7_metadata";
    private const string ClearHistoryHyperlinksCapability = "clear_history_hyperlinks";
    private const string CopyModePageDownCapability = "copy_mode_page_down";
    private const string DisplayMessageLiteralCapability = "display_message_literal";
    private const string DisplayMessageUpdatePaneCapability = "display_message_update_pane";
    private const string PopupOptionsCapability = "display_popup_3_3_options";
    private const string PopupKeyPolicyCapability = "display_popup_3_6_key_policy";
    private const string PasteRawBytesCapability = "paste_buffer_no_vis";
    private const string SendKeysClientCapability = "send_keys_client_keys";
    private const string SplitAppearanceCapability = "split_window_appearance";
    private const string SplitEmptyCapability = "split_window_empty";
    private const string NewPaneCommandCapability = "new_pane_command";


    private static void AddValue(List<string> arguments, string flag, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            arguments.Add(flag);
            arguments.Add(value);
        }
    }

    private static void AddValue(List<string> arguments, string flag, int? value)
    {
        if (value is int cells)
        {
            arguments.Add(flag);
            arguments.Add(cells.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void AddEnvironment(
        List<string> arguments,
        IReadOnlyDictionary<string, string>? environment)
    {
        if (environment is null)
        {
            return;
        }

        foreach ((string key, string value) in environment)
        {
            arguments.Add("-e");
            arguments.Add($"{key}={value}");
        }
    }

    private static bool Supports(Server owner, string capability) =>
        owner.Version is TmuxVersion version
        && TmuxCapabilities.IsSupported(version, capability);


    private static void LogTrimUnsupported(Action<TmuxLogEntry> sink, string? tmuxVersion) =>
        TmuxLog.Write(
            sink,
            EventLevel.Warning,
            6,
            "trailing-space trim flag omitted, tmux {TmuxVersion} does not carry it",
            ("TmuxVersion", tmuxVersion));

    private static void LogModeScreenUnsupported(Action<TmuxLogEntry> sink, string? tmuxVersion) =>
        TmuxLog.Write(
            sink,
            EventLevel.Warning,
            7,
            "mode-screen capture flag omitted, tmux {TmuxVersion} does not carry it",
            ("TmuxVersion", tmuxVersion));

    private static void LogCaptureMetadataUnsupported(Action<TmuxLogEntry> sink, string? tmuxVersion) =>
        TmuxLog.Write(
            sink,
            EventLevel.Warning,
            8,
            "capture metadata flags omitted, tmux {TmuxVersion} does not carry them",
            ("TmuxVersion", tmuxVersion));

    private static void LogHyperlinksUnsupported(Action<TmuxLogEntry> sink, string? tmuxVersion) =>
        TmuxLog.Write(
            sink,
            EventLevel.Warning,
            9,
            "hyperlink reset flag omitted, tmux {TmuxVersion} does not carry it",
            ("TmuxVersion", tmuxVersion));

    private static void LogPageDownUnsupported(Action<TmuxLogEntry> sink, string? tmuxVersion) =>
        TmuxLog.Write(
            sink,
            EventLevel.Warning,
            10,
            "copy-mode page-down flag omitted, tmux {TmuxVersion} does not carry it",
            ("TmuxVersion", tmuxVersion));

    private static void LogLiteralUnsupported(Action<TmuxLogEntry> sink, string? tmuxVersion) =>
        TmuxLog.Write(
            sink,
            EventLevel.Warning,
            11,
            "literal message flag omitted, tmux {TmuxVersion} will expand the message",
            ("TmuxVersion", tmuxVersion));

    private static void LogUpdatePaneUnsupported(Action<TmuxLogEntry> sink, string? tmuxVersion) =>
        TmuxLog.Write(
            sink,
            EventLevel.Warning,
            12,
            "pane redraw flag omitted, tmux {TmuxVersion} does not carry it",
            ("TmuxVersion", tmuxVersion));

    private static void LogPopupOptionsUnsupported(Action<TmuxLogEntry> sink, string? tmuxVersion) =>
        TmuxLog.Write(
            sink,
            EventLevel.Warning,
            13,
            "popup appearance flags omitted, tmux {TmuxVersion} does not carry them",
            ("TmuxVersion", tmuxVersion));

    private static void LogPopupKeyPolicyUnsupported(Action<TmuxLogEntry> sink, string? tmuxVersion) =>
        TmuxLog.Write(
            sink,
            EventLevel.Warning,
            14,
            "popup key flags omitted, tmux {TmuxVersion} does not carry them",
            ("TmuxVersion", tmuxVersion));

    private static void LogRawPasteUnsupported(Action<TmuxLogEntry> sink, string? tmuxVersion) =>
        TmuxLog.Write(
            sink,
            EventLevel.Warning,
            15,
            "raw paste flag omitted, tmux {TmuxVersion} already pastes raw bytes",
            ("TmuxVersion", tmuxVersion));

    private static void LogClientKeysUnsupported(Action<TmuxLogEntry> sink, string? tmuxVersion) =>
        TmuxLog.Write(
            sink,
            EventLevel.Warning,
            16,
            "send-keys client flags omitted, tmux {TmuxVersion} does not carry them",
            ("TmuxVersion", tmuxVersion));

    private static void LogSplitAppearanceUnsupported(Action<TmuxLogEntry> sink, string? tmuxVersion) =>
        TmuxLog.Write(
            sink,
            EventLevel.Warning,
            17,
            "split appearance flags omitted, tmux {TmuxVersion} does not carry them",
            ("TmuxVersion", tmuxVersion));

    private static void LogSplitEmptyUnsupported(Action<TmuxLogEntry> sink, string? tmuxVersion) =>
        TmuxLog.Write(
            sink,
            EventLevel.Warning,
            18,
            "empty split flag omitted, tmux {TmuxVersion} will spawn a shell instead",
            ("TmuxVersion", tmuxVersion));

    private static void LogChooseTreeSortTime(Action<TmuxLogEntry> sink, string? tmuxVersion) =>
        TmuxLog.Write(
            sink,
            EventLevel.Warning,
            20,
            "activity-time sort order omitted, tmux {TmuxVersion} dropped it",
            ("TmuxVersion", tmuxVersion));

    private static void LogDisplayMessageRefused(Action<TmuxLogEntry> sink, string tmuxError) =>
        TmuxLog.Write(
            sink,
            EventLevel.Warning,
            19,
            "tmux refused to display the message: {TmuxError}",
            ("TmuxError", tmuxError));

    // The version comes from state captured when the handle materialized, so
    // gating costs no extra tmux command and the call still dispatches once.
    private bool Requires(string capability, Action<Action<TmuxLogEntry>, string?> log)
    {
        Server owner = Server;
        if (Supports(owner, capability))
        {
            return true;
        }

        if (owner.Connection?.Options.LogSink is { } sink)
        {
            log(sink, owner.RawVersion);
        }

        return false;
    }

    private string Target => _id.ToString();

    private int ReadCapturedInt(string wireName, string relation) =>
        int.TryParse(
            ReadSnapshot(wireName),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out int value)
            ? value
            : throw new IncompleteSnapshotException(relation, SnapshotDepth.Server);

    [UnsupportedOSPlatform("windows")]
    private async Task RunAsync(List<string> arguments, CancellationToken cancellationToken)
    {
        TmuxCommandResult result = await _commandDispatcher
            .ExecuteAsync(arguments, cancellationToken)
            .ConfigureAwait(false);
        TmuxCommandFailure.ThrowIfFailed(result, arguments[0]);
    }
}
