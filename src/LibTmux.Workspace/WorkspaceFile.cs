using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Runtime.Versioning;

namespace LibTmux.Workspace;

/// <summary>Describes one pane in a supported tmuxp workspace.</summary>
public sealed class WorkspacePane
{
    private readonly ReadOnlyDictionary<string, string> _environment = WorkspaceCollections.CopyEnvironment(null, nameof(Environment));
    private readonly ReadOnlyCollection<WorkspaceCommand> _beforeCommands;
    private readonly ReadOnlyCollection<string> _shellCommandsBefore;

    private readonly ReadOnlyCollection<WorkspaceCommand> _commands;
    private readonly ReadOnlyCollection<string> _shellCommands;
    private readonly ReadOnlyDictionary<string, string> _options;

    /// <summary>Initializes a pane description.</summary>
    /// <param name="shellCommands">The commands to run, in order.</param>
    /// <param name="startDirectory">The directory the pane starts in.</param>
    /// <param name="focus">Whether the pane is left selected.</param>
    /// <param name="options">The pane options to set.</param>
    /// <param name="commands">Typed commands; use instead of <paramref name="shellCommands" /> to set Enter overrides.</param>
    /// <param name="enter">The pane's initial Enter state, or null for the default.</param>
    public WorkspacePane(
        IReadOnlyList<string>? shellCommands = null,
        string? startDirectory = null,
        bool focus = false,
        IReadOnlyDictionary<string, string>? options = null,
        IReadOnlyList<WorkspaceCommand>? commands = null,
        bool? enter = null)
    {
        _commands = WorkspaceCollections.CopyCommands(
            shellCommands, commands, nameof(shellCommands), nameof(commands));
        _shellCommands = WorkspaceCollections.CommandText(_commands);
        _beforeCommands = WorkspaceCollections.Copy<WorkspaceCommand>(null, nameof(BeforeCommands));
        _shellCommandsBefore = WorkspaceCollections.CommandText(_beforeCommands);
        _options = WorkspaceCollections.Copy(options, nameof(options));
        StartDirectory = startDirectory;
        Focus = focus;
        Enter = enter;
    }

    private WorkspacePane(
        WorkspacePane source,
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyList<string>? shellCommandsBefore,
        IReadOnlyList<WorkspaceCommand>? beforeCommands)
        : this(startDirectory: source.StartDirectory, focus: source.Focus,
            options: source.Options, commands: source.Commands, enter: source.Enter)
    {
        _environment = WorkspaceCollections.CopyEnvironment(environment, nameof(environment));
        _beforeCommands = WorkspaceCollections.CopyCommands(
            shellCommandsBefore,
            beforeCommands ?? (shellCommandsBefore is null ? source.BeforeCommands : null),
            nameof(shellCommandsBefore), nameof(beforeCommands));
        _shellCommandsBefore = WorkspaceCollections.CommandText(_beforeCommands);
    }

    /// <summary>Gets the environment entries contributed by this declaration.</summary>
    public IReadOnlyDictionary<string, string> Environment => _environment;

    /// <summary>Gets the commands prepended at this declaration level.</summary>
    public IReadOnlyList<string> ShellCommandsBefore => _shellCommandsBefore;

    /// <summary>Gets the typed commands prepended at this declaration level.</summary>
    public IReadOnlyList<WorkspaceCommand> BeforeCommands => _beforeCommands;

    /// <summary>Returns a declaration with replacement environment and pre-command defaults.</summary>
    /// <param name="environment">The local entries to copy, or null to preserve the existing entries.</param>
    /// <param name="shellCommandsBefore">The local commands to copy, or null to preserve the existing commands.</param>
    /// <param name="beforeCommands">Typed local commands; use instead of <paramref name="shellCommandsBefore" />.</param>
    /// <returns>A new declaration; empty collections clear the corresponding defaults.</returns>
    /// <exception cref="ArgumentException">An environment name is empty or contains '=' or NUL, or a value contains NUL.</exception>
    public WorkspacePane WithDefaults(
        IReadOnlyDictionary<string, string>? environment = null,
        IReadOnlyList<string>? shellCommandsBefore = null,
        IReadOnlyList<WorkspaceCommand>? beforeCommands = null) =>
        new(this, environment ?? Environment, shellCommandsBefore, beforeCommands);

