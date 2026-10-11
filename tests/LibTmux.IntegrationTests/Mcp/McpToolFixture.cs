using System.Runtime.Versioning;
using LibTmux.Internal;
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

    // The input preflight refuses a pane whose running command changes, and a
    // login shell runs helpers such as locale while it starts.
    internal static IReadOnlyDictionary<string, string?> PlainShellEnvironment { get; } =
        new Dictionary<string, string?>
        {
            ["SHELL"] = "/bin/sh",
            ["ENV"] = null,
            ["BASH_ENV"] = null,
        };

    private readonly TmuxTestFactory _factory = new();

    internal TmuxTestOptions Options { get; }

    // Called each time a wait for a prompt finds a pane that has drawn nothing.
    internal Action? WhenPaneHasNotDrawn { get; set; }

    internal TmuxConnectionAccessor Connection { get; }

    internal PaneActivityHub Activity { get; }

    internal ReadTools Read { get; }

    internal WriteTools Write { get; }

    internal CapabilityTools Capabilities { get; }

    /// <summary>Creates the server, session, window and pane a test works in, with the pane at its prompt.</summary>
    /// <param name="cancellationToken">Cancels the creation and the wait.</param>
    /// <returns>The scope, whose pane has drawn its first prompt.</returns>
    /// <remarks>
    /// A login shell runs its profile before the first prompt, and a running
    /// profile helper is what the pane's command reads as until then. The input
    /// preflight refuses a pane whose command changes between its two reads, so
    /// a test is handed a pane only once the shell is the command it will stay.
    /// </remarks>
    internal async Task<TemporaryHierarchyScope> CreateReadyHierarchyAsync(CancellationToken cancellationToken)
    {
        TemporaryHierarchyScope scope = await _factory
            .CreateHierarchyAsync(Options, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await WaitForShellsAsync(cancellationToken, scope.Pane.Id.ToString()).ConfigureAwait(false);
            return scope;
        }
        catch
        {
            await scope.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Waits until each pane has drawn something, which a shell does at its first prompt.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <param name="paneIds">The panes to wait for.</param>
    /// <returns>A task that completes when every pane has.</returns>
    internal async Task WaitForShellsAsync(CancellationToken cancellationToken, params string[] paneIds)
    {
        Server server = await Connection.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (string id in paneIds)
        {
            Pane pane = await TmuxTargets.PaneAsync(server, id, cancellationToken).ConfigureAwait(false);
            PaneWaitResult prompt = await pane.WaitUntilAsync(
                rows =>
                {
                    bool drawn = rows.Any(row => row.Trim().Length > 0);
                    if (!drawn)
                    {
                        WhenPaneHasNotDrawn?.Invoke();
                    }

                    return drawn;
                },
                TimeSpan.FromSeconds(10),
                cancellationToken).ConfigureAwait(false);
            Assert.True(prompt.Found, $"{id} never drew a prompt");
        }
    }

    internal static McpToolFixture Create(
        ServerPolicy? policy = null,
        CapabilityRegistry? registry = null,
        IReadOnlyDictionary<string, string?>? childEnvironment = null)
    {
        string configuredBinary = System.Environment.GetEnvironmentVariable("LIBTMUX_TMUX")
            ?? "tmux";
        string tmuxBinaryPath = McpStartup.ResolveExecutablePath(
            configuredBinary,
            System.Environment.GetEnvironmentVariable("PATH"));
        TmuxTestOptions options = new(new ServerConnectionOptions
        {
            TmuxBinaryPath = tmuxBinaryPath,
            SocketName = $"ltm-{Guid.NewGuid():N}"[..20],
            ConfigurationFile = "/dev/null",
            ChildEnvironment = childEnvironment ?? PlainShellEnvironment,
        });

        return Create(options, policy, registry);
    }

    internal static McpToolFixture Create(
        TmuxTestOptions options,
        ServerPolicy? policy = null,
        CapabilityRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        TmuxConnectionAccessor connection = new(
            options.ConnectionOptions,
            options.ConnectionOptions.SocketName);
        ServerPolicy effective = policy ?? new ServerPolicy
        {
            WaitCeiling = TimeSpan.FromSeconds(20),
        };

        PaneActivityHub activity = new();
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
