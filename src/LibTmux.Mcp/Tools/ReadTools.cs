using System.Runtime.Versioning;

namespace LibTmux.Mcp;

/// <summary>Everything an assistant can ask tmux without changing it.</summary>
/// <remarks>Implementation helpers used by the authoritative capability handlers.</remarks>
[UnsupportedOSPlatform("windows")]
internal sealed partial class ReadTools
{
    // .NET 8 caps a non-backtracking automaton at 1000 nodes. A literal uses
    // one node per character plus its root, so 999 UTF-8 bytes are portable.
    private const int MaximumRegexPatternBytes = 999;

    private readonly TmuxConnectionAccessor _connection;
    private readonly ServerPolicy _policy;
    private readonly PaneActivityHub _activity;

    /// <summary>Initializes the reading tools.</summary>
    /// <param name="connection">The servers the tools talk to.</param>
    /// <param name="policy">What the tools are allowed to spend.</param>
    /// <param name="activity">Tells a wait when a pane has printed something.</param>
    public ReadTools(
        TmuxConnectionAccessor connection,
        ServerPolicy policy,
        PaneActivityHub activity)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(activity);
        _connection = connection;
        _policy = policy;
        _activity = activity;
    }

    private Task<Server> ServerAsync(string? socketName, CancellationToken cancellationToken) =>
        _connection.GetAsync(socketName, cancellationToken);
}
