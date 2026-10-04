using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;

namespace LibTmux.IntegrationTests.Waiting;

[UnsupportedOSPlatform("windows")]
public sealed class PaneWaitTests
{
    private static readonly TimeSpan Arrival = TimeSpan.FromSeconds(5);

    [UnixFact]
    public async Task Waits_end_on_new_text_stop_patterns_and_any_output()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Pane building = await NewPaneAsync(raw, "build", "printf 'build: %s\\n' done", token);
        Pane failing = await NewPaneAsync(raw, "fail", "printf 'FAT''AL: no disk\\n'", token);
        Pane chatty = await NewPaneAsync(raw, "chat", "printf 'anything\\n'", token);

        PaneWaitResult matched = await AfterEntryAsync(
            raw,
            "build",
            building,
            new PaneWaitRequest { Patterns = [new Regex("build: done")], Timeout = Arrival },
            token);
        PaneWaitResult stopped = await AfterEntryAsync(
            raw,
            "fail",
            failing,
            new PaneWaitRequest { Patterns = [new Regex("listening")], StopPatterns = [new Regex("^FATAL")], Timeout = Arrival },
            token);
        PaneWaitResult any = await AfterEntryAsync(raw, "chat", chatty, new PaneWaitRequest { Timeout = Arrival }, token);

