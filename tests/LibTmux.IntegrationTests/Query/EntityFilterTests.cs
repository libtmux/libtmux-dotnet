using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Query;
using LibTmux.Testing;

namespace LibTmux.IntegrationTests.Query;

/// <summary>Filters the objects the library hands back, declaratively.</summary>
/// <remarks>
/// Translating and interpreting have to resolve the same pair: tmux calls a
/// field <c>session_name</c> and C# calls it <c>Name</c>. When only one side
/// knew that, a filter over a real session threw rather than matching, and the
/// only expressions that worked were over rows whose properties happened to be
/// spelled the way the wire is.
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed class EntityFilterTests
{
    [UnixFact]
    public async Task A_predicate_over_sessions_matches_what_tmux_reports()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TemporaryHierarchyScope scope = await Scope(token);

        await scope.Server.CreateSessionAsync(new NewSessionRequest(name: "build-one"), token);
        await scope.Server.CreateSessionAsync(new NewSessionRequest(name: "build-two"), token);
        await scope.Server.CreateSessionAsync(new NewSessionRequest(name: "other"), token);

        IReadOnlyList<Session> sessions = await scope.Server.GetSessionsAsync(token);
        IReadOnlyList<Session> building = sessions.Matching<Session>(
            session => session.Name.StartsWith("build", StringComparison.Ordinal));

        Assert.Equal(2, building.Count);
        Assert.All(building, session => Assert.StartsWith("build", session.Name, StringComparison.Ordinal));
    }

    [UnixFact]
    public async Task The_document_a_predicate_became_filters_the_same_way()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TemporaryHierarchyScope scope = await Scope(token);

        await scope.Session.CreateWindowAsync(new NewWindowRequest(name: "build-one"), token);
        await scope.Session.CreateWindowAsync(new NewWindowRequest(name: "other"), token);

        // The point of a document is that it can be written somewhere else and
        // still mean this, so the two paths have to agree.
        QueryDocument document = QueryExtensions.Translate<Window>(
            window => window.Name.StartsWith("build", StringComparison.Ordinal));
        IReadOnlyList<Window> windows = await scope.Session.GetWindowsAsync(token);

        Assert.Equal(
            windows.Matching<Window>(
                window => window.Name.StartsWith("build", StringComparison.Ordinal)).Count,
            windows.Matching(document).Count);
        Assert.Single(windows.Matching(document));
    }

    [UnixFact]
    public async Task A_relation_quantifier_reads_the_captured_windows()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TemporaryHierarchyScope scope = await Scope(token);
        await scope.Session.CreateWindowAsync(new NewWindowRequest(name: "build-one"), token);

        Server snapshot = await scope.Server.CaptureSnapshotAsync(SnapshotDepth.Windows, token);
        IReadOnlyList<Session> sessions = [.. snapshot.Sessions];

        IReadOnlyList<Session> building = sessions.Matching<Session>(
            session => session.Windows.Any(window => window.Name.StartsWith("build", StringComparison.Ordinal)));

        Assert.Single(building);
    }

    private static Task<TemporaryHierarchyScope> Scope(CancellationToken cancellationToken)
    {
        TmuxTestFactory factory = new();
        TmuxTestOptions options = new(new ServerConnectionOptions(
            tmuxBinaryPath: Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux",
            socketName: $"ltquery-{Guid.NewGuid():N}"[..24],
            configurationFile: "/dev/null"));
        return factory.CreateHierarchyAsync(options, cancellationToken);
    }
}
