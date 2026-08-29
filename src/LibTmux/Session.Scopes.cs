using System.Runtime.Versioning;

namespace LibTmux;

// Reaches the option, hook and environment tables this session scopes.
public sealed partial class Session
{
    private TmuxOptions? _options;

    /// <summary>Gets the options of this session.</summary>
    [UnsupportedOSPlatform("windows")]
    public TmuxOptions Options => _options ??= new TmuxOptions(
        _commandDispatcher,
        OptionScope.Session,
        _id.ToString(),
        TmuxOptions.DoubleEscapesDollar(_owner));

    private TmuxHooks? _hooks;

    /// <summary>Gets the hooks of this session.</summary>
    [UnsupportedOSPlatform("windows")]
    public TmuxHooks Hooks => _hooks ??= new TmuxHooks(
        _commandDispatcher,
        OptionScope.Session,
        _id.ToString());

    private TmuxEnvironment? _environment;

    /// <summary>Gets the environment panes created in this session inherit from.</summary>
    [UnsupportedOSPlatform("windows")]
    public TmuxEnvironment Environment => _environment ??= new TmuxEnvironment(
        _commandDispatcher,
        global: false,
        target: _id.ToString());
}
