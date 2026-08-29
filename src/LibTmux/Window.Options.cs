using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

// Reaches this window's option table.
public sealed partial class Window
{
    private TmuxOptions? _options;

    /// <summary>Gets the options of this window.</summary>
    /// <remarks>
    /// tmux once spelled these <c>set-window-option</c> and
    /// <c>show-window-options</c>. They are the ordinary option commands with
    /// the window flag, which is what this scope carries.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public TmuxOptions Options => _options ??= new TmuxOptions(
        _commandDispatcher,
        OptionScope.Window,
        _id.ToString(),
        DoubleEscapesDollar(Server));

    private static bool DoubleEscapesDollar(Server? owner) =>
        owner?.Version is TmuxVersion version
        && TmuxCapabilities.IsSupported(version, "option_dollar_double_escape");
}
