using System.IO.Pipelines;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LibTmux.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LibTmux.UnitTests;

[UnsupportedOSPlatform("windows")]
public sealed class ToolResponseBudgetFilterTests
{
    [Fact]
    public void Mutation_advice_is_separated_from_tmux_own_wording()
    {
        TmuxCommandResult result = new(
            ["move-window"],
            1,
            ReadOnlyMemory<byte>.Empty,
            ReadOnlyMemory<byte>.Empty,
            [],
            ["index in use: 0"]);
        TmuxCommandException error = new("move-window failed: index in use: 0", result);

        string advice = ToolFailureFilter.ActionableAdvice(
            "move_window",
            error,
            mayModify: true,
            "tmux refused the command: move-window failed: index in use: 0");

        // tmux ends its refusals without punctuation, so the warning ran into
        // them: "index in use: 0 tmux may have acted before the failure."
        Assert.Contains("in use: 0. tmux may have acted", advice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Oversized_text_and_structured_content_are_replaced_by_a_bounded_error()
    {
        var policy = new ServerPolicy { MaxBytes = 4_000 };
        string oversized = string.Concat(Enumerable.Repeat("\U0001f642", 3_000));
        var original = new CallToolResult
        {
            Content = [new TextContentBlock { Text = oversized }],
            StructuredContent = JsonSerializer.SerializeToElement(new { value = oversized }),
        };
        McpRequestHandler<CallToolRequestParams, CallToolResult> handler =
            ToolResponseBudgetFilter.Create(policy)(
                (_, _) => ValueTask.FromResult(original));

        CallToolResult filtered = await handler(null!, TestContext.Current.CancellationToken);

        Assert.True(filtered.IsError);
        Assert.Null(filtered.StructuredContent);
        string message = Assert.IsType<TextContentBlock>(Assert.Single(filtered.Content)).Text;
        Assert.Contains(ServerPolicy.MaxBytesVariable, message, StringComparison.Ordinal);
        Assert.True(Utf8JsonBudget.Fits(filtered, policy.MaxBytes, ToolJson.Options));
    }

    [Fact]
    public async Task A_result_that_fits_is_not_rewritten()
    {
        var policy = new ServerPolicy { MaxBytes = 4_000 };
        var original = new CallToolResult
        {
            Content = [new TextContentBlock { Text = "small" }],
            StructuredContent = JsonSerializer.SerializeToElement(new { value = "small" }),
        };
        McpRequestHandler<CallToolRequestParams, CallToolResult> handler =
            ToolResponseBudgetFilter.Create(policy)(
                (_, _) => ValueTask.FromResult(original));

        CallToolResult filtered = await handler(null!, TestContext.Current.CancellationToken);

        Assert.Same(original, filtered);
    }

    [Fact]
    public async Task Oversized_action_acknowledgement_stays_successful_after_one_dispatch()
    {
        var policy = new ServerPolicy { MaxBytes = 4_000 };
        var action = new ActionResult(
            new string('x', 8_000),
            PaneId: "%7",
            WindowId: "@8",
            SessionId: "$9");
        JsonElement structured = JsonSerializer.SerializeToElement(action, ToolJson.Options);
        var original = new CallToolResult
        {
            Content = [new TextContentBlock { Text = structured.GetRawText() }],
            StructuredContent = structured,
        };
        int dispatches = 0;
        McpRequestHandler<CallToolRequestParams, CallToolResult> handler =
            ToolResponseBudgetFilter.Create(policy)(
                (_, _) =>
                {
                    dispatches++;
                    return ValueTask.FromResult(original);
                });

        CallToolResult filtered = await handler(null!, TestContext.Current.CancellationToken);

        Assert.Equal(1, dispatches);
        Assert.NotEqual(true, filtered.IsError);
        Assert.True(Utf8JsonBudget.FitsToolResult(filtered, policy.MaxBytes, ToolJson.Options));
        JsonElement bounded = Assert.IsType<JsonElement>(filtered.StructuredContent);
        ActionResult acknowledgement = bounded.Deserialize<ActionResult>(ToolJson.Options)
            ?? throw new InvalidOperationException("The action acknowledgement was null.");
        Assert.Contains("completed", acknowledgement.Changed, StringComparison.Ordinal);
        Assert.Contains("Do not retry", acknowledgement.Changed, StringComparison.Ordinal);
        Assert.DoesNotContain(action.Changed, acknowledgement.Changed, StringComparison.Ordinal);
        Assert.Equal(action.PaneId, acknowledgement.PaneId);
        Assert.Equal(action.WindowId, acknowledgement.WindowId);
        Assert.Equal(action.SessionId, acknowledgement.SessionId);
    }

    [Fact]
    public async Task Oversized_mutating_error_keeps_conservative_retry_advice()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        BudgetProbeTools.LargeWriteErrorCalls = 0;
        await using BudgetProtocolHarness harness = await BudgetProtocolHarness.StartAsync(token);

        CallToolResult result = await harness.Client.CallToolAsync(
            "budget_probe_large_write_error",
            cancellationToken: token);

        Assert.True(result.IsError);
        Assert.Equal(1, BudgetProbeTools.LargeWriteErrorCalls);
        string message = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("tmux may have acted", message, StringComparison.Ordinal);
        Assert.Contains("Do not retry", message, StringComparison.Ordinal);
        Assert.True(Utf8JsonBudget.FitsToolResult(result, 4_000, ToolJson.Options));
    }

    [Fact]
    public async Task Oversized_read_error_does_not_claim_that_tmux_mutated_state()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using BudgetProtocolHarness harness = await BudgetProtocolHarness.StartAsync(token);

        CallToolResult result = await harness.Client.CallToolAsync(
            "budget_probe_large_read_error",
            cancellationToken: token);

        Assert.True(result.IsError);
        string message = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("The read failed", message, StringComparison.Ordinal);
        Assert.DoesNotContain("may have acted", message, StringComparison.Ordinal);
        Assert.Contains(ServerPolicy.MaxBytesVariable, message, StringComparison.Ordinal);
        Assert.True(Utf8JsonBudget.FitsToolResult(result, 4_000, ToolJson.Options));
    }

