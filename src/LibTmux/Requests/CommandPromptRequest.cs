using System.Runtime.Versioning;

namespace LibTmux;

/// <summary>What a command prompt is asking for.</summary>
/// <remarks>
/// tmux uses the type to decide which history the prompt draws on and how it
/// completes what is typed.
/// </remarks>
public enum PromptType
{
    /// <summary>A tmux command.</summary>
    Command,

    /// <summary>Text to search for.</summary>
    Search,

    /// <summary>A target to act on.</summary>
    Target,

    /// <summary>A window to act on.</summary>
    WindowTarget,
}

/// <summary>Describes one <c>command-prompt</c> invocation.</summary>
public sealed record CommandPromptRequest : ITmuxRequest<Server>
{
    /// <summary>Initializes a command prompt.</summary>
    /// <param name="template">The command to run, with the answer substituted in.</param>
    public CommandPromptRequest(string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        Template = template;
    }

    /// <summary>Gets the command to run, with the answer substituted in.</summary>
    public string Template { get; }

    /// <summary>Gets the text shown to the person answering.</summary>
    public string? Prompt { get; init; }

    /// <summary>Gets the answer the prompt starts with.</summary>
    public string? Inputs { get; init; }

    /// <summary>Gets the client to prompt, or null for the caller's own.</summary>
    public string? TargetClient { get; init; }

    /// <summary>Gets whether one keypress answers it.</summary>
    public bool OneKey { get; init; }

    /// <summary>Gets whether the answer is the key itself rather than text.</summary>
    public bool KeyOnly { get; init; }

    /// <summary>Gets whether the command runs on every keystroke.</summary>
    public bool OnInputChange { get; init; }

    /// <summary>Gets whether only digits are accepted.</summary>
    public bool Numeric { get; init; }

    /// <summary>Gets what the prompt is asking for.</summary>
    public PromptType? Type { get; init; }

    /// <summary>Gets whether the template is expanded as a format.</summary>
    public bool ExpandFormat { get; init; }

    /// <summary>Gets whether the answer is taken literally.</summary>
    public bool Literal { get; init; }

    /// <summary>Gets whether backspace on an empty prompt closes it.</summary>
    public bool BackspaceExits { get; init; }

    /// <summary>Gets whether the client keeps redrawing while prompting.</summary>
    public bool NoFreeze { get; init; }

    /// <summary>Returns a prompt request as one tmux command.</summary>
    /// <param name="server">The server the prompt is shown on.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <remarks>
    /// Batching does not soften the refusal below tmux 3.3: that version reads
    /// the type flag as something else, so a prompt asking for one is refused
    /// here exactly as it is when run alone.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="server" /> is null.</exception>
    /// <exception cref="TmuxVersionTooLowException">
    /// The request asks for a format or a prompt type and tmux is older than 3.3.
    /// </exception>
    [UnsupportedOSPlatform("windows")]
    public TmuxCommand ToCommand(Server server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return TmuxChaining.Command([.. server.BuildCommandPromptArguments(this)]);
    }
}