    /// <summary>Gets the commands to run, in order.</summary>
    public IReadOnlyList<string> ShellCommands => _shellCommands;

    /// <summary>Gets the typed pane commands in declaration order.</summary>
    public IReadOnlyList<WorkspaceCommand> Commands => _commands;

    /// <summary>Gets the pane's initial Enter state, or null for the default.</summary>
    public bool? Enter { get; }

    /// <summary>Gets the directory the pane starts in.</summary>
    public string? StartDirectory { get; }

    /// <summary>Gets whether the pane is left selected.</summary>
    public bool Focus { get; }

    /// <summary>Gets the pane options to set.</summary>
    public IReadOnlyDictionary<string, string> Options => _options;
}

/// <summary>Describes one window in a supported tmuxp workspace.</summary>
public sealed class WorkspaceWindow
{
    private readonly ReadOnlyDictionary<string, string> _environment = WorkspaceCollections.CopyEnvironment(null, nameof(Environment));
    private readonly ReadOnlyCollection<WorkspaceCommand> _beforeCommands;
    private readonly ReadOnlyCollection<string> _shellCommandsBefore;

    private readonly ReadOnlyDictionary<string, string> _options;
    private readonly ReadOnlyDictionary<string, string> _optionsAfter;
    private readonly ReadOnlyCollection<WorkspacePane> _panes;

    /// <summary>Initializes a window description.</summary>
    /// <param name="windowName">The window name.</param>
    /// <param name="startDirectory">The directory its panes start in.</param>
    /// <param name="layout">The layout to apply after creating its panes.</param>
    /// <param name="focus">Whether the window is left selected.</param>
    /// <param name="options">The window options to set before pane commands and splits.</param>
    /// <param name="panes">The panes to create, in order.</param>
    /// <param name="windowIndex">The session-relative window index, or null for the next free index.</param>
    /// <param name="optionsAfter">The window options to set after pane commands and the final layout.</param>
    public WorkspaceWindow(
        string? windowName = null,
        string? startDirectory = null,
        string? layout = null,
        bool focus = false,
        IReadOnlyDictionary<string, string>? options = null,
        IReadOnlyList<WorkspacePane>? panes = null,
        int? windowIndex = null,
        IReadOnlyDictionary<string, string>? optionsAfter = null)
    {
        if (windowIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(windowIndex), "The window index must be nonnegative.");
        WindowName = windowName;
        StartDirectory = startDirectory;
        Layout = layout;
        Focus = focus;
        WindowIndex = windowIndex;
        _options = WorkspaceCollections.Copy(options, nameof(options));
        _optionsAfter = WorkspaceCollections.Copy(optionsAfter, nameof(optionsAfter));
        _panes = WorkspaceCollections.Copy(panes, nameof(panes));
        _beforeCommands = WorkspaceCollections.Copy<WorkspaceCommand>(null, nameof(BeforeCommands));
        _shellCommandsBefore = WorkspaceCollections.CommandText(_beforeCommands);
    }

    private WorkspaceWindow(
        WorkspaceWindow source,
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyList<string>? shellCommandsBefore,
        IReadOnlyList<WorkspaceCommand>? beforeCommands)
        : this(source.WindowName, source.StartDirectory, source.Layout, source.Focus, source.Options, source.Panes,
            source.WindowIndex, source.OptionsAfter)
    {
        _environment = WorkspaceCollections.CopyEnvironment(environment, nameof(environment));
        _beforeCommands = WorkspaceCollections.CopyCommands(
            shellCommandsBefore,
            beforeCommands ?? (shellCommandsBefore is null ? source.BeforeCommands : null),
            nameof(shellCommandsBefore), nameof(beforeCommands));
        _shellCommandsBefore = WorkspaceCollections.CommandText(_beforeCommands);
    }

    /// <summary>Gets the environment entries contributed by this declaration.</summary>
    public IReadOnlyDictionary<string, string> Environment => _environment;

