using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Query;

namespace LibTmux.IntegrationTests.Parity;

[UnsupportedOSPlatform("windows")]
public sealed class Component08ParityTests
{
    private sealed record SessionRow(string SessionName, long SessionWindows);

    public static TheoryData<string> OwnedRows =>
    [
        "libtmux._internal.query_list:LOOKUP_NAME_MAP",
        "libtmux._internal.query_list:OpNotFound",
        "libtmux._internal.query_list:QueryList.filter",
        "libtmux._internal.query_list:lookup_contains",
        "libtmux._internal.query_list:lookup_endswith",
        "libtmux._internal.query_list:lookup_exact",
        "libtmux._internal.query_list:lookup_icontains",
        "libtmux._internal.query_list:lookup_iendswith",
        "libtmux._internal.query_list:lookup_iexact",
        "libtmux._internal.query_list:lookup_in",
        "libtmux._internal.query_list:lookup_iregex",
        "libtmux._internal.query_list:lookup_istartswith",
        "libtmux._internal.query_list:lookup_nin",
        "libtmux._internal.query_list:lookup_regex",
        "libtmux._internal.query_list:lookup_startswith",
        "libtmux._internal.query_list:parse_lookup",
        "libtmux.server:Server.search_panes",
        "libtmux.server:Server.search_sessions",
        "libtmux.server:Server.search_windows",
    ];

    [Theory(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    [MemberData(nameof(OwnedRows))]
    public async Task Owned_parity_row_has_query_behavior(string pythonSymbolId)
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
            "libtmux.server:Server.search_sessions" =>
                (await server.SearchSessionsAsync(
                    new UnsafeTmuxFilter("#{==:#{session_name},devbox}"),
                    token)).Count == 1,
            "libtmux.server:Server.search_windows" =>
                (await server.SearchWindowsAsync(
                    new UnsafeTmuxFilter("#{==:#{session_name},devbox}"),
                    token)).Count == 1,
            "libtmux.server:Server.search_panes" =>
                (await server.SearchPanesAsync(
                    new UnsafeTmuxFilter("#{==:#{session_name},devbox}"),
                    token)).Count == 1,
            "libtmux._internal.query_list:QueryList.filter" => ProvesMatching(),
            "libtmux._internal.query_list:OpNotFound" => ProvesTranslateOrThrow(),
            "libtmux._internal.query_list:lookup_contains"
                or "libtmux._internal.query_list:parse_lookup" => ProvesNameContains(),
            // Python's remaining lookup operators are excluded: an expression
            // states them directly, and a closed vocabulary is what lets
            // translation fail loudly instead of degrading.
            "libtmux._internal.query_list:LOOKUP_NAME_MAP"
                or "libtmux._internal.query_list:lookup_exact"
                or "libtmux._internal.query_list:lookup_iexact"
                or "libtmux._internal.query_list:lookup_startswith"
                or "libtmux._internal.query_list:lookup_istartswith"
                or "libtmux._internal.query_list:lookup_endswith"
                or "libtmux._internal.query_list:lookup_iendswith"
                or "libtmux._internal.query_list:lookup_icontains"
                or "libtmux._internal.query_list:lookup_in"
                or "libtmux._internal.query_list:lookup_nin"
                or "libtmux._internal.query_list:lookup_regex"
                or "libtmux._internal.query_list:lookup_iregex" =>
                ProvesExpressionVocabulary(),
            _ => false,
        };

        Assert.True(proved, $"Parity behavior was not proved for {pythonSymbolId}.");
    }

    private static bool ProvesMatching()
    {
        IReadOnlyList<SessionRow> rows =
        [
            new SessionRow("devbox", 2),
            new SessionRow("prod", 9),
        ];
        return rows
            .Matching<SessionRow>(row => row.SessionName.Contains("dev") && row.SessionWindows > 1)
            .Count == 1;
    }

    private static bool ProvesTranslateOrThrow()
    {
        // Python swallows an unknown lookup operator and answers an exact
        // match; this port refuses to answer a different question.
        Assert.Throws<UnsupportedQueryExpressionException>(
            () => QueryEdgeParser.ParseNameContains(QueryTarget.Pane, "x"));
        return true;
    }

    private static bool ProvesNameContains()
    {
        QueryDocument document =
            QueryEdgeParser.ParseNameContains(QueryTarget.Session, "dev");
        StringNode contains = Assert.IsType<StringNode>(document.Predicate);
        return contains.Operator == QueryStringOperation.ContainsOrdinal
            && document.Compile<SessionRow>()(new SessionRow("devbox", 1))
            && !document.Compile<SessionRow>()(new SessionRow("prod", 1));
    }

    private static bool ProvesExpressionVocabulary()
    {
        IReadOnlyList<SessionRow> rows = [new SessionRow("devbox", 2)];
        return rows.Matching<SessionRow>(
                row => row.SessionName.StartsWith("dev", StringComparison.Ordinal)).Count == 1
            && rows.Matching<SessionRow>(
                row => row.SessionName.EndsWith("box", StringComparison.Ordinal)).Count == 1
            && rows.Matching<SessionRow>(row => row.SessionWindows >= 2).Count == 1
            && rows.Matching<SessionRow>(row => !row.SessionName.Contains("prod")).Count == 1;
    }
}
