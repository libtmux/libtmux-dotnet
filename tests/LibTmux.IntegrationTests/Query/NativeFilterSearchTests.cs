using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;

namespace LibTmux.IntegrationTests.Query;

[UnsupportedOSPlatform("windows")]
public sealed class NativeFilterSearchTests
{
    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task A_native_filter_can_match_a_local_predicate()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        await raw.ExecuteAsync(["rename-session", "-t", "$0", "devbox"], token);
        await raw.ExecuteAsync(["new-session", "-d", "-s", "prod"], token);

        IReadOnlyList<Session> pushedDown = await server.SearchSessionsAsync(
            new UnsafeTmuxFilter("#{m:dev*,#{session_name}}"),
            token);
        List<Session> local = (await server.GetSessionsAsync(token))
            .Where(session => session.Snapshot?["session_name"]?.StartsWith("dev", StringComparison.Ordinal) == true)
            .ToList();

        // This one supported expression agrees; raw filters make no general
        // equivalence guarantee with the typed query vocabulary.
        Assert.Single(pushedDown);
        Assert.Single(local);
        Assert.Equal(local[0].Id, pushedDown[0].Id);
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task An_unknown_tmux_filter_token_yields_no_rows_rather_than_an_error()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);

        // tmux evaluates the raw text itself, so a token it does not know is
        // simply false. This is exactly why the type is named unsafe: the
        // closed catalog cannot protect a caller here.
        IReadOnlyList<Session> matched = await server.SearchSessionsAsync(
            new UnsafeTmuxFilter("#{==:#{not_a_real_token},x}"),
            token);

        Assert.Empty(matched);
        Assert.NotEmpty(await server.GetSessionsAsync(token));
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task A_failed_search_throws_where_a_listing_would_report_empty()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        await raw.ExecuteAsync(["kill-server"], token);

        // A caller who asked a question deserves to know it went unanswered,
        // while a listing may honestly report that nothing is there.
        await Assert.ThrowsAnyAsync<LibTmuxException>(
            () => server.SearchSessionsAsync(new UnsafeTmuxFilter("1"), token));
        Assert.Empty(await server.GetSessionsAsync(token));
    }

    private static Task<Server> ConnectAsync(
        RawTmuxTestContext raw,
        CancellationToken token) =>
        Server.ConnectAsync(
            new ServerConnectionOptions(
                tmuxBinaryPath: raw.TmuxBinaryPath,
                socketPath: raw.SocketPath,
                configurationFile: "/dev/null"),
            token);
}
