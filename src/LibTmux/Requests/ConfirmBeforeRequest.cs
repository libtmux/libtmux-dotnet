namespace LibTmux;

/// <summary>Describes one <c>confirm-before</c> invocation.</summary>
public sealed record ConfirmBeforeRequest
{
    private readonly string[] _command;

    /// <summary>Initializes a confirmation.</summary>
    /// <param name="command">The tmux command to run once confirmed.</param>
    public ConfirmBeforeRequest(IReadOnlyList<string> command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Count == 0)
        {
            throw new ArgumentException("A confirmation needs a command.", nameof(command));
        }

        _command = [.. command];
    }

    /// <summary>Gets the tmux command to run once confirmed.</summary>
    public IReadOnlyList<string> Command => _command;

    /// <summary>Gets the question shown, or null for tmux's own wording.</summary>
    public string? Prompt { get; init; }

    /// <summary>Gets the key that confirms, or null for tmux's default.</summary>
    public string? ConfirmKey { get; init; }

    /// <summary>Gets whether pressing enter confirms rather than cancels.</summary>
    public bool DefaultYes { get; init; }

    /// <summary>Gets the client to ask, or null for the caller's own.</summary>
    public string? TargetClient { get; init; }
}
