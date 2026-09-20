using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;

namespace LibTmux.IntegrationTests.ControlMode;

[UnsupportedOSPlatform("windows")]
public sealed class DeferredControlModeResultsTests
{
    [UnixFact]
    public Task Deferred_shell_output_keeps_concurrent_callers_aligned() =>
        VerifyDeferredResultsAsync(fail: false);

    [UnixFact]
    public Task Deferred_shell_failure_keeps_concurrent_callers_aligned() =>
        VerifyDeferredResultsAsync(fail: true);

    private static async Task VerifyDeferredResultsAsync(bool fail)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        CancellationToken token = timeout.Token;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await Server.ConnectAsync(new ServerConnectionOptions
        {
            TmuxBinaryPath = raw.TmuxBinaryPath,
            SocketPath = raw.SocketPath,
            ConfigurationFile = "/dev/null",
        }, token);
        await using IControlModeSession control = await server.EnterControlModeAsync(
            cancellationToken: token);
        TmuxVersion version = Assert.NotNull(server.Version);
        bool reportsDeferredShellResults = version < TmuxVersion.Parse("3.3")
            || version >= TmuxVersion.Parse("3.5");
        string channel = $"deferred-{Guid.NewGuid():N}";
        string tmux = $"'{raw.TmuxBinaryPath}' -S '{raw.SocketPath}'";
        string shell = $"{tmux} wait-for -S {channel}-started; "
            + $"{tmux} wait-for {channel}-release; "
            + "{ printf 'stdout-marker\\n'; printf 'stderr-marker\\n' >&2; } 2>&1; "
            + (fail ? "exit 7" : "exit 0");
        TmuxCommand command = TmuxCommand.Create("run-shell", shell);
        Task<IReadOnlyList<string>> result = control.SendAsync(command, token);
        try
        {
            Assert.Equal(0, (await raw.ExecuteAsync(
                ["wait-for", $"{channel}-started"], token)).ExitCode);
            Assert.False(result.IsCompleted);
            Task<IReadOnlyList<string>> following = control.SendAsync(
                TmuxCommand.Create("display-message", "-p", "following-marker"), token);
            Assert.Equal(0, (await raw.ExecuteAsync(
                ["wait-for", "-S", $"{channel}-release"], token)).ExitCode);
            if (fail)
            {
                if (reportsDeferredShellResults)
                {
                    ControlModeCommandException error = await Assert.ThrowsAsync<ControlModeCommandException>(
                        async () => await result);
                    Assert.Equal(command, error.Command);
                    Assert.Equal(["stdout-marker", "stderr-marker"], error.OutputLines);
                    Assert.Contains(error.ErrorLines, line => line.EndsWith("returned 7", StringComparison.Ordinal));
                }
                else
                {
                    // tmux 3.3a and 3.4 keep run-shell text in the pane and
                    // complete the control command without its child status.
                    Assert.Empty(await result);
                }
            }
            else if (reportsDeferredShellResults)
            {
                Assert.Equal(["stdout-marker", "stderr-marker"], await result);
            }
            else
            {
                Assert.Empty(await result);
            }

            Assert.Equal(["following-marker"], await following);
            Assert.True(control.IsRunning);
            Assert.Equal(["usable-marker"], await control.SendAsync(
                TmuxCommand.Create("display-message", "-p", "usable-marker"), token));
        }
        finally
        {
            await raw.ExecuteAsync(["wait-for", "-S", $"{channel}-release"], token);
        }
    }

    [UnixFact]
    public async Task Silent_shell_completion_waits_for_the_foreground_job()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        CancellationToken token = timeout.Token;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await Server.ConnectAsync(new ServerConnectionOptions
        {
            TmuxBinaryPath = raw.TmuxBinaryPath,
            SocketPath = raw.SocketPath,
            ConfigurationFile = "/dev/null",
        }, token);
        await using IControlModeSession control = await server.EnterControlModeAsync(
            cancellationToken: token);
        string channel = $"silent-{Guid.NewGuid():N}";
        string tmux = $"'{raw.TmuxBinaryPath}' -S '{raw.SocketPath}'";
        Task<IReadOnlyList<string>> result = control.SendAsync(TmuxCommand.Create(
            "run-shell", $"{tmux} wait-for -S {channel}-started; "
                + $"{tmux} wait-for {channel}-release; "
                + $"{tmux} set-option -g @silent-done yes"), token);
        try
        {
            Assert.Equal(0, (await raw.ExecuteAsync(
                ["wait-for", $"{channel}-started"], token)).ExitCode);
            Assert.False(result.IsCompleted);
        }
        finally
        {
            await raw.ExecuteAsync(["wait-for", "-S", $"{channel}-release"], token);
        }

        Assert.Empty(await result);
        Assert.Equal(["yes"], (await raw.ExecuteAsync(
            ["show-options", "-gv", "@silent-done"], token)).StandardOutputLines);
        await control.DisposeAsync();
        Assert.Empty((await raw.ExecuteAsync(["list-clients"], token)).StandardOutputLines);
    }
}
