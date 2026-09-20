using System.Runtime.Versioning;
using System.Text;
using LibTmux.Internal;

namespace LibTmux.UnitTests.Connection;

[UnsupportedOSPlatform("windows")]
public sealed class ServerInspectionTests
{
    [Fact]
    public async Task Inspection_preserves_endpoint_and_both_versions_without_initializing()
    {
        var fixture = new Fixture();
        Server inspected = Assert.IsType<Server>(await fixture.Server.InspectAsync(TestContext.Current.CancellationToken));

        Assert.False(fixture.Server.IsMaterialized);
        Assert.Null(fixture.Server.DaemonVersion);
        Assert.Same(fixture.Server.Connection, inspected.Connection);
        Assert.Equal(1, fixture.FactoryCalls);
        Assert.Equal(0, fixture.InitializerCalls);
        Assert.Equal(new ServerGeneration(91, 901), inspected.Generation);
        Assert.Equal(TmuxVersion.Parse("3.7c"), inspected.Version);
        Assert.Equal(TmuxVersion.Parse("3.2a"), inspected.DaemonVersion);
        Assert.Null(inspected.SnapshotMetadata);
        Assert.False(inspected.Sessions.IsCaptured);
        Assert.Single(fixture.Requests);

        Server captured = await inspected.CaptureSnapshotAsync(SnapshotDepth.Server, TestContext.Current.CancellationToken);
        Assert.Equal(inspected.DaemonVersion, captured.DaemonVersion);
        Assert.Equal(0, fixture.InitializerCalls);
    }

    [Fact]
    public async Task Materialized_inspection_guards_the_generation_and_refuses_replacement()
    {
        var fixture = new Fixture();
        Server inspected = Assert.IsType<Server>(await fixture.Server.InspectAsync(TestContext.Current.CancellationToken));
        Server repeated = Assert.IsType<Server>(await inspected.InspectAsync(TestContext.Current.CancellationToken));
        Assert.NotSame(inspected, repeated);
        Assert.Contains("if-shell", fixture.Requests[^1].LogicalArguments);
        fixture.Replaced = true;

        StaleServerGenerationException error = await Assert.ThrowsAsync<StaleServerGenerationException>(
            () => inspected.InspectAsync(TestContext.Current.CancellationToken));

        Assert.Equal(inspected.Generation, error.Expected);
        Assert.Equal(new ServerGeneration(91, 902), error.Actual);
        Assert.Equal(0, fixture.InitializerCalls);
    }

    [Theory]
    [InlineData("no server running on /tmp/owned", true)]
    [InlineData("error connecting to /tmp/owned (No such file or directory)", true)]
    [InlineData("error connecting to /tmp/owned (Permission denied)", false)]
    [InlineData("error connecting to /tmp/no server running (Permission denied)", false)]
    [InlineData("error connecting to /tmp/No such file or directory (Permission denied)", false)]
    [InlineData("no server running on /tmp/owned\npermission failure", false)]
    [InlineData("server exited unexpectedly", false)]
    public async Task Only_verified_connection_absence_returns_null(string diagnostic, bool absent)
    {
        var fixture = new Fixture { Diagnostic = diagnostic };
        if (absent)
        {
            Assert.Null(await fixture.Server.InspectAsync(TestContext.Current.CancellationToken));
        }
        else
        {
            await Assert.ThrowsAsync<TmuxCommandException>(
                () => fixture.Server.InspectAsync(TestContext.Current.CancellationToken));
        }
        Assert.Equal(0, fixture.InitializerCalls);
    }

    [Theory]
    [InlineData("91:901\tbogus\n")]
    [InlineData("91:0\t3.2a\n")]
    [InlineData("91:901\t3.2a\nextra\n")]
    public async Task Malformed_inspection_does_not_publish_an_identity(string output)
    {
        var fixture = new Fixture { Output = output };
        await Assert.ThrowsAsync<TmuxProtocolException>(
            () => fixture.Server.InspectAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cancellation_prevents_dispatch_and_late_publication()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = new Fixture();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Server.InspectAsync(cancellation.Token));
        Assert.Empty(fixture.Requests);

        using var late = new CancellationTokenSource();
        fixture.Replied = late.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Server.InspectAsync(late.Token));
        Assert.Single(fixture.Requests);
    }

    private sealed class Fixture
    {
        internal Fixture()
        {
            var connection = new TmuxConnection(new ServerConnectionOptions
            {
                SocketNameFactory = () => $"inspection-{++FactoryCalls}",
                InitializeAsync = (_, _) =>
                {
                    InitializerCalls++;
                    throw new InvalidOperationException("Inspection ran an initializer.");
                },
            }, FakeMultiplexer.AnsweringVersion(ExecuteAsync, "tmux 3.7c\n"), () => "inspection_stale");
            Server = new Server(connection, null, null);
        }

        internal Server Server { get; }
        internal int FactoryCalls { get; private set; }
        internal int InitializerCalls { get; private set; }
        internal List<TmuxCommandRequest> Requests { get; } = [];
        internal string? Diagnostic { get; init; }
        internal string Output { get; init; } = "91:901\t3.2a\n";
        internal bool Replaced { get; set; }
        internal Action? Replied { get; set; }

        private Task<TmuxCommandResult> ExecuteAsync(TmuxCommandRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            bool guarded = request.LogicalArguments.Contains("if-shell", StringComparer.Ordinal);
            string output = request.LogicalArguments[^1] == TmuxConnection.GenerationFormat ? "91:901\n" : Output;
            int exitCode = 0;
            string error = string.Empty;
            if (Diagnostic is not null)
            {
                output = string.Empty;
                error = Diagnostic + "\n";
                exitCode = 1;
            }
            else if (guarded && Replaced)
            {
                output = "91:902\n";
                error = "unknown command: inspection_stale\n";
                exitCode = 1;
            }
            else if (guarded)
            {
                output = "91:901\n" + output;
            }

            Replied?.Invoke();
            byte[] stdout = Encoding.UTF8.GetBytes(output);
            byte[] stderr = Encoding.UTF8.GetBytes(error);
            return Task.FromResult(new TmuxCommandResult(request.LogicalArguments, exitCode, stdout, stderr,
                Utf8BackslashDecoder.ProjectOutputLines(stdout), Utf8BackslashDecoder.ProjectErrorLines(stderr)));
        }
    }
}
