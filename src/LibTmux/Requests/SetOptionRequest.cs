using System.Runtime.Versioning;

namespace LibTmux;

/// <summary>Describes one <c>set-option</c> invocation.</summary>
public sealed record SetOptionRequest : ITmuxRequest<TmuxOptions>
{
    /// <summary>Initializes a request to set one option.</summary>
    /// <param name="name">The option to set, optionally with an array index.</param>
    /// <param name="value">The value to store.</param>
    public SetOptionRequest(
        string name,
        string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        Name = name;
        Value = value;
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
    public OptionScope? Scope { get; init; }

    /// <summary>Gets whether tmux expands the value as a format before storing it.</summary>
    public bool ExpandFormat { get; init; }

    /// <summary>Gets whether an already-set option is left alone.</summary>
    public bool PreventOverwrite { get; init; }

    /// <summary>Gets whether a rejected option is answered with nothing instead of an error.</summary>
    public bool Quiet { get; init; }

    /// <summary>Gets whether the value is appended to the existing one.</summary>
    public bool Append { get; init; }

    /// <summary>Gets whether the global table is set instead of the local one.</summary>
    public bool Global { get; init; }

    /// <summary>Returns an option request as one tmux command.</summary>
    /// <param name="options">The options handle whose scope the option is set in.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <remarks>
    /// This takes the options handle rather than a server, because which
    /// scope flags and target tmux receives follow from the handle the caller
    /// reached for: a window's options and a server's are the same request
    /// spelled differently.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="options" /> is null.</exception>
    [UnsupportedOSPlatform("windows")]
    public TmuxCommand ToCommand(TmuxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return TmuxChaining.Command([.. options.BuildSetArguments(this)]) with
        {
            RequiredGeneration = options.Generation,
        };
    }
}
