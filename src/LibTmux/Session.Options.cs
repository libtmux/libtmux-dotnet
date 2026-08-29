using System.Runtime.Versioning;

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
        TmuxOptions.DoubleEscapesDollar(_owner));
}
