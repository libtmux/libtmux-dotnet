using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using LibTmux.Internal;
using LibTmux.Mcp;
using LibTmux.UnitTests.Connection;
using ModelContextProtocol;

namespace LibTmux.UnitTests;

[UnsupportedOSPlatform("windows")]
public sealed class StructuredTextResultBudgetTests
{
    private const int MaxBytes = 4_000;

    [Fact]
    public void Every_content_bearing_result_fits_its_complete_tool_budget()
    {
        string[] lines = Enumerable.Range(0, 200)
            .Select(index => $"line {index}: \\\"quoted\\\" \\\\ path \U0001f642")
            .ToArray();

        AssertFits(lines, content => new CaptureResult("%1", content));
        AssertFits(lines, content => new TailResult("%1", content, WidestCursor(), false, false));
        AssertFits(lines, content => new WaitResult(
            "%1",
            WaitOutcome.Matched,
            "ready.*",
            content,
            1.25,
            30));
        AssertFits(lines, content => new RunResult("%1", 0, false, content, 1.25, 30));
        AssertFits(lines, content => new PaneSnapshot(
            Pane(),
            content,
            10,
            2,
            false));
    }

    [Fact]
    public void Truncation_keeps_the_newest_text_and_reports_exact_loss()
    {
        string[] lines = Enumerable.Range(0, 100)
            .Select(index => $"{index:D3}: {new string('x', 40)}")
            .ToArray();

        CaptureResult result = StructuredTextResultBudget.Fit(
            lines,
            maxLines: 40,
            MaxBytes,
            content => new CaptureResult("%1", content),
            "test-capture");

        Assert.True(result.Content.Truncated);
        Assert.EndsWith("099: " + new string('x', 40), result.Content.Lines[^1], StringComparison.Ordinal);
        Assert.Equal(
            JoinedUtf8ByteCount(lines) - JoinedUtf8ByteCount(result.Content.Lines),
            result.Content.DroppedBytes);
        Assert.True(result.Content.DroppedLines >= 60);
        AssertFits(result);
    }

