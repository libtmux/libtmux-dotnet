using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using LibTmux.Internal;
using LibTmux.Mcp;
using LibTmux.UnitTests.Connection;
using ModelContextProtocol;

namespace LibTmux.UnitTests;

[UnsupportedOSPlatform("windows")]
public sealed class SearchResultBudgetTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(501)]
    public void A_per_pane_limit_must_fit_the_server_wide_line_budget(int requested)
    {
        McpException error = Assert.Throws<McpException>(() =>
            ReadTools.ValidateSearchMatchLimit(requested, 500));

        Assert.Contains("between 1 and 500", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Incremental_accounting_matches_the_complete_budgeted_size()
    {
        MatchedLine first = new(-3, "quote \" and \U0001f642");
        MatchedLine second = new(8, "line\nwith\tcontrols");
        var exact = new SearchResult(
            "err(or)?",
            4,
            [new PaneMatch("%1", "@2", "$3", [first, second])],
            false);
        int exactBytes = Utf8JsonBudget.GetStructuredToolResultByteCount(exact, ToolJson.Options);
        var budget = new SearchResultBudget("err(or)?", 4, 10, exactBytes);
        List<MatchedLine> matches = [];

        Assert.Equal(
            SearchMatchBudgetOutcome.Added,
            budget.TryAdd("%1", "@2", "$3", matches, first));
        Assert.Equal(
            SearchMatchBudgetOutcome.Added,
            budget.TryAdd("%1", "@2", "$3", matches, second));
        Assert.NotEqual(
            SearchMatchBudgetOutcome.Added,
            budget.TryAdd("%1", "@2", "$3", matches, new MatchedLine(9, "x")));
        budget.Commit("%1", "@2", "$3", matches);

        SearchResult result = budget.Build(4, false);
        Assert.Equal(
            exactBytes,
            Utf8JsonBudget.GetStructuredToolResultByteCount(result, ToolJson.Options));
    }

    [Fact]
    public void The_global_match_ceiling_applies_across_panes()
    {
        var budget = new SearchResultBudget("x", 10, 2, 4_000);
        List<MatchedLine> firstPane = [];
        List<MatchedLine> secondPane = [];

        Assert.Equal(
            SearchMatchBudgetOutcome.Added,
            budget.TryAdd("%1", "@1", "$1", firstPane, new MatchedLine(0, "x")));
        budget.Commit("%1", "@1", "$1", firstPane);
        Assert.Equal(
            SearchMatchBudgetOutcome.Added,
            budget.TryAdd("%2", "@2", "$2", secondPane, new MatchedLine(0, "x")));
        Assert.Equal(
            SearchMatchBudgetOutcome.GlobalLimit,
            budget.TryAdd("%2", "@2", "$2", secondPane, new MatchedLine(1, "x")));
    }

    [Fact]
    public void A_local_cap_on_one_pane_does_not_stop_the_next_pane()
    {
        var budget = new SearchResultBudget("x", 2, 10, 4_000);
        Regex regex = ReadTools.CompilePattern("x", ignoreCase: false);

        SearchPaneBudgetOutcome first = ReadTools.AddSearchMatches(
            budget,
            "%1",
            "@1",
            "$1",
            ["x-one", "x-two"],
            0,
            regex,
            maxMatchesPerPane: 1,
            TestContext.Current.CancellationToken);
        SearchPaneBudgetOutcome second = ReadTools.AddSearchMatches(
            budget,
            "%2",
            "@2",
            "$2",
            ["x-three"],
            0,
            regex,
            maxMatchesPerPane: 1,
            TestContext.Current.CancellationToken);
        SearchResult result = budget.Build(2, truncated: true);

        Assert.Equal(SearchPaneBudgetOutcome.PerPaneLimit, first);
        Assert.Equal(SearchPaneBudgetOutcome.Complete, second);
        Assert.Equal(2, result.Panes.Count);
        Assert.Equal("x-three", result.Panes[1].Matches[0].Text);
    }

    [Fact]
    public void An_oversized_match_does_not_hide_a_later_small_match()
    {
        var budget = new SearchResultBudget("x", 1, 10, 4_000);
        Regex regex = ReadTools.CompilePattern("x", ignoreCase: false);

        SearchPaneBudgetOutcome outcome = ReadTools.AddSearchMatches(
            budget,
            "%1",
            "@1",
            "$1",
            ["x" + new string('z', 1_000_000), "x-small"],
            0,
            regex,
            maxMatchesPerPane: 10,
            TestContext.Current.CancellationToken);
        SearchResult result = budget.Build(1, truncated: outcome != SearchPaneBudgetOutcome.Complete);

        Assert.Equal(SearchPaneBudgetOutcome.OversizedMatchSkipped, outcome);
        PaneMatch pane = Assert.Single(result.Panes);
        MatchedLine match = Assert.Single(pane.Matches);
        Assert.Equal("x-small", match.Text);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void Byte_exhaustion_stops_scanning_the_current_and_later_panes()
    {
        var budget = new SearchResultBudget("x", 2, 500, 4_000);
        Regex regex = ReadTools.CompilePattern("x", ignoreCase: false);
        var lines = new RepeatedLines(1_000_000, "x");

        SearchPaneBudgetOutcome outcome = ReadTools.AddSearchMatches(
            budget,
            "%1",
            "@1",
            "$1",
            lines,
            0,
            regex,
            maxMatchesPerPane: 500,
            TestContext.Current.CancellationToken);

        Assert.Equal(SearchPaneBudgetOutcome.GlobalLimit, outcome);
        Assert.InRange(lines.Reads, 1, 200);
    }

    [Fact]
    public void A_cancelled_search_stops_before_scanning_more_rows()
    {
        var budget = new SearchResultBudget("x", 1, 500, 4_000);
        Regex regex = ReadTools.CompilePattern("x", ignoreCase: false);
        var lines = new RepeatedLines(1_000_000, "x");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => ReadTools.AddSearchMatches(
            budget,
            "%1",
            "@1",
            "$1",
            lines,
            0,
            regex,
            maxMatchesPerPane: 500,
            cancellation.Token));

        Assert.Equal(0, lines.Reads);
    }

    [Fact]
    public void A_regex_timeout_is_reported_as_an_actionable_mcp_error()
    {
        var budget = new SearchResultBudget("(a+)+$", 1, 10, 4_000);
        var regex = new Regex("(a+)+$", RegexOptions.None, TimeSpan.FromTicks(1));

        McpException error = Assert.Throws<McpException>(() => ReadTools.AddSearchMatches(
            budget,
            "%1",
            "@1",
            "$1",
            [new string('a', 10_000) + "!"],
            0,
            regex,
            maxMatchesPerPane: 10,
            TestContext.Current.CancellationToken));

        Assert.Contains("Simplify", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_large_history_row_does_not_hide_a_later_smaller_row()
    {
        var budget = new SearchResultBudget("x", 1, 500, 4_000);
        List<MatchedLine> matches = [];
        Assert.Equal(
            SearchMatchBudgetOutcome.Added,
            budget.TryAdd(
                "%1",
                "@1",
                "$1",
                matches,
                new MatchedLine(-20_000, new string('x', 1_430))));

        Assert.Equal(
            SearchMatchBudgetOutcome.ItemTooLarge,
            budget.TryAdd("%1", "@1", "$1", matches, new MatchedLine(-10_000, string.Empty)));
        Assert.Equal(
            SearchMatchBudgetOutcome.Added,
            budget.TryAdd("%1", "@1", "$1", matches, new MatchedLine(-999, string.Empty)));
    }

    [Fact]
    public void Multibyte_matches_never_push_the_complete_result_over_its_byte_ceiling()
    {
        const int maxBytes = 4_000;
        var budget = new SearchResultBudget("\U0001f642+", 20, 500, maxBytes);
        List<MatchedLine> matches = [];
        int row = 0;
        while (budget.TryAdd(
            "%1",
            "@1",
            "$1",
            matches,
            new MatchedLine(row++, string.Concat(Enumerable.Repeat("\U0001f642", 12))))
            == SearchMatchBudgetOutcome.Added)
        {
        }

        budget.Commit("%1", "@1", "$1", matches);
        SearchResult result = budget.Build(1, truncated: true);
        int bytes = Utf8JsonBudget.GetStructuredToolResultByteCount(result, ToolJson.Options);

        Assert.NotEmpty(result.Panes);
        Assert.True(bytes <= maxBytes, $"search result used {bytes} bytes");
        Assert.True(result.Truncated);
    }

    [Fact]
    public void A_pattern_that_consumes_the_whole_response_is_rejected_before_searching()
    {
        McpException error = Assert.Throws<McpException>(() =>
            new SearchResultBudget(new string('x', 500), 1, 10, 100));

        Assert.Contains("pattern alone", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_oversized_logical_line_is_rejected_without_copying_it()
    {
        var budget = new SearchResultBudget("x", 1, 10, 4_000);
        var match = new MatchedLine(0, new string('x', 1_000_000));
        List<MatchedLine> matches = [];
        _ = System.Text.Encoding.UTF8.GetByteCount("warm");
        long before = GC.GetAllocatedBytesForCurrentThread();

        SearchMatchBudgetOutcome added = budget.TryAdd("%1", "@1", "$1", matches, match);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.NotEqual(SearchMatchBudgetOutcome.Added, added);
        Assert.Empty(matches);
        Assert.True(allocated < 4_096, $"rejection allocated {allocated} bytes");
    }

    [Fact]
    public void Escape_heavy_matches_are_rejected_without_materializing_the_fragment()
    {
        const int maxBytes = 4_000_000;
        var budget = new SearchResultBudget("x", 1, 500, maxBytes);
        var match = new MatchedLine(0, new string('\u0001', 1_900_000));
        List<MatchedLine> matches = [];
        _ = Utf8JsonBudget.GetStructuredJsonFragmentByteCount(
            new MatchedLine(0, "warm"),
            ToolJson.Options);
        long before = GC.GetAllocatedBytesForCurrentThread();

        SearchMatchBudgetOutcome added = budget.TryAdd("%1", "@1", "$1", matches, match);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.NotEqual(SearchMatchBudgetOutcome.Added, added);
        Assert.Empty(matches);
        Assert.True(allocated < 4_000_000, $"rejection allocated {allocated:N0} bytes");
    }

    [Fact]
    public async Task An_oversized_pattern_is_rejected_before_any_tmux_dispatch()
    {
        int dispatches = 0;
        var connection = new TmuxConnection(
            new ServerConnectionOptions(socketName: "search-no-dispatch"),
            FakeMultiplexer.AnsweringVersion((request, _) =>
            {
                Interlocked.Increment(ref dispatches);
                return Task.FromResult(new TmuxCommandResult(
                    request.LogicalArguments,
                    0,
                    ReadOnlyMemory<byte>.Empty,
                    ReadOnlyMemory<byte>.Empty,
                    [],
                    []));
            }));
        var generation = new ServerGeneration(11, 22);
        var server = new Server(connection, generation, "tmux 3.7");
        using var accessor = new TmuxConnectionAccessor(server);
        await using var activity = new PaneActivityHub();
        var tools = new ReadTools(
            accessor,
            new ServerPolicy { MaxBytes = 128_000 },
            activity);

        McpException error = await Assert.ThrowsAsync<McpException>(() =>
            tools.SearchPanesAsync(
                new string('x', 4_097),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("4096", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, dispatches);
    }

    private sealed class RepeatedLines(int count, string value) : IReadOnlyList<string>
    {
        internal int Reads { get; private set; }

        public int Count { get; } = count;

        public string this[int index]
        {
            get
            {
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
                Reads++;
                return value;
            }
        }

        public IEnumerator<string> GetEnumerator() =>
            Enumerable.Repeat(value, Count).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }
}
