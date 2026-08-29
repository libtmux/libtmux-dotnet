using System.Globalization;
using System.Runtime.Versioning;
using LibTmux.Internal;
using Microsoft.Extensions.Logging;

namespace LibTmux;

/// <summary>What <c>show-messages</c> should list.</summary>
public enum ShowMessagesMode
{
    /// <summary>The server's own message log.</summary>
    Messages,

    /// <summary>The jobs the server is running.</summary>
    Jobs,

    /// <summary>What the server knows about attached terminals.</summary>
    Terminals,
}

// Server utilities omit unsupported commands and warn when optional flags must
// be downgraded.
public sealed partial class Server
{
    /// <summary>Binds a key to a tmux command.</summary>
    /// <param name="request">Which key, to what, and in which table.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task BindKeyAsync(BindKeyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunUtilityAsync(BuildBindKeyArguments(request), cancellationToken);
    }

    internal static List<string> BuildBindKeyArguments(BindKeyRequest request)
    {
        List<string> arguments = ["bind-key"];
        ServerUtilities.AddFlag(arguments, request.Repeat, "-r");
        ServerUtilities.AddValue(arguments, "-T", request.KeyTable);
        ServerUtilities.AddValue(arguments, "-N", request.Note);
        arguments.Add(request.Key);
        arguments.AddRange(request.Command);
        return arguments;
    }

    /// <summary>Removes a key binding.</summary>
    /// <param name="request">Which key, or every key in a table.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task UnbindKeyAsync(
        UnbindKeyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunUtilityAsync(BuildUnbindKeyArguments(request), cancellationToken);
    }

    internal static List<string> BuildUnbindKeyArguments(UnbindKeyRequest request)
    {
        List<string> arguments = ["unbind-key"];
        ServerUtilities.AddFlag(arguments, request.All, "-a");
        ServerUtilities.AddFlag(arguments, request.Quiet, "-q");
        ServerUtilities.AddValue(arguments, "-T", request.KeyTable);

        // tmux still wants a key after the all flag, and takes any one.
        arguments.Add(request.Key ?? "-a");
        return arguments;
    }

    /// <summary>Reads the key bindings.</summary>
    /// <param name="keyTable">The table to read, or null for every table.</param>
    /// <param name="format">The tmux format each binding is rendered with.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>One line per binding, as tmux rendered it.</returns>
    /// <remarks>Rendering with a format arrived in tmux 3.7.</remarks>
    [UnsupportedOSPlatform("windows")]
    public async Task<IReadOnlyList<string>> GetKeysAsync(
        string? keyTable = null,
        string? format = null,
        CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["list-keys"];
        ServerUtilities.AddValue(arguments, "-T", keyTable);
        if (format is not null
            && RequiresCapability(ServerUtilities.ListKeysFormatCapability, LogListKeysFormat))
        {
            ServerUtilities.AddValue(arguments, "-F", format);
        }

        return await ReadUtilityAsync(arguments, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Builds the arguments a prompt request sends.</summary>
    /// <remarks>
    /// The refusal below 3.3 belongs here rather than beside the dispatch,
    /// because tmux 3.2a reads the type flag as a pair of booleans meaning
    /// something else: a chained prompt that skipped the check would ask a
    /// different question rather than fail.
    /// </remarks>
    /// <exception cref="TmuxVersionTooLowException">
    /// The request asks for a format or a prompt type and tmux is older than 3.3.
    /// </exception>
    internal List<string> BuildCommandPromptArguments(CommandPromptRequest request)
    {
        // tmux 3.2a spells the type flag as a pair of booleans meaning
        // something else, so sending one there would ask a different question
        // rather than fail. Nothing is sent instead.
        if ((request.ExpandFormat || request.Type is not null)
            && !Supports(ServerUtilities.CommandPromptBackgroundCapability))
        {
            throw new TmuxVersionTooLowException(
                "Expanding a command prompt as a format, or naming what it asks for, requires tmux 3.3a.",
                TmuxVersion.Parse("3.3a"),
                Version ?? default);
        }

        List<string> arguments = ["command-prompt"];
        ServerUtilities.AddFlag(arguments, request.OneKey, "-1");
        ServerUtilities.AddFlag(arguments, request.Numeric, "-N");
        ServerUtilities.AddFlag(arguments, request.OnInputChange, "-i");
        ServerUtilities.AddFlag(arguments, request.KeyOnly, "-k");
        ServerUtilities.AddFlag(arguments, request.ExpandFormat, "-F");
        if (request.Literal
            && RequiresCapability(ServerUtilities.CommandPromptLiteralCapability, LogPromptLiteral))
        {
            arguments.Add("-l");
        }

        if (request.BackspaceExits
            && RequiresCapability(ServerUtilities.CommandPrompt37Capability, LogPrompt37))
        {
            arguments.Add("-e");
        }

        if (request.NoFreeze
            && RequiresCapability(ServerUtilities.CommandPrompt37Capability, LogPrompt37))
        {
            arguments.Add("-C");
        }

        ServerUtilities.AddValue(arguments, "-I", request.Inputs);
        ServerUtilities.AddValue(arguments, "-p", request.Prompt);
        ServerUtilities.AddValue(arguments, "-t", request.TargetClient);
        ServerUtilities.AddValue(
            arguments,
            "-T",
            request.Type is PromptType type ? ServerUtilities.GetPromptTypeName(type) : null);
        arguments.Add(request.Template);

        return arguments;
    }

    /// <summary>Asks a client for input and runs a command with the answer.</summary>
    /// <param name="request">What to ask, and how.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <exception cref="TmuxVersionTooLowException">
    /// The request asks for a format or a prompt type and tmux is older than 3.3.
    /// </exception>
    [UnsupportedOSPlatform("windows")]
    public Task ShowCommandPromptAsync(
        CommandPromptRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        List<string> arguments = BuildCommandPromptArguments(request);
        return RunUtilityAsync(arguments, cancellationToken);
    }

    /// <summary>Forgets what has been typed at command prompts.</summary>
    /// <param name="type">Which history to clear, or null for all of them.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <exception cref="TmuxVersionTooLowException">tmux is older than 3.3.</exception>
    [UnsupportedOSPlatform("windows")]
    public Task ClearPromptHistoryAsync(
        PromptType? type = null,
        CancellationToken cancellationToken = default)
    {
        RequireCommand(
            ServerUtilities.ClearPromptHistoryCapability,
            "clear-prompt-history");
        List<string> arguments = ["clear-prompt-history"];
        ServerUtilities.AddValue(
            arguments,
            "-T",
            type is PromptType value ? ServerUtilities.GetPromptTypeName(value) : null);
        return RunUtilityAsync(arguments, cancellationToken);
    }

    /// <summary>Reads what has been typed at command prompts.</summary>
    /// <param name="type">Which history to read, or null for all of them.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>One line per remembered entry.</returns>
    /// <exception cref="TmuxVersionTooLowException">tmux is older than 3.3.</exception>
    [UnsupportedOSPlatform("windows")]
    public async Task<IReadOnlyList<string>> GetPromptHistoryAsync(
        PromptType? type = null,
        CancellationToken cancellationToken = default)
    {
        RequireCommand(ServerUtilities.ShowPromptHistoryCapability, "show-prompt-history");
        List<string> arguments = ["show-prompt-history"];
        ServerUtilities.AddValue(
            arguments,
            "-T",
            type is PromptType value ? ServerUtilities.GetPromptTypeName(value) : null);
        return await ReadUtilityAsync(arguments, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Builds the arguments a confirmation request sends.</summary>
    /// <remarks>
    /// Naming the accepting key, and defaulting to yes, arrived in tmux 3.4,
    /// so this stays on the server that knows which one is answering.
    /// </remarks>
    internal List<string> BuildConfirmBeforeArguments(ConfirmBeforeRequest request)
    {
        List<string> arguments = ["confirm-before"];
        if (request.DefaultYes
            && RequiresCapability(
                ServerUtilities.ConfirmBeforeAcceptanceCapability,
                LogConfirmAcceptance))
        {
            arguments.Add("-y");
        }

        if (request.ConfirmKey is not null
            && RequiresCapability(
                ServerUtilities.ConfirmBeforeAcceptanceCapability,
                LogConfirmAcceptance))
        {
            ServerUtilities.AddValue(arguments, "-c", request.ConfirmKey);
        }

        ServerUtilities.AddValue(arguments, "-p", request.Prompt);
        ServerUtilities.AddValue(arguments, "-t", request.TargetClient);
        arguments.AddRange(request.Command);

        return arguments;
    }

    /// <summary>Asks a client to confirm before running a command.</summary>
    /// <param name="request">What to run, and how to ask.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <remarks>Naming the key, and defaulting to yes, arrived in tmux 3.4.</remarks>
    [UnsupportedOSPlatform("windows")]
    public Task ConfirmBeforeAsync(
        ConfirmBeforeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<string> arguments = BuildConfirmBeforeArguments(request);
        return RunUtilityAsync(arguments, cancellationToken);
    }

    /// <summary>Builds the arguments a menu request sends.</summary>
    /// <remarks>
    /// The style flags arrived in tmux 3.4 and the mouse flag in 3.5, so this
    /// stays on the server that knows which one is answering.
    /// </remarks>
    internal List<string> BuildDisplayMenuArguments(DisplayMenuRequest request)
    {
        List<string> arguments = ["display-menu"];
        ServerUtilities.AddFlag(arguments, request.StayOpen, "-O");
        if (request.Mouse
            && RequiresCapability(ServerUtilities.DisplayMenuMouseCapability, LogMenuMouse))
        {
            arguments.Add("-M");
        }

        if (SupportsMenuStyles())
        {
            ServerUtilities.AddValue(arguments, "-b", request.BorderLines);
            ServerUtilities.AddValue(arguments, "-C", request.StartingChoice);
            ServerUtilities.AddValue(arguments, "-H", request.SelectedStyle);
            ServerUtilities.AddValue(arguments, "-s", request.Style);
            ServerUtilities.AddValue(arguments, "-S", request.BorderStyle);
        }

        ServerUtilities.AddValue(arguments, "-c", request.TargetClient);
        ServerUtilities.AddValue(arguments, "-t", request.TargetPane);
        ServerUtilities.AddValue(arguments, "-T", request.Title);
        ServerUtilities.AddValue(arguments, "-x", request.X);
        ServerUtilities.AddValue(arguments, "-y", request.Y);
        foreach (TmuxMenuItem item in request.Items)
        {
            arguments.Add(item.Name);
            arguments.Add(item.Key);
            arguments.Add(item.Command);
        }

        return arguments;
    }

    /// <summary>Shows a menu on a client.</summary>
    /// <param name="request">What the menu offers, and how it looks.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <remarks>
    /// The style flags arrived in tmux 3.4 and the mouse flag in 3.5. Older
    /// servers are shown the same menu without them.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public Task ShowMenuAsync(
        DisplayMenuRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<string> arguments = BuildDisplayMenuArguments(request);
        return RunUtilityAsync(arguments, cancellationToken);
    }

    /// <summary>Builds the arguments a message request sends.</summary>
    /// <remarks>
    /// This stays on the server because two of the flags depend on which tmux
    /// is answering: literal expansion arrived in 3.4, and 3.2a refuses the
    /// target-client flag outright. A chained message has to be built the same
    /// way a direct one is.
    /// </remarks>
    internal List<string> BuildDisplayMessageArguments(DisplayMessageRequest request)
    {
        List<string> arguments = ["display-message"];
        ServerUtilities.AddFlag(arguments, request.ReturnText, "-p");
        ServerUtilities.AddFlag(arguments, request.AllFormats, "-a");
        ServerUtilities.AddFlag(arguments, request.Verbose, "-v");
        if (request.NoExpand
            && RequiresCapability(
                ServerUtilities.DisplayMessageLiteralCapability,
                LogMessageLiteral))
        {
            arguments.Add("-l");
        }

        ServerUtilities.AddFlag(arguments, request.Notify, "-N");
        if (request.TargetClient is not null
            && RequiresCapability(
                ServerUtilities.DisplayMessageClientCapability,
                LogMessageClient))
        {
            // tmux 3.2a prints its usage and refuses the command, even for a
            // client that is really attached. Its usage text advertises the
            // flag anyway, so only running it tells the truth.
            ServerUtilities.AddValue(arguments, "-c", request.TargetClient);
        }
        ServerUtilities.AddValue(
            arguments,
            "-d",
            request.Delay is TimeSpan delay
                ? ((long)delay.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)
                : null);
        ServerUtilities.AddValue(arguments, "-F", request.Format);
        if (request.Message.Length > 0)
        {
            arguments.Add(request.Message);
        }

        return arguments;
    }

    /// <summary>Shows a message on a client.</summary>
    /// <param name="request">What to show, and how.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The rendered text when it was asked for, and null otherwise.</returns>
    /// <remarks>
    /// tmux reports a bad format on its error stream rather than by failing, so
    /// a message it would not render is logged and answered with nothing.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public async Task<IReadOnlyList<string>?> DisplayMessageAsync(
        DisplayMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<string> arguments = BuildDisplayMessageArguments(request);

        TmuxCommandResult result = await _commandDispatcher
            .ExecuteAsync(arguments, cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode == 0)
        {
            return request.ReturnText ? result.StandardOutputLines : null;
        }

        if (Connection?.Options.Logger is ILogger logger)
        {
            LogDisplayMessageRefused(logger, string.Join('\n', result.StandardErrorLines));
        }

        return null;
    }

    /// <summary>Builds the arguments a shell request sends.</summary>
    /// <remarks>
    /// Three of these flags arrived at different tmux versions, so this stays
    /// on the server that knows which one is answering rather than becoming a
    /// helper a caller could reach without that knowledge.
    /// </remarks>
    internal List<string> BuildRunShellArguments(RunShellRequest request)
    {
        List<string> arguments = ["run-shell"];
        ServerUtilities.AddFlag(arguments, request.Background, "-b");
        ServerUtilities.AddFlag(arguments, request.AsTmuxCommand, "-C");
        if (request.ShowStandardError
            && RequiresCapability(
                ServerUtilities.RunShellStandardErrorCapability,
                LogRunShellStandardError))
        {
            arguments.Add("-E");
        }

        if (request.WorkingDirectory is not null
            && RequiresCapability(
                ServerUtilities.RunShellWorkingDirectoryCapability,
                LogRunShellWorkingDirectory))
        {
            ServerUtilities.AddValue(arguments, "-c", request.WorkingDirectory);
        }

        ServerUtilities.AddValue(
            arguments,
            "-d",
            request.Delay is TimeSpan delay
                ? ((long)delay.TotalSeconds).ToString(CultureInfo.InvariantCulture)
                : null);
        ServerUtilities.AddValue(arguments, "-t", request.TargetPane);
        arguments.Add(request.Command);
        if (request.Arguments is { Count: > 0 } extra
            && RequiresCapability(
                ServerUtilities.RunShellArgumentsCapability,
                LogRunShellArguments))
        {
            arguments.AddRange(extra);
        }

        return arguments;
    }

    /// <summary>Runs a shell command and reports what it printed.</summary>
    /// <param name="request">What to run, and how.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What the command printed, or null when tmux did not wait for it.</returns>
    /// <remarks>
    /// The directory flag arrived in tmux 3.4, the error-output flag in 3.6,
    /// and passing arguments without a shell in 3.7.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public async Task<IReadOnlyList<string>?> RunShellAsync(
        RunShellRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<string> arguments = BuildRunShellArguments(request);

        TmuxCommandResult result = await _commandDispatcher
            .ExecuteAsync(arguments, cancellationToken)
            .ConfigureAwait(false);
        TmuxCommandFailure.ThrowIfFailed(result, "run-shell");

        // Nothing has run yet when tmux was told not to wait, so there is
        // nothing it could report.
        return request.Background ? null : result.StandardOutputLines;
    }

    /// <summary>Runs one tmux command or another depending on a shell command.</summary>
    /// <param name="request">What to test, and what to run either way.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task IfShellAsync(IfShellRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunUtilityAsync(BuildIfShellArguments(request), cancellationToken);
    }

    internal static List<string> BuildIfShellArguments(IfShellRequest request)
    {
        List<string> arguments = ["if-shell"];
        ServerUtilities.AddFlag(arguments, request.Background, "-b");
        ServerUtilities.AddValue(arguments, "-t", request.TargetPane);
        arguments.Add(request.ShellCommand);
        arguments.Add(string.Join(' ', request.ThenCommand));
        if (request.ElseCommand is { Count: > 0 } otherwise)
        {
            arguments.Add(string.Join(' ', otherwise));
        }

        return arguments;
    }

    /// <summary>Waits on, signals, or locks a tmux channel.</summary>
    /// <param name="request">Which channel, and what to do with it.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <remarks>
    /// Waiting blocks until something else signals the channel, so a call that
    /// waits does not return on its own.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public Task WaitForAsync(WaitForRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunUtilityAsync(BuildWaitForArguments(request), cancellationToken);
    }

    internal static List<string> BuildWaitForArguments(WaitForRequest request)
    {
        List<string> arguments = ["wait-for"];
        if (ServerUtilities.GetWaitModeFlag(request.Mode) is string flag)
        {
            arguments.Add(flag);
        }

        arguments.Add(request.Channel);
        return arguments;
    }

    /// <summary>Builds the arguments an access request sends.</summary>
    /// <remarks>
    /// The command itself arrived in tmux 3.3, so the refusal belongs here
    /// rather than beside the dispatch: a chained request that skipped it
    /// would send a command older servers do not have.
    /// </remarks>
    /// <exception cref="TmuxVersionTooLowException">tmux is older than 3.3.</exception>
    internal List<string> BuildServerAccessArguments(ServerAccessRequest request)
    {
        RequireCommand(ServerUtilities.ServerAccessCapability, "server-access");
        List<string> arguments = ["server-access"];
        ServerUtilities.AddFlag(arguments, request.AllowUser is not null, "-a");
        ServerUtilities.AddFlag(arguments, request.DenyUser is not null, "-d");
        ServerUtilities.AddFlag(arguments, request.List, "-l");
        ServerUtilities.AddFlag(arguments, request.ReadOnly, "-r");
        ServerUtilities.AddFlag(arguments, request.ReadWrite, "-w");
        if ((request.AllowUser ?? request.DenyUser) is string user)
        {
            arguments.Add(user);
        }

        return arguments;
    }

    /// <summary>Grants or withdraws another user's access to this server.</summary>
    /// <param name="request">Who, and what they may do.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The current list when it was asked for, and null otherwise.</returns>
    /// <exception cref="TmuxVersionTooLowException">tmux is older than 3.3.</exception>
    [UnsupportedOSPlatform("windows")]
    public async Task<IReadOnlyList<string>?> ConfigureAccessAsync(
        ServerAccessRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<string> arguments = BuildServerAccessArguments(request);

        IReadOnlyList<string> lines = await ReadUtilityAsync(arguments, cancellationToken)
            .ConfigureAwait(false);
        return request.List ? lines : null;
    }

    /// <summary>Reads a tmux configuration file.</summary>
    /// <param name="path">The file to read.</param>
    /// <param name="quiet">Whether a missing file is passed over in silence.</param>
    /// <param name="parseOnly">Whether the file is checked rather than run.</param>
    /// <param name="verbose">Whether each command read is reported.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task SourceFileAsync(
        string path,
        bool quiet = false,
        bool parseOnly = false,
        bool verbose = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        List<string> arguments = ["source-file"];
        ServerUtilities.AddFlag(arguments, quiet, "-q");
        ServerUtilities.AddFlag(arguments, parseOnly, "-n");
        ServerUtilities.AddFlag(arguments, verbose, "-v");
        arguments.Add(path);
        return RunUtilityAsync(arguments, cancellationToken);
    }

    /// <summary>Locks every client attached to this server.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task LockAsync(CancellationToken cancellationToken = default) =>
        RunUtilityAsync(["lock-server"], cancellationToken);

    /// <summary>Reads what the server has been logging.</summary>
    /// <param name="targetClient">The client to read for, or null for the server.</param>
    /// <param name="mode">Which log to read.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>One line per entry.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<IReadOnlyList<string>> GetMessagesAsync(
        string? targetClient = null,
        ShowMessagesMode mode = ShowMessagesMode.Messages,
        CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["show-messages"];
        if (ServerUtilities.GetShowMessagesFlag(mode) is string flag)
        {
            arguments.Add(flag);
        }

        ServerUtilities.AddValue(arguments, "-t", targetClient);
        return await ReadUtilityAsync(arguments, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the commands this tmux knows.</summary>
    /// <param name="name">One command to describe, or null for all of them.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>One line per command, giving its syntax.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<IReadOnlyList<string>> GetCommandsAsync(
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["list-commands"];
        if (name is not null)
        {
            arguments.Add(name);
        }

        return await ReadUtilityAsync(arguments, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Puts text into a paste buffer.</summary>
    /// <param name="data">The text to store.</param>
    /// <param name="name">The buffer name, or null for a new one.</param>
    /// <param name="append">Whether the text joins what is already there.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task SetBufferAsync(
        string data,
        string? name = null,
        bool append = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        List<string> arguments = ["set-buffer"];
        ServerUtilities.AddFlag(arguments, append, "-a");
        ServerUtilities.AddValue(arguments, "-b", name);
        arguments.Add(data);
        return RunUtilityAsync(arguments, cancellationToken);
    }

    /// <summary>Puts a file's contents into a paste buffer.</summary>
    /// <param name="path">The file to read.</param>
    /// <param name="name">The buffer name, or null for a new one.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task LoadBufferAsync(
        string path,
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        List<string> arguments = ["load-buffer"];
        ServerUtilities.AddValue(arguments, "-b", name);
        arguments.Add(path);
        return RunUtilityAsync(arguments, cancellationToken);
    }

    /// <summary>Writes a paste buffer to a file.</summary>
    /// <param name="path">The file to write.</param>
    /// <param name="name">The buffer to write, or null for the most recent.</param>
    /// <param name="append">Whether the buffer joins what the file already holds.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task SaveBufferAsync(
        string path,
        string? name = null,
        bool append = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        List<string> arguments = ["save-buffer"];
        ServerUtilities.AddFlag(arguments, append, "-a");
        ServerUtilities.AddValue(arguments, "-b", name);
        arguments.Add(path);
        return RunUtilityAsync(arguments, cancellationToken);
    }

    /// <summary>Reads a paste buffer in full.</summary>
    /// <param name="name">The buffer to read, or null for the most recent.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>Everything the buffer holds.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<string> GetBufferAsync(
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["show-buffer"];
        ServerUtilities.AddValue(arguments, "-b", name);
        IReadOnlyList<string> lines = await ReadUtilityAsync(arguments, cancellationToken)
            .ConfigureAwait(false);
        return string.Join('\n', lines);
    }

    /// <summary>Forgets a paste buffer.</summary>
    /// <param name="name">The buffer to forget, or null for the most recent.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task DeleteBufferAsync(
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["delete-buffer"];
        ServerUtilities.AddValue(arguments, "-b", name);
        return RunUtilityAsync(arguments, cancellationToken);
    }

    internal static List<string> BuildListBuffersArguments(ListBuffersRequest? request)
    {
        List<string> arguments = ["list-buffers"];
        ServerUtilities.AddValue(arguments, "-F", request?.Format);
        ServerUtilities.AddValue(arguments, "-f", request?.Filter?.Value);

        return arguments;
    }

    /// <summary>Reads the paste buffers.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>Every buffer, with its size and a sample of its contents.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<IReadOnlyList<TmuxBuffer>> GetBuffersAsync(
        CancellationToken cancellationToken = default) =>
        ServerUtilities.ReadBuffers(
            await ReadUtilityAsync(["list-buffers"], cancellationToken).ConfigureAwait(false));

    /// <summary>Reads the paste buffers as tmux rendered them.</summary>
    /// <param name="request">The format and filter, or null for tmux's own.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>One line per buffer.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<IReadOnlyList<string>> GetBufferLinesAsync(
        ListBuffersRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        List<string> arguments = BuildListBuffersArguments(request);
        return await ReadUtilityAsync(arguments, cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(
        EventId = 21,
        Level = LogLevel.Warning,
        Message = "key listing format omitted, tmux {TmuxVersion} does not carry it")]
    private static partial void LogListKeysFormat(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 22,
        Level = LogLevel.Warning,
        Message = "prompt literal flag omitted, tmux {TmuxVersion} does not carry it")]
    private static partial void LogPromptLiteral(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 23,
        Level = LogLevel.Warning,
        Message = "prompt exit and redraw flags omitted, tmux {TmuxVersion} does not carry them")]
    private static partial void LogPrompt37(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 24,
        Level = LogLevel.Warning,
        Message = "confirmation key and default omitted, tmux {TmuxVersion} does not carry them")]
    private static partial void LogConfirmAcceptance(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 41,
        Level = LogLevel.Warning,
        Message = "message target client omitted, tmux {TmuxVersion} does not carry it")]
    private static partial void LogMessageClient(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 25,
        Level = LogLevel.Warning,
        Message = "menu mouse flag omitted, tmux {TmuxVersion} does not carry it")]
    private static partial void LogMenuMouse(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 26,
        Level = LogLevel.Warning,
        Message = "menu style flags omitted, tmux {TmuxVersion} does not carry them")]
    private static partial void LogMenuStyles(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 27,
        Level = LogLevel.Warning,
        Message = "message literal flag omitted, tmux {TmuxVersion} does not carry it")]
    private static partial void LogMessageLiteral(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 28,
        Level = LogLevel.Warning,
        Message = "shell error output flag omitted, tmux {TmuxVersion} does not carry it")]
    private static partial void LogRunShellStandardError(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 29,
        Level = LogLevel.Warning,
        Message = "shell working directory omitted, tmux {TmuxVersion} does not carry it")]
    private static partial void LogRunShellWorkingDirectory(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 30,
        Level = LogLevel.Warning,
        Message = "shell arguments omitted, tmux {TmuxVersion} passes them through a shell")]
    private static partial void LogRunShellArguments(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 31,
        Level = LogLevel.Warning,
        Message = "tmux refused to render the message: {Reported}")]
    private static partial void LogDisplayMessageRefused(ILogger logger, string reported);

    private bool Supports(string capability) =>
        Version is TmuxVersion version
        && TmuxCapabilities.IsSupported(version, capability);

    private bool SupportsMenuStyles() =>
        RequiresCapability(ServerUtilities.DisplayMenuStylesCapability, LogMenuStyles);

    private bool RequiresCapability(string capability, Action<ILogger, string?> log)
    {
        if (Supports(capability))
        {
            return true;
        }

        if (Connection?.Options.Logger is ILogger logger)
        {
            log(logger, RawVersion);
        }

        return false;
    }

    private void RequireCommand(string capability, string command)
    {
        if (Supports(capability))
        {
            return;
        }

        // The whole command is missing rather than one of its flags, so there
        // is nothing to send that would mean the same thing.
        throw new TmuxVersionTooLowException(
            $"The tmux command '{command}' requires tmux 3.3a.",
            TmuxVersion.Parse("3.3a"),
            Version ?? default);
    }

    [UnsupportedOSPlatform("windows")]
    private async Task RunUtilityAsync(
        List<string> arguments,
        CancellationToken cancellationToken)
    {
        TmuxCommandResult result = await _commandDispatcher
            .ExecuteAsync(arguments, cancellationToken)
            .ConfigureAwait(false);
        TmuxCommandFailure.ThrowIfFailed(result, arguments[0]);
    }

    [UnsupportedOSPlatform("windows")]
    private async Task<IReadOnlyList<string>> ReadUtilityAsync(
        List<string> arguments,
        CancellationToken cancellationToken)
    {
        TmuxCommandResult result = await _commandDispatcher
            .ExecuteAsync(arguments, cancellationToken)
            .ConfigureAwait(false);
        TmuxCommandFailure.ThrowIfFailed(result, arguments[0]);
        return result.StandardOutputLines;
    }
}
