using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;

namespace LibTmux.IntegrationTests.Collections;

[UnsupportedOSPlatform("windows")]
public sealed class ScopedCollectionTests
{
    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task Server_collections_span_every_session()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        await raw.ExecuteAsync(["new-session", "-d", "-s", "second"], token);
        await raw.ExecuteAsync(["new-window", "-t", "second:"], token);

        IReadOnlyList<Session> sessions = await server.GetSessionsAsync(token);
        IReadOnlyList<Window> windows = await server.GetWindowsAsync(token);
        IReadOnlyList<Pane> panes = await server.GetPanesAsync(token);

        // Server-wide listings cross session boundaries; a per-session listing
        // would report only one of these.
        Assert.Equal(2, sessions.Count);
        Assert.Equal(3, windows.Count);
        Assert.Equal(3, panes.Count);
        Assert.Equal(2, (await sessions[1].GetWindowsAsync(token)).Count);
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task Attached_sessions_are_empty_without_a_client()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);

        Assert.NotEmpty(await server.GetSessionsAsync(token));
        Assert.Empty(await server.GetAttachedSessionsAsync(token));
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task A_dead_server_preserves_listing_failures()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        await raw.ExecuteAsync(["kill-server"], token);

        await AssertListingFailuresAsync(server, token);
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task Listings_preserve_permission_and_invalid_socket_failures()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);

        File.SetUnixFileMode(raw.SocketPath, UnixFileMode.None);
        try
        {
            await AssertListingFailuresAsync(server, token);
        }
        finally
        {
            File.SetUnixFileMode(
                raw.SocketPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // The socket replaced by an ordinary file is a third route, and one a
        // caller reaches by pointing at a stale path something else reused.
        await raw.ExecuteAsync(["kill-server"], token);
        File.Delete(raw.SocketPath);
        File.WriteAllText(raw.SocketPath, string.Empty);

        await AssertListingFailuresAsync(server, token);
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task Explicit_liveness_checks_preserve_failures()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);

        Assert.True(await server.IsAliveAsync(token));
        await server.ThrowIfDeadAsync(token);
        await raw.ExecuteAsync(["kill-server"], token);

        TmuxCommandException listingFailure = await Assert.ThrowsAsync<TmuxCommandException>(
            () => server.GetSessionsAsync(token));

        // A listing failure's message must carry tmux's own reason, not just
        // "list-sessions failed." with the reason reachable only through
        // .Result.StandardErrorLines, matching every one-shot mutation's
        // TmuxCommandFailure.ThrowIfFailed.
        Assert.Contains("no server running", listingFailure.Message, StringComparison.Ordinal);
        Assert.False(await server.IsAliveAsync(token));

        TmuxCommandException failure = await Assert.ThrowsAsync<TmuxCommandException>(
            async () => await server.ThrowIfDeadAsync(token));

        Assert.NotEqual(0, failure.Result.ExitCode);
        Assert.Contains(
            failure.Result.StandardErrorLines,
            static line => line.Contains("no server running", StringComparison.Ordinal));
    }

    private static async Task AssertListingFailuresAsync(Server server, CancellationToken token)
    {
        foreach (Func<Task> list in new Func<Task>[]
        {
            () => server.GetSessionsAsync(token),
            () => server.GetAttachedSessionsAsync(token),
            () => server.GetWindowsAsync(token),
            () => server.GetPanesAsync(token),
            () => server.GetClientsAsync(token),
        })
        {
            TmuxCommandException error = await Assert.ThrowsAsync<TmuxCommandException>(list);
            Assert.NotEqual(0, error.Result.ExitCode);
            Assert.NotEmpty(error.Result.StandardErrorLines);
        }
    }

    private static Task<Server> ConnectAsync(
        RawTmuxTestContext raw,
        CancellationToken token) =>
        Server.ConnectAsync(
            new ServerConnectionOptions { TmuxBinaryPath = raw.TmuxBinaryPath, SocketPath = raw.SocketPath, ConfigurationFile = "/dev/null" },
            token);
}
