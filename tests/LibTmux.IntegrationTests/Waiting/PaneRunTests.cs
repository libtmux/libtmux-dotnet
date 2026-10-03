using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;

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
