namespace LibTmux;

/// <summary>Describes one <c>set-option -u</c> invocation.</summary>
public sealed record UnsetOptionRequest
{
    /// <summary>Initializes a request to unset one option.</summary>
    /// <param name="name">The option to unset, optionally with an array index.</param>
    public UnsetOptionRequest(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>Gets the option to unset, optionally with an array index.</summary>
    /// <remarks>
    /// tmux expands it as a format before it names anything, so a <c>#</c> in
    /// it does not survive verbatim.
    /// </remarks>
    public string Name { get; }

    /// <summary>Gets the scope to unset in, or null for the owner's own.</summary>
    public OptionScope? Scope { get; init; }

    /// <summary>Gets whether the global table is unset instead of the local one.</summary>
    public bool Global { get; init; }

    /// <summary>Gets whether every pane's override of the option goes too.</summary>
    public bool UnsetPaneOverrides { get; init; }

    /// <summary>Gets whether a missing option is answered with nothing instead of an error.</summary>
    public bool Quiet { get; init; }
}
