namespace LibTmux;

/// <summary>Describes one <c>show-hooks</c> invocation.</summary>
public sealed record ListHooksRequest
{
    /// <summary>Gets the scope to read, or null for the owner's own.</summary>
    public OptionScope? Scope { get; init; }

    /// <summary>Gets whether the global table is read instead of the local one.</summary>
    public bool Global { get; init; }
}
