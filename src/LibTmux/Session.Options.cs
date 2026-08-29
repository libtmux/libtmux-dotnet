using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

// Reaches this session's option table.
public sealed partial class Session
{
    private TmuxOptions? _options;

    /// <summary>Gets the options of this session.</summary>
    [UnsupportedOSPlatform("windows")]
    public TmuxOptions Options => _options ??= new TmuxOptions(
        _commandDispatcher,
        OptionScope.Session,
        _id.ToString(),
        DoubleEscapesDollar(Server));

    private static bool DoubleEscapesDollar(Server? owner) =>
        owner?.Version is TmuxVersion version
        && TmuxCapabilities.IsSupported(version, "option_dollar_double_escape");
}
