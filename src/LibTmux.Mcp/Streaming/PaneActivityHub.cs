using System.Runtime.Versioning;
using LibTmux.Internal;
using Microsoft.Extensions.Logging;

namespace LibTmux.Mcp;

/// <summary>Shares one control-mode observation client per watched session.</summary>
/// <remarks>
/// This MCP-facing lifetime keeps its client names available to the human-client
/// filter. The observation and wakeup rules live in the shared library.
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed class PaneActivityHub : IAsyncDisposable
{
    private readonly PaneActivitySource _source;

    /// <summary>Initializes pane observation for the MCP server.</summary>
    public PaneActivityHub(ILogger? logger = null, bool allowPollingFallback = false) =>
        _source = new PaneActivitySource(logger, allowPollingFallback);

    internal PaneActivityHub(
        Func<Pane, CancellationToken, Task<IControlModeSession>> startPaneSession,
        ILogger? logger = null,
        bool allowPollingFallback = false,
        TimeProvider? timeProvider = null) =>
        _source = new PaneActivitySource(
            startPaneSession, logger, allowPollingFallback, timeProvider);

    internal PaneActivitySource Source => _source;

    internal static TimeSpan PollInterval => PaneActivitySource.PollInterval;

    /// <summary>Gets whether a session is currently watched through control mode.</summary>
    public bool IsStreaming => _source.IsStreaming;

    /// <summary>Reports whether this hub opened the named control client.</summary>
    public bool OwnsControlClient(string clientName) => _source.OwnsControlClient(clientName);

    /// <summary>Watches a pane's session until the returned lease is disposed.</summary>
    public Task<IAsyncDisposable> WatchAsync(Pane pane, CancellationToken cancellationToken) =>
        _source.WatchAsync(pane, cancellationToken);

    internal Task<IAsyncDisposable> WatchAsync(
        string sessionId,
        Func<CancellationToken, Task<IControlModeSession>> startSession,
        CancellationToken cancellationToken) =>
        _source.WatchAsync(sessionId, startSession, cancellationToken);

    internal Task<IAsyncDisposable> WatchAsync(
        string endpointId,
        string sessionId,
        Func<CancellationToken, Task<IControlModeSession>> startSession,
        CancellationToken cancellationToken) =>
        _source.WatchAsync(endpointId, sessionId, startSession, cancellationToken);

    /// <summary>Waits for pane activity or until the timeout expires.</summary>
    public Task<bool> WaitForActivityAsync(
        string paneId,
        object? signalBefore,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        _source.WaitForActivityAsync(paneId, signalBefore, timeout, cancellationToken);

    internal bool RequireObservation(object? signal) => _source.RequireObservation(signal);

    internal static long EventsDropped(IAsyncDisposable lease) => PaneActivitySource.EventsDropped(lease);

    /// <summary>Captures a wake token before a pane read.</summary>
    public object? CaptureSignal(Pane pane) => _source.CaptureSignal(pane);

    /// <summary>Captures a wake token when exactly one session is watched.</summary>
    public object? CaptureSignal(string paneId) => _source.CaptureSignal(paneId);

    internal Task? CaptureSignal(string endpointId, string sessionId, string paneId) =>
        _source.CaptureSignal(endpointId, sessionId, paneId);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _source.DisposeAsync();
}
