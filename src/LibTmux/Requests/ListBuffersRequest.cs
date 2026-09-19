namespace LibTmux;

/// <summary>Describes one <c>list-buffers</c> invocation.</summary>
public sealed record ListBuffersRequest
{
    /// <summary>Gets the tmux format each buffer is rendered with.</summary>
    public string? Format { get; init; }

    /// <summary>Gets the tmux filter expression, kept as written.</summary>
    public UnsafeTmuxFilter? Filter { get; init; }
}
