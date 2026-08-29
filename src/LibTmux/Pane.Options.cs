using System.Runtime.Versioning;

namespace LibTmux;

// Reaches this pane's option table.
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
}
