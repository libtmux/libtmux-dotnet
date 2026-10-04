using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

// Reaches the option, hook and environment tables this session scopes, and
// the control client waits on its panes share.
public sealed partial class Session
{
    /// <summary>Keeps the control client that waits on this session's panes use attached until the handle is disposed.</summary>
    /// <param name="cancellationToken">Cancels attaching the client.</param>
    /// <returns>A handle; disposing it lets the client detach once no wait needs it.</returns>
    /// <remarks>
    /// <para>
    /// A wait on a pane attaches a control client to the pane's session and
    /// reads through it, and lets it go when the wait ends unless another wait
    /// still needs it. Attaching costs about as much as a tmux process start,
    /// so holding the client saves that for each wait in a series, which then
    /// costs little more than the keys it sends.
    /// </para>
    /// <para>
    /// The client ignores its size, so it leaves window sizes alone, and is
    /// excluded wherever this library tells observers from people attached.
    /// tmux itself still lists it among the session's clients while it is
    /// held. When control mode is unavailable the waits poll, and the handle
    /// holds nothing.
    /// </para>
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public Task<IAsyncDisposable> HoldWaitClientAsync(CancellationToken cancellationToken = default) =>
        PaneActivityHub.Shared.WatchAsync(this, cancellationToken);

    private TmuxOptions? _options;

    /// <summary>Gets the options of this session.</summary>
    [UnsupportedOSPlatform("windows")]
    public TmuxOptions Options => _options ??= new TmuxOptions(
        _commandDispatcher,
        OptionScope.Session,
        _id.ToString(),
        _owner,
        _generation);

    private TmuxHooks? _hooks;

    /// <summary>Gets the hooks of this session.</summary>
    [UnsupportedOSPlatform("windows")]
    public TmuxHooks Hooks => _hooks ??= new TmuxHooks(
        _commandDispatcher,
        OptionScope.Session,
        _id.ToString(),
        _generation);

    private TmuxEnvironment? _environment;

    /// <summary>Gets the environment panes created in this session inherit from.</summary>
    [UnsupportedOSPlatform("windows")]
    public TmuxEnvironment Environment => _environment ??= new TmuxEnvironment(
        _commandDispatcher,
        global: false,
        target: _id.ToString());
}
