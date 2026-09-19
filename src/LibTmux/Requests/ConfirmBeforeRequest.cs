using System.Runtime.Versioning;

namespace LibTmux;

/// <summary>Describes one <c>confirm-before</c> invocation.</summary>
public sealed record ConfirmBeforeRequest : ITmuxRequest<Server>
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

    /// <summary>Returns a confirmation request as one tmux command.</summary>
    /// <param name="server">The server the confirmation is shown on.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <remarks>
    /// Naming the accepting key, and defaulting to yes, arrived in tmux 3.4,
    /// so the server decides whether the built command carries them.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="server" /> is null.</exception>
    [UnsupportedOSPlatform("windows")]
    public TmuxCommand ToCommand(Server server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return TmuxChaining.Command([.. server.BuildConfirmBeforeArguments(this)]);
    }
}
