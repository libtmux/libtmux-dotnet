using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using System.Text;

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

    /// <summary>Quotes a word so a POSIX shell reads it as exactly that word.</summary>
    /// <param name="value">The word.</param>
    /// <returns>The quoted word.</returns>
    /// <remarks>
    /// Single quotes end every special meaning a shell has except their own, so
    /// the only thing to handle is a single quote in the input.
    /// </remarks>
    internal static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>Builds the tmux command line that reaches this same server.</summary>
    /// <param name="server">The server to address.</param>
    /// <param name="arguments">The tmux command and its arguments.</param>
    /// <returns>A shell-safe command line.</returns>
    /// <remarks>
    /// A command run from inside a pane inherits <c>TMUX</c> and would reach the
    /// ambient server, which is not necessarily the one this tool is driving.
    /// Naming the socket is what makes the two the same server.
    /// </remarks>
    internal static string TmuxCommandLine(Server server, params string[] arguments)
    {
        StringBuilder line = new(ShellQuote(server.ConnectionOptions.TmuxBinaryPath));
        if (server.ConnectionOptions.SocketPath is string path)
        {
            line.Append(" -S ").Append(ShellQuote(path));
        }
        else if (server.ConnectionOptions.SocketName is string name)
        {
            line.Append(" -L ").Append(ShellQuote(name));
        }

        foreach (string argument in arguments)
        {
            line.Append(' ').Append(ShellQuote(argument));
        }

        return line.ToString();
    }
}
