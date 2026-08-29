using System.Collections.ObjectModel;

namespace LibTmux;

/// <summary>Describes one <c>new-session</c> invocation.</summary>
/// <remarks>
/// Every flag is explicit rather than inferred, so the argv tmux receives is
/// readable from the call site instead of assembled by hidden defaults.
/// </remarks>
public sealed record NewSessionRequest
{
    /// <summary>Initializes a session-creation request.</summary>
    /// <param name="name">The session name, or null to let tmux choose.</param>
    /// <param name="replaceExisting">Whether a session of the same name is removed first.</param>
    /// <param name="attach">Whether the new session is attached rather than detached.</param>
    /// <param name="startDirectory">The working directory for the first pane.</param>
    /// <param name="windowName">The name of the first window.</param>
    /// <param name="command">The command the first pane runs.</param>
    /// <param name="width">The requested width.</param>
    /// <param name="height">The requested height.</param>
    /// <param name="environment">Environment entries set on the session.</param>
    /// <param name="detachOthers">Whether other clients are detached on attach.</param>
    /// <param name="noSize">Whether tmux may ignore the requested size.</param>
    /// <param name="clientFlags">Comma-separated client flags passed with <c>-f</c>.</param>
    public NewSessionRequest(
        string? name = null,
        bool replaceExisting = false,
        bool attach = false,
        string? startDirectory = null,
        string? windowName = null,
        string? command = null,
        string? width = null,
        string? height = null,
        IReadOnlyDictionary<string, string>? environment = null,
        bool detachOthers = false,
        bool noSize = false,
        string? clientFlags = null)
    {
        Name = name;
        ReplaceExisting = replaceExisting;
        Attach = attach;
        StartDirectory = startDirectory;
        WindowName = windowName;
        Command = command;
        Width = width;
        Height = height;
        // The request is read again at dispatch, so a caller that kept the
        // dictionary could otherwise change the argv after constructing it.
        Environment = environment is null
            ? null
            : new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(environment, StringComparer.Ordinal));
        DetachOthers = detachOthers;
        NoSize = noSize;
        ClientFlags = clientFlags;
    }

    /// <summary>Gets the session name, or null to let tmux choose.</summary>
    /// <remarks>
    /// tmux expands the name as a format, so a <c>#</c> in it does not survive
    /// verbatim.
    /// </remarks>
    public string? Name { get; }

    /// <summary>Gets whether a session of the same name is removed first.</summary>
    public bool ReplaceExisting { get; }

    /// <summary>Gets whether the new session is attached rather than detached.</summary>
    public bool Attach { get; }

    /// <summary>Gets the working directory for the first pane.</summary>
    public string? StartDirectory { get; }

    /// <summary>Gets the name of the first window.</summary>
    public string? WindowName { get; }

    /// <summary>Gets the command the first pane runs.</summary>
    public string? Command { get; }

    /// <summary>Gets the requested width.</summary>
    public string? Width { get; }

    /// <summary>Gets the requested height.</summary>
    public string? Height { get; }

    /// <summary>Gets the environment entries set on the session.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; }

    /// <summary>Gets whether other clients are detached on attach.</summary>
    public bool DetachOthers { get; }

    /// <summary>Gets whether tmux may ignore the requested size.</summary>
    public bool NoSize { get; }

    /// <summary>Gets the comma-separated client flags passed with <c>-f</c>.</summary>
    public string? ClientFlags { get; }
}
