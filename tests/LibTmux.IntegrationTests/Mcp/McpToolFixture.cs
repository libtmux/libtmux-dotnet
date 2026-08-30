using System.Runtime.Versioning;
using LibTmux.Mcp;
using LibTmux.Testing;

namespace LibTmux.IntegrationTests;

/// <summary>A server of each tier, wired to one throwaway tmux socket.</summary>
/// <remarks>
/// The tools are exercised directly rather than through the protocol. What is
/// worth testing here is what they do to tmux; that the protocol carries a
/// record is the SDK's job and is covered once, in
/// <see cref="McpProtocolTests" />.
/// </remarks>
[UnsupportedOSPlatform("windows")]
internal sealed class McpToolFixture : IAsyncDisposable
{
    private McpToolFixture(
        TmuxTestOptions options,
        TmuxConnectionAccessor connection,
        PaneActivityHub activity,
        JobStore jobs,
        ReadTools read,
        WriteTools write,
        DestructiveTools destructive)
    {
        Options = options;
        Connection = connection;
        Activity = activity;
        Jobs = jobs;
        Read = read;
        Write = write;
        Destructive = destructive;
    }

    internal TmuxTestOptions Options { get; }

    internal TmuxConnectionAccessor Connection { get; }

    internal PaneActivityHub Activity { get; }

    internal JobStore Jobs { get; }

    internal ReadTools Read { get; }

    internal WriteTools Write { get; }

    internal DestructiveTools Destructive { get; }

    internal static McpToolFixture Create(ServerPolicy? policy = null)
    {
        TmuxTestOptions options = new(new ServerConnectionOptions(
            tmuxBinaryPath: System.Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux",
            socketName: $"ltm-{Guid.NewGuid():N}"[..20],
            configurationFile: "/dev/null"));

        TmuxConnectionAccessor connection = new(
            options.ConnectionOptions,
            options.ConnectionOptions.SocketName);
        PaneActivityHub activity = new();
        JobStore jobs = new();
        ServerPolicy effective = policy ?? new ServerPolicy
        {
            Tier = SafetyTier.Destructive,
            WaitCeiling = TimeSpan.FromSeconds(20),
        };

        return new McpToolFixture(
            options,
            connection,
            activity,
            jobs,
            new ReadTools(connection, effective, activity),
            new WriteTools(connection, effective, activity, jobs),
            new DestructiveTools(connection));
    }

    public async ValueTask DisposeAsync()
    {
        await Activity.DisposeAsync().ConfigureAwait(false);
        await Jobs.DisposeAsync().ConfigureAwait(false);
        Connection.Dispose();
    }
}
