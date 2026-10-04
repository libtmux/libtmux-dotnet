using System.Diagnostics;
using System.Runtime.Versioning;
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
        _ = await shell.WaitUntilAsync(rows => rows.Any(row => row.Contains("wait-for", StringComparison.Ordinal)), Allowed, token);
        Assert.False(waiting.IsCompleted);
        await raw.ExecuteAsync(["wait-for", "-S", gate], token);
        PaneWaitResult done = await waiting;

        Assert.Equal((PaneWaitOutcome.Matched, "MARKER"), (done.Outcome, done.Pattern));
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

        PaneRunOutcome endless = await PaneRunner.RunAsync(
            shell.Server,
            shell,
            PaneRunRoute.From(shell),
            "sleep 60",
            TimeSpan.FromMilliseconds(100),
            suppressHistory: true,
            statusMarkerLifetime: TimeSpan.FromMinutes(1),
            new PaneRunHooks { Completed = () => released.TrySetResult(), FollowLimit = TimeSpan.FromMilliseconds(300) },
            PaneReader.Failure,
            token);

        Assert.True(endless.TimedOut);
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

        Assert.Equal(busy.Id, refusal.PaneId);
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
