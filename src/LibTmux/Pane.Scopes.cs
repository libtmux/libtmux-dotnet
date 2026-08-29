using System.Runtime.Versioning;

namespace LibTmux;

// Reaches the option and hook tables this pane scopes.
public sealed partial class Pane
{
    private TmuxOptions? _options;

    /// <summary>Gets the options of this pane.</summary>
    [UnsupportedOSPlatform("windows")]
    public TmuxOptions Options => _options ??= new TmuxOptions(
        _commandDispatcher,
        OptionScope.Pane,
        _id.ToString(),
        TmuxOptions.DoubleEscapesDollar(_owner));

    private TmuxHooks? _hooks;

    /// <summary>Gets the hooks of this pane.</summary>
    [UnsupportedOSPlatform("windows")]
    public TmuxHooks Hooks => _hooks ??= new TmuxHooks(
        _commandDispatcher,
        OptionScope.Pane,
        _id.ToString());
}
