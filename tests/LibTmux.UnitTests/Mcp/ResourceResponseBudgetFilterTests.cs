using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using LibTmux.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LibTmux.UnitTests;

public sealed class ResourceResponseBudgetFilterTests
{
    [Fact]
    public async Task Oversized_resource_content_is_refused_with_budget_guidance()
    {
        var policy = new ServerPolicy { MaxBytes = 4_000 };
        var original = new ReadResourceResult
        {
            Contents =
            [
                new TextResourceContents
                {
                    Uri = "budget://oversized",
                    MimeType = "application/json",
                    Text = new string('x', 8_000),
                },
            ],
        };
        McpRequestHandler<ReadResourceRequestParams, ReadResourceResult> handler =
            ResourceResponseBudgetFilter.Create(policy)(
                (_, _) => ValueTask.FromResult(original));

        McpException error = await Assert.ThrowsAsync<McpException>(
            () => handler(null!, TestContext.Current.CancellationToken).AsTask());

        Assert.Contains(ServerPolicy.MaxBytesVariable, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_resource_that_fits_is_not_rewritten()
    {
        var policy = new ServerPolicy { MaxBytes = 4_000 };
        var original = new ReadResourceResult
        {
            Contents =
            [
                new TextResourceContents
                {
                    Uri = "budget://fits",
                    MimeType = "application/json",
                    Text = "null",
                },
            ],
        };
        McpRequestHandler<ReadResourceRequestParams, ReadResourceResult> handler =
            ResourceResponseBudgetFilter.Create(policy)(
                (_, _) => ValueTask.FromResult(original));

        ReadResourceResult actual = await handler(
            null!,
            TestContext.Current.CancellationToken);

        Assert.Same(original, actual);
    }

    [Fact]
    public async Task Resource_protocol_results_stay_within_the_complete_byte_ceiling()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ResourceProtocolHarness harness = await ResourceProtocolHarness.StartAsync(token);
        int offset = harness.WireLength;

        ReadResourceResult result = await harness.Client.ReadResourceAsync(
            "budget://boundary",
            cancellationToken: token);

        Assert.NotEmpty(result.Contents);
        Assert.InRange(harness.ResourceResultBytesSince(offset), 1, 4_000);
    }

    [McpServerResourceType]
    private sealed class BudgetProbeResources
    {
        [McpServerResource(
            UriTemplate = "budget://boundary",
            Name = "budget_resource_boundary",
            MimeType = "text/plain")]
        public static string Boundary() => new('x', 3_000);
    }

    private sealed class ResourceProtocolHarness : IAsyncDisposable
    {
        private readonly McpServer _server;
        private readonly RecordingWriteStream _wire;
        private readonly ServiceProvider _services;

        private ResourceProtocolHarness(
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

        internal int ResourceResultBytesSince(int offset)
        {
            foreach (string frame in Encoding.UTF8.GetString(_wire.Snapshot(offset))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using JsonDocument document = JsonDocument.Parse(frame);
                if (document.RootElement.TryGetProperty("result", out JsonElement result)
                    && result.TryGetProperty("contents", out _))
                {
                    return Encoding.UTF8.GetByteCount(result.GetRawText());
                }
            }

            throw new InvalidOperationException("No resource result was written.");
        }

        internal static async Task<ResourceProtocolHarness> StartAsync(
            CancellationToken cancellationToken)
        {
            ServiceCollection services = new();
            services.AddLogging();
            services
                .AddMcpServer()
                .WithResources<BudgetProbeResources>()
                .WithRequestFilters(filters => filters.AddReadResourceFilter(
                    ResourceResponseBudgetFilter.Create(
                        new ServerPolicy { MaxBytes = 4_000 })));
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

            return new ResourceProtocolHarness(server, client, wire, provider);
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
