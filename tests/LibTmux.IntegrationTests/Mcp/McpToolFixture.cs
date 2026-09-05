using System.Runtime.Versioning;
using LibTmux.Mcp;
using LibTmux.Testing;

namespace LibTmux.IntegrationTests;

/// <summary>Direct tool helpers wired to one throwaway tmux socket.</summary>
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
        ReadTools read,
        WriteTools write,
        CapabilityTools capabilities)
    {
        Options = options;
        Connection = connection;
        Activity = activity;
        Read = read;
        Write = write;
        Capabilities = capabilities;
    }

    internal TmuxTestOptions Options { get; }

    internal TmuxConnectionAccessor Connection { get; }

    internal PaneActivityHub Activity { get; }

    internal ReadTools Read { get; }

    internal WriteTools Write { get; }

    internal CapabilityTools Capabilities { get; }

    internal static McpToolFixture Create(
        ServerPolicy? policy = null,
        CapabilityRegistry? registry = null)
    {
        TmuxTestOptions options = new(new ServerConnectionOptions(
            tmuxBinaryPath: System.Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux",
            socketName: $"ltm-{Guid.NewGuid():N}"[..20],
            configurationFile: "/dev/null"));

        TmuxConnectionAccessor connection = new(
            options.ConnectionOptions,
            options.ConnectionOptions.SocketName);
        PaneActivityHub activity = new();
        ServerPolicy effective = policy ?? new ServerPolicy
        {
            WaitCeiling = TimeSpan.FromSeconds(20),
        };

        var read = new ReadTools(connection, effective, activity);
        var write = new WriteTools(connection, effective, activity);
        return new McpToolFixture(
            options,
            connection,
            activity,
            read,
            write,
            new CapabilityTools(
                read,
                write,
                connection,
                registry ?? CapabilityRegistry.All(),
                effective));
    }

    public async ValueTask DisposeAsync()
    {
        await Activity.DisposeAsync().ConfigureAwait(false);
        Connection.Dispose();
    }
}
