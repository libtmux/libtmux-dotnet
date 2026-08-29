using System.Runtime.Versioning;
using System.Threading.Channels;

namespace LibTmux.UnitTests.ControlMode;

[UnsupportedOSPlatform("windows")]
public sealed class ControlModeCorrelationTests
{
    [Fact]
    public async Task A_request_owns_every_flagged_block_through_its_fence()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string[] sentinels = ["libtmux-control-first", "libtmux-control-following"];
        int sentinelIndex = 0;
        var process = new ScriptedProcess(expectedWrites: 2);
        await using var session = new ControlModeSession(
            process,
            sentinelFactory: () => sentinels[sentinelIndex++]);
        await session.WaitForReadyAsync(token);

        Task<IReadOnlyList<string>> first = session.SendAsync(
            TmuxCommand.Create("libtmux-expand"),
            token);
        Task<IReadOnlyList<string>> following = session.SendAsync(
            TmuxCommand.Create("display-message", "-p", "following"),
            token);
        await process.WritesObserved.Task.WaitAsync(token);

        Assert.Equal(
            [
                "'libtmux-expand'\nlibtmux-control-first",
                "'display-message' '-p' 'following'\nlibtmux-control-following",
            ],
            process.Writes);

        process.EmitBlock(number: 10, flags: 1, failed: false, "one");
        process.EmitBlock(number: 11, flags: 0, failed: false, "hook-output");
        process.EmitBlock(number: 12, flags: 1, failed: false, "two");
        process.EmitFence(number: 13, sentinels[0]);
        process.EmitBlock(number: 14, flags: 1, failed: false, "following");
        process.EmitFence(number: 15, sentinels[1]);

