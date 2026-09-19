namespace LibTmux;

/// <summary>Describes one <c>show-options</c> invocation for a single option.</summary>
public sealed record GetOptionRequest
{
    /// <summary>Initializes a request for one option.</summary>
    /// <param name="name">The option to read.</param>
    public GetOptionRequest(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>Gets the option to read.</summary>
    /// <remarks>
    /// tmux expands it as a format before it names anything, so a <c>#</c> in
    /// it does not survive verbatim.
    /// </remarks>
    public string Name { get; }

    /// <summary>Gets the scope to read in, or null for the owner's own.</summary>
    public OptionScope? Scope { get; init; }

    /// <summary>Gets whether the global table is read instead of the local one.</summary>
    public bool Global { get; init; }

    /// <summary>Gets whether hooks are listed alongside options.</summary>
    public bool IncludeHooks { get; init; }

    /// <summary>Gets whether values inherited from a parent scope are included.</summary>
    public bool IncludeInherited { get; init; }

    /// <summary>Gets whether a missing option is answered with nothing instead of an error.</summary>
    public bool Quiet { get; init; }
}
