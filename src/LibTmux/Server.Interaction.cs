using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

public sealed partial class Server
{
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
}