        Assert.Equal(["one", "two"], await first);
        Assert.Equal(["following"], await following);
    }

    [Fact]
    public async Task A_failed_command_keeps_its_typed_diagnostics()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        const string Sentinel = "libtmux-control-failure";
        var process = new ScriptedProcess(expectedWrites: 1);
        await using var session = new ControlModeSession(
            process,
            sentinelFactory: () => Sentinel);
        await session.WaitForReadyAsync(token);
        TmuxCommand command = TmuxCommand.Create("no-such-command");

        Task<IReadOnlyList<string>> send = session.SendAsync(command, token);
        await process.WritesObserved.Task.WaitAsync(token);
        process.EmitBlock(number: 10, flags: 1, failed: false, "before-error");
        process.EmitBlock(number: 11, flags: 1, failed: true, "unknown command");
        process.EmitFence(number: 12, Sentinel);

        ControlModeCommandException error =
            await Assert.ThrowsAsync<ControlModeCommandException>(async () => await send);
        Assert.Same(command, error.Command);
        Assert.Equal(["before-error"], error.OutputLines);
        Assert.Equal(["unknown command"], error.ErrorLines);
    }

    [Fact]
    public async Task Pending_admission_rejects_without_dispatching_past_its_limit()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string[] sentinels =
        [
            "libtmux-control-first",
            "libtmux-control-third",
        ];
        int sentinelIndex = 0;
        var process = new ScriptedProcess(expectedWrites: 1);
        await using var session = new ControlModeSession(
            process,
            sentinelFactory: () => sentinels[sentinelIndex++],
            limits: new ControlModeLimits(maxPendingCommands: 1));
        await session.WaitForReadyAsync(token);

        Task<IReadOnlyList<string>> first = session.SendAsync(TmuxCommand.Create("first"), token);
        await process.WritesObserved.Task.WaitAsync(token);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () =>
            {
                _ = session.SendAsync(TmuxCommand.Create("second"), token);
            });
        Assert.Contains("1-command pending limit", error.Message, StringComparison.Ordinal);
        Assert.Single(process.Writes);

        process.EmitFence(number: 10, sentinels[0]);
        Assert.Empty(await first);
        Task<IReadOnlyList<string>> third = session.SendAsync(TmuxCommand.Create("third"), token);
        Assert.Equal(2, process.Writes.Count);
        process.EmitFence(number: 11, sentinels[1]);
        Assert.Empty(await third);
    }

    [Fact]
    public async Task A_request_beyond_its_byte_limit_is_not_dispatched()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var process = new ScriptedProcess(expectedWrites: 1);
        await using var session = new ControlModeSession(
            process,
            sentinelFactory: () => "f",
            limits: new ControlModeLimits(maxPendingCommands: 1, maxRequestBytes: 8));
        await session.WaitForReadyAsync(token);
        using var requestBudget = CancellationTokenSource.CreateLinkedTokenSource(token);
        requestBudget.CancelAfter(TimeSpan.FromMilliseconds(100));

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(
            () => session.SendAsync(
                TmuxCommand.Create("display-message"),
                requestBudget.Token));

        Assert.Equal("command", error.ParamName);
        Assert.Contains("8-byte limit", error.Message, StringComparison.Ordinal);
        Assert.Empty(process.Writes);
        Assert.True(session.IsRunning);

        Task<IReadOnlyList<string>> following = session.SendAsync(TmuxCommand.Create("x"), token);
        await process.WritesObserved.Task.WaitAsync(token);
        process.EmitFence(number: 10, "f");
        Assert.Empty(await following);
    }

    [Fact]
    public async Task A_canceled_request_keeps_its_slot_until_its_fence()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string[] sentinels = ["libtmux-control-canceled", "libtmux-control-following"];
        int sentinelIndex = 0;
        var process = new ScriptedProcess(expectedWrites: 1);
        await using var session = new ControlModeSession(
            process,
            sentinelFactory: () => sentinels[sentinelIndex++],
            limits: new ControlModeLimits(maxPendingCommands: 1, maxReplyBytes: 20));
        await session.WaitForReadyAsync(token);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(token);

        Task<IReadOnlyList<string>> abandoned = session.SendAsync(
            TmuxCommand.Create("first"),
            canceled.Token);
        await process.WritesObserved.Task.WaitAsync(token);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await abandoned);
        InvalidOperationException full = Assert.Throws<InvalidOperationException>(
            () =>
            {
                _ = session.SendAsync(TmuxCommand.Create("too-early"), token);
            });
        Assert.Contains("1-command pending limit", full.Message, StringComparison.Ordinal);
        Assert.Single(process.Writes);

        await using IAsyncEnumerator<TmuxEvent> events =
            session.Events.GetAsyncEnumerator(token);
        process.EmitBlock(number: 10, flags: 1, failed: false, "discard-me");
        process.EmitFence(number: 11, sentinels[0]);
        process.EmitNotification("test-fence-drained");
        Assert.True(await events.MoveNextAsync());
        Task<IReadOnlyList<string>> following = session.SendAsync(
            TmuxCommand.Create("following"),
            token);
        Assert.Equal(2, process.Writes.Count);
        process.EmitBlock(number: 12, flags: 1, failed: false, "ok");
        process.EmitFence(number: 13, sentinels[1]);

        Assert.Equal(["ok"], await following);
    }

    [Fact]
    public async Task A_canceled_request_still_enforces_aggregate_reply_limits()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        const string Sentinel = "libtmux-control-canceled-bounded";
        var process = new ScriptedProcess(expectedWrites: 1);
        var session = new ControlModeSession(
            process,
            sentinelFactory: () => Sentinel,
            limits: new ControlModeLimits(maxReplyBytes: 5));
        await session.WaitForReadyAsync(token);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(token);

        Task<IReadOnlyList<string>> abandoned = session.SendAsync(
            TmuxCommand.Create("bounded"),
            canceled.Token);
        await process.WritesObserved.Task.WaitAsync(token);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await abandoned);

        process.EmitBlock(number: 10, flags: 1, failed: false, "123");
        process.EmitBlock(number: 11, flags: 1, failed: false, "456");

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => session.DisposeAsync().AsTask());
        Assert.Equal("A control-mode reply exceeded its 5-byte limit.", error.Message);
    }

    [Theory]
    [InlineData(ReplyLimit.Bytes)]
    [InlineData(ReplyLimit.Lines)]
    [InlineData(ReplyLimit.Blocks)]
    public async Task Aggregate_reply_limits_fail_the_session(ReplyLimit limit)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        const string Sentinel = "libtmux-control-bounded";
        ControlModeLimits limits = limit switch
        {
            ReplyLimit.Bytes => new ControlModeLimits(maxReplyBytes: 5),
            ReplyLimit.Lines => new ControlModeLimits(maxReplyLines: 1),
            ReplyLimit.Blocks => new ControlModeLimits(maxReplyBlocks: 1),
            _ => throw new ArgumentOutOfRangeException(nameof(limit)),
        };
        var process = new ScriptedProcess(expectedWrites: 1);
        var session = new ControlModeSession(
            process,
            sentinelFactory: () => Sentinel,
            limits: limits);
        await session.WaitForReadyAsync(token);
        Task<IReadOnlyList<string>> send = session.SendAsync(TmuxCommand.Create("bounded"), token);
        await process.WritesObserved.Task.WaitAsync(token);

        switch (limit)
        {
            case ReplyLimit.Bytes:
                process.EmitBlock(number: 10, flags: 1, failed: false, "123");
                process.EmitBlock(number: 11, flags: 1, failed: false, "456");
                break;
            case ReplyLimit.Lines:
                process.EmitBlock(number: 10, flags: 1, failed: false, string.Empty);
                process.EmitBlock(number: 11, flags: 1, failed: false, string.Empty);
                break;
            case ReplyLimit.Blocks:
                process.EmitBlock(number: 10, flags: 1, failed: false);
                process.EmitBlock(number: 11, flags: 1, failed: false);
                break;
        }

        process.EmitFence(number: 12, Sentinel);
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await send);
        InvalidDataException disposal = await Assert.ThrowsAsync<InvalidDataException>(
            () => session.DisposeAsync().AsTask());
        string unit = limit switch
        {
            ReplyLimit.Bytes => "5-byte",
            ReplyLimit.Lines => "1-line",
            ReplyLimit.Blocks => "1-block",
            _ => throw new ArgumentOutOfRangeException(nameof(limit)),
        };
        Assert.Equal($"A control-mode reply exceeded its {unit} limit.", error.Message);
        Assert.Same(error, disposal);
    }

    [Theory]
    [InlineData(BlockLimit.Bytes)]
    [InlineData(BlockLimit.Lines)]
    public async Task Hook_block_limits_fail_the_session(BlockLimit limit)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        ControlModeLimits limits = limit == BlockLimit.Bytes
            ? new ControlModeLimits(maxBlockBytes: 5)
            : new ControlModeLimits(maxBlockLines: 1);
        var process = new ScriptedProcess(expectedWrites: 0);
        var session = new ControlModeSession(process, limits: limits);
        await session.WaitForReadyAsync(token);

        process.EmitBlock(
            number: 10,
            flags: 0,
            failed: false,
            limit == BlockLimit.Bytes ? ["123456"] : ["one", "two"]);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => session.DisposeAsync().AsTask());
        string unit = limit == BlockLimit.Bytes ? "5-byte" : "1-line";
        Assert.Equal($"A control-mode block exceeded its {unit} limit.", error.Message);
    }

    [Fact]
    public async Task An_empty_error_block_does_not_invent_a_reported_line()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        const string Sentinel = "libtmux-control-empty-error";
        var process = new ScriptedProcess(expectedWrites: 1);
        await using var session = new ControlModeSession(
            process,
            sentinelFactory: () => Sentinel);
        await session.WaitForReadyAsync(token);

        Task<IReadOnlyList<string>> send = session.SendAsync(
            TmuxCommand.Create("empty-error"),
            token);
        await process.WritesObserved.Task.WaitAsync(token);
        process.EmitBlock(number: 10, flags: 1, failed: true);
        process.EmitFence(number: 11, Sentinel);

        ControlModeCommandException error =
            await Assert.ThrowsAsync<ControlModeCommandException>(async () => await send);
        Assert.Equal("The tmux command failed.", error.Message);
        Assert.Empty(error.ErrorLines);
    }

    [Theory]
    [InlineData("%begin")]
    [InlineData("%begin malformed")]
    [InlineData("%end 2 2 0")]
    [InlineData("%error 2 2 1")]
    public async Task A_reserved_guard_outside_a_block_fails_the_session(string line)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var process = new ScriptedProcess(expectedWrites: 0);
        var session = new ControlModeSession(process);
        await session.WaitForReadyAsync(token);

        process.EmitProtocolLine(line);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => session.DisposeAsync().AsTask());
        Assert.Contains("block guard", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Guard_looking_output_inside_a_block_remains_data()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        const string Sentinel = "libtmux-control-guard-data";
        var process = new ScriptedProcess(expectedWrites: 1);
        await using var session = new ControlModeSession(
            process,
            sentinelFactory: () => Sentinel);
        await session.WaitForReadyAsync(token);

        Task<IReadOnlyList<string>> send = session.SendAsync(
            TmuxCommand.Create("guard-looking-output"),
            token);
        await process.WritesObserved.Task.WaitAsync(token);
        process.EmitBlock(
            number: 10,
            flags: 1,
            failed: false,
            "%begin 9 9 1",
            "%begin 2 10 1",
            "%end 9 9 1",
            "%begin malformed",
            "%error 9 9 1");
        process.EmitFence(number: 11, Sentinel);

        Assert.Equal(
            [
                "%begin 9 9 1",
                "%begin 2 10 1",
                "%end 9 9 1",
                "%begin malformed",
                "%error 9 9 1",
            ],
            await send);
    }

    [Fact]
    public async Task A_stale_command_is_rejected_before_dispatch()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var attached = new ServerGeneration(processId: 10, startTime: 20);
        var process = new ScriptedProcess(expectedWrites: 0);
        await using var session = new ControlModeSession(process, generation: attached);
        await session.WaitForReadyAsync(token);
        TmuxCommand command = TmuxCommand.Create("display-message") with
        {
            RequiredGeneration = new ServerGeneration(processId: 11, startTime: 21),
        };

        StaleServerGenerationException error =
            await Assert.ThrowsAsync<StaleServerGenerationException>(
                () => session.SendAsync(command, token));

        Assert.Equal(command.RequiredGeneration, error.Expected);
        Assert.Equal(attached, error.Actual);
        Assert.Empty(process.Writes);
    }

    [Fact]
    public void Typed_arguments_render_without_a_second_physical_line()
    {
        TmuxCommand command = TmuxCommand.Create(
            "display-message",
            "-p",
            "a'b; $HOME \\ π\r\nend");

        string rendered = ControlModeCommandRenderer.Render(command);

        Assert.DoesNotContain('\r', rendered);
        Assert.DoesNotContain('\n', rendered);
        Assert.Contains("\\015\\012", rendered, StringComparison.Ordinal);
        Assert.EndsWith("\\145\\156\\144", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Rendered_byte_count_matches_the_physical_utf8_line()
    {
        TmuxCommand command = TmuxCommand.Create(
            "display-message",
            "-p",
            "a'b π",
            "line\r\nend");

        string rendered = ControlModeCommandRenderer.Render(command);

        Assert.Equal(
            System.Text.Encoding.UTF8.GetByteCount(rendered),
            ControlModeCommandRenderer.GetRenderedByteCount(command));
    }

    public enum BlockLimit
    {
        Bytes,
        Lines,
    }

    public enum ReplyLimit
    {
        Bytes,
        Lines,
        Blocks,
    }

    private sealed class ScriptedProcess : IControlModeProcess
    {
        private readonly Channel<string> _output = Channel.CreateUnbounded<string>();
        private readonly TaskCompletionSource _exited = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int _expectedWrites;
        private readonly List<string> _writes = [];
        private int _hasExited;

        internal ScriptedProcess(int expectedWrites)
        {
            _expectedWrites = expectedWrites;
            _output.Writer.TryWrite("%begin 1 1 0");
            _output.Writer.TryWrite("%end 1 1 0");
            if (expectedWrites == 0)
            {
                WritesObserved.TrySetResult();
            }
        }

        internal IReadOnlyList<string> Writes
        {
            get
            {
                lock (_writes)
                {
                    return [.. _writes];
                }
            }
        }

        internal TaskCompletionSource WritesObserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool HasExited => Volatile.Read(ref _hasExited) != 0;

        public Task WriteLineAsync(
            ReadOnlyMemory<char> command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_writes)
            {
                _writes.Add(command.ToString());
                if (_writes.Count == _expectedWrites)
                {
                    WritesObserved.TrySetResult();
                }
            }

            return Task.CompletedTask;
        }

        public Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public async Task<string?> ReadLineAsync()
        {
            while (await _output.Reader.WaitToReadAsync())
            {
                if (_output.Reader.TryRead(out string? line))
                {
                    return line;
                }
            }

            return null;
        }

        public void CloseInput() => Stop("%exit");

        public void Kill() => Stop("%exit killed");

        public Task WaitForExitAsync(CancellationToken cancellationToken = default) =>
            _exited.Task.WaitAsync(cancellationToken);

        public void Dispose() => Stop("%exit disposed");

        internal void EmitBlock(int number, int flags, bool failed, params string[] lines)
        {
            string suffix = $"2 {number} {flags}";
            _output.Writer.TryWrite($"%begin {suffix}");
            foreach (string line in lines)
            {
                _output.Writer.TryWrite(line);
            }

            _output.Writer.TryWrite($"%{(failed ? "error" : "end")} {suffix}");
        }

        internal void EmitFence(int number, string sentinel) =>
            EmitBlock(
                number,
                flags: 1,
                failed: true,
                $"parse error: unknown command: {sentinel}");

        internal void EmitNotification(string name) => _output.Writer.TryWrite($"%{name}");

        internal void EmitProtocolLine(string line) => _output.Writer.TryWrite(line);

        private void Stop(string exit)
        {
            if (Interlocked.Exchange(ref _hasExited, 1) != 0)
            {
                return;
            }

            _output.Writer.TryWrite(exit);
            _output.Writer.TryComplete();
            _exited.TrySetResult();
        }
    }
}
