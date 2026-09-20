using System.Globalization;

namespace LibTmux.Internal;

/// <summary>Builds the argv for the server's utility commands.</summary>
/// <remarks>
/// These commands share no shape with each other, so what is gathered here is
/// what they do share: the flags whose availability moved between tmux
/// versions, and the readings of what tmux prints back.
/// </remarks>
internal static class ServerUtilities
{
    /// <summary>The capability naming the whole <c>clear-prompt-history</c> command.</summary>
    internal const string ClearPromptHistoryCapability = "clear_prompt_history_command";

    /// <summary>The capability naming the whole <c>show-prompt-history</c> command.</summary>
    internal const string ShowPromptHistoryCapability = "show_prompt_history_command";

    /// <summary>The capability naming the whole <c>server-access</c> command.</summary>
    internal const string ServerAccessCapability = "server_access_command";

    /// <summary>The capability naming the prompt's format and background flags.</summary>
    internal const string CommandPromptBackgroundCapability = "command_prompt_background";

    /// <summary>The capability naming the prompt's literal flag.</summary>
    internal const string CommandPromptLiteralCapability = "command_prompt_literal";

    /// <summary>The capability naming the prompt's exit and redraw flags.</summary>
    internal const string CommandPrompt37Capability = "command_prompt_3_7_behavior";

    /// <summary>The capability naming the confirmation's background flag.</summary>
    internal const string ConfirmBeforeBackgroundCapability = "confirm_before_background";

    /// <summary>The capability naming the confirmation's key and default flags.</summary>
    internal const string ConfirmBeforeAcceptanceCapability = "confirm_before_acceptance";

    /// <summary>The capability naming the menu's style flags.</summary>
    internal const string DisplayMenuStylesCapability = "display_menu_styles";

    /// <summary>The capability naming the menu's mouse flag.</summary>
    internal const string DisplayMenuMouseCapability = "display_menu_mouse";

    /// <summary>The capability naming the message's target-client flag.</summary>
    internal const string DisplayMessageClientCapability = "display_message_client";

    /// <summary>The capability naming the message's literal flag.</summary>
    internal const string DisplayMessageLiteralCapability = "display_message_literal";

    /// <summary>The capability naming the key listing's format flag.</summary>
    internal const string ListKeysFormatCapability = "list_keys_format";

    /// <summary>The capability naming the shell command's argument list.</summary>
    internal const string RunShellArgumentsCapability = "run_shell_arguments";

    /// <summary>The capability naming the shell command's error output flag.</summary>
    internal const string RunShellStandardErrorCapability = "run_shell_show_stderr";

    /// <summary>The capability naming the shell command's directory flag.</summary>
    internal const string RunShellWorkingDirectoryCapability = "run_shell_working_directory";

    /// <summary>Adds a flag and its value when the value is worth sending.</summary>
    /// <param name="arguments">The argv being built.</param>
    /// <param name="flag">The tmux flag.</param>
    /// <param name="value">The value, or null to send neither.</param>
    internal static void AddValue(List<string> arguments, string flag, string? value)
    {
        if (value is not null)
        {
            arguments.Add(flag);
            arguments.Add(value);
        }
    }

    /// <summary>Adds a flag when it is wanted.</summary>
    /// <param name="arguments">The argv being built.</param>
    /// <param name="wanted">Whether the caller asked for it.</param>
    /// <param name="flag">The tmux flag.</param>
    internal static void AddFlag(List<string> arguments, bool wanted, string flag)
    {
        if (wanted)
        {
            arguments.Add(flag);
        }
    }

    /// <summary>Marks the end of flags, before the first caller-supplied positional value.</summary>
    /// <param name="arguments">The argv being built.</param>
    /// <remarks>
    /// tmux stops reading flags at the first argument that does not start with
    /// <c>-</c>, so a positional value the caller controls -- a shell command,
    /// a name, a buffer's contents -- reaches that parser as data only when
    /// something already unambiguous precedes it. <c>--</c> is that
    /// something on every supported tmux release. Add it immediately before
    /// the first such value; everything after it, including further
    /// caller-supplied values, is data regardless of what it starts with.
    /// </remarks>
    internal static void EndOptions(List<string> arguments) => arguments.Add("--");

    /// <summary>Names the tmux spelling of a prompt type.</summary>
    /// <param name="type">The type asked for.</param>
    /// <returns>The value tmux takes after its type flag.</returns>
    internal static string GetPromptTypeName(PromptType type) => type switch
    {
        PromptType.Command => "command",
        PromptType.Search => "search",
        PromptType.Target => "target",
        PromptType.WindowTarget => "window-target",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown prompt type."),
    };

    /// <summary>Names the tmux flag for a wait mode.</summary>
    /// <param name="mode">The mode asked for.</param>
    /// <returns>The flag, or null when waiting needs none.</returns>
    internal static string? GetWaitModeFlag(TmuxWaitMode mode) => mode switch
    {
        // Plain waiting is what tmux does with no flag at all.
        TmuxWaitMode.Wait => null,
        TmuxWaitMode.Signal => "-S",
        TmuxWaitMode.Lock => "-L",
        TmuxWaitMode.Unlock => "-U",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown wait mode."),
    };

    /// <summary>Names the tmux flag for a message listing.</summary>
    /// <param name="mode">What to list.</param>
    /// <returns>The flag, or null for the messages themselves.</returns>
    internal static string? GetShowMessagesFlag(ShowMessagesMode mode) => mode switch
    {
        TmuxShowMessagesDefault => null,
        ShowMessagesMode.Jobs => "-J",
        ShowMessagesMode.Terminals => "-T",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown message mode."),
    };

    /// <summary>Reads the buffers <c>list-buffers</c> printed.</summary>
    /// <param name="lines">The output lines.</param>
    /// <returns>One buffer per line tmux could be read from.</returns>
    /// <remarks>
    /// tmux prints a buffer as its name, then its size in bytes, then a sample
    /// of its contents in quotes. The sample can hold anything, so only the two
    /// fields before it are read by position.
    /// </remarks>
    internal static IReadOnlyList<TmuxBuffer> ReadBuffers(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        List<TmuxBuffer> buffers = [];
        foreach (string line in lines)
        {
            if (ReadBuffer(line) is TmuxBuffer buffer)
            {
                buffers.Add(buffer);
            }
        }

        return buffers;
    }

    private const ShowMessagesMode TmuxShowMessagesDefault = ShowMessagesMode.Messages;

    private static TmuxBuffer? ReadBuffer(string line)
    {
        if (line.Length == 0)
        {
            return null;
        }

        int colon = line.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            return null;
        }

        string name = line[..colon];
        string rest = line[(colon + 1)..].TrimStart();
        int space = rest.IndexOf(' ', StringComparison.Ordinal);
        string sizeText = space < 0 ? rest : rest[..space];
        if (!long.TryParse(
            sizeText,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out long size))
        {
            return null;
        }

        string? sample = space < 0 ? null : rest[(space + 1)..].TrimStart();
        if (sample is { Length: >= 2 } && sample[0] == '"' && sample[^1] == '"')
        {
            sample = sample[1..^1];
        }

        return new TmuxBuffer(name, size, sample);
    }
}
