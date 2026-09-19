namespace LibTmux;

/// <summary>Describes one <c>list-buffers</c> invocation.</summary>
public sealed record ListBuffersRequest : ITmuxRequest<Server>
{
    /// <summary>Gets the tmux format each buffer is rendered with.</summary>
    public string? Format { get; init; }

    /// <summary>Gets the tmux filter expression, kept as written.</summary>
    public UnsafeTmuxFilter? Filter { get; init; }

    /// <summary>Returns a buffer-listing request as one tmux command.</summary>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    public TmuxCommand ToCommand() =>
        TmuxChaining.Command([.. Server.BuildListBuffersArguments(this)]);

    /// <inheritdoc />
    TmuxCommand ITmuxRequest<Server>.ToCommand(Server target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return ToCommand();
    }
}
