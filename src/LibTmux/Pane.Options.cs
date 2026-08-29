using System.Runtime.Versioning;
using LibTmux.Internal;

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
        DoubleEscapesDollar(Server));

    private static bool DoubleEscapesDollar(Server? owner) =>
        owner?.Version is TmuxVersion version
        && TmuxCapabilities.IsSupported(version, "option_dollar_double_escape");
}
