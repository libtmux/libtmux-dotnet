namespace LibTmux;

/// <summary>Describes one <c>set-option</c> invocation.</summary>
public sealed record SetOptionRequest
{
    /// <summary>Initializes a request to set one option.</summary>
    /// <param name="name">The option to set, optionally with an array index.</param>
    /// <param name="value">The value to store.</param>
    /// <param name="scope">The scope to set in, or null for the owner's own.</param>
    /// <param name="expandFormat">Whether tmux expands the value as a format before storing it.</param>
    /// <param name="preventOverwrite">Whether an already-set option is left alone.</param>
    /// <param name="quiet">Whether a rejected option is answered with nothing instead of an error.</param>
    /// <param name="append">Whether the value is appended to the existing one.</param>
    /// <param name="global">Whether the global table is set instead of the local one.</param>
    public SetOptionRequest(
        string name,
        string value,
        OptionScope? scope = null,
        bool expandFormat = false,
        bool preventOverwrite = false,
        bool quiet = false,
        bool append = false,
        bool global = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        Name = name;
        Value = value;
        Scope = scope;
        ExpandFormat = expandFormat;
        PreventOverwrite = preventOverwrite;
        Quiet = quiet;
        Append = append;
        Global = global;
    }

    /// <summary>Gets the option to set, optionally with an array index.</summary>
    /// <remarks>
    /// tmux expands it as a format before it names anything, so a <c>#</c> in
    /// it does not survive verbatim.
    /// </remarks>
    public string Name { get; }

    /// <summary>Gets the value to store.</summary>
    public string Value { get; }

    /// <summary>Gets the scope to set in, or null for the owner's own.</summary>
    public OptionScope? Scope { get; }

    /// <summary>Gets whether tmux expands the value as a format before storing it.</summary>
    public bool ExpandFormat { get; }

    /// <summary>Gets whether an already-set option is left alone.</summary>
    public bool PreventOverwrite { get; }

    /// <summary>Gets whether a rejected option is answered with nothing instead of an error.</summary>
    public bool Quiet { get; }

    /// <summary>Gets whether the value is appended to the existing one.</summary>
    public bool Append { get; }

    /// <summary>Gets whether the global table is set instead of the local one.</summary>
    public bool Global { get; }
}
