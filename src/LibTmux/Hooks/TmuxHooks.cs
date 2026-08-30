using System.Globalization;
using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

/// <summary>One command a hook runs, and where it sits in the order.</summary>
/// <remarks>
/// The command is kept as tmux prints it. tmux normalises what it is given
/// once, on the way in, and prints the result unchanged from then on, so the
/// printed text can be handed straight back to <c>set-hook</c>.
/// </remarks>
public sealed record TmuxHookEntry
{
    /// <summary>Initializes one hook entry.</summary>
    /// <param name="index">Where the command sits in the hook's order.</param>
    /// <param name="command">The tmux command, as tmux prints it.</param>
    public TmuxHookEntry(int index, string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        Index = index;
        Command = command;
    }

    /// <summary>Gets where the command sits in the hook's order.</summary>
    public int Index { get; }

    /// <summary>Gets the tmux command, as tmux prints it.</summary>
    public string Command { get; }
}

/// <summary>One hook and every command it runs.</summary>
public sealed record TmuxHook
{
    private readonly TmuxHookEntry[] _values;

    /// <summary>Initializes one hook.</summary>
    /// <param name="name">The hook name, without an index.</param>
    /// <param name="values">The commands it runs, in the order tmux reported.</param>
    public TmuxHook(string name, IReadOnlyList<TmuxHookEntry> values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(values);
        Name = name;
        _values = [.. values];
    }

    /// <summary>Gets the hook name, without an index.</summary>
    public string Name { get; }

    /// <summary>Gets the commands it runs, in the order tmux reported.</summary>
    public IReadOnlyList<TmuxHookEntry> Values => _values;
}

