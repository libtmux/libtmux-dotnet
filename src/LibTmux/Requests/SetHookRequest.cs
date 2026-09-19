namespace LibTmux;

/// <summary>Describes one <c>set-hook</c> invocation.</summary>
public sealed record SetHookRequest
{
    /// <summary>Initializes a request to set one hook.</summary>
    /// <param name="name">The hook name, optionally with an array index.</param>
    /// <param name="value">The tmux command to run when the hook fires.</param>
    public SetHookRequest(
        string name,
        string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        Name = name;
        Value = value;
    }

    /// <summary>Gets the hook name, optionally with an array index.</summary>
    /// <remarks>
    /// tmux expands it as a format before it names anything, so a <c>#</c> in
    /// it does not survive verbatim.
    /// </remarks>
    public string Name { get; }

    /// <summary>Gets the tmux command to run when the hook fires.</summary>
    public string Value { get; }

    /// <summary>Gets the scope to set in, or null for the owner's own.</summary>
    public OptionScope? Scope { get; init; }

    /// <summary>Gets whether the global table is set instead of the local one.</summary>
    public bool Global { get; init; }

    /// <summary>Gets whether the hook is removed rather than set.</summary>
    public bool Unset { get; init; }

    /// <summary>Gets whether tmux also runs the command now.</summary>
    public bool RunImmediately { get; init; }

    /// <summary>Gets whether the command joins the hook's existing entries.</summary>
    public bool Append { get; init; }
}