    /// <summary>Gets the commands prepended at this declaration level.</summary>
    public IReadOnlyList<string> ShellCommandsBefore => _shellCommandsBefore;

    /// <summary>Gets the typed commands prepended at this declaration level.</summary>
    public IReadOnlyList<WorkspaceCommand> BeforeCommands => _beforeCommands;

    /// <summary>Returns a declaration with replacement environment and pre-command defaults.</summary>
    /// <param name="environment">The local entries to copy, or null to preserve the existing entries.</param>
    /// <param name="shellCommandsBefore">The local commands to copy, or null to preserve the existing commands.</param>
    /// <param name="beforeCommands">Typed local commands; use instead of <paramref name="shellCommandsBefore" />.</param>
    /// <returns>A new declaration; empty collections clear the corresponding defaults.</returns>
    /// <exception cref="ArgumentException">An environment name is empty or contains '=' or NUL, or a value contains NUL.</exception>
    public WorkspaceWindow WithDefaults(
        IReadOnlyDictionary<string, string>? environment = null,
        IReadOnlyList<string>? shellCommandsBefore = null,
        IReadOnlyList<WorkspaceCommand>? beforeCommands = null) =>
        new(this, environment ?? Environment, shellCommandsBefore, beforeCommands);

    /// <summary>Gets the window name.</summary>
    public string? WindowName { get; }

    /// <summary>Gets the requested session-relative window index, or null for the next free index.</summary>
    public int? WindowIndex { get; }

    /// <summary>Gets the directory its panes start in.</summary>
    public string? StartDirectory { get; }

    /// <summary>Gets the layout to apply after creating its panes.</summary>
    public string? Layout { get; }

    /// <summary>Gets whether the window is left selected.</summary>
    public bool Focus { get; }

    /// <summary>Gets the window options to set before pane commands and splits.</summary>
    public IReadOnlyDictionary<string, string> Options => _options;

    /// <summary>Gets the window options to set after pane commands and the final layout.</summary>
    public IReadOnlyDictionary<string, string> OptionsAfter => _optionsAfter;

    /// <summary>Gets the panes to create, in order.</summary>
    public IReadOnlyList<WorkspacePane> Panes => _panes;
}

/// <summary>Describes the supported subset of one tmuxp workspace.</summary>
/// <remarks>
/// Parsing rejects keys that require tmuxp's Python hooks or plugins. It does
/// not execute or silently discard configuration outside this model.
/// </remarks>
public sealed class WorkspaceFile
{
    private readonly ReadOnlyDictionary<string, string> _environment = WorkspaceCollections.CopyEnvironment(null, nameof(Environment));
    private readonly ReadOnlyCollection<WorkspaceCommand> _beforeCommands;
    private readonly ReadOnlyCollection<string> _shellCommandsBefore;

    private readonly ReadOnlyDictionary<string, string> _options;
    private readonly ReadOnlyDictionary<string, string> _globalOptions;
    private readonly ReadOnlyCollection<WorkspaceWindow> _windows;

    /// <summary>Initializes a workspace description.</summary>
    /// <param name="sessionName">The session name.</param>
    /// <param name="startDirectory">The directory its windows start in.</param>
    /// <param name="options">The session options to set.</param>
    /// <param name="windows">The windows to create, in order.</param>
    /// <param name="beforeScript">The host command retained for explicitly enabled execution.</param>
    /// <param name="globalOptions">The global session options to set before creating the declared windows.</param>
    /// <exception cref="ArgumentException">The host command is blank or contains NUL.</exception>
    public WorkspaceFile(
        string? sessionName = null,
        string? startDirectory = null,
        IReadOnlyDictionary<string, string>? options = null,
        IReadOnlyList<WorkspaceWindow>? windows = null,
        string? beforeScript = null,
        IReadOnlyDictionary<string, string>? globalOptions = null)
    {
        if (beforeScript is not null && (string.IsNullOrWhiteSpace(beforeScript) || beforeScript.Contains('\0')))
        {
            throw new ArgumentException("The before-script command must not be blank or contain NUL.", nameof(beforeScript));
        }

        SessionName = sessionName;
        StartDirectory = startDirectory;
        BeforeScript = beforeScript;
        _options = WorkspaceCollections.Copy(options, nameof(options));
        _globalOptions = WorkspaceCollections.Copy(globalOptions, nameof(globalOptions));
        _windows = WorkspaceCollections.Copy(windows, nameof(windows));
        _beforeCommands = WorkspaceCollections.Copy<WorkspaceCommand>(null, nameof(BeforeCommands));
        _shellCommandsBefore = WorkspaceCollections.CommandText(_beforeCommands);
    }

