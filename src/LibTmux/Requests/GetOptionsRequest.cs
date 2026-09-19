namespace LibTmux;

/// <summary>Describes one <c>show-options</c> invocation for every option in a scope.</summary>
public sealed record GetOptionsRequest
{
    /// <summary>Gets the scope to read in, or null for the owner's own.</summary>
    public OptionScope? Scope { get; init; }

    /// <summary>Gets whether the global table is read instead of the local one.</summary>
    public bool Global { get; init; }

    /// <summary>Gets whether hooks are listed alongside options.</summary>
    public bool IncludeHooks { get; init; }

    /// <summary>Gets whether values inherited from a parent scope are included.</summary>
    public bool IncludeInherited { get; init; }

    /// <summary>Gets whether an empty table is answered with nothing instead of an error.</summary>
    public bool Quiet { get; init; }
}