    [Fact]
    public void Fixed_metadata_that_cannot_fit_fails_with_operator_guidance()
    {
        McpException error = Assert.Throws<McpException>(() =>
            StructuredTextResultBudget.Fit(
                ["small"],
                maxLines: 10,
                MaxBytes,
                content => new CaptureResult(new string('x', MaxBytes), content),
                "test-capture"));

        Assert.Contains("test-capture metadata", error.Message, StringComparison.Ordinal);
        Assert.Contains(ServerPolicy.MaxBytesVariable, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_huge_logical_line_is_only_refined_after_it_is_bounded()
    {
        string huge = new('x', 1_000_000);
        _ = StructuredTextResultBudget.Fit(
            ["warmup"],
            maxLines: 10,
            MaxBytes,
            content => new CaptureResult("%1", content),
            "test-capture");
        long before = GC.GetAllocatedBytesForCurrentThread();

        CaptureResult result = StructuredTextResultBudget.Fit(
            [huge],
            maxLines: 10,
            MaxBytes,
            content => new CaptureResult("%1", content),
            "test-capture");

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.EndsWith("xxxx", result.Content.Lines[^1], StringComparison.Ordinal);
        Assert.True(allocated < 2_000_000, $"allocated {allocated:N0} bytes");
        AssertFits(result);
    }

    [Fact]
    public void Maximum_policy_refinement_allocates_a_small_multiple_of_the_result()
    {
        const int LargeBudget = 4_000_000;
        string huge = new('x', 5_000_000);
        _ = StructuredTextResultBudget.Fit(
            ["warmup"],
            maxLines: 10,
            LargeBudget,
            content => new CaptureResult("%1", content),
            "test-capture");
        long before = GC.GetAllocatedBytesForCurrentThread();

        CaptureResult result = StructuredTextResultBudget.Fit(
            [huge],
            maxLines: 10,
            LargeBudget,
            content => new CaptureResult("%1", content),
            "test-capture");

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(result.Content.Lines[0].Length > 1_500_000);
        Assert.True(allocated < 64_000_000, $"allocated {allocated:N0} bytes");
        Assert.True(
            Utf8JsonBudget.GetStructuredToolResultByteCount(result, ToolJson.Options)
            <= LargeBudget);
    }

    [Fact]
    public void Refinement_uses_headroom_when_older_text_escapes_more_than_the_tail()
    {
        const int LargeBudget = 4_000_000;
        string controls = new('\u0001', 500_000);
        string newest = new('x', 2_000_000);

        CaptureResult result = StructuredTextResultBudget.Fit(
            [controls, newest],
            maxLines: 10,
            LargeBudget,
            content => new CaptureResult("%1", content),
            "test-capture");

        Assert.Single(result.Content.Lines);
        Assert.True(
            result.Content.Lines[0].Length > 1_900_000,
            $"retained {result.Content.Lines[0].Length:N0} characters");
        Assert.EndsWith("xxxx", result.Content.Lines[0], StringComparison.Ordinal);
        Assert.True(
            Utf8JsonBudget.GetStructuredToolResultByteCount(result, ToolJson.Options)
            <= LargeBudget);
    }

    [Fact]
    public void Refinement_falls_back_below_an_escape_cost_discontinuity()
    {
        string newest = new('"', 274);

        CaptureResult result = StructuredTextResultBudget.Fit(
            [new string('x', 2_046), newest],
            maxLines: 10,
            MaxBytes,
            content => new CaptureResult("%1", content),
            "test-capture");

        string retained = Assert.Single(result.Content.Lines);
        Assert.EndsWith(new string('"', 4), retained, StringComparison.Ordinal);
        Assert.InRange(retained.Length, 200, newest.Length);
        AssertFits(result);
    }

    [Fact]
    public void Refinement_crosses_a_whole_line_plateau_to_use_available_headroom()
    {
        string[] lines =
        [
            new string('\\', 686),
            new string('\u0001', 1_088),
            new string('x', 422),
            new string('x', 187),
        ];

        CaptureResult result = StructuredTextResultBudget.Fit(
            lines,
            maxLines: 10,
            MaxBytes,
            content => new CaptureResult("%1", content),
            "test-capture");

        Assert.True(result.Content.Lines.Count >= 2);
        Assert.Equal(422, result.Content.Lines[^2].Length);
        Assert.Equal(187, result.Content.Lines[^1].Length);
        AssertFits(result);
    }

    [Fact]
    public void Refinement_keeps_a_long_boundary_suffix_before_a_short_newest_line()
    {
        CaptureResult result = StructuredTextResultBudget.Fit(
            [new string('x', 10_000), new string('y', 100)],
            maxLines: null,
            MaxBytes,
            content => new CaptureResult("%1", content),
            "test-capture");

        Assert.Equal(2, result.Content.Lines.Count);
        Assert.True(result.Content.Lines[0].Length > 1_000);
        Assert.Equal(new string('y', 100), result.Content.Lines[1]);
        AssertFits(result);
    }

    [Fact]
    public void One_byte_candidate_at_the_metadata_boundary_falls_back_without_throwing()
    {
        int low = 1;
        int high = MaxBytes;
        int best = 0;
        BoundedText empty = BoundedText.Fit(["xx"], 0, 1);
        while (low <= high)
        {
            int length = low + ((high - low) / 2);
            var probe = new PaneSnapshot(PaneWithPath(length), empty, 0, 0, false);
            if (Utf8JsonBudget.GetStructuredToolResultByteCount(probe, ToolJson.Options)
                <= MaxBytes)
            {
                best = length;
                low = length + 1;
            }
            else
            {
                high = length - 1;
            }
        }

        PaneSnapshot result = StructuredTextResultBudget.Fit(
            ["xx"],
            maxLines: 10,
            MaxBytes,
            content => new PaneSnapshot(PaneWithPath(best), content, 0, 0, false),
            "test-snapshot");

        Assert.True(result.Content.Truncated);
        AssertFits(result);
    }

    private static void AssertFits<T>(IReadOnlyList<string> lines, Func<BoundedText, T> create)
    {
        T result = StructuredTextResultBudget.Fit(
            lines,
            maxLines: 100,
            MaxBytes,
            create,
            "test-result");

        System.Reflection.PropertyInfo contentProperty = typeof(T).GetProperty("Content")
            ?? typeof(T).GetProperty("Output")
            ?? typeof(T).GetProperty("Tail")
            ?? throw new InvalidOperationException("The result has no bounded text property.");
        var content = Assert.IsType<BoundedText>(contentProperty.GetValue(result));
        Assert.True(content.Truncated);
        Assert.EndsWith("line 199: \\\"quoted\\\" \\\\ path \U0001f642", content.Lines[^1], StringComparison.Ordinal);
        AssertFits(result);
    }

    private static void AssertFits<T>(T result) =>
        Assert.True(
            Utf8JsonBudget.GetStructuredToolResultByteCount(result, ToolJson.Options) <= MaxBytes);

    // The cursor a tall pane issues is the widest field a tail result carries,
    // so a placeholder would stop measuring the case that fails first.
    private static string WidestCursor()
    {
        var connection = new TmuxConnection(
            new ServerConnectionOptions(socketName: "budget-cursor"),
            FakeMultiplexer.AnsweringVersion(static (request, _) => Task.FromResult(new TmuxCommandResult(
                request.LogicalArguments,
                0,
                ReadOnlyMemory<byte>.Empty,
                ReadOnlyMemory<byte>.Empty,
                [],
                []))));
        var generation = new ServerGeneration(int.MaxValue, long.MaxValue);
        var pane = new Pane(
            new Server(connection, generation, "tmux 3.7"),
            connection,
            generation,
            new PaneId(99_999),
            new Dictionary<string, string?>(StringComparer.Ordinal));
        return TailCursor.Build(
                pane,
                new PaneGridState(
                    int.MaxValue.ToString(CultureInfo.InvariantCulture),
                    2,
                    50_000,
                    20_000,
                    1,
                    false,
                    false),
                [.. Enumerable.Repeat(new string('\u0416', 80), 200)])
            .Encode();
    }

    private static PaneInfo Pane() => new(
        "%1",
        "@1",
        "$1",
        0,
        80,
        24,
        "shell",
        true,
        false,
        false,
        false,
        "bash",
        "/tmp",
        123,
        10,
        2_000,
        false);

    private static PaneInfo PaneWithPath(int pathLength) => new(
        "%0",
        "@0",
        "$0",
        0,
        1,
        1,
        null,
        false,
        false,
        false,
        false,
        null,
        new string('p', pathLength),
        null,
        null,
        null,
        false);

    private static int JoinedUtf8ByteCount(IReadOnlyList<string> lines) =>
        Encoding.UTF8.GetByteCount(string.Join('\n', lines));
}
