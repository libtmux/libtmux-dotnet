namespace LibTmux;

/// <summary>Describes one hook to read, run, or unset.</summary>
public sealed record HookRequest
{
    /// <summary>Initializes a request naming one hook.</summary>
    /// <param name="name">The hook name.</param>
    public HookRequest(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>Gets the hook name.</summary>
    /// <remarks>
    /// tmux expands it as a format before it names anything, so a <c>#</c> in
    /// it does not survive verbatim.
    /// </remarks>
    public string Name { get; }

    /// <summary>Gets the scope to reach it in, or null for the owner's own.</summary>
    public OptionScope? Scope { get; init; }

    /// <summary>Gets whether the global table is used instead of the local one.</summary>
    public bool Global { get; init; }
}
