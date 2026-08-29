using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text;
using LibTmux.Internal;
using LibTmux.Mcp;

using LibTmux.UnitTests.Connection;

namespace LibTmux.UnitTests.Mcp;

[UnsupportedOSPlatform("windows")]
public sealed class PasteTextCleanupTests
{
    [Fact]
    public async Task Cancellation_during_paste_uses_an_independent_cleanup_token()
    {
        await using var fixture = new PasteFixture(failPaste: true);
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);

        Task<ActionResult> pasting = fixture.Tools.PasteTextAsync(
            "secret text",
            paneId: "%1",
            cancellationToken: cancellation.Token);
        await fixture.PrimaryStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        fixture.ReleasePrimary();

        Exception? failure = await Record.ExceptionAsync(() => pasting);

        Assert.Same(fixture.PrimaryFailure, failure);
        Assert.False(fixture.DeleteTokenWasCancelled);
        Assert.Equal(fixture.CreatedBuffer, fixture.DeletedBuffer);
        Assert.Empty(fixture.Buffers);
    }

    [Fact]
    public async Task Ambiguous_set_buffer_failure_still_cleans_the_possible_buffer()
    {
        await using var fixture = new PasteFixture(failSetBuffer: true);
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);

        Task<ActionResult> pasting = fixture.Tools.PasteTextAsync(
            "secret text",
            paneId: "%1",
            cancellationToken: cancellation.Token);
        await fixture.PrimaryStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        fixture.ReleasePrimary();

        Exception? failure = await Record.ExceptionAsync(() => pasting);

        Assert.Same(fixture.PrimaryFailure, failure);
        Assert.False(fixture.DeleteTokenWasCancelled);
        Assert.Equal(fixture.CreatedBuffer, fixture.DeletedBuffer);
        Assert.Empty(fixture.Buffers);
    }

    [Fact]
    public async Task Not_dispatched_set_buffer_failure_does_not_delete_an_unowned_buffer()
    {
        await using var fixture = new PasteFixture(setBufferNotDispatched: true);

        Exception? failure = await Record.ExceptionAsync(() =>
            fixture.Tools.PasteTextAsync(
                "secret text",
                paneId: "%1",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Same(fixture.PrimaryFailure, failure);
        Assert.Null(fixture.CreatedBuffer);
        Assert.Null(fixture.DeletedBuffer);
    }

    [Fact]
    public async Task Cleanup_failure_is_attached_without_replacing_the_primary_failure()
    {
        await using var fixture = new PasteFixture(failPaste: true, failDelete: true);

        Task<ActionResult> pasting = fixture.Tools.PasteTextAsync(
            "secret text",
            paneId: "%1",
            cancellationToken: TestContext.Current.CancellationToken);
        await fixture.PrimaryStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        fixture.ReleasePrimary();

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => pasting);

        Assert.Same(fixture.PrimaryFailure, failure);
        Assert.Same(
            fixture.CleanupFailure,
            failure.Data[WriteTools.PasteBufferCleanupFailureDataKey]);
        Assert.Equal(
            fixture.CreatedBuffer,
            failure.Data[WriteTools.PasteBufferCleanupBufferDataKey]);
    }

    [Fact]
    public async Task Cleanup_failure_after_a_successful_paste_returns_do_not_retry_warning()
    {
        await using var fixture = new PasteFixture(failDelete: true);

        ActionResult result = await fixture.Tools.PasteTextAsync(
            "secret text",
            paneId: "%1",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("cleanup failed", result.Changed, StringComparison.Ordinal);
        Assert.Contains(fixture.CreatedBuffer!, result.Changed, StringComparison.Ordinal);
        Assert.Contains("Do not retry", result.Changed, StringComparison.Ordinal);
        Assert.Contains("tmux_list_buffers", result.Changed, StringComparison.Ordinal);
        Assert.Contains(
            $"tmux delete-buffer -b {fixture.CreatedBuffer}",
            result.Changed,
            StringComparison.Ordinal);
        Assert.Equal("%1", result.PaneId);
        Assert.False(fixture.DeleteTokenWasCancelled);
        Assert.Equal(fixture.CreatedBuffer, fixture.DeletedBuffer);
        Assert.Single(fixture.Buffers);
    }

    private sealed class PasteFixture : IAsyncDisposable
    {
        private static readonly ServerGeneration Generation = new(81, 801);

        private readonly bool _failSetBuffer;
        private readonly bool _setBufferNotDispatched;
        private readonly bool _failPaste;
        private readonly bool _failDelete;
        private readonly TaskCompletionSource _releasePrimary = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TmuxConnectionAccessor _accessor;
        private readonly PaneActivityHub _activity = new();
        private readonly JobStore _jobs = new();

        internal PasteFixture(
            bool failSetBuffer = false,
            bool failPaste = false,
            bool failDelete = false,
            bool setBufferNotDispatched = false)
        {
            _failSetBuffer = failSetBuffer;
            _failPaste = failPaste;
            _failDelete = failDelete;
            _setBufferNotDispatched = setBufferNotDispatched;
            PrimaryFailure = failSetBuffer
                ? new TmuxOperationCanceledException(
                    "set-buffer may have executed",
                    CancellationToken.None,
                    commandMayHaveExecuted: true,
                    clientProcessId: 801)
                : setBufferNotDispatched
                    ? new TmuxTransportException(
                        "set-buffer was not dispatched",
                        ["set-buffer"],
                        TmuxDispatchState.NotDispatched)
                    : new InvalidOperationException("paste failed");
            var connection = new TmuxConnection(
                new ServerConnectionOptions(socketName: "paste-cleanup-test"),
                FakeMultiplexer.AnsweringVersion(ExecuteAsync));
            var server = new Server(connection, Generation, "tmux 3.7");
            _accessor = new TmuxConnectionAccessor(server);
            Tools = new WriteTools(_accessor, new ServerPolicy(), _activity, _jobs);
        }

        internal ConcurrentDictionary<string, string> Buffers { get; } = new(
            StringComparer.Ordinal);

        internal string? CreatedBuffer { get; private set; }

        internal IOException CleanupFailure { get; } = new("cleanup failed");

        internal string? DeletedBuffer { get; private set; }

        internal bool DeleteTokenWasCancelled { get; private set; }

        internal Exception PrimaryFailure { get; }

        internal TaskCompletionSource PrimaryStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal WriteTools Tools { get; }

        public async ValueTask DisposeAsync()
        {
            _releasePrimary.TrySetResult();
            await _jobs.DisposeAsync().ConfigureAwait(false);
            await _activity.DisposeAsync().ConfigureAwait(false);
            _accessor.Dispose();
        }

        internal void ReleasePrimary() => _releasePrimary.TrySetResult();

        private async Task<TmuxCommandResult> ExecuteAsync(
            TmuxCommandRequest request,
            CancellationToken cancellationToken)
        {
            string[] arguments = [.. request.LogicalArguments];
            if (arguments.Contains("list-panes", StringComparer.Ordinal))
            {
                return Success(arguments, PaneListing());
            }

            if (arguments.Contains("set-buffer", StringComparer.Ordinal))
            {
                if (_setBufferNotDispatched)
                {
                    throw PrimaryFailure;
                }

                string buffer = ValueAfter(arguments, "-b");
                CreatedBuffer = buffer;
                Buffers[buffer] = arguments[^1];
                if (_failSetBuffer)
                {
                    await FailPrimaryAsync().ConfigureAwait(false);
                }

                return Success(arguments);
            }

            if (arguments.Contains("paste-buffer", StringComparer.Ordinal))
            {
                if (_failPaste)
                {
                    await FailPrimaryAsync().ConfigureAwait(false);
                }

                return Success(arguments);
            }

            if (arguments.Contains("delete-buffer", StringComparer.Ordinal))
            {
                DeleteTokenWasCancelled = cancellationToken.IsCancellationRequested;
                string deletedBuffer = ValueAfter(arguments, "-b");
                DeletedBuffer = deletedBuffer;
                if (_failDelete)
                {
                    throw CleanupFailure;
                }

                Buffers.TryRemove(deletedBuffer, out _);
                return Success(arguments);
            }

            return Success(arguments);
        }

        private async Task FailPrimaryAsync()
        {
            PrimaryStarted.TrySetResult();
            await _releasePrimary.Task.ConfigureAwait(false);
            throw PrimaryFailure;
        }

        private static string PaneListing()
        {
            FormatProjection projection = FormatProjection.Create(
                "list-panes",
                TmuxVersion.Parse("3.7"));
            return string.Concat(projection.Fields.Select(
                static field => FieldValue(field.WireName) + FormatProjection.RowSeparator)) + "\n";
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

        private static string ValueAfter(string[] arguments, string option)
        {
            int index = Array.IndexOf(arguments, option);
            return index >= 0 && index + 1 < arguments.Length
                ? arguments[index + 1]
                : throw new InvalidOperationException($"Missing {option} in tmux command.");
        }

        private static TmuxCommandResult Success(
            IReadOnlyList<string> arguments,
            string payload = "")
        {
            string standardOutput = $"{Generation.ProcessId}:{Generation.StartTime}\n{payload}";
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
