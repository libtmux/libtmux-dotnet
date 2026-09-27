using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;

namespace LibTmux.IntegrationTests.ControlMode;

[UnsupportedOSPlatform("windows")]
public sealed class DeferredControlModeResultsTests
{
    [UnixFact]
    public async Task Foreground_shell_failure_exposes_the_shell_exit_status()
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

        TmuxCommandException failure = await Assert.ThrowsAsync<TmuxCommandException>(
            () => server.RunShellAsync(new RunShellRequest("exit 7"), token));
        Assert.Equal(7, failure.Result.ExitCode);
    }

    [UnixFact]
    public async Task Foreground_shell_text_uses_the_process_route_and_leaves_control_usable()
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

        foreach (string name in new[] { "run-shell", "run-s" })
        {
            _ = Assert.Throws<NotSupportedException>(() =>
            {
                _ = control.SendAsync(TmuxCommand.Create(
                    name, "printf '%s\\n' '%exit' '%begin 1 2 1' '%window-add @999'"), token);
            });
        }
        Assert.Empty(await control.SendAsync(TmuxCommand.Create("run-shell", "-b", "true"), token));

        const string shell = "printf '%s\\n' '%exit' '%begin 1 2 1' '%window-add @999'";
        RawTmuxResult native = await raw.ExecuteAsync(["run-shell", shell], token);
        Assert.Equal(0, native.ExitCode);

        IReadOnlyList<string>? output = await server.RunShellAsync(new RunShellRequest(shell), token);
        Assert.Equal(native.StandardOutputLines, output);
        Assert.Equal(["following"], await control.SendAsync(
            TmuxCommand.Create("display-message", "-p", "following"), token));
        Assert.True(control.IsRunning);
    }
}