    internal WorkspaceFile(
        WorkspaceFile source,
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyList<string>? shellCommandsBefore,
        IReadOnlyList<WorkspaceCommand>? beforeCommands,
        FrozenDictionary<string, (long Line, long Column)>? sourceLocations = null)
        : this(source.SessionName, source.StartDirectory, source.Options, source.Windows, source.BeforeScript,
            source.GlobalOptions)
    {
        _environment = WorkspaceCollections.CopyEnvironment(environment, nameof(environment));
        _beforeCommands = WorkspaceCollections.CopyCommands(
            shellCommandsBefore,
            beforeCommands ?? (shellCommandsBefore is null ? source.BeforeCommands : null),
            nameof(shellCommandsBefore), nameof(beforeCommands));
        _shellCommandsBefore = WorkspaceCollections.CommandText(_beforeCommands);
        DirectoriesAreResolved = source.DirectoriesAreResolved;
        DocumentDirectory = source.DocumentDirectory;
        SourceLocations = sourceLocations ?? CopySourceLocations(source, environment, shellCommandsBefore, beforeCommands);
    }

    /// <summary>Gets the environment entries contributed by this declaration.</summary>
    public IReadOnlyDictionary<string, string> Environment => _environment;

    /// <summary>Gets the commands prepended at this declaration level.</summary>
    public IReadOnlyList<string> ShellCommandsBefore => _shellCommandsBefore;

    /// <summary>Gets the typed commands prepended at this declaration level.</summary>
    public IReadOnlyList<WorkspaceCommand> BeforeCommands => _beforeCommands;

    /// <summary>Returns a declaration with replacement environment and pre-command defaults.</summary>
    /// <param name="environment">The local entries to copy, or null to preserve the existing entries.</param>
    /// <param name="shellCommandsBefore">The local commands to copy, or null to preserve the existing commands.</param>
    /// <param name="beforeCommands">Typed local commands; use instead of <paramref name="shellCommandsBefore" />.</param>
    /// <returns>A new declaration; empty collections clear the corresponding defaults.</returns>
    /// <exception cref="ArgumentException">An environment name is empty or contains '=' or NUL, or a value contains NUL.</exception>
    public WorkspaceFile WithDefaults(
        IReadOnlyDictionary<string, string>? environment = null,
        IReadOnlyList<string>? shellCommandsBefore = null,
        IReadOnlyList<WorkspaceCommand>? beforeCommands = null) =>
        new(this, environment ?? Environment, shellCommandsBefore, beforeCommands);

    /// <summary>Gets the session name.</summary>
    public string? SessionName { get; }

    /// <summary>Gets the directory its windows start in.</summary>
    public string? StartDirectory { get; }

    /// <summary>Gets the session options to set.</summary>
    public IReadOnlyDictionary<string, string> Options => _options;

    /// <summary>Gets the global session options to set before creating the declared windows.</summary>
    /// <remarks>These can affect other sessions that inherit them and are not reversed by compensation.</remarks>
    public IReadOnlyDictionary<string, string> GlobalOptions => _globalOptions;

    /// <summary>Gets the windows to create, in order.</summary>
    public IReadOnlyList<WorkspaceWindow> Windows => _windows;

    /// <summary>Gets the host command retained without expansion or execution by parsing or resolution.</summary>
    public string? BeforeScript { get; }

    /// <summary>Gets the absolute document directory supplied to resolution, or null before resolution.</summary>
    public string? DocumentDirectory { get; internal init; }

