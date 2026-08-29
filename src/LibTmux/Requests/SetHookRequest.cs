namespace LibTmux;

/// <summary>Describes one <c>set-hook</c> invocation.</summary>
public sealed record SetHookRequest
{
    /// <summary>Initializes a request to set one hook.</summary>
    /// <param name="name">The hook name, optionally with an array index.</param>
    /// <param name="value">The tmux command to run when the hook fires.</param>
    /// <param name="scope">The scope to set in, or null for the owner's own.</param>
    /// <param name="global">Whether the global table is set instead of the local one.</param>
    /// <param name="unset">Whether the hook is removed rather than set.</param>
    /// <param name="runImmediately">Whether tmux also runs the command now.</param>
    /// <param name="append">Whether the command joins the hook's existing entries.</param>
    public SetHookRequest(
        string name,
        string value,
        OptionScope? scope = null,
        bool global = false,
        bool unset = false,
        bool runImmediately = false,
        bool append = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        Name = name;
        Value = value;
        Scope = scope;
        Global = global;
        Unset = unset;
        RunImmediately = runImmediately;
        Append = append;
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
    public OptionScope? Scope { get; }

    /// <summary>Gets whether the global table is set instead of the local one.</summary>
    public bool Global { get; }

    /// <summary>Gets whether the hook is removed rather than set.</summary>
    public bool Unset { get; }

    /// <summary>Gets whether tmux also runs the command now.</summary>
    public bool RunImmediately { get; }

    /// <summary>Gets whether the command joins the hook's existing entries.</summary>
    public bool Append { get; }
}
