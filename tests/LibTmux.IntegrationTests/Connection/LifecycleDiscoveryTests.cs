using System.Net.Sockets;
using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Internal;
using static LibTmux.IntegrationTests.Connection.LifecycleOwnershipTests;

namespace LibTmux.IntegrationTests.Connection;

[UnsupportedOSPlatform("windows")]
public sealed class LifecycleDiscoveryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [UnixFact]
    public async Task Multiple_roots_report_live_stale_failed_skipped_duplicate_and_missing_candidates()
    {
        await using var first = await Fixture.StartAsync();
        await using var second = await Fixture.StartAsync();
        string stalePath = Path.Combine(first.Root, "stale");
        using (var stale = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            stale.Bind(new UnixDomainSocketEndPoint(stalePath + ".temporary"));
            File.Move(stalePath + ".temporary", stalePath);
        }
        string stalledPath = Path.Combine(first.Root, "stalled");
        using var stalled = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        stalled.Bind(new UnixDomainSocketEndPoint(stalledPath));
        stalled.Listen(10);
        string linkPath = Path.Combine(second.Root, "alias");
        File.CreateSymbolicLink(linkPath, first.Options.SocketPath!);
        string config = Path.Combine(second.Root, "discovery.conf");
        string startup = Path.Combine(second.Root, "must-not-start");
        await File.WriteAllTextAsync(config, "run-shell 'touch " + startup + "'\n", Token);
        ServerDiscoveryResult result = await Server.DiscoverAsync(new()
        {
            Roots = [first.Root, second.Root, first.Root + "/.", Path.Combine(second.Root, "missing")],
            IncludeConfiguredRoots = false,
            Connection = first.Options with { ConfigurationFile = config },
            ProbeTimeout = TimeSpan.FromMilliseconds(120),
        }, Token);
        Assert.False(result.Truncated);
        Assert.Equal(2, result.Servers.Count);
        Assert.Equal(2, result.Servers.Select(item => item.Server.Generation).Distinct().Count());
        Assert.Contains(result.Diagnostics, item => item.Path == stalePath && item.Kind is "absent" or "probe-error");
        Assert.Contains(result.Diagnostics, item => item.Path == stalledPath && item.Kind == "probe-error");
        Assert.Contains(result.Diagnostics, item => item.Path == linkPath && item.Kind == "skipped");
        Assert.Contains(result.Diagnostics, item => item.Kind == "duplicate");
        Assert.Contains(result.Diagnostics, item => item.Kind == "root-error");
        Assert.False(File.Exists(startup));
        Assert.Null(await Server.Open(first.Options with { SocketPath = stalePath }).InspectAsync(Token));
        Assert.NotNull(await first.Server.InspectAsync(Token));
        Assert.NotNull(await second.Server.InspectAsync(Token));
    }

    [UnixFact]
    public async Task Configured_uid_root_and_explicit_root_are_both_discovered()
    {
        await using var first = await Fixture.StartAsync();
        await using var second = new Fixture();
        ServerConnectionOptions named = second.Options with
        {
            SocketPath = null,
            SocketName = "configured",
            ChildEnvironment = new Dictionary<string, string?> { ["TMUX_TMPDIR"] = second.Root },
        };
        await using OwnedServerScope owned = await Server.CreateOwnedAsync(named, Token);
        ServerDiscoveryResult result = await Server.DiscoverAsync(new()
        {
            Roots = [first.Root],
            Connection = named,
        }, Token);
        Assert.Equal(2, result.Servers.Count);
        Assert.Contains(result.Servers, item => item.SocketPath == Path.Combine(second.Root, "tmux-" + UnixSocketDirectory.UserId, "configured"));
        Assert.False(result.Truncated);
    }

    [UnixFact]
    public async Task Entry_probe_root_and_time_bounds_report_truncation_without_starting_daemons()
    {
        await using var first = await Fixture.StartAsync();
        await using var second = await Fixture.StartAsync();
        var options = new ServerDiscoveryOptions
        {
            Roots = [first.Root, second.Root],
            IncludeConfiguredRoots = false,
            Connection = first.Options,
        };
        foreach (ServerDiscoveryOptions bounded in new[]
        {
            options with { MaximumEntries = 1 },
            options with { MaximumProbes = 1 },
            options with { MaximumRoots = 1 },
            options with { Timeout = TimeSpan.FromTicks(1) },
        })
        {
            ServerDiscoveryResult result = await Server.DiscoverAsync(bounded, Token);
            Assert.True(result.Truncated);
            Assert.Contains(result.Diagnostics, item => item.Kind == "limit");
            Assert.InRange(result.EntriesVisited, 0, bounded.MaximumEntries);
            Assert.InRange(result.ProbesAttempted, 0, bounded.MaximumProbes);
        }
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Server.DiscoverAsync(options, cancellation.Token));
        Assert.NotNull(await first.Server.InspectAsync(Token));
        Assert.NotNull(await second.Server.InspectAsync(Token));
    }
}
