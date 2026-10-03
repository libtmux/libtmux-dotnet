using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;

using LibTmux.Internal;

namespace LibTmux.Mcp;

/// <summary>Everything an assistant can change about tmux, short of removing it.</summary>
/// <remarks>
/// Resources passed to the constructor remain owned by the caller.
/// </remarks>
[UnsupportedOSPlatform("windows")]
internal sealed partial class WriteTools : IAsyncDisposable
{
    private readonly object _lifetimeGate = new();
    private readonly TmuxConnectionAccessor _connection;
    private readonly ServerPolicy _policy;
    private readonly PaneActivityHub _activity;
    private readonly ResourceOwnership _ownership;
    private Task? _disposeTask;

    /// <summary>Initializes the changing tools.</summary>
    /// <param name="connection">The servers the tools talk to.</param>
    /// <param name="policy">What the tools are allowed to spend.</param>
    /// <param name="activity">Tells a wait when a pane has printed something.</param>
    /// <remarks>Disposing the tools does not dispose these caller-owned resources.</remarks>
    public WriteTools(
        TmuxConnectionAccessor connection,
        ServerPolicy policy,
        PaneActivityHub activity)
        : this(connection, policy, activity, ResourceOwnership.None)
    {
    }

    internal WriteTools(
        TmuxConnectionAccessor connection,
        ServerPolicy policy,
        PaneActivityHub activity,
        ResourceOwnership ownership)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(activity);
        _connection = connection;
        _policy = policy;
        _activity = activity;
        _ownership = ownership;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_lifetimeGate)
        {
            _disposeTask ??= DisposeOwnedAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeOwnedAsync()
    {
        List<Exception> failures = [];
        if (_ownership.HasFlag(ResourceOwnership.Activity))
        {
            try
            {
                await _activity.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                failures.Add(error);
            }
        }

        if (_ownership.HasFlag(ResourceOwnership.Connection))
        {
            try
            {
                _connection.Dispose();
            }
            catch (Exception error)
            {
                failures.Add(error);
            }
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        if (failures.Count > 1)
        {
            throw new AggregateException(failures);
        }
    }

    private Task<Server> ServerAsync(string? socketName, CancellationToken cancellationToken) =>
        _connection.GetAsync(socketName, cancellationToken);

    [Flags]
    internal enum ResourceOwnership
    {
        None = 0,
        Connection = 1,
        Activity = 2,
    }
}
