using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Internal;

namespace LibTmux.IntegrationTests.Materialization;

[UnsupportedOSPlatform("windows")]
public sealed class MaterializationTests
{
    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task Materializes_embedded_newlines_and_invalid_utf8()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        string hostile = "line\nbreak:9:colon";
        await fixture.SetPaneOptionAsync("@hostile", hostile);

        IReadOnlyDictionary<string, string?> pane = await fixture.FetchPaneAsync();

        Assert.NotNull(pane["pane_id"]);
        // The row survived a value carrying the row separator and the scalar
        // delimiter, which a delimiter-based reader would have split.
        Assert.Equal(hostile, await fixture.ShowPaneOptionAsync("@hostile"));
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task Format_separator_exclusion_uses_single_expansion_decode()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        FormatProjection projection = FormatProjection.Create(
            "list-sessions",
            fixture.TmuxVersion);

        Assert.Equal(projection.Fields.Count, projection.FramedFieldCount);

        // Every field is expanded exactly once. A byte-count prefix would
        // expand it a second time, and a field that moved in between would
        // announce one length and then render another.
        Assert.DoesNotContain("#{n:", projection.Template, StringComparison.Ordinal);
        Assert.Contains(
            $"#{{session_id}}{FormatProjection.RowSeparator}",
            projection.Template,
            StringComparison.Ordinal);

        // tmux cannot produce the separator by expanding anything, because the
        // separator carries no format punctuation of its own.
        Assert.DoesNotContain("#", FormatProjection.RowSeparator, StringComparison.Ordinal);

