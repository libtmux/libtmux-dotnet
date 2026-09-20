using System.Collections.ObjectModel;

namespace LibTmux.Workspace;

/// <summary>Describes one pane in a supported tmuxp workspace.</summary>
public sealed class WorkspacePane
{
    private readonly ReadOnlyDictionary<string, string> _environment = WorkspaceCollections.CopyEnvironment(null, nameof(Environment));
    private readonly ReadOnlyCollection<string> _shellCommandsBefore = WorkspaceCollections.Copy<string>(null, nameof(ShellCommandsBefore));

    private readonly ReadOnlyCollection<string> _shellCommands;

    /// <summary>Initializes a pane description.</summary>
    /// <param name="shellCommands">The commands to run, in order.</param>
    /// <param name="startDirectory">The directory the pane starts in.</param>
    /// <param name="focus">Whether the pane is left selected.</param>
    public WorkspacePane(
        IReadOnlyList<string>? shellCommands = null,
        string? startDirectory = null,
        bool focus = false)
    {
        _shellCommands = WorkspaceCollections.Copy(shellCommands, nameof(shellCommands));
        StartDirectory = startDirectory;
        Focus = focus;
    }

    private WorkspacePane(
        WorkspacePane source,
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyList<string> shellCommandsBefore)
        : this(source.ShellCommands, source.StartDirectory, source.Focus)
    {
        _environment = WorkspaceCollections.CopyEnvironment(environment, nameof(environment));
        _shellCommandsBefore = WorkspaceCollections.Copy(shellCommandsBefore, nameof(shellCommandsBefore));
    }

    /// <summary>Gets the environment entries contributed by this declaration.</summary>
    public IReadOnlyDictionary<string, string> Environment => _environment;

    /// <summary>Gets the commands prepended at this declaration level.</summary>
    public IReadOnlyList<string> ShellCommandsBefore => _shellCommandsBefore;

    /// <summary>Returns a declaration with replacement environment and pre-command defaults.</summary>
    /// <param name="environment">The local entries to copy, or null to preserve the existing entries.</param>
    /// <param name="shellCommandsBefore">The local commands to copy, or null to preserve the existing commands.</param>
    /// <returns>A new declaration; empty collections clear the corresponding defaults.</returns>
    /// <exception cref="ArgumentException">An environment name is empty or contains '=' or NUL, or a value contains NUL.</exception>
    public WorkspacePane WithDefaults(
        IReadOnlyDictionary<string, string>? environment = null,
        IReadOnlyList<string>? shellCommandsBefore = null) =>
        new(this, environment ?? Environment, shellCommandsBefore ?? ShellCommandsBefore);

    /// <summary>Gets the commands to run, in order.</summary>
    public IReadOnlyList<string> ShellCommands => _shellCommands;

    /// <summary>Gets the directory the pane starts in.</summary>
    public string? StartDirectory { get; }

    /// <summary>Gets whether the pane is left selected.</summary>
    public bool Focus { get; }
}

/// <summary>Describes one window in a supported tmuxp workspace.</summary>
public sealed class WorkspaceWindow
{
    private readonly ReadOnlyDictionary<string, string> _environment = WorkspaceCollections.CopyEnvironment(null, nameof(Environment));
    private readonly ReadOnlyCollection<string> _shellCommandsBefore = WorkspaceCollections.Copy<string>(null, nameof(ShellCommandsBefore));

    private readonly ReadOnlyDictionary<string, string> _options;
    private readonly ReadOnlyCollection<WorkspacePane> _panes;

    /// <summary>Initializes a window description.</summary>
    /// <param name="windowName">The window name.</param>
    /// <param name="startDirectory">The directory its panes start in.</param>
    /// <param name="layout">The layout to apply after creating its panes.</param>
    /// <param name="focus">Whether the window is left selected.</param>
    /// <param name="options">The window options to set.</param>
    /// <param name="panes">The panes to create, in order.</param>
    public WorkspaceWindow(
        string? windowName = null,
        string? startDirectory = null,
        string? layout = null,
        bool focus = false,
        IReadOnlyDictionary<string, string>? options = null,
        IReadOnlyList<WorkspacePane>? panes = null)
    {
        WindowName = windowName;
        StartDirectory = startDirectory;
        Layout = layout;
        Focus = focus;
        _options = WorkspaceCollections.Copy(options, nameof(options));
        _panes = WorkspaceCollections.Copy(panes, nameof(panes));
    }

    private WorkspaceWindow(
        WorkspaceWindow source,
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyList<string> shellCommandsBefore)
        : this(source.WindowName, source.StartDirectory, source.Layout, source.Focus, source.Options, source.Panes)
    {
        _environment = WorkspaceCollections.CopyEnvironment(environment, nameof(environment));
        _shellCommandsBefore = WorkspaceCollections.Copy(shellCommandsBefore, nameof(shellCommandsBefore));
    }

    /// <summary>Gets the environment entries contributed by this declaration.</summary>
    public IReadOnlyDictionary<string, string> Environment => _environment;

    /// <summary>Gets the commands prepended at this declaration level.</summary>
    public IReadOnlyList<string> ShellCommandsBefore => _shellCommandsBefore;

