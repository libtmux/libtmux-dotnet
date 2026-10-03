using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Mcp;
using LibTmux.Testing;

namespace LibTmux.IntegrationTests;

[Collection("tmux control clients")]
[UnsupportedOSPlatform("windows")]
public sealed class PaneWaitConsistencyTests
{
    [UnixFact]
    public async Task A_core_text_observer_reads_rendered_output_and_preserves_the_daemon()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        await using TemporaryServerScope scope = await new TmuxTestFactory()
            .CreateServerAsync(mcp.Options, token);
        Session session = await scope.Server.CreateSessionAsync(new NewSessionRequest
        {
            Name = "text-observer",
            Command = "exec /bin/cat",
            Width = "80",
            Height = "24",
        }, token);
        Pane pane = Assert.Single(await Assert.Single(
            await session.GetWindowsAsync(token)).GetPanesAsync(token));
        const string Marker = "observer-rendered-ready";

        await using (PaneTextObserver observer = new())
        {
            Task<PaneTextWaitResult> waiting = observer.WaitForTextAsync(
                pane,
                new PaneTextWaitRequest
                {
                    Patterns = [Marker],
                    SimpleMatch = true,
                    Timeout = TimeSpan.FromSeconds(3),
                    TailLines = 4,
                },
                token);
            await pane.SendTextAsync(Marker, enter: false, cancellationToken: token);
            PaneTextWaitResult result = await waiting;

            Assert.True(result.Outcome is PaneTextWaitOutcome.Matched
                or PaneTextWaitOutcome.PresentAtEntry);
            Assert.Equal(Marker, result.MatchedPattern);
            Assert.Contains(result.Tail, line => line.Contains(Marker, StringComparison.Ordinal));
            Assert.False(result.PollingFallback);
            Assert.Equal(0, result.EventsDropped);
        }

        Assert.True(await scope.Server.IsAliveAsync(token));
    }

    [UnixFact]
    public async Task An_established_wait_recovers_after_unstable_captures()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        await using TemporaryServerScope scope = await new TmuxTestFactory()
            .CreateServerAsync(mcp.Options, token);
        Session session = await scope.Server.CreateSessionAsync(new NewSessionRequest
        {
            Name = "busy-wait",
            Command = "exec /bin/cat",
            Width = "80",
            Height = "24",
        }, token);
        Window window = Assert.Single(await session.GetWindowsAsync(token));
        Pane pane = Assert.Single(await window.GetPanesAsync(token));
        const string Marker = "ready-after-resize";
        int captures = 0;
        int mutations = 0;
        int stateSamples = 0;
        TmuxInterceptor interceptor = async (invocation, next, cancellation) =>
        {
            TmuxCommandResult result = await next(cancellation);
            if (invocation.Arguments.Any(argument => argument.Contains(
                    "#{pane_pid}\t#{history_size}\t#{history_limit}", StringComparison.Ordinal))
                && ++stateSamples == 2)
            {
                // Publish and observe output after the baseline's final state
                // sample, before the wait takes its next activity signal.
                object? signal = mcp.Activity.CaptureSignal(pane.Id.ToString());
                _ = Assert.IsAssignableFrom<Task>(signal);
                await pane.SendTextAsync(Marker, enter: false, cancellationToken: cancellation);
                Assert.True(await mcp.Activity.WaitForActivityAsync(
                    pane.Id.ToString(), signal, TimeSpan.FromSeconds(1), cancellation));
            }

            if (invocation.Arguments.Contains("capture-pane", StringComparer.Ordinal))
            {
                int capture = ++captures;
                // Keep the entry baseline stable, then invalidate all three
                // incremental reads and all three fallback visible reads.
                if (capture is >= 2 and <= 7)
                {
                    window = await window.ResizeAsync(new ResizeWindowRequest
                    {
                        Height = capture % 2 == 0 ? 25 : 24,
                    }, cancellation);
                    mutations++;
                }
            }

            return result;
        };
        using TmuxConnectionAccessor connection = new(Server.Open(
            scope.Server.ConnectionOptions with { Interceptor = interceptor }));
        ReadTools reads = new(connection, new ServerPolicy(), mcp.Activity);

        WaitResult result = await reads.WaitForTextAsync(
            pane.Id.ToString(), [Marker], timeoutSeconds: 5, cancellationToken: token);

        Assert.Equal(6, mutations);
        Assert.Equal(WaitOutcome.Matched, result.Outcome);
        Assert.Contains(Marker, result.Tail.Lines);
        Assert.False(result.PollingFallback);
        Assert.False(mcp.Activity.IsStreaming);
    }
}
