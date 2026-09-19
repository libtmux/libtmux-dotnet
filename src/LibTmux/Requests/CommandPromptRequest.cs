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
public sealed record CommandPromptRequest
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
}