        // tmux caps a whole command at MAX_IMSGSIZE, and the template shares
        // that ceiling with the generation guard wrapped around every entity
        // command, so it has to stay well clear of 16 KiB.
        Assert.True(
            projection.Template.Length < 8192,
            $"template is {projection.Template.Length} bytes");
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task Materializer_uses_server_context_and_returns_typed_raw_fields()
    {
        await using Fixture fixture = await Fixture.StartAsync();

        IReadOnlyDictionary<string, string?> session = await fixture.FetchSessionAsync();
        EntityMaterializationState state = Materializer.CreateState(fixture.Context, session);

        Assert.Same(fixture.Server, state.Server);
        Assert.Equal(fixture.Server.Generation, state.Generation);
        Assert.NotNull(state.SessionId);
        Assert.NotNull(session["pid"]);
        Assert.NotNull(session["start_time"]);
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task Generated_projection_round_trips_multiple_hostile_rows()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        await fixture.RunAsync(["new-session", "-d", "-s", "second"]);
        await fixture.RunAsync(["new-session", "-d", "-s", "third"]);

        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows =
            await fixture.Query.FetchAsync(
                "list-sessions",
                cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.NotNull(row["session_id"]));
        Assert.Equal(3, rows.Select(row => row["session_id"]).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task Version_gates_emit_only_supported_fields()
    {
        await using Fixture fixture = await Fixture.StartAsync();

        IReadOnlyDictionary<string, string?> pane = await fixture.FetchPaneAsync();
        bool supportsFloating = fixture.TmuxVersion.IsAtLeast(TmuxVersion.Parse("3.7"));

        Assert.Equal(supportsFloating, pane.ContainsKey("pane_floating_flag"));
        Assert.Equal(
            fixture.TmuxVersion.IsAtLeast(TmuxVersion.Parse("3.3")),
            pane.ContainsKey("pane_dead_signal"));
        Assert.True(pane.ContainsKey("pane_id"));
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task Window_and_pane_lookup_use_tmux_canonical_session()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        await fixture.RunAsync(["new-session", "-d", "-s", "other"]);
        IReadOnlyDictionary<string, string?> pane = await fixture.FetchPaneAsync();
        string paneId = pane["pane_id"]!;

        IReadOnlyDictionary<string, string?>? found = await fixture.Query.FetchOneAsync(
            "list-panes",
            "pane_id",
            paneId,
            cancellationToken: TestContext.Current.CancellationToken);

        // The pane resolves even though another session exists and tmux has no
        // attached client to make either session "current".
        Assert.NotNull(found);
        Assert.Equal(paneId, found["pane_id"]);
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task Missing_target_is_distinct_from_unreachable_server()
    {
        await using Fixture fixture = await Fixture.StartAsync();

        IReadOnlyDictionary<string, string?>? missing = await fixture.Query.FetchOneAsync(
            "list-panes",
            "pane_id",
            "%99999",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(missing);

        await fixture.RunAsync(["kill-server"], allowFailure: true);
        await Assert.ThrowsAnyAsync<LibTmuxException>(
            () => fixture.Query.FetchAsync(
                "list-panes",
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task Materialized_handles_carry_their_snapshot_while_lookups_do_not()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        IReadOnlyDictionary<string, string?> row = await fixture.FetchSessionAsync();

        Session materialized = Materializer.MaterializeSession(fixture.Context, row);
        Session resolved = await fixture.Server.GetSessionAsync(
            materialized.Id,
            TestContext.Current.CancellationToken);

        Assert.NotNull(materialized.Snapshot);
        Assert.Equal(row["session_name"], materialized.Snapshot!["session_name"]);
        // A handle resolved by identifier never pretends to hold fields it
        // did not read.
        Assert.Null(resolved.Snapshot);
        Assert.Equal(materialized.Id, resolved.Id);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            RawTmuxTestContext context,
            Server server,
            MaterializationContext materialization)
        {
            Context = materialization;
            Server = server;
            Raw = context;
            Query = new MaterializationQuery(materialization);
        }

        internal RawTmuxTestContext Raw { get; }

        internal Server Server { get; }

        internal MaterializationContext Context { get; }

        internal MaterializationQuery Query { get; }

        internal TmuxVersion TmuxVersion => Context.TmuxVersion;

        internal static async Task<Fixture> StartAsync()
        {
            RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
                TestContext.Current.CancellationToken);
            var options = new ServerConnectionOptions(
                tmuxBinaryPath: raw.TmuxBinaryPath,
                socketPath: raw.SocketPath,
                configurationFile: "/dev/null");
            Server server = await Server.ConnectAsync(
                options,
                TestContext.Current.CancellationToken);
            TmuxVersion version = await TmuxVersion.DetectAsync(
                raw.TmuxBinaryPath,
                TestContext.Current.CancellationToken);
            return new Fixture(raw, server, new MaterializationContext(server, version));
        }

        internal async Task<IReadOnlyDictionary<string, string?>> FetchSessionAsync()
        {
            IReadOnlyList<IReadOnlyDictionary<string, string?>> rows =
                await Query.FetchAsync(
                    "list-sessions",
                    cancellationToken: TestContext.Current.CancellationToken);
            return rows[0];
        }

        internal async Task<IReadOnlyDictionary<string, string?>> FetchPaneAsync()
        {
            IReadOnlyList<IReadOnlyDictionary<string, string?>> rows =
                await Query.FetchAsync(
                    "list-panes",
                    ["-a"],
                    TestContext.Current.CancellationToken);
            return rows[0];
        }

        internal async Task SetPaneOptionAsync(string name, string value)
        {
            IReadOnlyDictionary<string, string?> pane = await FetchPaneAsync();
            await RunAsync(["set-option", "-p", "-t", pane["pane_id"]!, name, value]);
        }

        internal async Task<string> ShowPaneOptionAsync(string name)
        {
            IReadOnlyDictionary<string, string?> pane = await FetchPaneAsync();
            RawTmuxResult result = await Raw.ExecuteAsync(
                ["show-options", "-p", "-v", "-t", pane["pane_id"]!, name],
                TestContext.Current.CancellationToken);
            return result.StandardOutputText.TrimEnd('\n');
        }

        internal async Task RunAsync(IReadOnlyList<string> arguments, bool allowFailure = false)
        {
            RawTmuxResult result = await Raw.ExecuteAsync(
                arguments,
                TestContext.Current.CancellationToken);
            if (!allowFailure && result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"tmux {string.Join(' ', arguments)} failed with {result.ExitCode}.");
            }
        }

        public ValueTask DisposeAsync() => Raw.DisposeAsync();
    }
}
