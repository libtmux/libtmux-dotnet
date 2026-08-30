using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace LibTmux.Mcp;

/// <summary>Tells subscribed clients when the tmux hierarchy changed.</summary>
/// <remarks>
/// <para>
/// tmux reports a window appearing, a session changing or a layout moving to a
/// client in control mode, without being asked. Forwarding those as resource
/// updates means a client's view of the hierarchy invalidates itself, instead
/// of being re-listed on a timer in case something moved.
/// </para>
/// <para>
/// One control client per exact server generation, started only once somebody
/// subscribes, and stopped when its last subscriber goes. It attaches with
/// <c>no-output</c> as well as <c>ignore-size</c>: this one wants to hear about
/// the hierarchy and not about every byte a pane prints, and tmux will keep
/// the pane traffic out of the stream if asked.
/// </para>
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed class HierarchyWatcher : IAsyncDisposable
{
    /// <summary>The notifications that mean the hierarchy is not what it was.</summary>
    /// <remarks>
    /// Named rather than "anything tmux says": a bell or an activity flag is
    /// not a change to what exists, and waking every subscriber for one would
    /// make the subscription cost more than the polling it replaces.
    /// </remarks>
    private static readonly HashSet<string> Structural = new(StringComparer.Ordinal)
    {
        "session-changed",
        "session-renamed",
        "session-window-changed",
        "sessions-changed",
        "window-add",
        "window-close",
        "window-renamed",
        "window-pane-changed",
        "layout-change",
        "unlinked-window-add",
        "unlinked-window-close",
        "pane-mode-changed",
        "client-detached",
        "client-session-changed",
    };

    private readonly object _endpointsGate = new();
    private readonly Dictionary<HierarchyWatchKey, HierarchyEndpointWatch> _endpoints = [];
    private readonly ILogger? _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<CancellationToken, Task>? _beforeRecoveryOutcome;
    private readonly Action<bool>? _recoveryOutcomeObserved;
    private Task? _disposeTask;
    private bool _disposed;

    /// <summary>Initializes the watcher.</summary>
    /// <param name="logger">Records why a control client could not start.</param>
    public HierarchyWatcher(ILogger? logger = null)
        : this(logger, static (delay, token) => Task.Delay(delay, token), null, null)
    {
    }

    internal HierarchyWatcher(
        ILogger? logger,
        Func<TimeSpan, CancellationToken, Task> delay,
        Func<CancellationToken, Task>? beforeRecoveryOutcome = null,
        Action<bool>? recoveryOutcomeObserved = null)
    {
        ArgumentNullException.ThrowIfNull(delay);
        _logger = logger;
        _delay = delay;
        _beforeRecoveryOutcome = beforeRecoveryOutcome;
        _recoveryOutcomeObserved = recoveryOutcomeObserved;
    }

    /// <summary>Gets the resource URIs this watcher will notify about.</summary>
    /// <remarks>
    /// The ones whose content a structural change can alter. A pane's text
    /// changes constantly and is not a structural change, so it is not here:
    /// a subscription that fired on every keystroke would be a firehose.
    /// </remarks>
    public static IReadOnlyList<string> Watchable { get; } =
    [
        "tmux://hierarchy",
        "tmux://sessions",
        "tmux://servers",
    ];

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_endpointsGate)
        {
            if (_disposeTask is null)
            {
                _disposed = true;
                HierarchyEndpointWatch[] endpoints = [.. _endpoints.Values];
                _endpoints.Clear();
                _disposeTask = DisposeEndpointsAsync(endpoints);
            }

            return new ValueTask(_disposeTask);
        }
    }

    private static Task DisposeEndpointsAsync(HierarchyEndpointWatch[] endpoints) =>
        Task.WhenAll(endpoints.Select(endpoint => endpoint.DisposeAsync().AsTask()));

    /// <summary>Starts reporting changes to one resource.</summary>
    /// <param name="uri">The resource the client subscribed to.</param>
    /// <param name="announce">
    /// Told which resources changed. Passed in rather than known, because how a
    /// change reaches a client is the protocol's business and moves with it —
    /// the revision that replaced <c>resources/subscribe</c> with
    /// <c>subscriptions/listen</c> changed the delivery and not this.
    /// </param>
    /// <param name="tmux">The tmux server to watch.</param>
    /// <param name="cancellationToken">Cancels starting the control client.</param>
    public async Task SubscribeAsync(
        string uri,
        Func<IReadOnlyList<string>, Task> announce,
        Server tmux,
        CancellationToken cancellationToken)
    {
        await SubscribeAsync(uri, announce, announce, tmux, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Starts one independently owned subscription.</summary>
    internal async Task SubscribeAsync(
        string uri,
        object subscriberKey,
        Func<IReadOnlyList<string>, Task> announce,
        Server tmux,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tmux);
        Server materialized = tmux.IsMaterialized
            ? tmux
            : await tmux.ConnectAsync(cancellationToken).ConfigureAwait(false);
        HierarchyWatchKey key = HierarchyWatchKey.From(materialized);
        await SubscribeAsync(
                uri,
                subscriberKey,
                announce,
                key,
                token => materialized.EnterControlModeAsync(cancellationToken: token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Starts one subscription with a supplied control-client factory.</summary>
    internal async Task SubscribeAsync(
        string uri,
        object subscriberKey,
        Func<IReadOnlyList<string>, Task> announce,
        Func<CancellationToken, Task<IControlModeSession>> startSession,
        CancellationToken cancellationToken) =>
        await SubscribeAsync(
                uri,
                subscriberKey,
                announce,
                HierarchyWatchKey.ForTest("default", new ServerGeneration(1, 1)),
                startSession,
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Starts a test subscription for an exact endpoint generation.</summary>
    internal async Task SubscribeAsync(
        string uri,
        object subscriberKey,
        Func<IReadOnlyList<string>, Task> announce,
        string endpointFingerprint,
        ServerGeneration generation,
        Func<CancellationToken, Task<IControlModeSession>> startSession,
        CancellationToken cancellationToken) =>
        await SubscribeAsync(
                uri,
                subscriberKey,
                announce,
                HierarchyWatchKey.ForTest(endpointFingerprint, generation),
                startSession,
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Stops reporting changes to one resource.</summary>
    /// <param name="uri">The resource the client unsubscribed from.</param>
    public async Task UnsubscribeAsync(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        foreach (HierarchyEndpointWatch endpoint in SnapshotEndpoints())
        {
            if (!endpoint.RemoveAllReferences(uri))
            {
                continue;
            }

            await RetireIfUnusedAsync(endpoint).ConfigureAwait(false);
        }
    }

    /// <summary>Stops one independently owned subscription.</summary>
    internal async Task UnsubscribeAsync(string uri, object subscriberKey)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(subscriberKey);
        foreach (HierarchyEndpointWatch endpoint in SnapshotEndpoints())
        {
            if (!endpoint.TryRemoveReference(uri, subscriberKey))
            {
                continue;
            }

            await RetireIfUnusedAsync(endpoint).ConfigureAwait(false);
        }
    }

    /// <summary>Answers whether a tmux notification changes what exists.</summary>
    /// <param name="name">The notification name, without its leading percent.</param>
    /// <returns><see langword="true" /> when subscribers should be told.</returns>
    internal static bool IsStructural(string name) => Structural.Contains(name);

    /// <summary>Answers whether an event requires invalidating the hierarchy.</summary>
    internal static bool InvalidatesHierarchy(TmuxEvent observed) =>
        observed is TmuxEventsDroppedEvent
        || observed is TmuxNotificationEvent notification && IsStructural(notification.Name);

    private async Task SubscribeAsync(
        string uri,
        object subscriberKey,
        Func<IReadOnlyList<string>, Task> announce,
        HierarchyWatchKey key,
        Func<CancellationToken, Task<IControlModeSession>> startSession,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(subscriberKey);
        ArgumentNullException.ThrowIfNull(announce);
        ArgumentNullException.ThrowIfNull(startSession);

        HierarchyEndpointWatch endpoint;
        bool added = false;
        while (true)
        {
            lock (_endpointsGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_endpoints.TryGetValue(key, out endpoint!))
                {
                    endpoint = new HierarchyEndpointWatch(
                        key,
                        _logger,
                        _delay,
                        _beforeRecoveryOutcome,
                        _recoveryOutcomeObserved);
                    _endpoints.Add(key, endpoint);
                }
            }

            await endpoint.EnterSubscriptionAsync(cancellationToken).ConfigureAwait(false);
            bool acquired = false;
            try
            {
                lock (_endpointsGate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (_endpoints.TryGetValue(key, out HierarchyEndpointWatch? current)
                        && ReferenceEquals(current, endpoint))
                    {
                        acquired = endpoint.TryAddReference(
                            uri,
                            subscriberKey,
                            announce,
                            out added);
                        if (!acquired)
                        {
                            _endpoints.Remove(key);
                        }
                    }
                }
            }
            finally
            {
                if (!acquired)
                {
                    endpoint.ExitSubscription();
                }
            }

            if (acquired)
            {
                break;
            }
        }

        try
        {
            await endpoint.EnsureStartedAsync(startSession, cancellationToken)
                .ConfigureAwait(false);
            lock (_endpointsGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
            }
        }
        catch
        {
            if (added)
            {
                endpoint.TryRemoveReference(uri, subscriberKey);
            }

            await RetireIfUnusedAsync(endpoint).ConfigureAwait(false);
            throw;
        }
        finally
        {
            endpoint.ExitSubscription();
        }
    }

    private HierarchyEndpointWatch[] SnapshotEndpoints()
    {
        lock (_endpointsGate)
        {
            return [.. _endpoints.Values];
        }
    }

    private async Task RetireIfUnusedAsync(HierarchyEndpointWatch endpoint)
    {
        if (!await endpoint.StopIfUnusedAsync().ConfigureAwait(false))
        {
            return;
        }

        lock (_endpointsGate)
        {
            if (_endpoints.TryGetValue(endpoint.Key, out HierarchyEndpointWatch? current)
                && ReferenceEquals(current, endpoint))
            {
                _endpoints.Remove(endpoint.Key);
            }
        }
    }
}
