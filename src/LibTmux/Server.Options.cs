using System.Runtime.Versioning;

namespace LibTmux;

// Reaches the server's own option table.
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
}
