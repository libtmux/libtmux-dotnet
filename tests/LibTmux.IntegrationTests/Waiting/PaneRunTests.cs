using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Internal;

namespace LibTmux.IntegrationTests.Waiting;

[UnsupportedOSPlatform("windows")]
public sealed class PaneRunTests
{
    private static readonly TimeSpan Allowed = TimeSpan.FromSeconds(10);

    [UnixFact]
    public async Task A_run_reports_its_exit_status_and_only_its_own_output()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Pane shell = await NewPaneAsync(raw, ["/bin/sh"], token);

        PaneRunResult failed = await shell.RunAsync("printf 'first\\nsecond\\n'; exit 3", Allowed, token);
        PaneRunResult passed = await shell.RunAsync("printf 'third\\n'", Allowed, token);

        Assert.Equal((3, false, true), (failed.ExitStatus, failed.TimedOut, failed.Started));
        Assert.Equal(["first", "second"], failed.Output);
        Assert.False(failed.Succeeded);
        Assert.True(passed.Succeeded);
        Assert.Equal(["third"], passed.Output);
    }

    [UnixFact]
    public async Task A_run_bounds_rendered_output_and_reports_what_was_omitted()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Pane shell = await NewPaneAsync(raw, ["/bin/sh"], token);

        PaneRunResult lines = await shell.RunAsync(
            new PaneRunRequest("printf 'one\\ntwo\\nthree\\n'")
            {
                Timeout = Allowed,
                MaxOutputLines = 2,
            }, token);
        Assert.Equal(["two", "three"], lines.Output);
        Assert.Equal(1, lines.OmittedOutputLines);
        Assert.Equal(4, lines.OmittedOutputBytes);
        Assert.Equal(shell.Id, lines.PaneId);
        Assert.Equal(Allowed, lines.EffectiveTimeout);

        PaneRunResult bytes = await shell.RunAsync(
            new PaneRunRequest("printf 'αβγ\\n'")
            {
                Timeout = Allowed,
                MaxOutputBytes = 4,
            }, token);
        Assert.Equal(["βγ"], bytes.Output);
        Assert.Equal(0, bytes.OmittedOutputLines);
        Assert.Equal(2, bytes.OmittedOutputBytes);
    }

    [UnixFact]
    public async Task Sending_a_command_waits_for_its_output_not_the_screen_before_or_its_echo()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Pane shell = await NewPaneAsync(raw, ["/bin/sh"], token);
        _ = await shell.RunAsync("printf 'MARKER\\n'", Allowed, token);
        string gate = $"{raw.SessionName}-send";

        Task<PaneWaitResult> waiting = shell.SendTextAndWaitAsync(
            $"'{raw.TmuxBinaryPath}' -S '{raw.SocketPath}' wait-for {gate}; echo MARKER",
            "MARKER",
            Allowed,
            token);
        // The typed line wraps where the socket path's length puts it, which
        // can be inside "wait-for", so the rows are read as one text.
        _ = await shell.WaitUntilAsync(rows => string.Concat(rows).Contains("wait-for", StringComparison.Ordinal), Allowed, token);
        Assert.False(waiting.IsCompleted);
        await raw.ExecuteAsync(["wait-for", "-S", gate], token);
        PaneWaitResult done = await waiting;

        Assert.Equal((PaneWaitOutcome.Matched, "MARKER"), (done.Outcome, done.Pattern));
    }

    [UnixFact]
    public async Task A_send_wait_ignores_old_patterns_when_new_text_extends_the_same_row()
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Allowed);
        CancellationToken token = deadline.Token;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        string baseline = $"{raw.SessionName}-entry-row";
        string partial = $"{raw.SessionName}-partial-row";
        string tmux = $"{PaneRunner.ShellQuote(raw.TmuxBinaryPath)} -S {PaneRunner.ShellQuote(raw.SocketPath)}";
        string program = "stty -echo; printf 'old ready fatal'; "
            + $"{tmux} wait-for -S {baseline}; read -r trigger; printf ' re'; "
            + $"{tmux} wait-for -S {partial}; read -r resume; printf 'ady\\n'; exec /bin/cat";
        Pane pane = await NewPaneAsync(raw, ["/bin/sh", "-c", program], token);
        await raw.ExecuteAsync(["wait-for", baseline], token);
        Assert.Contains("old ready fatal", await pane.CaptureAsync(cancellationToken: token));

        Task<PaneWaitResult> waiting = pane.SendKeysAndWaitAsync(
            new SendKeysRequest { Text = "trigger", Literal = true },
            PaneWaitRequest.FromTextPatterns(["ready"], ["fatal"], simpleMatch: true) with
            {
                Timeout = Allowed,
                TailLines = 24,
                MaxOutputBytes = 64,
            },
            token);
        await raw.ExecuteAsync(["wait-for", partial], token);
        Assert.Contains("old ready fatal re", await pane.CaptureAsync(cancellationToken: token));
        await pane.SendKeysAsync(new SendKeysRequest { Text = "resume", Literal = true }, token);
        PaneWaitResult done = await waiting;

        Assert.Equal((PaneWaitOutcome.Matched, "ready"), (done.Outcome, done.Pattern));
        Assert.Equal(pane.Id, done.PaneId);
        Assert.Equal(["old ready fatal ready"], done.Tail.Where(line => line.Length > 0));
        Assert.InRange(done.Tail.Count, 1, 24);
        Assert.InRange(Encoding.UTF8.GetByteCount(string.Join('\n', done.Tail)), 0, 64);
    }

    [UnixFact]
    public async Task A_run_that_outlasts_its_timeout_says_so_and_is_followed_to_completion()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Pane shell = await NewPaneAsync(raw, ["/bin/sh"], token);

        PaneRunResult slow = await shell.RunAsync("sleep 1; printf 'late\\n'", TimeSpan.FromMilliseconds(200), token);

        Assert.True(slow.TimedOut);
        Assert.Null(slow.ExitStatus);
        PaneWaitResult finished = await shell.WaitForTextAsync("late", Allowed, token);
        Assert.True(finished.Found);

        // Following the run removes its status option when it ends, long
        // before tmux's own scheduled removal a minute after the timeout.
        await TmuxWait.UntilAsync(
            async ct => !(await StatusOptionsAsync(raw, shell, ct)).Any(),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(50),
            cancellationToken: token);
    }

    [UnixFact]
    public async Task A_run_that_never_finishes_is_followed_only_until_its_limit()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Pane shell = await NewPaneAsync(raw, ["/bin/sh"], token);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unresolved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        PaneRunOutcome endless = await PaneRunner.RunAsync(
            shell.Server,
            shell,
            PaneRunRoute.From(shell),
            "sleep 60",
            TimeSpan.FromMilliseconds(100),
            suppressHistory: true,
            statusMarkerLifetime: TimeSpan.FromMinutes(1),
            new PaneRunHooks
            {
                Completed = () => released.TrySetResult(),
                Unresolved = () => unresolved.TrySetResult(),
                FollowLimit = TimeSpan.FromMilliseconds(300),
            },
            PaneReader.Failure,
            token);

        Assert.True(endless.TimedOut);
        await unresolved.Task.WaitAsync(Allowed, token);
        Assert.False(released.Task.IsCompleted);
        await raw.ExecuteAsync(["kill-pane", "-t", shell.Id.ToString()], token);
        Assert.True(await PaneRunner.TryReconcilePendingAsync(shell, token));
        await released.Task.WaitAsync(Allowed, token);
    }

    [UnixFact]
    public async Task An_unresolved_run_stays_reserved_then_late_status_allows_the_next_run()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Pane shell = await NewPaneAsync(raw, ["/bin/sh"], token);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string gate = $"{raw.SessionName}-late-run";
        string quotedBinary = PaneRunner.ShellQuote(raw.TmuxBinaryPath);
        string quotedSocket = PaneRunner.ShellQuote(raw.SocketPath);

        PaneRunOutcome endless = await PaneRunner.RunAsync(
            shell.Server,
            shell,
            PaneRunRoute.From(shell),
            $"{quotedBinary} -S {quotedSocket} wait-for {gate}; printf 'late-ready\\n'",
            TimeSpan.FromMilliseconds(100),
            suppressHistory: true,
            statusMarkerLifetime: TimeSpan.FromMinutes(1),
            new PaneRunHooks
            {
                Completed = () => released.TrySetResult(),
                Unresolved = () => stopped.TrySetResult(),
                FollowLimit = TimeSpan.FromMilliseconds(300),
            },
            PaneReader.Failure,
            token);

        Assert.True(endless.TimedOut);
        await stopped.Task.WaitAsync(Allowed, token);
        Assert.False(released.Task.IsCompleted);
        LibTmuxException pending = await Assert.ThrowsAsync<LibTmuxException>(
            () => shell.RunAsync("printf 'must-not-send\\n'", Allowed, token));
        Assert.Contains("already has a command", pending.Message, StringComparison.Ordinal);

        await raw.ExecuteAsync(["wait-for", "-S", gate], token);
        _ = await TmuxWait.UntilAsync(
            async ct => (await StatusOptionsAsync(raw, shell, ct)).Any(),
            Allowed,
            TimeSpan.FromMilliseconds(50),
            cancellationToken: token);
        PaneRunResult next = await shell.RunAsync("printf 'after-reconcile\\n'", Allowed, token);
        Assert.Equal(0, next.ExitStatus);
        Assert.Contains("after-reconcile", next.Output);
        await released.Task.WaitAsync(Allowed, token);
    }

    [UnixFact]
    public async Task An_unresolved_run_releases_ownership_when_its_pane_ends()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Pane shell = await NewPaneAsync(raw, ["/bin/sh"], token);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string gate = $"{raw.SessionName}-ended-run";
        string command = $"{PaneRunner.ShellQuote(raw.TmuxBinaryPath)} -S "
            + $"{PaneRunner.ShellQuote(raw.SocketPath)} wait-for {gate}";

        PaneRunOutcome pending = await PaneRunner.RunAsync(
            shell.Server,
            shell,
            PaneRunRoute.From(shell),
            command,
            TimeSpan.FromMilliseconds(100),
            suppressHistory: true,
            statusMarkerLifetime: TimeSpan.FromMinutes(1),
            new PaneRunHooks
            {
                Completed = () => released.TrySetResult(),
                Unresolved = () => stopped.TrySetResult(),
                FollowLimit = TimeSpan.FromMilliseconds(300),
            },
            PaneReader.Failure,
            token);

        Assert.True(pending.TimedOut);
        await stopped.Task.WaitAsync(Allowed, token);
        Assert.False(released.Task.IsCompleted);
        await raw.ExecuteAsync(["kill-pane", "-t", shell.Id.ToString()], token);

        await Assert.ThrowsAsync<TmuxObjectNotFoundException>(
            () => shell.RunAsync("printf 'never\\n'", Allowed, token));
        await released.Task.WaitAsync(Allowed, token);
    }

    // A shell killed mid-run never signals the run, which ends within seconds
    // rather than at its timeout. A pane tmux keeps still shows what the
    // command printed; one tmux closes leaves nothing to read.
    [UnixFact]
    public async Task A_run_ends_soon_after_its_panes_program_exits()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Pane closing = await NewPaneAsync(raw, ["/bin/sh"], token);
        Pane kept = await NewPaneAsync(raw, ["/bin/sh"], token);
        await raw.ExecuteAsync(["set-option", "-p", "-t", kept.Id.ToString(), "remain-on-exit", "on"], token);

        PaneRunResult closed = await RunUntilShellKilledAsync(closing, token);
        PaneRunResult dead = await RunUntilShellKilledAsync(kept, token);

        Assert.Equal((true, null, false), (closed.PaneExited, closed.ExitStatus, closed.TimedOut));
        Assert.Empty(closed.Output);
        Assert.Equal((true, null, false), (dead.PaneExited, dead.ExitStatus, dead.TimedOut));
        Assert.Contains("before", dead.Output);
        Assert.True(
            closed.Elapsed < TimeSpan.FromSeconds(15) && dead.Elapsed < TimeSpan.FromSeconds(15),
            $"The runs ended after {closed.Elapsed} and {dead.Elapsed}.");
    }

    [UnixFact]
    public async Task A_pane_not_at_a_shell_is_refused_before_anything_is_sent()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Pane busy = await NewPaneAsync(raw, ["sleep", "60"], token);

        TmuxPaneException refusal = await Assert.ThrowsAsync<TmuxPaneException>(
            () => busy.RunAsync("echo never", Allowed, token));

        Assert.Equal((busy.Id, TmuxDispatchState.NotDispatched), (refusal.PaneId, refusal.Dispatch));
        RawTmuxResult buffers = await raw.ExecuteAsync(["list-buffers"], token);
        Assert.Empty(buffers.StandardOutputLines);
    }

    // Killed from outside, as a crash or the OOM killer would: a command that
    // killed its own shell could still record its status as tmux hangs it up.
    private static async Task<PaneRunResult> RunUntilShellKilledAsync(Pane pane, CancellationToken token)
    {
        Task<PaneRunResult> running = pane.RunAsync("printf 'before\\n'; sleep 30", TimeSpan.FromSeconds(60), token);
        _ = await pane.WaitForTextAsync("before", Allowed, token);
        using Process shell = Process.GetProcessById(pane.ProcessId);
        shell.Kill();
        return await running;
    }

    private static async Task<IEnumerable<string>> StatusOptionsAsync(
        RawTmuxTestContext raw,
        Pane pane,
        CancellationToken token)
    {
        RawTmuxResult options = await raw.ExecuteAsync(["show-options", "-p", "-t", pane.Id.ToString()], token);
        return options.StandardOutputLines.Where(line => line.StartsWith("@lt_s_", StringComparison.Ordinal));
    }

    // Several words run the program directly, with no shell in between.
    private static async Task<Pane> NewPaneAsync(RawTmuxTestContext raw, string[] command, CancellationToken token)
    {
        RawTmuxResult created = await raw.ExecuteAsync(
            ["new-window", "-d", "-P", "-F", "#{pane_id}", "-t", raw.SessionName, "--", .. command],
            token);
        Server server = await Server.ConnectAsync(
            new ServerConnectionOptions
            {
                TmuxBinaryPath = raw.TmuxBinaryPath,
                SocketPath = raw.SocketPath,
                ConfigurationFile = "/dev/null",
            },
            token);
        Pane pane = await server.GetPaneAsync(PaneId.Parse(created.StandardOutputText.Trim()), token);
        return command is ["/bin/sh"]
            ? await WaitForPromptAsync(pane, token)
            : pane;
    }

    // A fresh shell reads its input only once it prints its first prompt.
    private static async Task<Pane> WaitForPromptAsync(Pane pane, CancellationToken token)
    {
        _ = await pane.WaitForTextAsync(
            new PaneWaitRequest { Patterns = [new System.Text.RegularExpressions.Regex(@"[$#] ?$")], Timeout = Allowed },
            token);
        return pane;
    }
}
