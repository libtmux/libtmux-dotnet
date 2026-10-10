using System.Diagnostics;
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

    [Theory(Skip = "Requires Unix sockets.", SkipType = typeof(UnixTestEnvironment), SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Probe_cancellation_keeps_its_cause_and_only_normalizes_a_requested_caller(
        bool cancelCaller, bool originalCallerToken)
    {
        await using var fixture = new Fixture();
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(fixture.Options.SocketPath!));
        using var caller = new CancellationTokenSource();
        using var other = new CancellationTokenSource();
        other.Cancel();
        var original = new OperationCanceledException("Probe cancellation detail.",
            new IOException("Probe cause."), originalCallerToken ? caller.Token : other.Token);
        Task<ServerDiscoveryResult> pending = Server.DiscoverAsync(new()
        {
            Roots = [fixture.Root],
            IncludeConfiguredRoots = false,
            Connection = fixture.Options with
            {
                Interceptor = (invocation, next, token) =>
                {
                    if (!invocation.Arguments.Contains("display-message", StringComparer.Ordinal))
                    {
                        return next(token);
                    }
                    if (cancelCaller)
                    {
                        caller.Cancel();
                    }
                    return Task.FromException<TmuxCommandResult>(original);
                },
            },
        }, caller.Token);
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(pending.IsCanceled);
        if (cancelCaller && !originalCallerToken)
        {
            Assert.Equal(caller.Token, error.CancellationToken);
            Assert.Equal(original.Message, error.Message);
            Assert.Same(original, error.InnerException);
        }
        else
        {
            Assert.Same(original, error);
        }
    }

    [UnixFact]
    public async Task Cancellation_during_a_probe_keeps_the_caller_token_and_client_diagnostics()
    {
        var fixture = new Fixture();
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(fixture.Options.SocketPath!));
        listener.Listen(1);
        using var caller = new CancellationTokenSource();
        Task<ServerDiscoveryResult> pending = Server.DiscoverAsync(new()
        {
            Roots = [fixture.Root],
            IncludeConfiguredRoots = false,
            Connection = fixture.Options,
            ProbeTimeout = TimeSpan.FromSeconds(10),
            Timeout = TimeSpan.FromSeconds(20),
        }, caller.Token);
        try
        {
            // Accepting the connection proves the real tmux client entered the probe.
            using Socket accepted = await listener.AcceptAsync(Token).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5), Token);
            caller.Cancel();
            TmuxOperationCanceledException error = await Assert.ThrowsAsync<TmuxOperationCanceledException>(
                () => pending.WaitAsync(TimeSpan.FromSeconds(5), Token));
            Assert.True(pending.IsCanceled);
            Assert.True(error.CommandMayHaveExecuted);
            Assert.True(error.ClientProcessId > 0);
            Assert.Equal(caller.Token, error.CancellationToken);
            TmuxOperationCanceledException original = Assert.IsType<TmuxOperationCanceledException>(error.InnerException);
            Assert.Equal(original.Message, error.Message);
            Assert.Equal(original.ClientProcessId, error.ClientProcessId);
            Assert.Equal(original.CommandMayHaveExecuted, error.CommandMayHaveExecuted);
            Assert.NotEqual(caller.Token, original.CancellationToken);
        }
        finally
        {
            caller.Cancel();
            Exception? outcome = await Record.ExceptionAsync(
                () => pending.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
            bool exited = false;
            if (outcome is TmuxOperationCanceledException canceled)
            {
                try
                {
                    using Process client = Process.GetProcessById(canceled.ClientProcessId);
                    await client.WaitForExitAsync(CancellationToken.None)
                        .WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
                    exited = client.HasExited;
                }
                catch (ArgumentException)
                {
                    exited = true;
                }
            }
            listener.Dispose();
            if (exited)
            {
                await fixture.DisposeAsync();
            }
            else
            {
                TestContext.Current.SendDiagnosticMessage("Retained unverified probe root: {0}", fixture.Root);
            }
            Assert.True(exited, "The probe client exit must be observed before removing its root.");
        }
    }

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

    [Theory(Skip = "Requires Unix symbolic links.", SkipType = typeof(UnixTestEnvironment), SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    [InlineData("explicit")]
    [InlineData("selected")]
    [InlineData("configured")]
    public async Task Root_components_resolve_symlink_parent_semantics_before_enumeration(string source)
    {
        await using var fixture = await Fixture.StartAsync();
        string actual = Path.Combine(fixture.Root, "actual");
        string child = Path.Combine(actual, "child");
        string empty = Path.Combine(fixture.Root, "empty");
        Directory.CreateDirectory(child);
        Directory.CreateDirectory(empty);
        Directory.CreateSymbolicLink(Path.Combine(fixture.Root, "link"), child);
        string alias = Path.Combine(fixture.Root, "link", "..");
        ServerConnectionOptions creation = source == "configured"
            ? fixture.Options with
            {
                SocketPath = null,
                SocketName = "actual-daemon",
                ChildEnvironment = new Dictionary<string, string?> { ["TMUX_TMPDIR"] = actual },
            }
            : fixture.Options with { SocketPath = Path.Combine(actual, "daemon") };
        await using OwnedServerScope target = await Server.CreateOwnedAsync(creation, Token);
        ServerDiscoveryResult result = await Server.DiscoverAsync(new()
        {
            Roots = source == "explicit" ? [alias] : [],
            IncludeConfiguredRoots = source != "explicit",
            Connection = fixture.Options with
            {
                SocketPath = Path.Combine(source == "selected" ? alias : empty, "daemon"),
                ChildEnvironment = new Dictionary<string, string?> { ["TMUX_TMPDIR"] = source == "configured" ? alias : empty },
            },
        }, Token);
        Assert.Equal(target.Value.Generation, Assert.Single(result.Servers).Server.Generation);
        Assert.Contains("/link/../", result.Servers[0].SocketPath, StringComparison.Ordinal);
        Assert.False(result.Truncated);
        Assert.NotNull(await fixture.Server.InspectAsync(Token));
    }

    [Theory(Skip = "Requires Unix path resolution.", SkipType = typeof(UnixTestEnvironment), SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    [InlineData("explicit")]
    [InlineData("selected")]
    [InlineData("configured")]
    public async Task Missing_intermediate_root_components_do_not_authorize_the_lexical_parent(string source)
    {
        await using var fixture = await Fixture.StartAsync();
        string empty = Path.Combine(fixture.Root, "empty");
        Directory.CreateDirectory(empty);
        string suffix = "tmux-" + UnixSocketDirectory.UserId;
        Directory.CreateDirectory(Path.Combine(fixture.Root, suffix));
        string invalid = Path.Combine(fixture.Root, "missing", "..");
        ServerDiscoveryResult result = await Server.DiscoverAsync(new()
        {
            Roots = source == "explicit" ? [invalid] : [],
            IncludeConfiguredRoots = source != "explicit",
            Connection = fixture.Options with
            {
                SocketPath = Path.Combine(source == "selected" ? invalid : empty, "socket"),
                ChildEnvironment = new Dictionary<string, string?> { ["TMUX_TMPDIR"] = source == "configured" ? invalid : empty },
            },
        }, Token);
        string expected = source == "configured" ? Path.Combine(invalid, suffix) : invalid;
        Assert.Contains(result.Diagnostics, item => item.Path == expected && item.Kind == "root-error");
        Assert.Empty(result.Servers);
        Assert.Equal(0, result.EntriesVisited);
        Assert.Equal(0, result.ProbesAttempted);
        Assert.False(result.Truncated);
        Assert.NotNull(await fixture.Server.InspectAsync(Token));
    }

    [Theory(Skip = "Requires Unix sockets.", SkipType = typeof(UnixTestEnvironment), SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Duplicate_root_inputs_cannot_hide_an_omitted_root(int maximumRoots)
    {
        await using var fixture = await Fixture.StartAsync();
        string empty = Path.Combine(fixture.Root, "empty");
        Directory.CreateDirectory(empty);
        ServerDiscoveryResult result = await Server.DiscoverAsync(new()
        {
            Roots = [empty, empty, fixture.Root],
            IncludeConfiguredRoots = false,
            MaximumRoots = maximumRoots,
            Connection = fixture.Options,
        }, Token);
        Assert.Empty(result.Servers);
        Assert.True(result.Truncated);
        Assert.Contains(result.Diagnostics, item => item.Kind == "limit");
        Assert.Equal(0, result.ProbesAttempted);
        Assert.NotNull(await fixture.Server.InspectAsync(Token));

        ServerDiscoveryResult complete = await Server.DiscoverAsync(new()
        {
            Roots = [empty, empty],
            IncludeConfiguredRoots = false,
            MaximumRoots = 2,
            Connection = fixture.Options,
        }, Token);
        Assert.False(complete.Truncated);
        Assert.Contains(complete.Diagnostics, item => item.Path == empty && item.Kind == "duplicate");
    }
}
