using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;

namespace LibTmux.IntegrationTests.Connection;

/// <summary>What owning a server does when one is already listening, and when stopping it fails.</summary>
/// <remarks>
/// Each socket root here is the test's own, so "the default socket" is one
/// nobody else uses. On a developer's machine it is the server they are
/// sitting in, which is what the refusal protects.
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed class OwnedServerAdoptionTests
{
    [UnixFact]
    public async Task Refuses_a_default_socket_that_is_already_serving()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string root = CreateSocketRoot();
        Server occupant = Server.Open(Options(root, socketName: null));
        try
        {
            await occupant.CreateSessionAsync(new NewSessionRequest { Name = "occupant" }, token);

            InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
                () => Server.CreateOwnedAsync(Options(root, socketName: null), token));

            Assert.Contains("default socket", refused.Message, StringComparison.Ordinal);
            Assert.True(await occupant.IsAliveAsync(token), "the occupying server was adopted and stopped");
        }
        finally
        {
            await CleanUpAsync(occupant, root, token);
        }
    }

    [UnixFact]
    public async Task Owns_a_named_socket_while_the_default_one_is_serving()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string root = CreateSocketRoot();
        Server occupant = Server.Open(Options(root, socketName: null));
        try
        {
            await occupant.CreateSessionAsync(new NewSessionRequest { Name = "occupant" }, token);

            await using (OwnedServerScope owned = await Server.CreateOwnedAsync(Options(root, "owned"), token))
            {
                Session created = await owned.Value.CreateSessionAsync(new NewSessionRequest { Name = "owned" }, token);
                Assert.Equal("owned", created.Name);
            }

            Assert.True(await occupant.IsAliveAsync(token));
        }
        finally
        {
            await CleanUpAsync(occupant, root, token);
        }
    }

    [UnixFact]
    public async Task Disposing_again_after_a_failed_stop_stops_the_server()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string root = CreateSocketRoot();
        Server observer = Server.Open(Options(root, "owned"));
        try
        {
            (OwnedServerScope owned, string refuse) = await CreateRefusableAsync(root, token);

            await File.WriteAllTextAsync(refuse, string.Empty, token);
            await Assert.ThrowsAnyAsync<LibTmuxException>(() => owned.DisposeAsync().AsTask());
            Assert.True(await observer.IsAliveAsync(token));

            File.Delete(refuse);
            await owned.DisposeAsync();
            Assert.False(await observer.IsAliveAsync(token), "a second dispose returned without stopping the server");
        }
        finally
        {
            await CleanUpAsync(observer, root, token);
        }
    }

    [UnixFact]
    public async Task Concurrent_disposals_share_one_stop_and_its_failure()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string root = CreateSocketRoot();
        Server observer = Server.Open(Options(root, "owned"));
        try
        {
            (OwnedServerScope owned, string refuse) = await CreateRefusableAsync(root, token);

            await File.WriteAllTextAsync(refuse, string.Empty, token);
            Task first = owned.DisposeAsync().AsTask();
            Task second = owned.DisposeAsync().AsTask();
            await Assert.ThrowsAnyAsync<LibTmuxException>(() => first);
            await Assert.ThrowsAnyAsync<LibTmuxException>(() => second);
            Assert.True(await observer.IsAliveAsync(token));

            File.Delete(refuse);
            await owned.DisposeAsync();
            Assert.False(await observer.IsAliveAsync(token));
        }
        finally
        {
            await CleanUpAsync(observer, root, token);
        }
    }

    // An owned server whose tmux exits 1 while the returned file exists, so a
    // test can make stopping it fail and then succeed.
    private static async Task<(OwnedServerScope Owned, string Refuse)> CreateRefusableAsync(
        string root,
        CancellationToken cancellationToken)
    {
        string refuse = Path.Combine(root, "refuse");
        string tmux = Path.Combine(root, "tmux");
        await TestExecutable.WriteAsync(
            tmux,
            $"#!/bin/sh\n[ -e '{refuse}' ] && exit 1\nexec '{Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux"}' \"$@\"\n",
            cancellationToken);
        OwnedServerScope owned = await Server.CreateOwnedAsync(
            Options(root, "owned") with { TmuxBinaryPath = tmux },
            cancellationToken);
        await owned.Value.CreateSessionAsync(new NewSessionRequest { Name = "kept" }, cancellationToken);
        return (owned, refuse);
    }

    private static string CreateSocketRoot()
    {
        // A socket path is limited to about 104 bytes and this root is part of
        // one, `<root>/tmux-<uid>/<name>`, so it is short rather than descriptive.
        string root = Path.Combine(Path.GetTempPath(), $"lt-{Guid.NewGuid():N}"[..11]);
        Directory.CreateDirectory(root);
        return root;
    }

    private static ServerConnectionOptions Options(string root, string? socketName) =>
        new()
        {
            TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux",
            SocketName = socketName,
            ConfigurationFile = "/dev/null",
            ChildEnvironment = new Dictionary<string, string?> { ["TMUX_TMPDIR"] = root },
        };

    private static async Task CleanUpAsync(Server server, string root, CancellationToken cancellationToken)
    {
        if (await server.IsAliveAsync(cancellationToken))
        {
            await server.KillAsync(cancellationToken);
        }

        Directory.Delete(root, recursive: true);
    }
}
