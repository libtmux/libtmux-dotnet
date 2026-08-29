using System.Runtime.Versioning;

namespace LibTmux;

// Reaches the option, hook and environment tables this server scopes.
public sealed partial class Server
{
    private TmuxOptions? _options;

    /// <summary>Gets the options of this server.</summary>
    /// <remarks>
    /// Server options belong to the daemon rather than to anything inside it,
    /// so nothing is targeted and the global table is the only one there is.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public TmuxOptions Options => _options ??= new TmuxOptions(
        _commandDispatcher,
        OptionScope.Server,
        null,
        TmuxOptions.DoubleEscapesDollar(this));

    private TmuxHooks? _hooks;

    /// <summary>Gets the hooks of this server.</summary>
    /// <remarks>
    /// tmux has no server hook table of its own: the global one is it, which is
    /// why these are reached with the global flag rather than a server flag.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public TmuxHooks Hooks => _hooks ??= new TmuxHooks(
        _commandDispatcher,
        OptionScope.Server,
        null);

    private TmuxEnvironment? _environment;

    /// <summary>Gets the environment new sessions inherit from.</summary>
    [UnsupportedOSPlatform("windows")]
    public TmuxEnvironment Environment => _environment ??= new TmuxEnvironment(
        _commandDispatcher,
        global: true,
        target: null);
}
