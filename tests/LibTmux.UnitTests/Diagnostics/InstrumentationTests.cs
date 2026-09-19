using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.Versioning;
using LibTmux.Internal;
using LibTmux.UnitTests.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace LibTmux.UnitTests.Diagnostics;

/// <summary>
/// Each test tags its dispatch with a socket nobody else uses, so the process-wide
/// activity source and meter stay usable while tests run in parallel.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class InstrumentationTests
{
    [UnixFact]
    public async Task A_dispatched_command_is_traced_with_its_subcommand_and_socket()
    {
        string socket = $"trace-{Guid.NewGuid():N}";
        List<Activity> spans = [];
        using ActivityListener listener = ListenForSpans(spans);

        await Dispatch(socket, exitCode: 0, ["list-sessions", "-F", "#{session_id}"]);

        Activity span = Assert.Single(Snapshot(spans), each => Tag(each, "tmux.socket") == socket);
        Assert.Equal("list-sessions", span.DisplayName);
        Assert.Equal("list-sessions", Tag(span, "tmux.subcommand"));
        Assert.Equal("0", Tag(span, "tmux.exit_code"));
        Assert.Equal(ActivityStatusCode.Ok, span.Status);
    }

    [UnixFact]
    public async Task A_command_tmux_refused_is_traced_as_an_error()
    {
        string socket = $"trace-{Guid.NewGuid():N}";
        List<Activity> spans = [];
        using ActivityListener listener = ListenForSpans(spans);

        await Dispatch(socket, exitCode: 1, ["kill-session"]);

        Activity span = Assert.Single(Snapshot(spans), each => Tag(each, "tmux.socket") == socket);
        Assert.Equal("1", Tag(span, "tmux.exit_code"));
        Assert.Equal(ActivityStatusCode.Error, span.Status);
    }

    [UnixFact]
    public async Task A_transport_that_throws_is_traced_with_the_failure_type()
    {
        string socket = $"trace-{Guid.NewGuid():N}";
        List<Activity> spans = [];
        using ActivityListener listener = ListenForSpans(spans);
        var dispatcher = new TmuxCommandDispatcher(
            static (_, _) => throw new TmuxTransportException("gone", ["list-sessions"]),
            new TmuxCommandContext(NullLogger.Instance, socket));

        await Assert.ThrowsAsync<TmuxTransportException>(
            () => dispatcher.ExecuteAsync(["list-sessions"], TestContext.Current.CancellationToken));

        Activity span = Assert.Single(Snapshot(spans), each => Tag(each, "tmux.socket") == socket);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(typeof(TmuxTransportException).FullName, Tag(span, "error.type"));
    }

    [UnixFact]
    public async Task A_dispatched_command_records_its_duration()
    {
        string socket = $"meter-{Guid.NewGuid():N}";
        List<KeyValuePair<string, object?>[]> measured = [];
        using MeterListener listener = ListenForDuration(socket, measured);

        await Dispatch(socket, exitCode: 0, ["list-panes"]);

        KeyValuePair<string, object?>[] tags;
        lock (measured)
        {
            tags = Assert.Single(measured);
        }

        Assert.Equal("list-panes", tags.Single(tag => tag.Key == "tmux.subcommand").Value);
        Assert.Equal(0, tags.Single(tag => tag.Key == "tmux.exit_code").Value);
    }

    [UnixFact]
    public async Task A_command_that_outlives_the_timeout_fails_as_dispatch_unknown()
    {
        var dispatcher = new TmuxCommandDispatcher(
            static async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                throw new UnreachableException();
            },
            new TmuxCommandContext(
                NullLogger.Instance,
                "timeout",
                TimeSpan.FromMilliseconds(50)));

        TmuxTransportException expired = await Assert.ThrowsAsync<TmuxTransportException>(
            () => dispatcher.ExecuteAsync(["list-sessions"], TestContext.Current.CancellationToken));

        // tmux may have run the command before it stopped answering, so a retry
        // is not automatically safe.
        Assert.Equal(TmuxDispatchState.Unknown, expired.Dispatch);
        Assert.Equal(["list-sessions"], expired.Arguments);
    }

    [UnixFact]
    public async Task A_guarded_chain_is_traced_and_bounded_by_the_timeout()
    {
        // A chain holding a pane or window command runs under a generation
        // guard. That path is still one tmux command, so the timeout and the
        // span apply to it as to any other.
        string socket = $"guarded-{Guid.NewGuid():N}";
        List<Activity> spans = [];
        using ActivityListener listener = ListenForSpans(spans);
        var dispatcher = new TmuxCommandDispatcher(
            static (_, _) => throw new UnreachableException(),
            new TmuxCommandContext(NullLogger.Instance, socket, TimeSpan.FromMilliseconds(50)));
        var chain = new TmuxChain(
            dispatcher,
            [new TmuxCommand("list-panes", []) { RequiredGeneration = new ServerGeneration(1, 2) }],
            static async (_, _, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                throw new UnreachableException();
            });

        // Without the timeout this would wait on the caller alone, so bound it.
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        caller.CancelAfter(TimeSpan.FromSeconds(5));
        TmuxTransportException expired = await Assert.ThrowsAsync<TmuxTransportException>(
            () => chain.ExecuteAsync(caller.Token));

        Assert.Equal(TmuxDispatchState.Unknown, expired.Dispatch);
        Activity span = Assert.Single(Snapshot(spans), each => Tag(each, "tmux.socket") == socket);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
    }

    [UnixFact]
    public async Task A_caller_who_cancels_sees_cancellation_not_a_timeout()
    {
        using var caller = new CancellationTokenSource();
        var dispatcher = new TmuxCommandDispatcher(
            async (_, token) =>
            {
                await caller.CancelAsync();
                await Task.Delay(Timeout.Infinite, token);
                throw new UnreachableException();
            },
            new TmuxCommandContext(NullLogger.Instance, "cancel", TimeSpan.FromMinutes(5)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => dispatcher.ExecuteAsync(["list-sessions"], caller.Token));
    }

    private static async Task Dispatch(string socket, int exitCode, string[] arguments)
    {
        var dispatcher = new TmuxCommandDispatcher(
            (asked, _) => Task.FromResult(
                new TmuxCommandResult(
                    asked,
                    exitCode,
                    ReadOnlyMemory<byte>.Empty,
                    ReadOnlyMemory<byte>.Empty,
                    [],
                    [])),
            new TmuxCommandContext(NullLogger.Instance, socket));
        await dispatcher.ExecuteAsync(arguments, TestContext.Current.CancellationToken);
    }

    /// <summary>Copies under the lock: the listener appends while the assert reads.</summary>
    private static Activity[] Snapshot(List<Activity> spans)
    {
        lock (spans)
        {
            return [.. spans];
        }
    }

    private static string? Tag(Activity activity, string name) =>
        activity.GetTagItem(name)?.ToString();

    private static ActivityListener ListenForSpans(List<Activity> spans)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TmuxDiagnostics.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (spans)
                {
                    spans.Add(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static MeterListener ListenForDuration(
        string socket,
        List<KeyValuePair<string, object?>[]> measured)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, subscribing) =>
            {
                if (instrument.Meter.Name == TmuxDiagnostics.MeterName
                    && instrument.Name == TmuxDiagnostics.CommandDurationInstrumentName)
                {
                    subscribing.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            if (tags.ToArray().Any(tag => tag.Key == "tmux.socket" && (string?)tag.Value == socket))
            {
                lock (measured)
                {
                    measured.Add(tags.ToArray());
                }
            }
        });
        listener.Start();
        return listener;
    }
}