        Assert.Equal((PaneWaitOutcome.Matched, "build: done"), (matched.Outcome, matched.Pattern));
        Assert.Equal((PaneWaitOutcome.Stopped, "^FATAL"), (stopped.Outcome, stopped.Pattern));
        Assert.Equal(PaneWaitOutcome.AnyOutput, any.Outcome);
        Assert.DoesNotContain(await building.Server.GetClientsAsync(token), client => client.IsControlClient);
    }

    // A read through the wait's own control client is a round trip; one
    // through a tmux process is a process start, about ten times the cost.
    [UnixFact]
    public async Task A_wait_reads_the_pane_through_its_control_client()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        RawTmuxResult created = await raw.ExecuteAsync(
            ["new-window", "-d", "-P", "-F", "#{pane_id}", "-t", raw.SessionName, "sh"],
            token);
        int reads = 0;
        Server server = await Server.ConnectAsync(
            new ServerConnectionOptions
            {
                TmuxBinaryPath = raw.TmuxBinaryPath,
                SocketPath = raw.SocketPath,
                ConfigurationFile = "/dev/null",
                Interceptor = (request, next, cancellationToken) =>
                {
                    // A read captures the screen, or samples the grid state
                    // around the capture.
                    if (request.Arguments.Any(argument => argument == "capture-pane"
                        || argument.Contains("#{history_size}", StringComparison.Ordinal)))
                    {
                        Interlocked.Increment(ref reads);
                    }

                    return next(cancellationToken);
                },
            },
            token);
        Pane pane = await server.GetPaneAsync(PaneId.Parse(created.StandardOutputText.Trim()), token);
        Interlocked.Exchange(ref reads, 0);

        PaneWaitResult result = await pane.SendTextAndWaitAsync(
            "printf '%s-done\\n' read", "read-done", Arrival, token);

        Assert.True(result.Found, $"The wait ended {result.Outcome}.");
        Assert.Equal(0, Volatile.Read(ref reads));
    }

    // Within a tenth of history-limit, a read finds its place by the row the
    // cursor was on. That row is a bare prompt the next command rewrites, and
    // the fresh prompt below it hashes the same.
    [UnixFact]
    public async Task A_wait_sees_new_output_once_history_nears_its_limit()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        RawTmuxResult created = await raw.ExecuteAsync(
            ["new-window", "-d", "-P", "-F", "#{pane_id}", "-t", raw.SessionName, "sh"],
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

        // 1,830 lines put history past 1,800 of tmux's default 2,000.
        await pane.SendTextAsync("seq 1 1830", cancellationToken: token);
        PaneWaitResult filled = await pane.WaitUntilAsync(
            rows => rows.Any(row => row == "1830"), Arrival, token);
        Assert.True(filled.Found, "seq never finished printing");

        PaneWaitResult result = await pane.SendTextAndWaitAsync(
            "printf '%s-done\\n' near", "near-done", Arrival, token);

        Assert.True(result.Found, $"The wait ended {result.Outcome}.");
    }

    [UnixFact]
    public async Task A_pane_tmux_no_longer_has_is_reported_as_not_found_by_every_read()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        RawTmuxResult created = await raw.ExecuteAsync(
            ["new-window", "-d", "-P", "-F", "#{pane_id}", "-t", raw.SessionName, "sleep 60"],
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
        await pane.KillAsync(cancellationToken: token);

        await Assert.ThrowsAsync<TmuxObjectNotFoundException>(() => pane.WaitForTextAsync("x", Arrival, token));
        await Assert.ThrowsAsync<TmuxObjectNotFoundException>(() => pane.WaitUntilAsync(_ => true, Arrival, token));
        await Assert.ThrowsAsync<TmuxObjectNotFoundException>(() => pane.ReadOutputSinceAsync(cancellationToken: token));
        await Assert.ThrowsAsync<TmuxObjectNotFoundException>(() => pane.RunAsync("true", Arrival, token));
    }

    // A character with combining marks stacked on it takes up to 32 bytes in
    // one cell, so a wide row of them is longer than a control client accepts
    // in a line; read through the client, it would end it. 2,450 cells of 31
    // bytes make a 75,950-byte row.
    [UnixFact]
    public async Task A_wide_row_of_combining_marks_leaves_the_held_client_running()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        string name = raw.SessionName + "-wide";
        RawTmuxResult created = await raw.ExecuteAsync(
            ["new-session", "-d", "-P", "-F", "#{pane_id}", "-s", name, "-x", "2500", "-y", "12", "sh"],
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

        async Task<string[]> ControlClientsAsync() =>
            [.. (await server.GetClientsAsync(token)).Where(client => client.IsControlClient).Select(client => client.Name)];

        await using (await pane.Session.HoldWaitClientAsync(token))
        {
            string[] held = await ControlClientsAsync();
            PaneWaitResult result = await pane.SendTextAndWaitAsync(
                "i=0; while [ $i -lt 2450 ]; do printf 'e\\314\\201\\314\\201\\314\\201\\314\\201\\314\\201\\314\\201\\314\\201\\314\\201\\314\\201\\314\\201\\314\\201\\314\\201\\314\\201\\314\\201\\314\\201'; i=$((i+1)); done; printf '\\n%s-done\\n' wide",
                "wide-done",
                TimeSpan.FromSeconds(20),
                token);

            Assert.True(result.Found, $"The wait ended {result.Outcome}.");
            Assert.Single(held);
            Assert.Equal(held, await ControlClientsAsync());
        }
    }

    // A narrower pane rewraps the rows above a position, which moves it down;
    // near history-limit, where a read finds its place by content, it must
    // still be found there.
    [UnixFact]
    public async Task A_position_is_found_again_after_the_pane_narrows()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        string name = raw.SessionName + "-narrow";
        RawTmuxResult created = await raw.ExecuteAsync(
            ["new-session", "-d", "-P", "-F", "#{pane_id}", "-s", name, "-x", "60", "-y", "10", "sh"],
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

        // 620 lines of 45 columns take three rows each at 20 columns: past
        // nine tenths of tmux's default 2,000 lines of history.
        await pane.SendTextAsync(
            "seq -f 'line-%04g-padding-padding-padding-padding' 1 620; printf 'held-%s' here; sleep 2; printf '\\n%s-done\\n' narrow",
            cancellationToken: token);
        Assert.True((await pane.WaitUntilAsync(rows => rows.Any(row => row.Contains("held-here", StringComparison.Ordinal)), Arrival, token)).Found);
        PaneOutputSince start = await pane.ReadOutputSinceAsync(cancellationToken: token);
        await raw.ExecuteAsync(["resize-window", "-t", name, "-x", "20"], token);
        Assert.True((await pane.WaitUntilAsync(rows => rows.Any(row => row.Contains("narrow-done", StringComparison.Ordinal)), Arrival, token)).Found);

        PaneOutputSince next = await pane.ReadOutputSinceAsync(start.Position, token);

        Assert.False(next.LinesMissed, string.Join(" | ", next.Lines));
        Assert.Contains("narrow-done", next.Lines);
    }

    [UnixFact]
    public async Task Reading_since_a_position_returns_only_what_is_new()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        RawTmuxResult created = await raw.ExecuteAsync(
            ["new-window", "-d", "-P", "-F", "#{pane_id}", "-t", raw.SessionName, "sh"],
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
        Assert.True((await pane.SendTextAndWaitAsync("printf '%s-done\\n' before", "before-done", Arrival, token)).Found);

        PaneOutputSince start = await pane.ReadOutputSinceAsync(cancellationToken: token);
        Assert.True((await pane.SendTextAndWaitAsync("printf '%s-done\\n' after", "after-done", Arrival, token)).Found);
        PaneOutputSince next = await pane.ReadOutputSinceAsync(start.Position, token);
        PaneOutputSince idle = await pane.ReadOutputSinceAsync(next.Position, token);

        Assert.Empty(start.Lines);
        Assert.Contains("after-done", next.Lines);
        Assert.DoesNotContain("before-done", next.Lines);
        Assert.False(next.LinesMissed);
        Assert.DoesNotContain("after-done", idle.Lines);

        Pane other = await pane.SplitAsync(cancellationToken: token);
        await Assert.ThrowsAsync<ArgumentException>(() => other.ReadOutputSinceAsync(next.Position, token));
    }

    [UnixFact]
    public async Task A_held_wait_client_serves_a_series_of_waits_and_then_detaches()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        RawTmuxResult created = await raw.ExecuteAsync(
            ["new-window", "-d", "-P", "-F", "#{pane_id}", "-t", raw.SessionName, "sh"],
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

        async Task<string[]> ControlClientsAsync() =>
            [.. (await server.GetClientsAsync(token)).Where(client => client.IsControlClient).Select(client => client.Name)];

        string[] during = [];
        await using (await pane.Session.HoldWaitClientAsync(token))
        {
            string[] held = await ControlClientsAsync();
            foreach (string mark in new[] { "first", "second" })
            {
                PaneWaitResult result = await pane.SendTextAndWaitAsync(
                    $"printf '%s-done\\n' {mark}", $"{mark}-done", Arrival, token);
                Assert.True(result.Found, $"The {mark} wait ended {result.Outcome}.");
                during = [.. during, .. await ControlClientsAsync()];
            }

            Assert.Single(held);
            Assert.All(during, name => Assert.Equal(held[0], name));
        }

        Assert.Empty(await ControlClientsAsync());
    }

    [UnixFact]
    public async Task Text_already_showing_answers_at_once_and_absent_text_times_out()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Pane pane = await NewPaneAsync(raw, "go", "printf 'ser''ver ready\\n'", token);
        await raw.ExecuteAsync(["wait-for", "-S", Channel(raw, "go")], token);
        await raw.ExecuteAsync(["wait-for", Channel(raw, "go-done")], token);

        PaneWaitResult present = await pane.WaitForTextAsync("server ready", Arrival, token);
        PaneWaitResult absent = await pane.WaitForTextAsync("never", TimeSpan.FromMilliseconds(200), token);

        Assert.Equal(PaneWaitOutcome.PresentAtEntry, present.Outcome);
        Assert.Equal(PaneWaitOutcome.TimedOut, absent.Outcome);
        Assert.True(absent.Elapsed >= TimeSpan.FromMilliseconds(200));
    }

    [UnixFact]
    public async Task Exits_closes_and_full_screen_programs_end_a_wait_before_its_deadline()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Pane exiting = await NewPaneAsync(raw, "exit", "exit 3", token);
        await raw.ExecuteAsync(["set-option", "-p", "-t", exiting.Id.ToString(), "remain-on-exit", "on"], token);
        Pane closing = await NewPaneAsync(raw, "close", "exit 0", token);
        Pane fullScreen = await NewPaneAsync(raw, "screen", "printf '\\033[?1049h'; exec sleep 60", token);
        PaneWaitRequest never = new() { Patterns = [new Regex("never")], Timeout = Arrival };

        PaneWaitResult exited = await AfterEntryAsync(raw, "exit", exiting, never, token);
        PaneWaitResult closed = await AfterEntryAsync(raw, "close", closing, never, token);
        PaneWaitResult repainting = await AfterEntryAsync(raw, "screen", fullScreen, never, token);

        Assert.Equal(PaneWaitOutcome.PaneExited, exited.Outcome);
        Assert.Equal(PaneWaitOutcome.PaneExited, closed.Outcome);
        Assert.Equal(PaneWaitOutcome.AlternateScreen, repainting.Outcome);
    }

    [UnixFact]
    public async Task Conditions_see_rows_a_full_screen_program_draws()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Pane pane = await NewPaneAsync(raw, "draw", "printf '\\033[?1049h\\033[5;3Hdra''wn'", token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<PaneWaitResult> waiting = pane.WaitUntilAsync(
            rows =>
            {
                entered.TrySetResult();
                return rows.Count > 4 && rows[4] == "  drawn";
            },
            Arrival,
            token);
        await entered.Task.WaitAsync(token);
        Assert.False(waiting.IsCompleted);
        await raw.ExecuteAsync(["wait-for", "-S", Channel(raw, "draw")], token);
        PaneWaitResult drawn = await waiting;

        Assert.Equal(PaneWaitOutcome.Matched, drawn.Outcome);
        Assert.Null(drawn.Pattern);
    }

    [UnixFact]
    public async Task A_condition_wait_fails_when_another_program_replaces_the_pane()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Pane pane = await NewPaneAsync(raw, "never", "true", token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<PaneWaitResult> waiting = pane.WaitUntilAsync(
            _ =>
            {
                entered.TrySetResult();
                return false;
            },
            Arrival,
            token);
        await entered.Task.WaitAsync(token);
        await raw.ExecuteAsync(
            ["respawn-pane", "-k", "-t", pane.Id.ToString(), "printf 'replaced\\n'; exec sleep 60"],
            token);

        await Assert.ThrowsAsync<TmuxPaneException>(() => waiting);
    }

    // Starts the wait, lets the pane run its gated command only once the wait
    // has read the screen it began with, and returns how the wait ended.
    private static async Task<PaneWaitResult> AfterEntryAsync(
        RawTmuxTestContext raw,
        string gate,
        Pane pane,
        PaneWaitRequest request,
        CancellationToken token)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<PaneWaitResult> waiting = pane.WaitForTextAsync(request, (_, _) => entered.TrySetResult(), token);
        await Task.WhenAny(entered.Task, waiting).WaitAsync(token);
        Assert.False(waiting.IsCompleted);
        await raw.ExecuteAsync(["wait-for", "-S", Channel(raw, gate)], token);
        return await waiting;
    }

    private static string Channel(RawTmuxTestContext raw, string gate) => $"{raw.SessionName}-{gate}";

    private static async Task<Pane> NewPaneAsync(
        RawTmuxTestContext raw,
        string gate,
        string command,
        CancellationToken token)
    {
        string tmux = $"'{raw.TmuxBinaryPath}'";
        RawTmuxResult created = await raw.ExecuteAsync(
            [
                "new-window", "-d", "-P", "-F", "#{pane_id}", "-t", raw.SessionName,
                $"{tmux} wait-for {Channel(raw, gate)}; {command}; {tmux} wait-for -S {Channel(raw, gate + "-done")}; exec sleep 60",
            ],
            token);
        Server server = await Server.ConnectAsync(
            new ServerConnectionOptions
            {
                TmuxBinaryPath = raw.TmuxBinaryPath,
                SocketPath = raw.SocketPath,
                ConfigurationFile = "/dev/null",
            },
            token);
        return await server.GetPaneAsync(PaneId.Parse(created.StandardOutputText.Trim()), token);
    }
}
