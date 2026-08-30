using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

/// <summary>The options of one server, session, window, or pane.</summary>
/// <remarks>
/// tmux keeps four option tables and picks between them with a flag, so the
/// scope is fixed when the accessor is reached rather than passed to every
/// call. A request may still name another scope, which is what makes a
/// window-scoped read from a session possible without a second accessor.
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed class TmuxOptions
{
    private readonly TmuxCommandDispatcher _dispatcher;
    private readonly string? _target;
    private readonly bool _doubleEscapedDollar;

    /// <summary>Reports whether a tmux escapes a dollar sign twice in an option value.</summary>
    /// <param name="owner">The server answering, or null when it is not known.</param>
    internal static bool DoubleEscapesDollar(Server? owner) =>
        owner?.Version is TmuxVersion version
        && TmuxCapabilities.IsSupported(version, "option_dollar_double_escape");

    internal TmuxOptions(
        TmuxCommandDispatcher dispatcher,
        OptionScope scope,
        string? target,
        bool doubleEscapedDollar = false,
        ServerGeneration? generation = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
        _target = target;
        _doubleEscapedDollar = doubleEscapedDollar;
        Scope = scope;
        Generation = generation;
    }

    /// <summary>Gets the server generation this table was reached through.</summary>
    /// <remarks>
    /// A batched option command carries the target as plain text, so it needs
    /// the generation for the same reason a batched pane command does.
    /// </remarks>
    internal ServerGeneration? Generation { get; }

    /// <summary>Gets the scope these options are read and written in by default.</summary>
    public OptionScope Scope { get; }

    /// <summary>Builds the arguments a named read sends.</summary>
    internal List<string> BuildGetArguments(GetOptionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<string> arguments = ["show-options"];
        AddReadFlags(arguments, request.Scope, request.Global, request.IncludeHooks, request.IncludeInherited, request.Quiet);
        AddTarget(arguments, request.Scope);
        arguments.Add(request.Name);

        return arguments;
    }

    /// <summary>Reads one option.</summary>
    /// <param name="request">Which option to read, and how.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>
    /// Every entry tmux reported for the name: one for an ordinary option, and
    /// one per set index for an array.
    /// </returns>
    /// <exception cref="TmuxOptionException">tmux rejected the option name.</exception>
    public async Task<IReadOnlyList<TmuxOption>> GetAsync(
        GetOptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<string> arguments = BuildGetArguments(request);
        return OptionParser.ParseRows(
            await ReadAsync(arguments, request.Name, cancellationToken).ConfigureAwait(false),
            _doubleEscapedDollar);
    }

    /// <summary>Builds the arguments a whole-scope read sends.</summary>
    internal List<string> BuildGetAllArguments(GetOptionsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<string> arguments = ["show-options"];
        AddReadFlags(arguments, request.Scope, request.Global, request.IncludeHooks, request.IncludeInherited, request.Quiet);
        AddTarget(arguments, request.Scope);

        return arguments;
    }

    /// <summary>Reads every option in the scope.</summary>
    /// <param name="request">How to read them, or null for the plain reading.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>Every option tmux reported, in the order it reported them.</returns>
    /// <exception cref="TmuxOptionException">tmux rejected the request.</exception>
    public async Task<IReadOnlyList<TmuxOption>> GetAllAsync(
        GetOptionsRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        request ??= new GetOptionsRequest();
        List<string> arguments = BuildGetAllArguments(request);
        return OptionParser.ParseRows(
            await ReadAsync(arguments, "show-options", cancellationToken).ConfigureAwait(false),
            _doubleEscapedDollar);
    }

    /// <summary>Builds the arguments a set request sends.</summary>
    /// <remarks>
    /// This stays on the options handle because which scope flags and target
    /// tmux receives depends on the scope this handle was made for, not on the
    /// request alone.
    /// </remarks>
    internal List<string> BuildSetArguments(SetOptionRequest request)
    {
        List<string> arguments = ["set-option"];
        AddScope(arguments, request.Scope);
        AddFlag(arguments, request.Global, "-g");
        AddFlag(arguments, request.ExpandFormat, "-F");
        AddFlag(arguments, request.PreventOverwrite, "-o");
        AddFlag(arguments, request.Quiet, "-q");
        AddFlag(arguments, request.Append, "-a");
        AddTarget(arguments, request.Scope);
        arguments.Add(request.Name);
        arguments.Add(request.Value);

        return arguments;
    }

    /// <summary>Sets one option.</summary>
    /// <param name="request">Which option to set, to what, and how.</param>
    /// <param name="cancellationToken">Cancels the tmux commands.</param>
    /// <returns>The value tmux holds afterwards.</returns>
    /// <exception cref="TmuxOptionException">tmux rejected the option or its value.</exception>
    /// <remarks>
    /// The value is read back rather than echoed, because tmux does not always
    /// store what it was given: an appended value joins what was there, and a
    /// format is expanded before it lands.
    /// </remarks>
    public async Task<TmuxOptionValue> SetAsync(
        SetOptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<string> arguments = BuildSetArguments(request);

        var sequence = new TmuxMutationSequence();
        _ = await sequence.MutateAsync(
                () => _dispatcher.ExecuteAsync(arguments, cancellationToken),
                value => OptionFailure.ThrowIfFailed(value, request.Name))
            .ConfigureAwait(false);

        IReadOnlyList<TmuxOption> stored = await sequence
            .ObserveAsync(() => GetAsync(
                new GetOptionRequest(request.Name, request.Scope, request.Global, quiet: true),
                cancellationToken))
            .ConfigureAwait(false);
        return sequence.Observe(() =>
            stored.Count > 0
                ? stored[^1].Value
                : new TmuxOptionValue(null, TmuxOptionState.Absent, null, null));
    }

    /// <summary>Builds the arguments an unset request sends.</summary>
    /// <remarks>
    /// Like setting, this stays on the options handle: which scope flags and
    /// target tmux receives follow from the handle rather than the request.
    /// </remarks>
    internal List<string> BuildUnsetArguments(UnsetOptionRequest request)
    {
        List<string> arguments = ["set-option"];
        AddScope(arguments, request.Scope);
        AddFlag(arguments, request.Global, "-g");
        AddFlag(arguments, request.Quiet, "-q");

        // tmux spells the wider unset with a capital, and it means the same
        // thing one level down: drop what every pane overrode as well.
        arguments.Add(request.UnsetPaneOverrides ? "-U" : "-u");
        AddTarget(arguments, request.Scope);
        arguments.Add(request.Name);

        return arguments;
    }

    /// <summary>Unsets one option, returning it to what it inherits.</summary>
    /// <param name="request">Which option to unset, and how.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <exception cref="TmuxOptionException">tmux rejected the option name.</exception>
    public async Task UnsetAsync(
        UnsetOptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<string> arguments = BuildUnsetArguments(request);

        TmuxCommandResult result = await _dispatcher
            .ExecuteAsync(arguments, cancellationToken)
            .ConfigureAwait(false);
        OptionFailure.ThrowIfFailed(result, request.Name);
    }

    private static void AddFlag(List<string> arguments, bool wanted, string flag)
    {
        if (wanted)
        {
            arguments.Add(flag);
        }
    }

    private void AddReadFlags(
        List<string> arguments,
        OptionScope? scope,
        bool global,
        bool includeHooks,
        bool includeInherited,
        bool quiet)
    {
        AddScope(arguments, scope);
        AddFlag(arguments, global, "-g");
        AddFlag(arguments, includeHooks, "-H");
        AddFlag(arguments, includeInherited, "-A");
        AddFlag(arguments, quiet, "-q");
    }

    private void AddScope(List<string> arguments, OptionScope? scope)
    {
        // The session table is the one tmux reaches without a flag, so naming
        // it means saying nothing.
        string flag = CommandFlagCatalog.GetOptionScopeFlag(scope ?? Scope);
        if (flag.Length > 0)
        {
            arguments.Add(flag);
        }
    }

    private void AddTarget(List<string> arguments, OptionScope? scope)
    {
        // Server options belong to no object, and tmux refuses a target for
        // them even when one would be harmless elsewhere.
        if (_target is null || (scope ?? Scope) == OptionScope.Server)
        {
            return;
        }

        arguments.Add("-t");
        arguments.Add(_target);
    }

    private async Task<IReadOnlyList<string>> ReadAsync(
        List<string> arguments,
        string optionName,
        CancellationToken cancellationToken)
    {
        TmuxCommandResult result = await _dispatcher
            .ExecuteAsync(arguments, cancellationToken)
            .ConfigureAwait(false);
        OptionFailure.ThrowIfFailed(result, optionName);
        return result.StandardOutputLines;
    }
}