    /// <summary>Returns a declaration with replacement environment and pre-command defaults.</summary>
    /// <param name="environment">The local entries to copy, or null to preserve the existing entries.</param>
    /// <param name="shellCommandsBefore">The local commands to copy, or null to preserve the existing commands.</param>
    /// <returns>A new declaration; empty collections clear the corresponding defaults.</returns>
    /// <exception cref="ArgumentException">An environment name is empty or contains '=' or NUL, or a value contains NUL.</exception>
    public WorkspaceWindow WithDefaults(
        IReadOnlyDictionary<string, string>? environment = null,
        IReadOnlyList<string>? shellCommandsBefore = null) =>
        new(this, environment ?? Environment, shellCommandsBefore ?? ShellCommandsBefore);

    /// <summary>Gets the window name.</summary>
    public string? WindowName { get; }

    /// <summary>Gets the directory its panes start in.</summary>
    public string? StartDirectory { get; }

    /// <summary>Gets the layout to apply after creating its panes.</summary>
    public string? Layout { get; }

    /// <summary>Gets whether the window is left selected.</summary>
    public bool Focus { get; }

    /// <summary>Gets the window options to set.</summary>
    public IReadOnlyDictionary<string, string> Options => _options;

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
    private readonly ReadOnlyCollection<string> _shellCommandsBefore = WorkspaceCollections.Copy<string>(null, nameof(ShellCommandsBefore));

    private readonly ReadOnlyDictionary<string, string> _options;
    private readonly ReadOnlyCollection<WorkspaceWindow> _windows;

    /// <summary>Initializes a workspace description.</summary>
    /// <param name="sessionName">The session name.</param>
    /// <param name="startDirectory">The directory its windows start in.</param>
    /// <param name="options">The session options to set.</param>
    /// <param name="windows">The windows to create, in order.</param>
    public WorkspaceFile(
        string? sessionName = null,
        string? startDirectory = null,
        IReadOnlyDictionary<string, string>? options = null,
        IReadOnlyList<WorkspaceWindow>? windows = null)
    {
        SessionName = sessionName;
        StartDirectory = startDirectory;
        _options = WorkspaceCollections.Copy(options, nameof(options));
        _windows = WorkspaceCollections.Copy(windows, nameof(windows));
    }

    private WorkspaceFile(
        WorkspaceFile source,
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyList<string> shellCommandsBefore)
        : this(source.SessionName, source.StartDirectory, source.Options, source.Windows)
    {
        _environment = WorkspaceCollections.CopyEnvironment(environment, nameof(environment));
        _shellCommandsBefore = WorkspaceCollections.Copy(shellCommandsBefore, nameof(shellCommandsBefore));
        DirectoriesAreResolved = source.DirectoriesAreResolved;
    }

    /// <summary>Gets the environment entries contributed by this declaration.</summary>
    public IReadOnlyDictionary<string, string> Environment => _environment;

    /// <summary>Gets the commands prepended at this declaration level.</summary>
    public IReadOnlyList<string> ShellCommandsBefore => _shellCommandsBefore;

    /// <summary>Returns a declaration with replacement environment and pre-command defaults.</summary>
    /// <param name="environment">The local entries to copy, or null to preserve the existing entries.</param>
    /// <param name="shellCommandsBefore">The local commands to copy, or null to preserve the existing commands.</param>
    /// <returns>A new declaration; empty collections clear the corresponding defaults.</returns>
    /// <exception cref="ArgumentException">An environment name is empty or contains '=' or NUL, or a value contains NUL.</exception>
    public WorkspaceFile WithDefaults(
        IReadOnlyDictionary<string, string>? environment = null,
        IReadOnlyList<string>? shellCommandsBefore = null) =>
        new(this, environment ?? Environment, shellCommandsBefore ?? ShellCommandsBefore);

    /// <summary>Gets the session name.</summary>
    public string? SessionName { get; }

    /// <summary>Gets the directory its windows start in.</summary>
    public string? StartDirectory { get; }

    /// <summary>Gets the session options to set.</summary>
    public IReadOnlyDictionary<string, string> Options => _options;

    /// <summary>Gets the windows to create, in order.</summary>
    public IReadOnlyList<WorkspaceWindow> Windows => _windows;

    internal bool DirectoriesAreResolved { get; init; }

    /// <summary>Resolves inherited pane directories against an explicit document base.</summary>
    /// <param name="baseDirectory">The absolute directory containing the declaration.</param>
    /// <param name="variables">The only variables available to directory expansion.</param>
    /// <returns>A new declaration with absolute inherited directories.</returns>
    /// <remarks>
    /// Resolves relative paths against the parent declaration's directory. Expansion
    /// accepts $NAME, ${NAME}, and a leading ~ using the supplied HOME variable;
    /// $$ produces a literal dollar sign. It reads neither the process environment
    /// nor the filesystem. The builder treats resolved directories as literal paths,
    /// including tmux format characters. Commands, names and option values remain literal.
    /// </remarks>
    /// <exception cref="ArgumentException">The document base is not absolute.</exception>
    /// <exception cref="WorkspaceFormatException">A directory or expansion is invalid.</exception>
    public WorkspaceFile Resolve(
        string baseDirectory,
        IReadOnlyDictionary<string, string>? variables = null) =>
        WorkspacePathResolver.Resolve(this, baseDirectory, variables);

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
