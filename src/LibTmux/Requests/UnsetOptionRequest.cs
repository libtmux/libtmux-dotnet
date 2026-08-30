namespace LibTmux;

/// <summary>Describes one <c>set-option -u</c> invocation.</summary>
public sealed record UnsetOptionRequest
{
    /// <summary>Initializes a request to unset one option.</summary>
    /// <param name="name">The option to unset, optionally with an array index.</param>
    /// <param name="scope">The scope to unset in, or null for the owner's own.</param>
    /// <param name="global">Whether the global table is unset instead of the local one.</param>
    /// <param name="unsetPaneOverrides">Whether every pane's override of the option goes too.</param>
    /// <param name="quiet">Whether a missing option is answered with nothing instead of an error.</param>
    public UnsetOptionRequest(
        string name,
        OptionScope? scope = null,
        bool global = false,
        bool unsetPaneOverrides = false,
        bool quiet = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Scope = scope;
        Global = global;
        UnsetPaneOverrides = unsetPaneOverrides;
        Quiet = quiet;
    }

    /// <summary>Gets the option to unset, optionally with an array index.</summary>
    /// <remarks>
    /// tmux expands it as a format before it names anything, so a <c>#</c> in
    /// it does not survive verbatim.
    /// </remarks>
    public string Name { get; }

    /// <summary>Gets the scope to unset in, or null for the owner's own.</summary>
    public OptionScope? Scope { get; }

    /// <summary>Gets whether the global table is unset instead of the local one.</summary>
    public bool Global { get; }

    /// <summary>Gets whether every pane's override of the option goes too.</summary>
    public bool UnsetPaneOverrides { get; }

    /// <summary>Gets whether a missing option is answered with nothing instead of an error.</summary>
    public bool Quiet { get; }
}
