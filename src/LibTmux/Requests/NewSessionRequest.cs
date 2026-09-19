using System.Collections.ObjectModel;

namespace LibTmux;

/// <summary>Describes one <c>new-session</c> invocation.</summary>
/// <remarks>
/// Every flag is explicit rather than inferred, so the argv tmux receives is
/// readable from the call site instead of assembled by hidden defaults.
/// </remarks>
public sealed record NewSessionRequest
{
    private readonly IReadOnlyDictionary<string, string>? _environment;

    /// <summary>Gets the session name, or null to let tmux choose.</summary>
    /// <remarks>
    /// tmux expands the name as a format, so a <c>#</c> in it does not survive
    /// verbatim.
    /// </remarks>
    public string? Name { get; init; }

    /// <summary>Gets whether a session of the same name is removed first.</summary>
    public bool ReplaceExisting { get; init; }

    /// <summary>Gets whether the new session is attached rather than detached.</summary>
    public bool Attach { get; init; }

    /// <summary>Gets the working directory for the first pane.</summary>
    /// <remarks>
    /// tmux expands it as a format before it changes directory, so a <c>#</c>
    /// in it does not survive verbatim.
    /// </remarks>
    public string? StartDirectory { get; init; }

    /// <summary>Gets the name of the first window.</summary>
    /// <remarks>
    /// tmux expands it as a format before it names anything, so a <c>#</c> in
    /// it does not survive verbatim.
    /// </remarks>
    public string? WindowName { get; init; }

    /// <summary>Gets the command the first pane runs.</summary>
    public string? Command { get; init; }

    /// <summary>Gets the requested width.</summary>
    public string? Width { get; init; }

    /// <summary>Gets the requested height.</summary>
    public string? Height { get; init; }

    /// <summary>Gets the environment entries set on the session.</summary>
    public IReadOnlyDictionary<string, string>? Environment
    {
        get => _environment;

        // The request is read again at dispatch, so a caller that kept the
        // dictionary could otherwise change the argv after building it.
        init => _environment = value is null
            ? null
            : new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(value, StringComparer.Ordinal));
    }

    /// <summary>Gets whether other clients are detached on attach.</summary>
    public bool DetachOthers { get; init; }

    /// <summary>Gets whether tmux may ignore the requested size.</summary>
    public bool NoSize { get; init; }

    /// <summary>Gets the comma-separated client flags passed with <c>-f</c>.</summary>
    public string? ClientFlags { get; init; }
}
