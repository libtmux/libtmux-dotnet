using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;

namespace LibTmux.IntegrationTests.Chaining;

/// <summary>Proves a chain refuses a target from a server that has since restarted.</summary>
/// <remarks>
/// tmux reuses IDs: a pane called <c>%0</c> on a restarted server is a different
/// pane from the one a handle was read from, so a stale handle succeeds against
/// the wrong object instead of failing.
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed class ChainGenerationTests
{
    [UnixFact]
    public async Task A_chained_entity_command_is_refused_after_the_server_restarts()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;

        Server first = await ConnectAsync(raw, token);
        Session session = (await first.GetSessionsAsync(token))[0];
        Window window = (await session.GetWindowsAsync(token))[0];
        Pane pane = (await window.GetPanesAsync(token))[0];

        // The server this pane was read from goes away, and a new one takes the
        // same socket and hands out the same IDs. The old server has to be gone
        // first: it unlinks the socket as it exits, which would take the
        // replacement's socket with it.
        await first.KillAsync(token);
        await raw.WaitForServerExitAsync(token);
        RawTmuxResult replacement = await raw.ExecuteAsync(
            ["new-session", "-d", "-s", "replacement"],
            token);
        Assert.True(
            replacement.ExitCode == 0,
            $"the replacement server did not start: {replacement.StandardErrorText}");

        // Starting a server and being able to talk to it are not the same
        // instant, and how far apart they are depends on the machine.
        Server second = await ConnectWhenReadyAsync(raw, token);

        // The chain runs on the new server but from handles read through the
        // old one -- the shape a stale-handle bug takes. Every command built
        // from an entity carries that entity's target as plain text, so each
        // has to be refused rather than only the one this started with.
        TmuxCommand[] stale =
        [
            new SendKeysRequest("echo stale").ToCommand(pane),
            new SelectPaneRequest().ToCommand(pane),
            new SelectLayoutRequest("tiled").ToCommand(window),
            new NewWindowRequest(name: "stale").ToCommand(session),
            new SetOptionRequest("@stale", "1").ToCommand(pane.Options),
            new SetHookRequest("after-new-window", "display-message x")
                .ToCommand(session.Hooks),
        ];

        foreach (TmuxCommand command in stale)
        {
            await Assert.ThrowsAsync<StaleServerGenerationException>(
                () => second.Chain().Then(command).ExecuteAsync(token));
        }
    }

    [UnixFact]
    public async Task A_chain_mixing_two_servers_is_refused_before_anything_runs()
    {
        await using RawTmuxTestContext first = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        await using RawTmuxTestContext second = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;

        Server one = await ConnectAsync(first, token);
        Server two = await ConnectAsync(second, token);
        Pane fromOne = (await (await (await one.GetSessionsAsync(token))[0]
            .GetWindowsAsync(token))[0].GetPanesAsync(token))[0];
        Pane fromTwo = (await (await (await two.GetSessionsAsync(token))[0]
            .GetWindowsAsync(token))[0].GetPanesAsync(token))[0];

        // At most one server can be the one the chain runs against, so mixing
        // them is refused before anything executes rather than partially run.
        TmuxChain chain = one.Chain()
            .Then(new SendKeysRequest("echo one").ToCommand(fromOne))
            .Then(new SendKeysRequest("echo two").ToCommand(fromTwo));

        InvalidOperationException failure =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => chain.ExecuteAsync(token));

        Assert.Contains("generation", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [UnixFact]
    public async Task A_chain_of_entity_commands_still_runs_on_the_server_it_came_from()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;

        Server server = await ConnectAsync(raw, token);
        Pane pane = (await (await (await server.GetSessionsAsync(token))[0]
            .GetWindowsAsync(token))[0].GetPanesAsync(token))[0];

        // The guard has to be invisible when nothing is stale, or it would just
        // be a way to break chaining.
        await server.Chain()
            .Then(new SendKeysRequest("echo fresh").ToCommand(pane))
            .ExecuteAsync(token);
    }

    private static async Task<Server> ConnectWhenReadyAsync(
        RawTmuxTestContext raw,
        CancellationToken token)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await ConnectAsync(raw, token);
            }
            catch (LibTmuxException) when (attempt < 100)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), token);
            }
        }
    }

    private static Task<Server> ConnectAsync(RawTmuxTestContext raw, CancellationToken token) =>
        Server.ConnectAsync(
            new ServerConnectionOptions(
                tmuxBinaryPath: raw.TmuxBinaryPath,
                socketPath: raw.SocketPath,
                configurationFile: "/dev/null"),
            token);
}
