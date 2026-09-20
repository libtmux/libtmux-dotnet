using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

/// <summary>One variable in a tmux environment.</summary>
/// <remarks>
/// tmux keeps three states, not two. A variable can hold a value, be absent, or
/// be marked removed, which tells tmux to strip it from the environment of
/// panes it spawns even though the parent process has it.
/// </remarks>
public sealed record TmuxEnvironmentEntry
{
    /// <summary>Initializes one environment variable.</summary>
    /// <param name="name">The variable name.</param>
    /// <param name="value">The value, or null when the variable is marked removed.</param>
    /// <param name="isRemoved">Whether tmux strips this variable from new panes.</param>
    public TmuxEnvironmentEntry(string name, string? value, bool isRemoved)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Value = value;
        IsRemoved = isRemoved;
    }

    /// <summary>Gets the variable name.</summary>
    public string Name { get; }

    /// <summary>Gets the value, or null when the variable is marked removed.</summary>
    public string? Value { get; }

    /// <summary>Gets whether tmux strips this variable from new panes.</summary>
    public bool IsRemoved { get; }
}

/// <summary>The environment tmux gives to the processes it spawns.</summary>
/// <remarks>
/// A server has one environment and each session another. Which one a new pane
/// inherits depends on where it is created, so the two are reached separately
/// rather than merged.
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed class TmuxEnvironment
{
    private readonly TmuxCommandDispatcher _dispatcher;
    private readonly bool _global;
    private readonly string? _target;

    internal TmuxEnvironment(TmuxCommandDispatcher dispatcher, bool global, string? target)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
        _global = global;
        _target = target;
    }

    /// <summary>Reads every variable in this environment.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>Every variable tmux reported, in the order it reported them.</returns>
    /// <remarks>
    /// Variables set hidden are not reported. tmux keeps them for the panes it
    /// spawns but will not read them back out.
    /// </remarks>
    public async Task<IReadOnlyList<TmuxEnvironmentEntry>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        TmuxCommandResult result = await RunAsync(Build("show-environment"), cancellationToken)
            .ConfigureAwait(false);
        TmuxCommandFailure.ThrowIfFailed(result, "show-environment");
        List<TmuxEnvironmentEntry> entries = [];
        foreach (string line in result.StandardOutputLines)
        {
            if (Read(line) is TmuxEnvironmentEntry entry)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    /// <summary>Reads one variable.</summary>
    /// <param name="name">The variable name.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The variable, or null when this environment has no such name.</returns>
    public async Task<TmuxEnvironmentEntry?> GetAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        List<string> arguments = Build("show-environment");
        ServerUtilities.EndOptions(arguments);
        arguments.Add(name);
        TmuxCommandResult result = await RunAsync(arguments, cancellationToken)
            .ConfigureAwait(false);

        if (NamesMissingVariable(result, name))
        {
            return null;
        }

        TmuxCommandFailure.ThrowIfFailed(result, "show-environment");

        // A hidden name succeeds and says nothing; only that and the exact
        // missing-variable result are ordinary absence answers.
        return result.StandardOutputLines.Count > 0
            ? Read(result.StandardOutputLines[0])
            : null;
    }

    /// <summary>Sets one variable.</summary>
    /// <param name="name">The variable name.</param>
    /// <param name="value">The value to give it.</param>
    /// <param name="expandFormats">Whether tmux expands the value as a format first.</param>
    /// <param name="hidden">Whether the value is kept but not reported back.</param>
    /// <param name="cancellationToken">Cancels the tmux commands.</param>
    /// <returns>The variable as tmux holds it afterwards.</returns>
    public async Task<TmuxEnvironmentEntry> SetAsync(
        string name,
        string value,
        bool expandFormats = false,
        bool hidden = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        List<string> arguments = Build("set-environment");
        if (expandFormats)
        {
            arguments.Add("-F");
        }

        if (hidden)
        {
            arguments.Add("-h");
        }

        ServerUtilities.EndOptions(arguments);
        arguments.Add(name);
        arguments.Add(value);
        var sequence = new TmuxMutationSequence();
        _ = await sequence.MutateAsync(
                () => RunAsync(arguments, cancellationToken),
                static value => TmuxCommandFailure.ThrowIfFailed(value, "set-environment"))
            .ConfigureAwait(false);

        // A hidden variable cannot be read back; visible values are returned
        // exactly as tmux stored them, including any format expansion.
        TmuxEnvironmentEntry? stored = await sequence
            .ObserveAsync(() => GetAsync(name, cancellationToken))
            .ConfigureAwait(false);
        return sequence.Observe(() =>
            stored ?? (hidden
                ? new TmuxEnvironmentEntry(name, null, false)
                : throw new TmuxProtocolException(
                    $"tmux did not report the stored environment variable '{name}'.",
                    TmuxDispatchState.Dispatched)));
    }

    /// <summary>Marks a variable removed for the panes tmux spawns.</summary>
    /// <param name="name">The variable name.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <remarks>
    /// This is not the same as unsetting. tmux remembers the removal so that a
    /// pane it spawns does not inherit the variable from its own parent.
    /// </remarks>
    public async Task RemoveAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        List<string> arguments = Build("set-environment");
        arguments.Add("-r");
        ServerUtilities.EndOptions(arguments);
        arguments.Add(name);
        TmuxCommandResult result = await RunAsync(arguments, cancellationToken)
            .ConfigureAwait(false);
        TmuxCommandFailure.ThrowIfFailed(result, "set-environment");
    }

    /// <summary>Forgets a variable entirely.</summary>
    /// <param name="name">The variable name.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    public async Task UnsetAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        List<string> arguments = Build("set-environment");
        arguments.Add("-u");
        ServerUtilities.EndOptions(arguments);
        arguments.Add(name);
        TmuxCommandResult result = await RunAsync(arguments, cancellationToken)
            .ConfigureAwait(false);
        TmuxCommandFailure.ThrowIfFailed(result, "set-environment");
    }

    private static TmuxEnvironmentEntry? Read(string line)
    {
        if (line.Length == 0)
        {
            return null;
        }

        int separator = line.IndexOf('=', StringComparison.Ordinal);

        // tmux writes a removed variable as its name behind a minus sign, with
        // no value to write.
        if (line[0] == '-' && separator < 0)
        {
            return line.Length > 1 ? new TmuxEnvironmentEntry(line[1..], null, true) : null;
        }

        // Values are printed as they are held, so everything past the first
        // equals sign belongs to the value, spaces and all.
        return separator > 0
            ? new TmuxEnvironmentEntry(line[..separator], line[(separator + 1)..], false)
            : new TmuxEnvironmentEntry(line, null, false);
    }

    private static bool NamesMissingVariable(TmuxCommandResult result, string name) =>
        result.ExitCode == 1
        && result.StandardOutputLines.Count == 0
        && result.StandardErrorLines.Count == 1
        && string.Equals(
            result.StandardErrorLines[0],
            $"unknown variable: {name}",
            StringComparison.Ordinal);

    private List<string> Build(string subcommand)
    {
        List<string> arguments = [subcommand];
        if (_global)
        {
            arguments.Add("-g");
        }

        if (_target is not null)
        {
            arguments.Add("-t");
            arguments.Add(_target);
        }

        return arguments;
    }

    private Task<TmuxCommandResult> RunAsync(
        List<string> arguments,
        CancellationToken cancellationToken) =>
        _dispatcher.ExecuteAsync(arguments, cancellationToken);
}
