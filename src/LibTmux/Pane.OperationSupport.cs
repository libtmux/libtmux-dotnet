using System.Globalization;
using System.Runtime.Versioning;
using LibTmux.Internal;
using Microsoft.Extensions.Logging;

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


    [LoggerMessage(
        EventId = 6,
        Level = LogLevel.Warning,
        Message = "trailing-space trim flag omitted, tmux {TmuxVersion} does not carry it")]
    private static partial void LogTrimUnsupported(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 7,
        Level = LogLevel.Warning,
        Message = "mode-screen capture flag omitted, tmux {TmuxVersion} does not carry it")]
    private static partial void LogModeScreenUnsupported(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 8,
        Level = LogLevel.Warning,
        Message = "capture metadata flags omitted, tmux {TmuxVersion} does not carry them")]
    private static partial void LogCaptureMetadataUnsupported(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 9,
        Level = LogLevel.Warning,
        Message = "hyperlink reset flag omitted, tmux {TmuxVersion} does not carry it")]
    private static partial void LogHyperlinksUnsupported(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 10,
        Level = LogLevel.Warning,
        Message = "copy-mode page-down flag omitted, tmux {TmuxVersion} does not carry it")]
    private static partial void LogPageDownUnsupported(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 11,
        Level = LogLevel.Warning,
        Message = "literal message flag omitted, tmux {TmuxVersion} will expand the message")]
    private static partial void LogLiteralUnsupported(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 12,
        Level = LogLevel.Warning,
        Message = "pane redraw flag omitted, tmux {TmuxVersion} does not carry it")]
    private static partial void LogUpdatePaneUnsupported(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 13,
        Level = LogLevel.Warning,
        Message = "popup appearance flags omitted, tmux {TmuxVersion} does not carry them")]
    private static partial void LogPopupOptionsUnsupported(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 14,
        Level = LogLevel.Warning,
        Message = "popup key flags omitted, tmux {TmuxVersion} does not carry them")]
    private static partial void LogPopupKeyPolicyUnsupported(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 15,
        Level = LogLevel.Warning,
        Message = "raw paste flag omitted, tmux {TmuxVersion} already pastes raw bytes")]
    private static partial void LogRawPasteUnsupported(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 16,
        Level = LogLevel.Warning,
        Message = "send-keys client flags omitted, tmux {TmuxVersion} does not carry them")]
    private static partial void LogClientKeysUnsupported(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 17,
        Level = LogLevel.Warning,
        Message = "split appearance flags omitted, tmux {TmuxVersion} does not carry them")]
    private static partial void LogSplitAppearanceUnsupported(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 18,
        Level = LogLevel.Warning,
        Message = "empty split flag omitted, tmux {TmuxVersion} will spawn a shell instead")]
    private static partial void LogSplitEmptyUnsupported(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 20,
        Level = LogLevel.Warning,
        Message = "activity-time sort order omitted, tmux {TmuxVersion} dropped it")]
    private static partial void LogChooseTreeSortTime(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 19,
        Level = LogLevel.Warning,
        Message = "tmux refused to display the message: {TmuxError}")]
    private static partial void LogDisplayMessageRefused(ILogger logger, string tmuxError);

    // The version comes from state captured when the handle materialized, so
    // gating costs no extra tmux command and the call still dispatches once.
    private bool Requires(string capability, Action<ILogger, string?> log)
    {
        Server owner = Server;
        if (Supports(owner, capability))
        {
            return true;
        }

        if (owner.Connection?.Options.Logger is ILogger logger)
        {
            log(logger, owner.RawVersion);
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