/// <summary>The hooks of one server, session, window, or pane.</summary>
/// <remarks>
/// Every hook is an array, even with one entry, so a hook is read as a name and
/// a run of indexed commands rather than as a single value.
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed class TmuxHooks
{
    private readonly TmuxCommandDispatcher _dispatcher;
    private readonly string? _target;

    internal TmuxHooks(
        TmuxCommandDispatcher dispatcher,
        OptionScope scope,
        string? target,
        ServerGeneration? generation = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
        _target = target;
        Scope = scope;
        Generation = generation;
    }

    /// <summary>Gets the server generation this table was reached through.</summary>
    /// <remarks>
    /// A batched hook command carries the target as plain text, so it needs the
    /// generation for the same reason a batched pane command does.
    /// </remarks>
    internal ServerGeneration? Generation { get; }

    /// <summary>Gets the scope these hooks are read and written in by default.</summary>
    public OptionScope Scope { get; }

    /// <summary>Reads one hook.</summary>
    /// <param name="request">Which hook to read.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The hook, or null when nothing is set for that name.</returns>
    /// <exception cref="TmuxOptionException">tmux rejected the hook name.</exception>
    public async Task<TmuxHook?> GetAsync(
        HookRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyList<TmuxHook> hooks = await ReadAsync(
                request.Scope,
                request.Global,
                request.Name,
                cancellationToken)
            .ConfigureAwait(false);
        return hooks.FirstOrDefault(hook =>
            string.Equals(hook.Name, request.Name, StringComparison.Ordinal));
    }

    /// <summary>Reads every hook in the scope.</summary>
    /// <param name="request">Which scope to read, or null for the plain reading.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>Every hook tmux reported, in the order it reported them.</returns>
    public Task<IReadOnlyList<TmuxHook>> GetAllAsync(
        ListHooksRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        request ??= new ListHooksRequest();
        return ReadAsync(request.Scope, request.Global, null, cancellationToken);
    }

    /// <summary>Builds the arguments running a hook sends.</summary>
    /// <remarks>
    /// A hook request names a hook without saying what to do with it, and
    /// running one is a different tmux command from removing it, so each is
    /// built separately rather than by one that has to guess.
    /// </remarks>
    internal List<string> BuildRunArguments(HookRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<string> arguments = ["set-hook"];
        AddScope(arguments, request.Scope);
        AddFlag(arguments, request.Global, "-g");
        arguments.Add("-R");
        AddTarget(arguments, request.Scope);
        arguments.Add(request.Name);
        return arguments;
    }

    /// <summary>Builds the arguments removing a hook sends.</summary>
    internal List<string> BuildUnsetArguments(HookRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<string> arguments = ["set-hook"];
        AddScope(arguments, request.Scope);
        AddFlag(arguments, request.Global, "-g");
        arguments.Add("-u");
        AddTarget(arguments, request.Scope);
        arguments.Add(request.Name);
        return arguments;
    }

    /// <summary>Builds every command a multi-entry hook request sends.</summary>
    /// <remarks>
    /// This one request is several tmux commands: a clear when it was asked
    /// for, then one per entry. Batching them is what a chain is for, so this
    /// answers a list rather than pretending to be a single command.
    /// </remarks>
    internal IReadOnlyList<List<string>> BuildSetAllArguments(SetHooksRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<List<string>> commands = [];
        if (request.ClearExisting)
        {
            commands.Add(BuildSetArguments(new SetHookRequest(
                request.Name,
                string.Empty,
                request.Scope,
                request.Global,
                unset: true)));
        }

        foreach (KeyValuePair<int, string> entry in request.Values.OrderBy(static value => value.Key))
        {
            string indexed = string.Create(
                CultureInfo.InvariantCulture,
                $"{request.Name}[{entry.Key}]");
            List<string> arguments = ["set-hook"];
            AddScope(arguments, request.Scope);
            AddFlag(arguments, request.Global, "-g");
            AddTarget(arguments, request.Scope);
            arguments.Add(indexed);
            arguments.Add(entry.Value);
            commands.Add(arguments);
        }

        return commands;
    }

    /// <summary>Builds the arguments a hook listing sends.</summary>
    internal List<string> BuildListArguments(ListHooksRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        OptionScope? scope = request.Scope;
        bool global = request.Global;
        List<string> arguments = ["show-hooks"];
        AddScope(arguments, scope);
        AddFlag(arguments, global, "-g");
        AddTarget(arguments, scope);

        return arguments;
    }

    /// <summary>Builds the arguments a hook request sends.</summary>
    /// <remarks>
    /// This stays on the hooks handle because which scope flags and target
    /// tmux receives follow from the handle the caller reached for, the same
    /// way they do for options.
    /// </remarks>
    internal List<string> BuildSetArguments(SetHookRequest request)
    {
        List<string> arguments = ["set-hook"];
        AddScope(arguments, request.Scope);
        AddFlag(arguments, request.Global, "-g");
        AddFlag(arguments, request.Unset, "-u");
        AddFlag(arguments, request.RunImmediately, "-R");
        AddFlag(arguments, request.Append, "-a");
        AddTarget(arguments, request.Scope);
        arguments.Add(request.Name);

        // Unsetting and running take no command, and tmux reads one that is
        // given anyway as a stray argument.
        if (!request.Unset && !request.RunImmediately)
        {
            arguments.Add(request.Value);
        }

        return arguments;
    }

    /// <summary>Sets one hook entry.</summary>
    /// <param name="request">Which hook to set, to what, and how.</param>
    /// <param name="cancellationToken">Cancels the tmux commands.</param>
    /// <returns>The hook as tmux holds it afterwards.</returns>
    /// <exception cref="TmuxOptionException">tmux rejected the hook or its command.</exception>
    public async Task<TmuxHook> SetAsync(
        SetHookRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<string> arguments = BuildSetArguments(request);

        return await TmuxMutationSequence.RunAsync(
                () => DispatchAsync(arguments, request.Name, cancellationToken),
                () => ReadBackAsync(
                    request.Name,
                    request.Scope,
                    request.Global,
                    cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Sets several entries of one hook.</summary>
    /// <param name="request">Which hook to set, to what, and how.</param>
    /// <param name="cancellationToken">Cancels the tmux commands.</param>
    /// <returns>The hook as tmux holds it afterwards.</returns>
    /// <exception cref="TmuxOptionException">tmux rejected the hook or a command.</exception>
    public async Task<TmuxHook> SetAsync(
        SetHooksRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sequence = new TmuxMutationSequence();
        if (request.ClearExisting)
        {
            await sequence.MutateAsync(() => SetAsync(
                new SetHookRequest(
                    request.Name,
                    string.Empty,
                    request.Scope,
                    request.Global,
                    unset: true),
                cancellationToken))
                .ConfigureAwait(false);
        }

        foreach (KeyValuePair<int, string> entry in request.Values.OrderBy(static value => value.Key))
        {
            string indexed = string.Create(
                CultureInfo.InvariantCulture,
                $"{request.Name}[{entry.Key}]");
            List<string> arguments = ["set-hook"];
            AddScope(arguments, request.Scope);
            AddFlag(arguments, request.Global, "-g");
            AddTarget(arguments, request.Scope);
            arguments.Add(indexed);
            arguments.Add(entry.Value);
            await sequence
                .MutateAsync(() => DispatchAsync(arguments, request.Name, cancellationToken))
                .ConfigureAwait(false);
        }

        return await sequence
            .ObserveAsync(() => ReadBackAsync(
                request.Name,
                request.Scope,
                request.Global,
                cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Runs a hook's commands now, without waiting for it to fire.</summary>
    /// <param name="request">Which hook to run.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <exception cref="TmuxOptionException">tmux rejected the hook name.</exception>
    public Task RunAsync(HookRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return DispatchAsync(BuildRunArguments(request), request.Name, cancellationToken);
    }

    /// <summary>Removes a hook.</summary>
    /// <param name="request">Which hook to remove.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <exception cref="TmuxOptionException">tmux rejected the hook name.</exception>
    public Task UnsetAsync(HookRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<string> arguments = ["set-hook"];
        AddScope(arguments, request.Scope);
        AddFlag(arguments, request.Global, "-g");
        arguments.Add("-u");
        AddTarget(arguments, request.Scope);
        arguments.Add(request.Name);
        return DispatchAsync(arguments, request.Name, cancellationToken);
    }

    private static void AddFlag(List<string> arguments, bool wanted, string flag)
    {
        if (wanted)
        {
            arguments.Add(flag);
        }
    }

    private static List<TmuxHook> Group(IReadOnlyList<string> lines)
    {
        List<TmuxHook> hooks = [];
        List<TmuxHookEntry> entries = [];
        string? current = null;
        foreach (string line in lines)
        {
            if (ReadEntry(line) is not (string name, TmuxHookEntry entry))
            {
                continue;
            }

            if (current is not null && !string.Equals(current, name, StringComparison.Ordinal))
            {
                hooks.Add(new TmuxHook(current, entries));
                entries = [];
            }

            current = name;
            entries.Add(entry);
        }

        if (current is not null)
        {
            hooks.Add(new TmuxHook(current, entries));
        }

        return hooks;
    }

    private static (string Name, TmuxHookEntry Entry)? ReadEntry(string line)
    {
        int separator = line.IndexOf(' ', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return null;
        }

        string name = line[..separator];

        // The command is kept exactly as printed. Unescaping it would leave
        // text that no longer parses as the command it describes.
        string command = line[(separator + 1)..];
        int index = 0;
        if (name.Length > 2 && name[^1] == ']')
        {
            int open = name.LastIndexOf('[');
            if (open > 0
                && int.TryParse(
                    name.AsSpan(open + 1, name.Length - open - 2),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int parsed))
            {
                index = parsed;
                name = name[..open];
            }
        }

        return name.Length == 0 ? null : (name, new TmuxHookEntry(index, command));
    }

    private void AddScope(List<string> arguments, OptionScope? scope)
    {
        // tmux reaches session hooks with no flag at all, and server hooks are
        // the global ones, so only two scopes name themselves.
        string flag = CommandFlagCatalog.GetHookScopeFlag(scope ?? Scope);
        if (flag.Length > 0)
        {
            arguments.Add(flag);
        }
    }

    private void AddTarget(List<string> arguments, OptionScope? scope)
    {
        OptionScope effective = scope ?? Scope;
        if (_target is null || effective == OptionScope.Server)
        {
            return;
        }

        arguments.Add("-t");
        arguments.Add(_target);
    }

    private async Task DispatchAsync(
        List<string> arguments,
        string hookName,
        CancellationToken cancellationToken)
    {
        TmuxCommandResult result = await _dispatcher
            .ExecuteAsync(arguments, cancellationToken)
            .ConfigureAwait(false);
        OptionFailure.ThrowIfFailed(result, hookName);
    }

    private async Task<IReadOnlyList<TmuxHook>> ReadAsync(
        OptionScope? scope,
        bool global,
        string? name,
        CancellationToken cancellationToken)
    {
        List<string> arguments = ["show-hooks"];
        AddScope(arguments, scope);
        AddFlag(arguments, global, "-g");
        AddTarget(arguments, scope);

        TmuxCommandResult result = await _dispatcher
            .ExecuteAsync(arguments, cancellationToken)
            .ConfigureAwait(false);
        OptionFailure.ThrowIfFailed(result, name ?? "show-hooks");
        return Group(result.StandardOutputLines);
    }

    private async Task<TmuxHook> ReadBackAsync(
        string name,
        OptionScope? scope,
        bool global,
        CancellationToken cancellationToken)
    {
        // The name may carry an index, and what comes back is the whole hook.
        int bracket = name.IndexOf('[', StringComparison.Ordinal);
        string bare = bracket > 0 ? name[..bracket] : name;
        TmuxHook? hook = await GetAsync(new HookRequest(bare, scope, global), cancellationToken)
            .ConfigureAwait(false);
        return hook ?? new TmuxHook(bare, []);
    }
}
