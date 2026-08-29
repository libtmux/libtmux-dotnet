using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Query;

namespace LibTmux.IntegrationTests.Parity;

[UnsupportedOSPlatform("windows")]
public sealed class Component09ParityTests
{
    public static TheoryData<string> OwnedRows =>
    [
        "libtmux._internal.query_list:<module>",
    ];

    [Theory(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    [MemberData(nameof(OwnedRows))]
    public async Task Owned_parity_row_has_wire_behavior(string pythonSymbolId)
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await Server.ConnectAsync(
            new ServerConnectionOptions(
                tmuxBinaryPath: raw.TmuxBinaryPath,
                socketPath: raw.SocketPath,
                configurationFile: "/dev/null"),
            token);
        await raw.ExecuteAsync(["rename-session", "-t", "$0", "devbox"], token);

        bool proved = pythonSymbolId switch
        {
            "libtmux._internal.query_list:<module>" =>
                await ProvesWireDocumentAsync(server, token),
            _ => false,
        };

        Assert.True(proved, $"Parity behavior was not proved for {pythonSymbolId}.");
    }

    private static async Task<bool> ProvesWireDocumentAsync(
        Server server,
        CancellationToken token)
    {
        // The document a caller could put on a wire must select the same live
        // objects the same predicate selects locally.
        QueryDocument document =
            QueryEdgeParser.ParseNameContains(QueryTarget.Session, "dev");
        IReadOnlyList<Session> sessions = await server.GetSessionsAsync(token);
        Func<Session, bool> predicate = document.Compile<Session>();
        Assert.NotNull(predicate);

        return document.Target == QueryTarget.Session
            && document.Schema == QueryDocument.CurrentSchema
            && document.Version == QueryDocument.CurrentVersion
            && sessions.Count == 1
            && sessions[0].Snapshot?["session_name"] == "devbox"
            && predicate(sessions[0]);
    }
}
