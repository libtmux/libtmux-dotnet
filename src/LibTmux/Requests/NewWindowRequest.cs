using System.Collections.ObjectModel;

namespace LibTmux;

/// <summary>Describes one <c>new-window</c> invocation.</summary>
public sealed record NewWindowRequest
{
    /// <summary>Initializes a window-creation request.</summary>
    /// <param name="name">The window name.</param>
    /// <param name="startDirectory">The working directory for the first pane.</param>
    /// <param name="attach">Whether the new window becomes current.</param>
    /// <param name="index">The window index to create at.</param>
    /// <param name="command">The command the first pane runs.</param>
    /// <param name="environment">Environment entries set on the window.</param>
    /// <param name="direction">Whether to insert before or after the target.</param>
    /// <param name="targetWindow">The window to insert relative to.</param>
    /// <param name="killExisting">Whether an existing window at the index is replaced.</param>
    /// <param name="selectExisting">Whether an existing window is selected instead.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="index" /> and <paramref name="targetWindow" /> both name
    /// a position, so only one of them can be sent.
    /// </exception>
    public NewWindowRequest(
        string? name = null,
        string? startDirectory = null,
        bool attach = false,
        string? index = null,
        string? command = null,
        IReadOnlyDictionary<string, string>? environment = null,
        WindowDirection? direction = null,
        string? targetWindow = null,
        bool killExisting = false,
        bool selectExisting = false)
    {
        if (index is not null && targetWindow is not null)
        {
            throw new ArgumentException(
                "A window position comes from either an index or a target window, not both.",
                nameof(targetWindow));
        }

        Name = name;
        StartDirectory = startDirectory;
        Attach = attach;
        Index = index;
        Command = command;
        // The request is read again at dispatch, so a caller that kept the
        // dictionary could otherwise change the argv after constructing it.
        Environment = environment is null
            ? null
            : new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(environment, StringComparer.Ordinal));
        Direction = direction;
        TargetWindow = targetWindow;
        KillExisting = killExisting;
        SelectExisting = selectExisting;
    }

    /// <summary>Gets the window name.</summary>
    /// <remarks>
    /// tmux expands the name as a format, so a <c>#</c> in it does not survive
    /// verbatim.
    /// </remarks>
    public string? Name { get; }

    /// <summary>Gets the working directory for the first pane.</summary>
    public string? StartDirectory { get; }

    /// <summary>Gets whether the new window becomes current.</summary>
    public bool Attach { get; }

    /// <summary>Gets the window index to create at.</summary>
    public string? Index { get; }

    /// <summary>Gets the command the first pane runs.</summary>
    public string? Command { get; }

    /// <summary>Gets the environment entries set on the window.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; }

    /// <summary>Gets whether to insert before or after the target.</summary>
    public WindowDirection? Direction { get; }

    /// <summary>Gets the window to insert relative to.</summary>
    public string? TargetWindow { get; }

    /// <summary>Gets whether an existing window at the index is replaced.</summary>
    public bool KillExisting { get; }

    /// <summary>Gets whether an existing window is selected instead.</summary>
    public bool SelectExisting { get; }

    /// <summary>Returns this request aimed at one window.</summary>
    /// <param name="targetWindow">The window to insert relative to.</param>
    /// <returns>A copy carrying the target.</returns>
    /// <exception cref="ArgumentException">
    /// The request already names an index, which is the other way to say where
    /// the window goes.
    /// </exception>
    internal NewWindowRequest WithTargetWindow(string targetWindow) =>
        Index is null
            ? new NewWindowRequest(
                Name,
                StartDirectory,
                Attach,
                index: null,
                Command,
                Environment,
                Direction,
                targetWindow,
                KillExisting,
                SelectExisting)
            : throw new ArgumentException(
                "A window created next to another cannot also name an index.",
                nameof(targetWindow));
}