    [Fact]
    public async Task Paste_primary_and_cleanup_failure_names_the_owned_buffer_on_the_wire()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using BudgetProtocolHarness harness = await BudgetProtocolHarness.StartAsync(token);

        CallToolResult result = await harness.Client.CallToolAsync(
            "budget_probe_paste_cleanup_failure",
            cancellationToken: token);

        Assert.True(result.IsError);
        string message = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains(BudgetProbeTools.PasteBuffer, message, StringComparison.Ordinal);
        Assert.Contains("may still contain", message, StringComparison.Ordinal);
        Assert.Contains("Do not retry", message, StringComparison.Ordinal);
        Assert.Contains("tmux delete-buffer -b", message, StringComparison.Ordinal);
        Assert.True(Utf8JsonBudget.FitsToolResult(result, 4_000, ToolJson.Options));
    }

    [Fact]
    public void Dispatch_advice_is_conservative_only_for_mutating_tools()
    {
        var error = new TmuxTransportException(
            "the client disappeared",
            ["send-keys"],
            TmuxDispatchState.Unknown);

        string read = ToolFailureFilter.ActionableAdvice(
            "capture_pane",
            error,
            mayModify: false,
            "The read failed.");
        string write = ToolFailureFilter.ActionableAdvice(
            "run_shell_command",
            error,
            mayModify: true,
            "The dispatch failed.");

        Assert.Equal("The read failed.", read);
        Assert.Contains("Do not retry", write, StringComparison.Ordinal);
        Assert.Contains("Inspect tmux state first", write, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Oversized_protocol_metadata_is_filtered_with_the_rest_of_the_result()
    {
        var policy = new ServerPolicy { MaxBytes = 4_000 };
        var original = new CallToolResult
        {
            Content = [new TextContentBlock { Text = "small" }],
        };
        System.Reflection.PropertyInfo metaProperty = typeof(CallToolResult).GetProperty("Meta")
            ?? throw new InvalidOperationException("CallToolResult.Meta was not found.");
        object? metadata = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(new { blob = new string('m', 8_000) }),
            metaProperty.PropertyType,
            ToolJson.Options);
        Assert.NotNull(metadata);
        metaProperty.SetValue(original, metadata);
        McpRequestHandler<CallToolRequestParams, CallToolResult> handler =
            ToolResponseBudgetFilter.Create(policy)(
                (_, _) => ValueTask.FromResult(original));

        CallToolResult filtered = await handler(null!, TestContext.Current.CancellationToken);

        Assert.True(filtered.IsError);
        Assert.Null(filtered.Meta);
        Assert.True(Utf8JsonBudget.Fits(filtered, policy.MaxBytes, ToolJson.Options));
    }

    [Fact]
    public async Task The_wire_protocol_never_receives_an_oversized_tool_result()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using BudgetProtocolHarness harness = await BudgetProtocolHarness.StartAsync(token);

        CallToolResult result = await harness.Client.CallToolAsync(
            "budget_probe_large",
            cancellationToken: token);

        Assert.True(result.IsError);
        Assert.Null(result.StructuredContent);
        string message = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("Narrow", message, StringComparison.Ordinal);
        Assert.True(Utf8JsonBudget.Fits(result, 4_000, ToolJson.Options));
    }

    [Fact]
    public async Task Read_batch_uses_its_fixed_aggregate_wire_budget()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using BudgetProtocolHarness harness = await BudgetProtocolHarness.StartAsync(token);

        CallToolResult result = await harness.Client.CallToolAsync(
            "call_read_tools_batch",
            cancellationToken: token);

        Assert.NotEqual(true, result.IsError);
        JsonElement structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal(1, structured.GetProperty("results").GetArrayLength());
        Assert.False(structured.GetProperty("truncated").GetBoolean());
        Assert.True(Utf8JsonBudget.GetByteCount(result, ToolJson.Options) > 4_000);
    }

    [Fact]
    public async Task Structured_result_accounting_matches_the_sdk_wire_shape()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using BudgetProtocolHarness harness = await BudgetProtocolHarness.StartAsync(token);

        CallToolResult actual = await harness.Client.CallToolAsync(
            "budget_probe_search",
            cancellationToken: token);
        SearchResult value = BudgetProbeTools.Search();

        int actualBytes = Utf8JsonBudget.GetByteCount(actual, ToolJson.Options);
        int budgetedBytes = Utf8JsonBudget.GetStructuredToolResultByteCount(
            value,
            ToolJson.Options);

        Assert.True(actualBytes <= budgetedBytes);
        Assert.True(budgetedBytes - actualBytes < Utf8JsonBudget.ProtocolMetadataReserve);
        Assert.InRange(budgetedBytes, 3_500, 4_000);
        Assert.InRange(actualBytes, 1, 4_000);
        Assert.NotEqual(true, actual.IsError);
    }

    [Theory]
    [InlineData("plain ASCII")]
    [InlineData("quote \" slash \\\\ control \\n tab \\t")]
    [InlineData("emoji 🙂 CJK 雪 HTML <>&")]
    public void Streaming_structured_accounting_matches_materialized_json(string value)
    {
        var result = new BudgetProbeResult(value);
        JsonElement structured = JsonSerializer.SerializeToElement(result, ToolJson.Options);
        var materialized = new CallToolResult
        {
            Content = [new TextContentBlock { Text = structured.GetRawText() }],
            StructuredContent = structured,
        };

        int expected = checked(
            Utf8JsonBudget.GetByteCount(materialized, ToolJson.Options)
            + Utf8JsonBudget.ProtocolMetadataReserve);

        Assert.Equal(
            expected,
            Utf8JsonBudget.GetStructuredToolResultByteCount(result, ToolJson.Options));
    }

    [Theory]
    [InlineData("plain ASCII")]
    [InlineData("quote \" slash \\\\ control \\n tab \\t")]
    [InlineData("emoji 🙂 CJK 雪 HTML <>&")]
    public void Streaming_fragment_accounting_matches_materialized_json(string value)
    {
        var result = new BudgetProbeResult(value);
        byte[] raw = JsonSerializer.SerializeToUtf8Bytes(result, ToolJson.Options);
        string text = Encoding.UTF8.GetString(raw);
        int embeddedContentBytes = checked(
            JsonSerializer.SerializeToUtf8Bytes(text, ToolJson.Options).Length - 2);

        Assert.Equal(
            checked(raw.Length + embeddedContentBytes),
            Utf8JsonBudget.GetStructuredJsonFragmentByteCount(result, ToolJson.Options));
    }

    [Fact]
    public async Task Task_result_wrapper_stays_within_the_complete_byte_ceiling()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using BudgetProtocolHarness harness = await BudgetProtocolHarness.StartAsync(token);
        int wireOffset = harness.WireLength;

        CallToolResult actual = await harness.Client.CallToolWithPollingAsync(
            new CallToolRequestParams { Name = "budget_probe_boundary" },
            cancellationToken: token);

        Assert.NotEqual(true, actual.IsError);
        Assert.InRange(harness.CompletedTaskResultBytesSince(wireOffset), 1, 4_000);
    }

    [McpServerToolType]
    private sealed class BudgetProbeTools
    {
        internal const string PasteBuffer = "libtmux_mcp_0123456789ab";

        internal static int LargeWriteErrorCalls { get; set; }

        [McpServerTool(Name = "budget_probe_large", UseStructuredContent = true)]
        public static BudgetProbeResult Large() => new(new string('x', 16_000));

        [McpServerTool(Name = "call_read_tools_batch", UseStructuredContent = true)]
        public static ReadToolBatchResult ReadBatch() => new(
            [
                new ReadToolCallResult(
                    Index: 0,
                    Tool: "capture_pane",
                    Success: true,
                    Error: null,
                    Result: JsonValue.Create(new string('b', 16_000)),
                    ResultTruncated: false),
            ],
            Succeeded: 1,
            Failed: 0,
            StoppedAt: null,
            Truncated: false,
            TruncatedBytes: 0,
            OnError: "stop");

        [McpServerTool(Name = "budget_probe_search", UseStructuredContent = true)]
        public static SearchResult Search()
        {
            var budget = new SearchResultBudget("quote \\\" and \U0001f642", 2, 100, 4_000);
            List<MatchedLine> matches = [];
            int row = 0;
            while (true)
            {
                var match = new MatchedLine(row, $"line {row}: \\\"\\n\U0001f642\U0001f642");
                if (budget.TryAdd("%1", "@1", "$1", matches, match)
                    != SearchMatchBudgetOutcome.Added)
                {
                    break;
                }

                row++;
            }

            budget.Commit("%1", "@1", "$1", matches);
            return budget.Build(2, truncated: true);
        }

        [McpServerTool(Name = "budget_probe_boundary", UseStructuredContent = true)]
        public static BudgetProbeResult Boundary()
        {
            BudgetProbeResult best = new(string.Empty);
            int low = 1;
            int high = 4_000;
            while (low <= high)
            {
                int length = low + ((high - low) / 2);
                var candidate = new BudgetProbeResult(new string('x', length));
                if (Utf8JsonBudget.GetStructuredToolResultByteCount(candidate, ToolJson.Options)
                    <= 4_000)
                {
                    best = candidate;
                    low = length + 1;
                }
                else
                {
                    high = length - 1;
                }
            }

            return best;
        }

        [McpServerTool(
            Name = "budget_probe_large_write_error",
            Destructive = true,
            OpenWorld = false,
            UseStructuredContent = true)]
        public static BudgetProbeResult LargeWriteError()
        {
            LargeWriteErrorCalls++;
            throw new TmuxTransportException(
                new string('w', 16_000),
                ["send-keys"],
                TmuxDispatchState.Unknown);
        }

        [McpServerTool(
            Name = "budget_probe_large_read_error",
            ReadOnly = true,
            OpenWorld = false,
            UseStructuredContent = true)]
        public static BudgetProbeResult LargeReadError() =>
            throw new TmuxTransportException(
                new string('r', 16_000),
                ["capture-pane"],
                TmuxDispatchState.Unknown);

        [McpServerTool(
            Name = "budget_probe_paste_cleanup_failure",
            Destructive = true,
            OpenWorld = true,
            UseStructuredContent = true)]
        public static BudgetProbeResult PasteCleanupFailure()
        {
            var error = new InvalidOperationException(new string('p', 16_000));
            error.Data[WriteTools.PasteBufferCleanupFailureDataKey] =
                new IOException("delete-buffer failed");
            error.Data[WriteTools.PasteBufferCleanupBufferDataKey] = PasteBuffer;
            throw error;
        }
    }

    private sealed record BudgetProbeResult(string Value);

    private sealed class BudgetProtocolHarness : IAsyncDisposable
    {
        private readonly McpServer _server;
        private readonly RecordingWriteStream _wire;
        private readonly ServiceProvider _services;

        private BudgetProtocolHarness(
            McpServer server,
            McpClient client,
            RecordingWriteStream wire,
            ServiceProvider services)
        {
            _server = server;
            Client = client;
            _wire = wire;
            _services = services;
        }

        internal McpClient Client { get; }

        internal int WireLength => _wire.RecordedLength;

        internal int CompletedTaskResultBytesSince(int offset)
        {
            foreach (string frame in Encoding.UTF8.GetString(_wire.Snapshot(offset))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using JsonDocument document = JsonDocument.Parse(frame);
                if (document.RootElement.TryGetProperty("result", out JsonElement result)
                    && result.TryGetProperty("status", out JsonElement status)
                    && status.ValueEquals("completed"u8)
                    && result.TryGetProperty("result", out _))
                {
                    return Encoding.UTF8.GetByteCount(result.GetRawText());
                }
            }

            throw new InvalidOperationException("No completed task result was written.");
        }

        internal static async Task<BudgetProtocolHarness> StartAsync(
            CancellationToken cancellationToken)
        {
            ServiceCollection services = new();
            services.AddLogging();
            services
                .AddMcpServer()
                .WithTools<BudgetProbeTools>(ToolJson.Options)
                .WithRequestFilters(filters => filters.AddCallToolFilter(next =>
                    ToolResponseBudgetFilter.Create(
                        new ServerPolicy { MaxBytes = 4_000 })(
                            ToolFailureFilter.Create()(next))))
                .WithTasks(
                    new InMemoryMcpTaskStore(),
                    tasks => tasks.ExecutionModeSelector = _ => McpTaskExecutionMode.Optional);
            ServiceProvider provider = services.BuildServiceProvider();

            Pipe clientToServer = new();
            Pipe serverToClient = new();
            var wire = new RecordingWriteStream(serverToClient.Writer.AsStream());
            McpServer server = McpServer.Create(
                new StreamServerTransport(
                    clientToServer.Reader.AsStream(),
                    wire),
                provider.GetRequiredService<IOptions<McpServerOptions>>().Value,
                provider.GetRequiredService<ILoggerFactory>(),
                provider);
            _ = server.RunAsync(CancellationToken.None);
            McpClient client = await McpClient.CreateAsync(
                new StreamClientTransport(
                    clientToServer.Writer.AsStream(),
                    serverToClient.Reader.AsStream()),
                cancellationToken: cancellationToken);

            return new BudgetProtocolHarness(server, client, wire, provider);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync().ConfigureAwait(false);
            await _server.DisposeAsync().ConfigureAwait(false);
            await _services.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class RecordingWriteStream(Stream inner) : Stream
    {
        private readonly object _gate = new();
        private readonly MemoryStream _recording = new();

        internal int RecordedLength
        {
            get
            {
                lock (_gate)
                {
                    return checked((int)_recording.Length);
                }
            }
        }

        internal byte[] Snapshot(int offset)
        {
            lock (_gate)
            {
                return _recording.ToArray()[offset..];
            }
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            Record(buffer.AsSpan(offset, count));
            inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Record(buffer);
            inner.Write(buffer);
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            Record(buffer.Span);
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _recording.Dispose();
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private void Record(ReadOnlySpan<byte> bytes)
        {
            lock (_gate)
            {
                _recording.Write(bytes);
            }
        }
    }
}
