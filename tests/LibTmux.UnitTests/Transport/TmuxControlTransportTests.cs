using System.Runtime.Versioning;
using System.Threading.Channels;
using LibTmux.Internal;

namespace LibTmux.UnitTests.Transport;

[UnsupportedOSPlatform("windows")]
public sealed class TmuxControlTransportTests
{
    [Fact]
    public async Task A_command_that_may_have_run_when_the_client_ended_fails_as_unknown_and_is_not_repeated()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var processed = new List<string>();
        TmuxControlTransport transport = Create(processed, endAfterIdentity: false, endOnCommand: true);

        TmuxTransportException failure = await Assert.ThrowsAsync<TmuxTransportException>(() => transport.ExecuteAsync(
            TmuxCommandRequest.Single(["rename-window", "-t", "@1", "build"]),
            token));

        Assert.Equal(TmuxDispatchState.Unknown, failure.Dispatch);
        Assert.Empty(processed);
    }

    [Fact]
    public async Task A_read_that_met_the_end_of_the_client_runs_again_on_a_process()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var processed = new List<string>();
        TmuxControlTransport transport = Create(processed, endAfterIdentity: false, endOnCommand: true);

        TmuxCommandResult result = await transport.ExecuteAsync(
            TmuxCommandRequest.Single(["list-windows", "-t", "$1"]),
            token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["list-windows"], processed);
    }

    [Fact]
    public async Task A_guarded_read_that_met_the_end_of_the_client_runs_again_on_a_process()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var processed = new List<string>();
        TmuxControlTransport transport = Create(processed, endAfterIdentity: false, endOnCommand: true);

        // The shape an entity's read has: the generation question, the guard, the read.
        TmuxCommandResult result = await transport.ExecuteAsync(
            TmuxCommandRequest.Group(
                false,
                ["display-message", "-p", "#{pid}:#{start_time}"],
                ["if-shell", "-F", "#{==:#{pid}:#{start_time},1:2}", "", "libtmux_stale_x"],
                ["list-sessions"]),
            token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["display-message"], processed);
    }

    [Fact]
    public async Task A_command_the_ended_client_never_took_runs_on_a_process_whatever_it_does()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var processed = new List<string>();
        TmuxControlTransport transport = Create(processed, endAfterIdentity: true, endOnCommand: false);

        TmuxCommandResult result = await transport.ExecuteAsync(
            TmuxCommandRequest.Single(["rename-window", "-t", "@1", "build"]),
            token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["rename-window"], processed);
    }

    [Fact]
    public async Task A_command_after_one_that_ended_the_clients_session_does_not_meet_the_ending_client()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var started = new List<ScriptedClient>();
        TmuxControlClient client = TmuxControlClient.For($"unit-{Guid.NewGuid():N}");
        var transport = new TmuxControlTransport(
            (request, _) =>
            {
                // tmux has marked the client for exit by the time the kill answers,
                // but the client has not yet seen it.
                if (request.LogicalArguments[0] == "kill-session")
                {
                    started[0].Dying = true;
                }

                return Task.FromResult(new TmuxCommandResult(request.LogicalArguments, 0, default, default, [], []));
            },
            client,
            () =>
            {
                var next = new ScriptedClient(endAfterIdentity: false, endOnCommand: false);
                started.Add(next);
                return new ControlModeSession(next);
            });

        _ = await transport.ExecuteAsync(TmuxCommandRequest.Single(["rename-window", "-t", "@1", "first"]), token);
        _ = await transport.ExecuteAsync(TmuxCommandRequest.Single(["kill-session", "-t", "$1"]), token);
        TmuxCommandResult after = await transport.ExecuteAsync(
            TmuxCommandRequest.Single(["rename-window", "-t", "@1", "second"]),
            token);

        Assert.Equal(0, after.ExitCode);
        Assert.Equal(2, started.Count);
    }

    [Fact]
    public async Task A_message_with_no_client_named_goes_to_the_other_client_and_not_to_the_library_client()
    {
        Probe probe = Probe.With(otherClient: true);

        _ = await probe.RunAsync(["display-message", "-p", "-t", "%1", "#{pane_id}"]);
        _ = await probe.RunAsync(["display-message", "-t", "%1", "hello"]);

        // -p prints and shows nothing, so it stays on the client; the message
        // names the client it is for.
        Assert.Single(probe.Client.Commands, command => command.Contains("'-p'", StringComparison.Ordinal));
        Assert.Equal(
            [["display-message", "-c", "client-9", "-t", "%1", "hello"]],
            probe.Processed);
    }

    [Fact]
    public async Task A_message_with_no_other_client_to_show_on_is_not_sent()
    {
        Probe probe = Probe.With(otherClient: false);

        TmuxCommandResult result = await probe.RunAsync(["display-message", "hello"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(probe.Processed);
        Assert.DoesNotContain(probe.Client.Commands, command => command.Contains("hello", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_message_is_left_alone_where_tmux_cannot_name_its_client()
    {
        Probe probe = Probe.With(otherClient: true, version: "3.2a");

        _ = await probe.RunAsync(["display-message", "hello"]);

        Assert.Equal([["display-message", "hello"]], probe.Processed);
    }

    [Fact]
    public async Task A_buffer_copied_out_is_copied_to_the_other_client_or_only_set_when_there_is_none()
    {
        Probe other = Probe.With(otherClient: true);
        _ = await other.RunAsync(["set-buffer", "-w", "-b", "b", "data"]);
        _ = await other.RunAsync(["set-buffer", "-b", "b", "data"]);

        Probe alone = Probe.With(otherClient: false);
        _ = await alone.RunAsync(["set-buffer", "-aw", "data"]);

        Assert.Equal([["set-buffer", "-t", "client-9", "-w", "-b", "b", "data"]], other.Processed);
        Assert.Single(other.Client.Commands, command => command.Contains("'set-buffer' '-b'", StringComparison.Ordinal));
        Assert.Equal([["set-buffer", "-a", "data"]], alone.Processed);
    }

    [Fact]
    public async Task Keys_sent_as_typed_go_to_the_other_client_or_nowhere()
    {
        Probe other = Probe.With(otherClient: true);
        _ = await other.RunAsync(["send-keys", "-K", "-t", "%1", "x"]);
        _ = await other.RunAsync(["send-keys", "-t", "%1", "x"]);

        Probe alone = Probe.With(otherClient: false);
        TmuxCommandResult result = await alone.RunAsync(["send-keys", "-K", "x"]);

        Assert.Equal([["send-keys", "-c", "client-9", "-K", "-t", "%1", "x"]], other.Processed);
        Assert.Single(other.Client.Commands, command => command.Contains("'send-keys' '-t'", StringComparison.Ordinal));
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(alone.Processed);
    }

    [Fact]
    public async Task A_reply_over_a_limit_fails_its_command_and_leaves_the_client_and_the_others_alone()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var limits = new ControlModeLimits(maxBlockLines: 2, failOnlyOversizedCommand: true);
        Probe probe = Probe.With(otherClient: false, limits: limits);
        _ = await probe.RunAsync(["list-windows"]);

        // Sent together: the reply to the first is too large, the second's is not.
        Task<TmuxCommandResult> large = probe.Transport.ExecuteAsync(
            TmuxCommandRequest.Single(["set-option", "-g", "big", "x"]), token);
        Task<TmuxCommandResult> following = probe.Transport.ExecuteAsync(
            TmuxCommandRequest.Single(["rename-window", "-t", "@1", "a"]), token);

        TmuxTransportException failure = await Assert.ThrowsAsync<TmuxTransportException>(() => large);
        Assert.Equal(TmuxDispatchState.Dispatched, failure.Dispatch);
        Assert.Equal(0, (await following).ExitCode);
        Assert.Equal(0, (await probe.RunAsync(["rename-window", "-t", "@1", "b"])).ExitCode);
        Assert.Empty(probe.Processed);
    }

    [Fact]
    public async Task A_read_whose_reply_is_over_a_limit_is_asked_of_a_process()
    {
        var limits = new ControlModeLimits(maxBlockLines: 2, failOnlyOversizedCommand: true);
        Probe probe = Probe.With(otherClient: false, limits: limits);

        TmuxCommandResult result = await probe.RunAsync(["show-options", "-g", "big"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([["show-options", "-g", "big"]], probe.Processed);
    }

    [Fact]
    public async Task A_window_moved_out_of_the_clients_session_runs_on_a_process_and_the_client_is_asked_after()
    {
        Probe probe = Probe.With(otherClient: false);

        _ = await probe.RunAsync(["rename-window", "-t", "@1", "a"]);
        _ = await probe.RunAsync(["move-window", "-s", "@1", "-t", "other:"]);

        Assert.Equal([["move-window", "-s", "@1", "-t", "other:"]], probe.Processed);
        Assert.Contains(probe.Client.Commands, command => command.Contains("'rename-window'", StringComparison.Ordinal));
        Assert.Contains(probe.Client.Commands, command => command.Contains("'display-message' '-p' ''", StringComparison.Ordinal));
    }

    private sealed class Probe
    {
        private readonly TmuxControlTransport _transport;

        private Probe(ScriptedClient client, List<string[]> processed, TmuxControlTransport transport)
        {
            Client = client;
            Processed = processed;
            _transport = transport;
        }

        internal TmuxControlTransport Transport => _transport;

        internal ScriptedClient Client { get; }

        internal List<string[]> Processed { get; }

        internal static Probe With(bool otherClient, string version = "3.4", ControlModeLimits? limits = null)
        {
            var client = new ScriptedClient(endAfterIdentity: false, endOnCommand: false)
            {
                Replies = command => command.Contains("'list-clients'", StringComparison.Ordinal) && otherClient
                    ? ["5\tclient-9\t$1"]
                    : command.Contains("'big'", StringComparison.Ordinal) ? ["one", "two", "three", "four"] : [],
            };
            var processed = new List<string[]>();
            var transport = new TmuxControlTransport(
                (request, _) =>
                {
                    processed.Add([.. request.Commands[0]]);
                    return Task.FromResult(new TmuxCommandResult(request.LogicalArguments, 0, default, default, [], []));
                },
                TmuxControlClient.For($"unit-{Guid.NewGuid():N}"),
                () => new ControlModeSession(client, limits: limits),
                () => TmuxVersion.Parse(version));
            return new Probe(client, processed, transport);
        }

        // The first command starts the client; the ones that follow find it attached.
        internal async Task<TmuxCommandResult> RunAsync(string[] command)
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            if (!_started)
            {
                _started = true;
                _ = await _transport.ExecuteAsync(TmuxCommandRequest.Single(["list-windows"]), token);
            }

            return await _transport.ExecuteAsync(TmuxCommandRequest.Single(command), token);
        }

        private bool _started;
    }

    private static TmuxControlTransport Create(List<string> processed, bool endAfterIdentity, bool endOnCommand)
    {
        TmuxControlClient client = TmuxControlClient.For($"unit-{Guid.NewGuid():N}");
        return new TmuxControlTransport(
            (request, _) =>
            {
                processed.Add(request.LogicalArguments[0]);
                return Task.FromResult(new TmuxCommandResult(request.LogicalArguments, 0, default, default, [], []));
            },
            client,
            () => new ControlModeSession(new ScriptedClient(endAfterIdentity, endOnCommand)));
    }

    // Answers the attach and the identity query, then ends as scripted.
    private sealed class ScriptedClient : IControlModeProcess
    {
        private readonly Channel<string> _output = Channel.CreateUnbounded<string>();
        private readonly bool _endAfterIdentity;
        private readonly bool _endOnCommand;
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writes;
        private int _hasExited;

        internal ScriptedClient(bool endAfterIdentity, bool endOnCommand)
        {
            _endAfterIdentity = endAfterIdentity;
            _endOnCommand = endOnCommand;
            _output.Writer.TryWrite("%begin 1 1 0");
            _output.Writer.TryWrite("%end 1 1 0");
        }

        // What the client answers a command with, by the command's text.
        internal Func<string, string[]>? Replies { get; set; }

        // The commands written to the client after its identity query.
        internal List<string> Commands { get; } = [];

        // Set when tmux has already marked this client for exit: the next thing
        // written to it ends it without an answer.
        internal bool Dying { get; set; }

        public bool HasExited => Volatile.Read(ref _hasExited) != 0;

        public Task WriteLineAsync(ReadOnlyMemory<char> command, CancellationToken cancellationToken)
        {
            if (Dying)
            {
                Stop();
                return Task.CompletedTask;
            }

            string sentinel = command.ToString().Split('\n')[^1];
            int write = Interlocked.Increment(ref _writes);
            if (write == 1)
            {
                // The identity query, then the fence that closes the request.
                _output.Writer.TryWrite("%begin 1 2 1");
                _output.Writer.TryWrite("client-1\t$1");
                _output.Writer.TryWrite("%end 1 2 1");
                Fence(3, sentinel);
                if (_endAfterIdentity)
                {
                    Stop();
                }
            }
            else if (_endOnCommand)
            {
                Stop();
            }
            else
            {
                // The command's answer, then the fence.
                int number = 10 * write;
                string text = command.ToString().Split('\n')[0];
                lock (Commands)
                {
                    Commands.Add(text);
                }

                _output.Writer.TryWrite($"%begin 1 {number} 1");
                foreach (string line in Replies?.Invoke(text) ?? [])
                {
                    _output.Writer.TryWrite(line);
                }

                _output.Writer.TryWrite($"%end 1 {number} 1");
                Fence(number + 1, sentinel);
            }

            return Task.CompletedTask;
        }

        public Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

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

        public void CloseInput() => Stop();

        public void Kill() => Stop();

        public Task WaitForExitAsync(CancellationToken cancellationToken = default) =>
            _exited.Task.WaitAsync(cancellationToken);

        public void Dispose() => Stop();

        private void Fence(int number, string sentinel)
        {
            _output.Writer.TryWrite($"%begin 1 {number} 1");
            _output.Writer.TryWrite($"parse error: unknown command: {sentinel}");
            _output.Writer.TryWrite($"%error 1 {number} 1");
        }

        private void Stop()
        {
            if (Interlocked.Exchange(ref _hasExited, 1) != 0)
            {
                return;
            }

            _output.Writer.TryWrite("%exit");
            _output.Writer.TryComplete();
            _exited.TrySetResult();
        }
    }
}
