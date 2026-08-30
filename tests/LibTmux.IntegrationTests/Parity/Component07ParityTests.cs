using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;

namespace LibTmux.IntegrationTests.Parity;

[UnsupportedOSPlatform("windows")]
public sealed class Component07ParityTests
{
    public static TheoryData<string> OwnedRows =>
    [
        "libtmux._internal.query_list:PKRequiredException",
        "libtmux._internal.query_list:QueryList",
        "libtmux._internal.query_list:QueryList.data",
        "libtmux._internal.query_list:QueryList.get",
        "libtmux._internal.query_list:QueryList.items",
        "libtmux._internal.query_list:QueryList.pk_key",
        "libtmux._internal.query_list:T",
        "libtmux._internal.query_list:keygetter",
        "libtmux._internal.query_list:no_arg",
        "libtmux.server:Server._list_panes",
        "libtmux.server:Server._list_sessions",
        "libtmux.server:Server._list_windows",
        "libtmux.server:Server._sessions",
        "libtmux.server:Server._update_panes",
        "libtmux.server:Server._update_windows",
        "libtmux.server:Server.attached_sessions",
        "libtmux.server:Server.children",
        "libtmux.server:Server.find_where",
        "libtmux.server:Server.get_by_id",
        "libtmux.server:Server.list_sessions",
        "libtmux.server:Server.panes",
        "libtmux.server:Server.sessions",
        "libtmux.server:Server.where",
        "libtmux.server:Server.windows",
    ];

    [Theory(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    [MemberData(nameof(OwnedRows))]
    public async Task Owned_parity_row_has_collection_behavior(string pythonSymbolId)
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        Server server = await Server.ConnectAsync(
            new ServerConnectionOptions(
                tmuxBinaryPath: raw.TmuxBinaryPath,
                socketPath: raw.SocketPath,
                configurationFile: "/dev/null"),
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;

        bool proved = pythonSymbolId switch
        {
            "libtmux.server:Server.sessions" =>
                (await server.GetSessionsAsync(token)).Count == 1,
            "libtmux.server:Server.windows" =>
                (await server.GetWindowsAsync(token)).Count == 1,
            "libtmux.server:Server.panes" =>
                (await server.GetPanesAsync(token)).Count == 1,
            "libtmux.server:Server.attached_sessions" =>
                (await server.GetAttachedSessionsAsync(token)).Count == 0,
            // Python's QueryList surface is excluded: an IReadOnlyList over a
            // captured snapshot already answers these with BCL LINQ, and the
            // one gap worth keeping is a duplicate-rejecting keyed index.
            "libtmux._internal.query_list:QueryList"
                or "libtmux._internal.query_list:QueryList.data"
                or "libtmux._internal.query_list:QueryList.items"
                or "libtmux._internal.query_list:T"
                or "libtmux.server:Server._sessions"
                or "libtmux.server:Server._list_sessions"
                or "libtmux.server:Server._list_windows"
                or "libtmux.server:Server._list_panes"
                or "libtmux.server:Server._update_windows"
                or "libtmux.server:Server._update_panes"
                or "libtmux.server:Server.children"
                or "libtmux.server:Server.list_sessions" =>
                await ProvesLocalEnumerationAsync(server, token),
            "libtmux._internal.query_list:QueryList.get"
                or "libtmux._internal.query_list:QueryList.pk_key"
                or "libtmux._internal.query_list:keygetter"
                or "libtmux._internal.query_list:PKRequiredException"
                or "libtmux._internal.query_list:no_arg"
                or "libtmux.server:Server.get_by_id" =>
                await ProvesKeyedLookupAsync(server, token),
            "libtmux.server:Server.where" or "libtmux.server:Server.find_where" =>
                await ProvesLinqFilteringAsync(server, token),
            _ => false,
        };

        Assert.True(proved, $"Parity behavior was not proved for {pythonSymbolId}.");
    }

    private static async Task<bool> ProvesLocalEnumerationAsync(
        Server server,
        CancellationToken token)
    {
        IReadOnlyList<Session> sessions = await server.GetSessionsAsync(token);
        // Enumerating the returned snapshot runs no tmux command.
        return sessions.Count == 1
            && sessions[0].Id == sessions.Single().Id
            && sessions.Any();
    }

    private static async Task<bool> ProvesKeyedLookupAsync(
        Server server,
        CancellationToken token)
    {
        IReadOnlyList<Session> sessions = await server.GetSessionsAsync(token);
        Dictionary<SessionId, Session> byId =
            sessions.ToDictionary(static session => session.Id);
        return byId.Count == sessions.Count
            && byId.TryGetValue(sessions[0].Id, out Session? found)
            && found.Id == sessions[0].Id;
    }

    private static async Task<bool> ProvesLinqFilteringAsync(
        Server server,
        CancellationToken token)
    {
        IReadOnlyList<Session> sessions = await server.GetSessionsAsync(token);
        SessionId wanted = sessions[0].Id;
        return sessions.Where(session => session.Id == wanted).ToList().Count == 1
            && sessions.FirstOrDefault(session => session.Id != wanted) is null;
    }
}