    internal bool DirectoriesAreResolved { get; init; }

    internal FrozenDictionary<string, (long Line, long Column)> SourceLocations { get; } =
        FrozenDictionary<string, (long Line, long Column)>.Empty;

    internal WorkspaceFormatException At(string path, string message, Exception? innerException = null) =>
        new(SourceLocations.TryGetValue(path, out (long Line, long Column) location)
            ? FormattableString.Invariant($"{message} At line {location.Line}, column {location.Column}.")
            : message, innerException);

    private static FrozenDictionary<string, (long Line, long Column)> CopySourceLocations(
        WorkspaceFile source,
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyList<string>? shellCommandsBefore,
        IReadOnlyList<WorkspaceCommand>? beforeCommands)
    {
        bool replaceEnvironment = !ReferenceEquals(environment, source.Environment);
        bool replaceCommands = shellCommandsBefore is not null
            || (beforeCommands is not null && !ReferenceEquals(beforeCommands, source.BeforeCommands));
        if ((!replaceEnvironment && !replaceCommands) || source.SourceLocations.Count == 0)
            return source.SourceLocations;

        return source.SourceLocations.Where(pair =>
                !(replaceEnvironment && (pair.Key == "environment" || pair.Key.StartsWith("environment.", StringComparison.Ordinal)))
                && !(replaceCommands && (pair.Key == "shell_command_before" || pair.Key.StartsWith("shell_command_before[", StringComparison.Ordinal))))
            .ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>Resolves inherited pane directories against an explicit document base.</summary>
    /// <param name="baseDirectory">The absolute directory containing the declaration.</param>
    /// <param name="variables">The only variables available to directory and option-value expansion.</param>
    /// <returns>A new declaration with absolute inherited directories and its document directory.</returns>
    /// <remarks>
    /// Resolves relative paths against the parent declaration's directory. Expansion
    /// accepts $NAME, ${NAME}, and a leading ~ using the supplied HOME variable;
    /// $$ produces a literal dollar sign. Session, window and pane option values
    /// expand supplied variables; unknown variables remain literal. Resolution reads
    /// neither the process environment nor the filesystem. The builder treats resolved
    /// directories as literal paths, including tmux format characters. Commands, the
    /// host script, names and environment values remain literal. Failures retain the
    /// original YAML or JSON value's line and column when the declaration was parsed.
    /// </remarks>
    /// <exception cref="ArgumentException">The document base is not absolute.</exception>
    /// <exception cref="WorkspaceFormatException">A directory or expansion is invalid.</exception>
    public WorkspaceFile Resolve(
        string baseDirectory,
        IReadOnlyDictionary<string, string>? variables = null) =>
        WorkspacePathResolver.Resolve(this, baseDirectory, variables);

    /// <summary>Creates a workspace declaration from a captured session without reaching tmux.</summary>
    /// <param name="session">The session with captured windows, panes, and active pane relations.</param>
    /// <returns>An unresolved declaration without a document directory.</returns>
    /// <remarks>
    /// Preserves captured window placements and pane order, names, layouts, and focus.
    /// Repeated links become separate declared windows. Captured pane directories
    /// escape literal dollars for a later explicit <see cref="Resolve" />; captured
    /// null paths remain unspecified. Commands, environment, options, terminal text,
    /// entity identifiers, pane indices, and shared-link identity are not
    /// reconstructed.
    /// Foreground command names do not establish shell intent. Restoring a native
    /// custom layout can change which pane occupies a position; see
    /// <see cref="Window.SelectLayoutAsync" />.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The session is null.</exception>
    /// <exception cref="IncompleteSnapshotException">A required field or relation was not captured.</exception>
    /// <exception cref="TmuxProtocolException">A captured window active flag is malformed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static WorkspaceFile FromSnapshot(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        CapturedRelation<Window> captured = session.Windows;
        WorkspaceWindow[] windows = new WorkspaceWindow[captured.Count];
        for (int windowIndex = 0; windowIndex < windows.Length; windowIndex++)
        {
            Window window = captured[windowIndex];
            Pane activePane = window.ActivePane.Value;
            WorkspacePane[] panes = new WorkspacePane[window.Panes.Count];
            for (int paneIndex = 0; paneIndex < panes.Length; paneIndex++)
            {
                Pane pane = window.Panes[paneIndex];
                panes[paneIndex] = new WorkspacePane(
                    startDirectory: pane.CurrentPath?.Replace("$", "$$", StringComparison.Ordinal),
                    focus: pane == activePane);
            }

            windows[windowIndex] = new WorkspaceWindow(
                windowName: window.Name, layout: window.Layout, focus: window.IsActive, panes: panes,
                windowIndex: window.Edge.WindowIndex);
        }

        return new WorkspaceFile(sessionName: session.Name, windows: windows);
    }

