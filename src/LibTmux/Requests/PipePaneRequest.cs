namespace LibTmux;

/// <summary>Describes one <c>pipe-pane</c> invocation.</summary>
public sealed record PipePaneRequest
{
    /// <summary>Gets the command to pipe through, or null to stop piping.</summary>
    /// <remarks>
    /// Omitting a command does not leave an existing pipe alone: it stops it.
    /// </remarks>
    public string? Command { get; init; }

    /// <summary>Gets whether only pane output is piped.</summary>
    public bool OutputOnly { get; init; }

    /// <summary>Gets whether only pane input is piped.</summary>
    public bool InputOnly { get; init; }

    /// <summary>Gets whether an identical existing pipe is stopped instead.</summary>
    public bool Toggle { get; init; }
}
