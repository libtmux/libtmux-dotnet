using System.Collections.ObjectModel;

namespace LibTmux;

/// <summary>Describes one <c>new-window</c> invocation.</summary>
public sealed record NewWindowRequest
{
    private readonly IReadOnlyDictionary<string, string>? _environment;

    /// <summary>Gets the window name.</summary>
    /// <remarks>
    /// tmux expands the name as a format, so a <c>#</c> in it does not survive
    /// verbatim.
    /// </remarks>
    public string? Name { get; init; }

    /// <summary>Gets the working directory for the first pane.</summary>
    /// <remarks>
    /// tmux expands it as a format before it changes directory, so a <c>#</c>
    /// in it does not survive verbatim.
    /// </remarks>
    public string? StartDirectory { get; init; }

    /// <summary>Gets whether the new window becomes current.</summary>
    public bool Attach { get; init; }

    /// <summary>Gets the window index to create at.</summary>
    public string? Index { get; init; }

    /// <summary>Gets the command the first pane runs.</summary>
    public string? Command { get; init; }

    /// <summary>Gets the environment entries set on the window.</summary>
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

    /// <summary>Gets whether to insert before or after the target.</summary>
    public WindowDirection? Direction { get; init; }

    /// <summary>Gets the window to insert relative to.</summary>
    public string? TargetWindow { get; init; }

    /// <summary>Gets whether an existing window at the index is replaced.</summary>
    public bool KillExisting { get; init; }

    /// <summary>Gets whether an existing window is selected instead.</summary>
    public bool SelectExisting { get; init; }

    /// <summary>Returns this request aimed at one window.</summary>
    /// <param name="targetWindow">The window to insert relative to.</param>
    /// <returns>A copy carrying the target.</returns>
    /// <exception cref="ArgumentException">
    /// The request already names an index, which is the other way to say where
    /// the window goes.
    /// </exception>
    internal NewWindowRequest WithTargetWindow(string targetWindow) =>
        Index is null
            ? this with { TargetWindow = targetWindow }
            : throw new ArgumentException(
                "A window created next to another cannot also name an index.",
                nameof(targetWindow));

    /// <summary>Resolves where the window goes, refusing two answers.</summary>
    /// <returns>The target window or index, or null for the next free index.</returns>
    /// <exception cref="ArgumentException">Both an index and a target window are set.</exception>
    /// <remarks>
    /// Initializers cannot check one property against another, so the pairing
    /// is settled where the position is derived. Every caller that sends a
    /// new window needs this value, so none can reach tmux having skipped the
    /// check.
    /// </remarks>
    internal string? ResolvePosition() =>
        Index is not null && TargetWindow is not null
            ? throw new ArgumentException(
                "A window position comes from either an index or a target window, not both.",
                nameof(TargetWindow))
            : TargetWindow ?? Index;
}