    /// <summary>Reads a workspace from tmuxp YAML or JSON.</summary>
    /// <param name="yaml">The file contents.</param>
    /// <returns>The parsed workspace.</returns>
    /// <exception cref="WorkspaceFormatException">
    /// The input is too large, malformed, contains more than one document, or
    /// uses a key or value shape outside the supported subset.
    /// </exception>
    public static WorkspaceFile Parse(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        return WorkspaceYamlParser.Parse(yaml);
    }
}

/// <summary>Thrown when a workspace file cannot be read.</summary>
public sealed class WorkspaceFormatException : LibTmuxException
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="message">The invalid part of the workspace.</param>
    /// <param name="innerException">The underlying YAML failure, when present.</param>
    public WorkspaceFormatException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

internal static class WorkspaceCollections
{
    internal static ReadOnlyCollection<WorkspaceCommand> CopyCommands(
        IReadOnlyList<string>? shellCommands,
        IReadOnlyList<WorkspaceCommand>? commands,
        string shellCommandsParameter,
        string commandsParameter)
    {
        if (shellCommands is not null && commands is not null)
        {
            throw new ArgumentException(
                "Use either string commands or typed commands, not both.",
                commandsParameter);
        }

        if (commands is not null)
        {
            return Copy(commands, commandsParameter);
        }

        ReadOnlyCollection<string> text = Copy(shellCommands, shellCommandsParameter);
        return Array.AsReadOnly(text.Select(command => new WorkspaceCommand(command)).ToArray());
    }

    internal static ReadOnlyCollection<string> CommandText(
        IReadOnlyList<WorkspaceCommand> commands) =>
        Array.AsReadOnly(commands.Select(command => command.Text).ToArray());

    internal static bool IsValidEnvironmentName(string name) =>
        name.Length > 0 && !name.Contains('=') && !name.Contains('\0');

    internal static ReadOnlyDictionary<string, string> CopyEnvironment(
        IReadOnlyDictionary<string, string>? values,
        string parameterName)
    {
        ReadOnlyDictionary<string, string> copy = Copy(values, parameterName);
        foreach ((string name, string value) in copy)
        {
            if (!IsValidEnvironmentName(name))
            {
                throw new ArgumentException(
                    "Environment names must be nonempty and cannot contain '=' or NUL.",
                    parameterName);
            }

            if (value.Contains('\0'))
            {
                throw new ArgumentException("Environment values cannot contain NUL.", parameterName);
            }
        }

        return copy;
    }

    public static ReadOnlyCollection<T> Copy<T>(
        IReadOnlyList<T>? values,
        string parameterName)
        where T : class
    {
        T[] copy = values is null ? [] : [.. values];
        if (copy.Any(static value => value is null))
        {
            throw new ArgumentException("The collection cannot contain null.", parameterName);
        }

        return Array.AsReadOnly(copy);
    }

    public static ReadOnlyDictionary<string, string> Copy(
        IReadOnlyDictionary<string, string>? values,
        string parameterName)
    {
        Dictionary<string, string> copy = new(StringComparer.Ordinal);
        if (values is not null)
        {
            foreach ((string key, string value) in values)
            {
                if (key is null || value is null)
                {
                    throw new ArgumentException(
                        "Option names and values cannot be null.",
                        parameterName);
                }

                copy.Add(key, value);
            }
        }

        return new ReadOnlyDictionary<string, string>(copy);
    }
}
