using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text;
using LibTmux.Internal;
using LibTmux.Mcp;

using LibTmux.UnitTests.Connection;

namespace LibTmux.UnitTests;

[UnsupportedOSPlatform("windows")]
public sealed class ReadToolsHistoryTests
{
    private const string HistoryOnlyLine = "archived failure from scrollback";

    [Fact]
    public async Task Capture_history_asks_tmux_for_the_beginning_and_returns_oldest_content()
    {
        await using var fixture = new HistoryFixture();

        CaptureResult result = await fixture.Tools.CapturePaneAsync(
            paneId: "%1",
            includeHistory: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(HistoryOnlyLine, result.Content.Lines);
        AssertBeginningOfHistoryCapture(fixture.Commands);
    }

    [Fact]
    public async Task Search_history_asks_tmux_for_the_beginning_and_finds_oldest_content()
    {
        await using var fixture = new HistoryFixture();

        SearchResult result = await fixture.Tools.SearchPanesAsync(
            pattern: "archived failure",
            includeHistory: true,
            ignoreCase: false,
            cancellationToken: TestContext.Current.CancellationToken);

        PaneMatch pane = Assert.Single(result.Panes);
        MatchedLine match = Assert.Single(pane.Matches);
        Assert.Equal(HistoryOnlyLine, match.Text);
        Assert.Equal(-1, match.Row);
        AssertBeginningOfHistoryCapture(fixture.Commands);
    }

    [Fact]
    public async Task Streaming_full_history_sentinel_reaches_the_beginning()
    {
        await using var fixture = new HistoryFixture();
        CancellationToken token = TestContext.Current.CancellationToken;
        Pane pane = Assert.Single(await fixture.Server.GetPanesAsync(token));

        IReadOnlyList<string> lines = await PaneReader.CaptureAsync(
            pane,
            int.MinValue,
            token);

        Assert.Contains(HistoryOnlyLine, lines);
        AssertBeginningOfHistoryCapture(fixture.Commands);
    }

    [Fact]
    public void Streaming_anchor_scan_observes_cancellation_between_candidates()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var rows = new CancellingRows(cancellation, cancelAt: 8, count: 1_000);
        var cursor = new TailCursor(
            Version: 3,
            EndpointFingerprint: "endpoint",
            ServerProcessId: 1,
            ServerStartTime: 2,
            PaneId: "%1",
            PanePid: "3",
            HistorySize: 1_000,
            PaneHeight: 24,
            AnchorAbsolute: 500,
            AnchorHash: TailCursor.HashLine("absent anchor"),
            BelowCount: 0,
            BelowHash: null,
            SuffixCount: 0,
            SuffixHash: null,
            RowHashes: null);

        Assert.Throws<OperationCanceledException>(() =>
            PaneReader.FindUniqueAnchor(rows, cursor, cancellation.Token));
        Assert.InRange(rows.Reads, 8, 9);
    }

    private static void AssertBeginningOfHistoryCapture(
        IEnumerable<string[]> commands)
    {
        string[] command = Assert.Single(
            commands,
            static arguments => arguments.Contains(
                "capture-pane",
                StringComparer.Ordinal));
        int captureIndex = Array.IndexOf(command, "capture-pane");

        Assert.True(captureIndex >= 0);
        Assert.True(
            HasBeginningOfHistory(command, captureIndex),
            $"Expected capture-pane -S -, got: {string.Join(' ', command[captureIndex..])}");
    }

    private static bool HasBeginningOfHistory(string[] arguments, int start) =>
        Enumerable.Range(start, arguments.Length - start - 1)
            .Any(index => arguments[index] == "-S" && arguments[index + 1] == "-");

    private sealed class CancellingRows(
        CancellationTokenSource cancellation,
        int cancelAt,
        int count) : IReadOnlyList<string>
    {
        public int Count { get; } = count;

        internal int Reads { get; private set; }

        public string this[int index]
        {
            get
            {
                Reads++;
                if (index == cancelAt)
                {
                    cancellation.Cancel();
                }

                return $"row {index}";
            }
        }

        public IEnumerator<string> GetEnumerator() =>
            Enumerable.Range(0, Count).Select(index => this[index]).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }

    private sealed class HistoryFixture : IAsyncDisposable
    {
        private static readonly ServerGeneration Generation = new(71, 701);
        private static readonly IReadOnlyList<string> HistoryLines =
        [
            HistoryOnlyLine,
            .. Enumerable.Range(0, 24).Select(static index => $"visible line {index:D2}"),
        ];

        private readonly TmuxConnectionAccessor _accessor;
        private readonly PaneActivityHub _activity = new();

        internal HistoryFixture()
        {
            var connection = new TmuxConnection(
                new ServerConnectionOptions(socketName: "history-test"),
                FakeMultiplexer.AnsweringVersion(ExecuteAsync));
            var server = new Server(connection, Generation, "tmux 3.7");
            Server = server;
            _accessor = new TmuxConnectionAccessor(server);
            Tools = new ReadTools(
                _accessor,
                new ServerPolicy { MaxBytes = 128_000 },
                _activity);
        }

        internal ConcurrentQueue<string[]> Commands { get; } = new();

        internal ReadTools Tools { get; }

        internal Server Server { get; }

        public async ValueTask DisposeAsync()
        {
            await _activity.DisposeAsync().ConfigureAwait(false);
            _accessor.Dispose();
        }

        private Task<TmuxCommandResult> ExecuteAsync(
            TmuxCommandRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] arguments = [.. request.LogicalArguments];
            Commands.Enqueue(arguments);

            string payload = arguments.Contains("list-panes", StringComparer.Ordinal)
                ? PaneListing()
                : arguments.Contains("capture-pane", StringComparer.Ordinal)
                    ? PaneCapture(arguments)
                    : string.Empty;
            string output = $"{Generation.ProcessId}:{Generation.StartTime}\n{payload}";
            return Task.FromResult(Result(arguments, output));
        }

        private static string PaneListing()
        {
            FormatProjection projection = FormatProjection.Create(
                "list-panes",
                TmuxVersion.Parse("3.7"));
            return string.Concat(projection.Fields.Select(
                static field => FieldValue(field.WireName) + FormatProjection.RowSeparator)) + "\n";
        }

        private static string PaneCapture(string[] arguments)
        {
            int commandIndex = Array.IndexOf(arguments, "capture-pane");
            bool fromBeginning = HasBeginningOfHistory(arguments, commandIndex);
            IReadOnlyList<string> lines = fromBeginning
                ? HistoryLines
                : HistoryLines.Skip(1).ToArray();
            return string.Join('\n', lines) + "\n";
        }

        private static string FieldValue(string field) => field switch
        {
            "pid" => Generation.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "start_time" => Generation.StartTime.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            "session_id" => "$1",
            "window_id" => "@1",
            "pane_id" => "%1",
            "pane_width" => "80",
            "pane_height" => "24",
            "pane_active" => "1",
            _ => string.Empty,
        };

        private static TmuxCommandResult Result(
            IReadOnlyList<string> arguments,
            string standardOutput)
        {
            byte[] output = Encoding.UTF8.GetBytes(standardOutput);
            return new TmuxCommandResult(
                arguments,
                0,
                output,
                ReadOnlyMemory<byte>.Empty,
                Utf8BackslashDecoder.ProjectOutputLines(output),
                []);
        }
    }
}
