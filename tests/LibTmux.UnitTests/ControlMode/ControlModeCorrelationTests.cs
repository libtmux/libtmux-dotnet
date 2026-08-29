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
