using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using System.Threading.Channels;

namespace LibTmux.UnitTests.ControlMode;

[UnsupportedOSPlatform("windows")]
public sealed class ControlModeSessionFailureTests
{
    [Fact]
    public async Task A_faulted_pump_cannot_skip_process_and_write_lock_disposal()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var pumpFailure = new IOException("control output failed");
        var process = new FaultedPumpProcess(pumpFailure);
        var writeLock = new SemaphoreSlim(1, 1);
        var session = new ControlModeSession(process, writeLock);

        IOException readinessFailure = await Assert.ThrowsAsync<IOException>(
            () => session.WaitForReadyAsync(token));
        IOException observed = await Assert.ThrowsAsync<IOException>(
            () => session.DisposeAsync().AsTask());

        Assert.Same(pumpFailure, readinessFailure);
        Assert.Same(pumpFailure, observed);
        Assert.True(process.DisposeCalled);
        Assert.Throws<ObjectDisposedException>(() =>
        {
            _ = writeLock.Wait(0, token);
        });
    }

    [Fact]
    public async Task Pump_and_cleanup_failures_are_both_preserved()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var pumpFailure = new IOException("control output failed");
        var cleanupFailure = new InvalidOperationException("process dispose failed");
        var process = new FaultedPumpProcess(pumpFailure, cleanupFailure);
        var writeLock = new SemaphoreSlim(1, 1);
        var session = new ControlModeSession(process, writeLock);

        IOException readinessFailure = await Assert.ThrowsAsync<IOException>(
            () => session.WaitForReadyAsync(token));
        AggregateException observed = await Assert.ThrowsAsync<AggregateException>(
            () => session.DisposeAsync().AsTask());

        Assert.Same(pumpFailure, readinessFailure);
        Assert.Equal(2, observed.InnerExceptions.Count);
        Assert.Same(pumpFailure, observed.InnerExceptions[0]);
        Assert.Same(cleanupFailure, observed.InnerExceptions[1]);
        Assert.True(process.DisposeCalled);
        Assert.Throws<ObjectDisposedException>(() =>
        {
            _ = writeLock.Wait(0, token);
        });
    }

    [Fact]
    public async Task Disposal_kills_a_client_whose_write_holds_the_dispatch_lock()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var process = new StalledWriteProcess();
        var writeLock = new SemaphoreSlim(1, 1);
        var session = new ControlModeSession(
            process,
            writeLock,
            TimeSpan.FromMilliseconds(250));

        await session.WaitForReadyAsync(token);
        Task<IReadOnlyList<string>> send = session.SendAsync(
            TmuxCommand.Create("display-message", "-p", "stuck"),
            token);
        await process.WriteStarted.Task.WaitAsync(token);

        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2), token);
        IOException writeFailure = await Assert.ThrowsAsync<IOException>(async () => await send);

        Assert.Equal("The client was killed during its write.", writeFailure.Message);
        Assert.True(process.KillCalled);
        Assert.True(process.DisposeCalled);
        Assert.False(session.IsRunning);
        Assert.Throws<ObjectDisposedException>(() =>
        {
            _ = writeLock.Wait(0, token);
        });
    }

    [Fact]
    public async Task Disposal_does_not_orphan_a_reply_from_an_enqueued_write()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var process = new StalledWriteProcess(replyOnRelease: true);
        var session = new ControlModeSession(process);
        await session.WaitForReadyAsync(token);

        Task<IReadOnlyList<string>> send = session.SendAsync(
            TmuxCommand.Create("display-message", "-p", "reply"),
            token);
        await process.WriteStarted.Task.WaitAsync(token);

        Task disposal = session.DisposeAsync().AsTask();
        process.ReleaseWrite();

        Assert.Equal(["reply"], await send.WaitAsync(token));
        await disposal.WaitAsync(token);
        Assert.True(process.DisposeCalled);
    }

    [Fact]
    public async Task A_canceled_write_lock_wait_releases_its_unenqueued_slot()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var process = new StalledWriteProcess();
        var session = new ControlModeSession(
            process,
            disposalBudget: TimeSpan.FromMilliseconds(250),
            limits: new ControlModeLimits(maxPendingCommands: 2));
        await session.WaitForReadyAsync(token);

        Task<IReadOnlyList<string>> first = session.SendAsync(
            TmuxCommand.Create("first"),
            token);
        await process.WriteStarted.Task.WaitAsync(token);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<IReadOnlyList<string>> waiting = session.SendAsync(
            TmuxCommand.Create("waiting"),
            canceled.Token);

        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting);
        Task<IReadOnlyList<string>> admitted = session.SendAsync(
            TmuxCommand.Create("admitted"),
            token);
        Assert.False(admitted.IsCompleted);

        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2), token);
        await Assert.ThrowsAsync<IOException>(async () => await first);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await admitted);
    }

    [Fact]
    public async Task Raw_eof_faults_without_a_synthetic_exit_event()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var process = new TerminalWhileRunningProcess();
        var session = new ControlModeSession(process);

        await session.WaitForReadyAsync(token);
        await using IAsyncEnumerator<TmuxEvent> events =
            session.Events.GetAsyncEnumerator(token);
        process.EndOutput();
        EndOfStreamException eventFailure =
            await Assert.ThrowsAsync<EndOfStreamException>(async () =>
                await events.MoveNextAsync().AsTask().WaitAsync(token));

        Assert.False(session.IsRunning);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => session.SendAsync(
                TmuxCommand.Create("display-message", "-p", "too-late"),
                token));
        EndOfStreamException disposalFailure =
            await Assert.ThrowsAsync<EndOfStreamException>(
                () => session.DisposeAsync().AsTask());
        Assert.Same(eventFailure, disposalFailure);
        Assert.True(process.DisposeCalled);
    }

    [Fact]
    public async Task Disposal_induced_eof_completes_with_an_exit_event()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var process = new TerminalWhileRunningProcess();
        var session = new ControlModeSession(process);

        await session.WaitForReadyAsync(token);
        await session.DisposeAsync().AsTask().WaitAsync(token);

        await using IAsyncEnumerator<TmuxEvent> events =
            session.Events.GetAsyncEnumerator(token);
        Assert.True(await events.MoveNextAsync());
        TmuxExitEvent exit = Assert.IsType<TmuxExitEvent>(events.Current);
        Assert.Null(exit.Reason);
        Assert.False(await events.MoveNextAsync());
        Assert.True(process.DisposeCalled);
    }

    [Fact]
    public async Task Exit_after_attach_reports_the_unanswered_command()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var process = new TerminalWhileRunningProcess();
        var session = new ControlModeSession(process);

        await session.WaitForReadyAsync(token);
        Task<IReadOnlyList<string>> command = session.SendAsync(
            TmuxCommand.Create("display-message", "-p", "unanswered"),
            token);
        await process.Flushed.Task.WaitAsync(token);
        process.Exit("server stopped");

        InvalidOperationException failure =
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await command);
        Assert.Equal(
            "The tmux control client exited before answering a pending command.",
            failure.Message);

        var observed = new List<TmuxEvent>();
        await foreach (TmuxEvent item in session.Events.WithCancellation(token))
        {
            observed.Add(item);
        }

        TmuxExitEvent exit = Assert.IsType<TmuxExitEvent>(Assert.Single(observed));
        Assert.Equal("server stopped", exit.Reason);
        await session.DisposeAsync();
        Assert.True(process.DisposeCalled);
    }

    [Fact]
    public async Task Terminal_fault_rejects_commands_when_the_process_still_claims_to_run()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var pumpFailure = new IOException("control output failed after attach");
        var process = new TerminalWhileRunningProcess();
        var session = new ControlModeSession(process);

        await session.WaitForReadyAsync(token);
        Task eventsCompleted = DrainEventsAsync(session.Events, token);
        process.EndOutput(pumpFailure);
        IOException eventFailure = await Assert.ThrowsAsync<IOException>(
            async () => await eventsCompleted.WaitAsync(token));

        Assert.False(session.IsRunning);
        Assert.Same(pumpFailure, eventFailure);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => session.SendAsync(
                TmuxCommand.Create("display-message", "-p", "too-late"),
                token));
        IOException disposalFailure = await Assert.ThrowsAsync<IOException>(
            () => session.DisposeAsync().AsTask());
        Assert.Same(pumpFailure, disposalFailure);
        Assert.True(process.DisposeCalled);
    }

    [Fact]
    public async Task Raw_eof_during_final_check_cannot_escape_the_pending_sweep()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var process = new TerminalWhileRunningProcess(endDuringFinalCheck: true);
        var session = new ControlModeSession(process);

        await session.WaitForReadyAsync(token);
        TmuxCommand command = TmuxCommand.Create("display-message", "-p", "racing");
        EndOfStreamException terminalFailure =
            await Assert.ThrowsAsync<EndOfStreamException>(async () =>
                await session.SendAsync(command, token)
                    .WaitAsync(TimeSpan.FromSeconds(2), token));

        Assert.Equal(
            "The tmux control stream ended without an %exit notification.",
            terminalFailure.Message);
        Assert.False(session.IsRunning);
        Assert.Equal([ControlModeCommandRenderer.Render(command)], process.WriteAttempts);
        EndOfStreamException disposalFailure =
            await Assert.ThrowsAsync<EndOfStreamException>(
                () => session.DisposeAsync().AsTask());
        Assert.Same(terminalFailure, disposalFailure);
        Assert.True(process.DisposeCalled);
    }

    [Fact]
    public async Task A_truncated_attach_block_never_marks_the_session_ready()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TruncatedBlockProcess process = TruncatedBlockProcess.ForAttach();
        var session = new ControlModeSession(process);

        InvalidDataException readinessFailure =
            await Assert.ThrowsAsync<InvalidDataException>(
                () => session.WaitForReadyAsync(token));
        InvalidDataException disposalFailure =
            await Assert.ThrowsAsync<InvalidDataException>(
                () => session.DisposeAsync().AsTask());

        Assert.Same(readinessFailure, disposalFailure);
        Assert.True(process.DisposeCalled);
    }

    [Fact]
    public async Task A_terminated_error_attach_block_fails_readiness_with_tmux_output()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TruncatedBlockProcess process = TruncatedBlockProcess.ForAttachError();
        var session = new ControlModeSession(process);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.WaitForReadyAsync(token));

        Assert.Equal("can't find pane: missing", error.Message);
        await session.DisposeAsync();
        Assert.True(process.DisposeCalled);
    }

    [Fact]
    public async Task A_truncated_command_block_fails_every_pending_command()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TruncatedBlockProcess process = TruncatedBlockProcess.ForCommands();
        var session = new ControlModeSession(process);

        await session.WaitForReadyAsync(token);
        TmuxCommand firstCommand = TmuxCommand.Create("first");
        TmuxCommand secondCommand = TmuxCommand.Create("second");
        Task<IReadOnlyList<string>> first = session.SendAsync(firstCommand, token);
        Task<IReadOnlyList<string>> second = session.SendAsync(secondCommand, token);
        await process.TwoCommandsDispatched.Task.WaitAsync(token);

        process.EndCommandBlockEarly();

        InvalidDataException firstFailure =
            await Assert.ThrowsAsync<InvalidDataException>(async () => await first);
        InvalidDataException secondFailure =
            await Assert.ThrowsAsync<InvalidDataException>(async () => await second);
        InvalidDataException disposalFailure =
            await Assert.ThrowsAsync<InvalidDataException>(
                () => session.DisposeAsync().AsTask());

        Assert.Same(firstFailure, secondFailure);
        Assert.Same(firstFailure, disposalFailure);
        Assert.Equal(
            [
                ControlModeCommandRenderer.Render(firstCommand),
                ControlModeCommandRenderer.Render(secondCommand),
            ],
            process.WriteAttempts);
        Assert.True(process.DisposeCalled);
    }

    [Fact]
    public async Task An_event_burst_drops_oldest_without_blocking_a_reply_or_exit()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        const int ExtraNotifications = 56;
        int notificationCount = ControlModeSession.EventBufferCapacity + ExtraNotifications;
        var process = new BurstOutputProcess(notificationCount);
        var session = new ControlModeSession(process);

        await session.WaitForReadyAsync(token);
        IReadOnlyList<string> reply = await session.SendAsync(
            TmuxCommand.Create("display-message", "-p", "reply"),
            token);
        await session.DisposeAsync();

        var observed = new List<TmuxEvent>();
        await foreach (TmuxEvent item in session.Events.WithCancellation(token))
        {
            observed.Add(item);
        }

        int expectedDropped = notificationCount + 1 - ControlModeSession.EventBufferCapacity;
        Assert.Equal(["reply-ok"], reply);
        Assert.Equal(ControlModeSession.EventBufferCapacity + 1, observed.Count);
        TmuxEventsDroppedEvent loss = Assert.IsType<TmuxEventsDroppedEvent>(observed[0]);
        Assert.Equal(expectedDropped, loss.Count);
        Assert.Equal(expectedDropped, loss.TotalDropped);

        for (int offset = 0; offset < ControlModeSession.EventBufferCapacity - 1; offset++)
        {
            TmuxNotificationEvent notification =
                Assert.IsType<TmuxNotificationEvent>(observed[offset + 1]);
            Assert.Equal("burst", notification.Name);
            Assert.Equal(
                [(expectedDropped + offset).ToString(CultureInfo.InvariantCulture)],
                notification.Arguments);
        }

        TmuxExitEvent exit = Assert.IsType<TmuxExitEvent>(observed[^1]);
        Assert.Equal("done", exit.Reason);
        Assert.True(process.DisposeCalled);
    }

    [Theory]
    [InlineData(DispatchFailurePoint.PartialWrite)]
    [InlineData(DispatchFailurePoint.Flush)]
    public async Task An_ambiguous_dispatch_fails_pending_and_rejects_the_next_command(
        DispatchFailurePoint failurePoint)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var dispatchFailure = new IOException($"{failurePoint} failed");
        var process = new AmbiguousDispatchProcess(failurePoint, dispatchFailure);
        var session = new ControlModeSession(process);
        TmuxCommand pendingCommand = TmuxCommand.Create("display-message", "-p", "pending");
        TmuxCommand ambiguousCommand = TmuxCommand.Create("display-message", "-p", "ambiguous");
        TmuxCommand nextCommand = TmuxCommand.Create("display-message", "-p", "next");
        string pendingLine = ControlModeCommandRenderer.Render(pendingCommand);
        string ambiguousLine = ControlModeCommandRenderer.Render(ambiguousCommand);
        string nextLine = ControlModeCommandRenderer.Render(nextCommand);

        try
        {
            await session.WaitForReadyAsync(token);
            Task<IReadOnlyList<string>> pending = session.SendAsync(pendingCommand, token);
            await process.FirstDispatchCompleted.Task.WaitAsync(token);

            Task<IReadOnlyList<string>> ambiguous = session.SendAsync(ambiguousCommand, token);
            await process.FailureEntered.Task.WaitAsync(token);
            Task<IReadOnlyList<string>> next = session.SendAsync(nextCommand, token);

            process.ReleaseFailure();

            InvalidOperationException pendingError =
                await Assert.ThrowsAsync<InvalidOperationException>(async () => await pending);
            IOException ambiguousError =
                await Assert.ThrowsAsync<IOException>(async () => await ambiguous);
            await Assert.ThrowsAsync<ObjectDisposedException>(async () => await next);

            Assert.Same(dispatchFailure, pendingError.InnerException);
            Assert.Same(dispatchFailure, ambiguousError);
            Assert.Equal([pendingLine, ambiguousLine], process.WriteAttempts);
            Assert.DoesNotContain(nextLine, process.WriteAttempts);
            Assert.Equal(
                failurePoint == DispatchFailurePoint.PartialWrite
                    ? ambiguousLine[..8]
                    : ambiguousLine,
                process.AmbiguousAcceptedText);
            Assert.True(process.InputClosed);
            Assert.True(process.DisposeCalled);
            Assert.False(session.IsRunning);
        }
        finally
        {
            process.ReleaseFailure();
            await session.DisposeAsync();
        }
    }

    public enum DispatchFailurePoint
    {
        PartialWrite,
        Flush,
    }

    private static async Task DrainEventsAsync(
        IAsyncEnumerable<TmuxEvent> events,
        CancellationToken cancellationToken)
    {
        await foreach (TmuxEvent _ in events.WithCancellation(cancellationToken))
        {
        }
    }

    private static string FirstPromptLine(ReadOnlyMemory<char> input)
    {
        ReadOnlySpan<char> characters = input.Span;
        int separator = characters.IndexOf('\n');
        return separator < 0
            ? characters.ToString()
            : characters[..separator].ToString();
    }

    private static string RequestFence(ReadOnlyMemory<char> input)
    {
        ReadOnlySpan<char> characters = input.Span;
        int separator = characters.LastIndexOf('\n');
        return separator < 0
            ? string.Empty
            : characters[(separator + 1)..].ToString();
    }

    private sealed class TruncatedBlockProcess : IControlModeProcess
    {
        private readonly Channel<string> _output = Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
            });
        private readonly TaskCompletionSource _exited = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _flushCalls;
        private int _hasExited;

        private TruncatedBlockProcess()
        {
        }

        internal bool DisposeCalled { get; private set; }

        internal TaskCompletionSource TwoCommandsDispatched { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal List<string> WriteAttempts { get; } = [];

        public bool HasExited => Volatile.Read(ref _hasExited) != 0;

        internal static TruncatedBlockProcess ForAttach()
        {
            var process = new TruncatedBlockProcess();
            process._output.Writer.TryWrite("%begin 1 1 0");
            process._output.Writer.TryWrite("partial attach output");
            process.CompleteOutput();
            return process;
        }

        internal static TruncatedBlockProcess ForCommands()
        {
            var process = new TruncatedBlockProcess();
            process._output.Writer.TryWrite("%begin 1 1 0");
            process._output.Writer.TryWrite("%end 1 1 0");
            return process;
        }

        internal static TruncatedBlockProcess ForAttachError()
        {
            var process = new TruncatedBlockProcess();
            process._output.Writer.TryWrite("%begin 1 1 0");
            process._output.Writer.TryWrite("can't find pane: missing");
            process._output.Writer.TryWrite("%error 1 1 0");
            return process;
        }

        public Task WriteLineAsync(
            ReadOnlyMemory<char> command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteAttempts.Add(FirstPromptLine(command));
            return Task.CompletedTask;
        }

        public Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _flushCalls) == 2)
            {
                TwoCommandsDispatched.TrySetResult();
            }

            return Task.CompletedTask;
        }

        public async Task<string?> ReadLineAsync()
        {
            try
            {
                return await _output.Reader.ReadAsync().ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        public void CloseInput() => CompleteOutput();

        public void Kill() => CompleteOutput();

        public Task WaitForExitAsync(CancellationToken cancellationToken = default) =>
            _exited.Task.WaitAsync(cancellationToken);

        public void Dispose() => DisposeCalled = true;

        internal void EndCommandBlockEarly()
        {
            _output.Writer.TryWrite("%begin 2 2 0");
            _output.Writer.TryWrite("partial command output");
            CompleteOutput();
        }

        private void CompleteOutput()
        {
            Volatile.Write(ref _hasExited, 1);
            _output.Writer.TryComplete();
            _exited.TrySetResult();
        }
    }

    private sealed class BurstOutputProcess : IControlModeProcess
    {
        private readonly Channel<string> _output = Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
            });
        private readonly TaskCompletionSource _exited = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int _notificationCount;
        private int _hasExited;
        private string _sentinel = string.Empty;

        internal BurstOutputProcess(int notificationCount)
        {
            _notificationCount = notificationCount;
            _output.Writer.TryWrite("%begin 1 1 0");
            _output.Writer.TryWrite("%end 1 1 0");
        }

        internal bool DisposeCalled { get; private set; }

        public bool HasExited => Volatile.Read(ref _hasExited) != 0;

        public Task WriteLineAsync(
            ReadOnlyMemory<char> command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _sentinel = RequestFence(command);
            return Task.CompletedTask;
        }

        public Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int replyAfter = ControlModeSession.EventBufferCapacity + 17;
            for (int index = 0; index < _notificationCount; index++)
            {
                if (index == replyAfter)
                {
                    QueueReply();
                }

                _output.Writer.TryWrite(
                    "%burst " + index.ToString(CultureInfo.InvariantCulture));
            }

            QueueFence();
            _output.Writer.TryWrite("%exit done");
            CompleteOutput();
            return Task.CompletedTask;
        }

        public async Task<string?> ReadLineAsync()
        {
            try
            {
                return await _output.Reader.ReadAsync().ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        public void CloseInput() => CompleteOutput();

        public void Kill() => CompleteOutput();

        public Task WaitForExitAsync(CancellationToken cancellationToken = default) =>
            _exited.Task.WaitAsync(cancellationToken);

        public void Dispose() => DisposeCalled = true;

        private void QueueReply()
        {
            _output.Writer.TryWrite("%begin 2 2 1");
            _output.Writer.TryWrite("reply-ok");
            _output.Writer.TryWrite("%end 2 2 1");
        }

        private void QueueFence()
        {
            _output.Writer.TryWrite("%begin 2 3 1");
            _output.Writer.TryWrite($"parse error: unknown command: {_sentinel}");
            _output.Writer.TryWrite("%error 2 3 1");
        }

        private void CompleteOutput()
        {
            Volatile.Write(ref _hasExited, 1);
            _output.Writer.TryComplete();
            _exited.TrySetResult();
        }
    }

    private sealed class FaultedPumpProcess(
        Exception pumpFailure,
        Exception? disposeFailure = null) : IControlModeProcess
    {
        internal bool DisposeCalled { get; private set; }

        public bool HasExited => true;

        public Task WriteLineAsync(
            ReadOnlyMemory<char> command,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No command should be written.");

        public Task FlushAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No command should be flushed.");

        public Task<string?> ReadLineAsync() => Task.FromException<string?>(pumpFailure);

        public void CloseInput() => throw new InvalidOperationException(
            "An exited process should not have its input closed.");

        public void Kill() => throw new InvalidOperationException(
            "An exited process should not be killed.");

        public Task WaitForExitAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public void Dispose()
        {
            DisposeCalled = true;
            if (disposeFailure is not null)
            {
                throw disposeFailure;
            }
        }
    }

    private sealed class TerminalWhileRunningProcess : IControlModeProcess
    {
        private readonly Channel<string> _output = Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });
        private readonly TaskCompletionSource _exited = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _terminalRead = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _endDuringFinalCheck;
        private int _hasExitedReads;

        internal TerminalWhileRunningProcess(bool endDuringFinalCheck = false)
        {
            _endDuringFinalCheck = endDuringFinalCheck;
            _output.Writer.TryWrite("%begin 1 1 0");
            _output.Writer.TryWrite("%end 1 1 0");
        }

        internal bool DisposeCalled { get; private set; }

        internal TaskCompletionSource Flushed { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal List<string> WriteAttempts { get; } = [];

        public bool HasExited
        {
            get
            {
                if (_endDuringFinalCheck &&
                    Interlocked.Increment(ref _hasExitedReads) == 2)
                {
                    EndOutput();
                    _terminalRead.Task.GetAwaiter().GetResult();
                }

                return false;
            }
        }

        public Task WriteLineAsync(
            ReadOnlyMemory<char> command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteAttempts.Add(FirstPromptLine(command));
            return Task.CompletedTask;
        }

        public Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Flushed.TrySetResult();
            return Task.CompletedTask;
        }

        public async Task<string?> ReadLineAsync()
        {
            try
            {
                return await _output.Reader.ReadAsync().ConfigureAwait(false);
            }
            catch (ChannelClosedException error)
            {
                _terminalRead.TrySetResult();
                if (error.InnerException is not null)
                {
                    ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                }

                return null;
            }
        }

        public void CloseInput()
        {
            _output.Writer.TryComplete();
            _exited.TrySetResult();
        }

        public void Kill() => CloseInput();

        public Task WaitForExitAsync(CancellationToken cancellationToken = default) =>
            _exited.Task.WaitAsync(cancellationToken);

        public void Dispose() => DisposeCalled = true;

        internal void Exit(string reason)
        {
            _output.Writer.TryWrite($"%exit {reason}");
            EndOutput();
        }

        internal void EndOutput(Exception? failure = null) =>
            _output.Writer.TryComplete(failure);
    }

    private sealed class StalledWriteProcess : IControlModeProcess
    {
        private readonly Channel<string> _output = Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
            });
        private readonly TaskCompletionSource _exited = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseWrite = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _replyOnRelease;
        private int _hasExited;

        internal StalledWriteProcess(bool replyOnRelease = false)
        {
            _replyOnRelease = replyOnRelease;
            _output.Writer.TryWrite("%begin 1 1 0");
            _output.Writer.TryWrite("%end 1 1 0");
        }

        internal bool DisposeCalled { get; private set; }

        internal bool KillCalled { get; private set; }

        internal TaskCompletionSource WriteStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool HasExited => Volatile.Read(ref _hasExited) != 0;

        public async Task WriteLineAsync(
            ReadOnlyMemory<char> command,
            CancellationToken cancellationToken)
        {
            string sentinel = RequestFence(command);
            WriteStarted.TrySetResult();
            await _releaseWrite.Task.ConfigureAwait(false);
            if (_replyOnRelease)
            {
                _output.Writer.TryWrite("%begin 2 2 1");
                _output.Writer.TryWrite("reply");
                _output.Writer.TryWrite("%end 2 2 1");
                _output.Writer.TryWrite("%begin 2 3 1");
                _output.Writer.TryWrite($"parse error: unknown command: {sentinel}");
                _output.Writer.TryWrite("%error 2 3 1");
                return;
            }

            throw new IOException("The client was killed during its write.");
        }

        public Task FlushAsync(CancellationToken cancellationToken)
        {
            if (!_replyOnRelease)
            {
                throw new InvalidOperationException("A stalled write must not be flushed.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public async Task<string?> ReadLineAsync()
        {
            try
            {
                return await _output.Reader.ReadAsync().ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        public void CloseInput()
        {
            if (!_replyOnRelease)
            {
                throw new InvalidOperationException(
                    "Forced disposal must kill rather than close behind an active writer.");
            }

            Volatile.Write(ref _hasExited, 1);
            _output.Writer.TryComplete();
            _exited.TrySetResult();
        }

        public void Kill()
        {
            KillCalled = true;
            Volatile.Write(ref _hasExited, 1);
            _releaseWrite.TrySetResult();
            _output.Writer.TryComplete();
            _exited.TrySetResult();
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken = default) =>
            _exited.Task.WaitAsync(cancellationToken);

        public void Dispose() => DisposeCalled = true;

        internal void ReleaseWrite() => _releaseWrite.TrySetResult();
    }

    private sealed class AmbiguousDispatchProcess : IControlModeProcess
    {
        private readonly Channel<string> _output = Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
            });
        private readonly TaskCompletionSource _allowFailure = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _exited = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Exception _dispatchFailure;
        private readonly DispatchFailurePoint _failurePoint;
        private int _flushCalls;
        private int _hasExited;
        private int _writeCalls;

        internal AmbiguousDispatchProcess(
            DispatchFailurePoint failurePoint,
            Exception dispatchFailure)
        {
            _failurePoint = failurePoint;
            _dispatchFailure = dispatchFailure;
            QueueAttachReply();
        }

        internal string AmbiguousAcceptedText { get; private set; } = string.Empty;

        internal bool DisposeCalled { get; private set; }

        internal TaskCompletionSource FailureEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource FirstDispatchCompleted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool InputClosed { get; private set; }

        internal List<string> WriteAttempts { get; } = [];

        public bool HasExited => Volatile.Read(ref _hasExited) != 0;

        public async Task WriteLineAsync(
            ReadOnlyMemory<char> command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string text = FirstPromptLine(command);
            WriteAttempts.Add(text);
            int call = Interlocked.Increment(ref _writeCalls);
            if (call != 2 || _failurePoint != DispatchFailurePoint.PartialWrite)
            {
                if (call == 2)
                {
                    AmbiguousAcceptedText = text;
                }

                return;
            }

            AmbiguousAcceptedText = text[..Math.Min(8, text.Length)];
            FailureEntered.TrySetResult();
            await _allowFailure.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            throw _dispatchFailure;
        }

        public async Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int call = Interlocked.Increment(ref _flushCalls);
            if (call == 1)
            {
                FirstDispatchCompleted.TrySetResult();
                return;
            }

            if (call == 2 && _failurePoint == DispatchFailurePoint.Flush)
            {
                FailureEntered.TrySetResult();
                await _allowFailure.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                throw _dispatchFailure;
            }
        }

        public async Task<string?> ReadLineAsync()
        {
            try
            {
                return await _output.Reader.ReadAsync().ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        public void CloseInput()
        {
            InputClosed = true;
            Volatile.Write(ref _hasExited, 1);
            _output.Writer.TryComplete();
            _exited.TrySetResult();
        }

        public void Kill() => CloseInput();

        public Task WaitForExitAsync(CancellationToken cancellationToken = default) =>
            _exited.Task.WaitAsync(cancellationToken);

        public void Dispose() => DisposeCalled = true;

        internal void ReleaseFailure() => _allowFailure.TrySetResult();

        private void QueueAttachReply()
        {
            _output.Writer.TryWrite("%begin 1 1 0");
            _output.Writer.TryWrite("%end 1 1 0");
        }
    }
}
