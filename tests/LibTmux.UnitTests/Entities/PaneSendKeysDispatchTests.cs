using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text;
using LibTmux.Internal;

namespace LibTmux.UnitTests.Entities;

[UnsupportedOSPlatform("windows")]
public sealed class PaneSendKeysDispatchTests
{
    private static readonly ServerGeneration Generation = new(91, 901);

    [Fact]
    public async Task Send_text_uses_literal_mode()
    {
        var dispatched = new ConcurrentQueue<string[]>();
        Pane pane = CreatePane((request, _) =>
        {
            dispatched.Enqueue([.. request.LogicalArguments]);
            return Task.FromResult(Success(request.LogicalArguments));
        });

        await pane.SendTextAsync(
            "Enter",
            enter: false,
            TestContext.Current.CancellationToken);

        string[] sent = Assert.Single(dispatched);
        int commandStart = Array.IndexOf(sent, "send-keys");
        Assert.NotEqual(-1, commandStart);
        Assert.Equal(["send-keys", "-t", "%1", "-l", "Enter"], sent[commandStart..]);
    }

    [Fact]
    public async Task Enter_not_dispatched_after_text_is_reported_as_unknown()
    {
        var dispatched = new ConcurrentQueue<string[]>();
        var enterFailure = new TmuxTransportException(
            "Enter was not dispatched.",
            ["send-keys", "-t", "%1", "Enter"],
            TmuxDispatchState.NotDispatched);
        Pane pane = CreatePane((request, _) =>
        {
            string[] arguments = [.. request.LogicalArguments];
            if (arguments.Contains("send-keys", StringComparer.Ordinal))
            {
                dispatched.Enqueue(arguments);
                if (dispatched.Count == 2)
                {
                    throw enterFailure;
                }
            }

            return Task.FromResult(Success(arguments));
        });

        LibTmuxException failure = await Assert.ThrowsAsync<LibTmuxException>(() =>
            pane.SendKeysAsync(
                new SendKeysRequest(text: "payload", enter: true, literal: true),
                TestContext.Current.CancellationToken));

        Assert.Equal(TmuxDispatchState.Unknown, failure.Dispatch);
        TmuxTransportException inner = Assert.IsType<TmuxTransportException>(
            failure.InnerException);
        Assert.Equal(TmuxDispatchState.NotDispatched, inner.Dispatch);
        Assert.Contains("text was sent", failure.Message, StringComparison.Ordinal);
        Assert.Contains("do not retry", failure.Message, StringComparison.Ordinal);
        Assert.Equal(2, dispatched.Count);
        Assert.Equal("Enter", dispatched.Last()[^1]);
    }

    [Fact]
    public async Task Cancellation_between_text_and_enter_is_reported_as_unknown()
    {
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        int sendStages = 0;
        OperationCanceledException? enterFailure = null;
        Pane pane = CreatePane(async (request, cancellationToken) =>
        {
            string[] arguments = [.. request.LogicalArguments];
            if (arguments.Contains("send-keys", StringComparer.Ordinal))
            {
                int stage = Interlocked.Increment(ref sendStages);
                if (stage == 1)
                {
                    await cancellation.CancelAsync();
                }
                else
                {
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    catch (OperationCanceledException error)
                    {
                        enterFailure = error;
                        throw;
                    }
                }
            }

            return Success(arguments);
        });

        LibTmuxException failure = await Assert.ThrowsAsync<LibTmuxException>(() =>
            pane.SendKeysAsync(
                new SendKeysRequest(text: "payload", enter: true, literal: true),
                cancellation.Token));

        Assert.Equal(TmuxDispatchState.Unknown, failure.Dispatch);
        Assert.Same(enterFailure, failure.InnerException);
        Assert.Equal(2, Volatile.Read(ref sendStages));
    }

    [Fact]
    public async Task Text_stage_failure_keeps_its_not_dispatched_state()
    {
        int sendStages = 0;
        Pane pane = CreatePane((request, _) =>
        {
            string[] arguments = [.. request.LogicalArguments];
            if (arguments.Contains("send-keys", StringComparer.Ordinal))
            {
                Interlocked.Increment(ref sendStages);
                throw new TmuxTransportException(
                    "Text was not dispatched.",
                    arguments,
                    TmuxDispatchState.NotDispatched);
            }

            return Task.FromResult(Success(arguments));
        });

        TmuxTransportException failure = await Assert.ThrowsAsync<TmuxTransportException>(() =>
            pane.SendKeysAsync(
                new SendKeysRequest(text: "payload", enter: true, literal: true),
                TestContext.Current.CancellationToken));

        Assert.Equal(TmuxDispatchState.NotDispatched, failure.Dispatch);
        Assert.Equal(1, Volatile.Read(ref sendStages));
    }

    private static Pane CreatePane(
        Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>> execute)
    {
        var connection = new TmuxConnection(
            new ServerConnectionOptions(socketName: "send-keys-dispatch-test"),
            execute,
            implementation: TmuxImplementation.Tmux);
        return new Pane(connection, Generation, new PaneId(1));
    }

    private static TmuxCommandResult Success(IReadOnlyList<string> arguments)
    {
        byte[] output = Encoding.UTF8.GetBytes(
            $"{Generation.ProcessId}:{Generation.StartTime}\n");
        return new TmuxCommandResult(
            arguments,
            0,
            output,
            ReadOnlyMemory<byte>.Empty,
            Utf8BackslashDecoder.ProjectOutputLines(output),
            []);
    }
}
