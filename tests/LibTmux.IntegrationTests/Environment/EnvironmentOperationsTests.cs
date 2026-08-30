using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;

// A namespace segment named Environment would shadow System.Environment for
// every file in the assembly, so this sits beside its folder-mate instead.
namespace LibTmux.IntegrationTests;

[UnsupportedOSPlatform("windows")]
public sealed class EnvironmentOperationsTests
{
    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task Set_show_unset_and_remove_flags_emit_exact_argv()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        Session session = await TestHierarchy.RequireFirstSessionAsync(server, token);

        // A value goes in and comes back as it was, spaces and all.
        TmuxEnvironmentEntry stored = await server.Environment.SetAsync(
            "LIBTMUX_PLAIN",
            "a b  c",
            cancellationToken: token);
        Assert.Equal("LIBTMUX_PLAIN", stored.Name);
        Assert.Equal("a b  c", stored.Value);
        Assert.False(stored.IsRemoved);

        // A format is expanded before it lands, so what is stored is not what
        // was sent.
        TmuxEnvironmentEntry expanded = await session.Environment.SetAsync(
            "LIBTMUX_EXPANDED",
            "#{session_name}",
            expandFormats: true,
            cancellationToken: token);
        Assert.Equal(session.Name, expanded.Value);

        // Hidden means tmux keeps the value for the panes it spawns but will
        // not read it back, so there is nothing to report.
        TmuxEnvironmentEntry hidden = await server.Environment.SetAsync(
            "LIBTMUX_HIDDEN",
            "secret",
            hidden: true,
            cancellationToken: token);
        Assert.Equal("LIBTMUX_HIDDEN", hidden.Name);
        Assert.Null(await server.Environment.GetAsync("LIBTMUX_HIDDEN", token));
        Assert.DoesNotContain(
            await server.Environment.GetAllAsync(token),
            entry => entry.Name == "LIBTMUX_HIDDEN");

        // Removing is not unsetting: tmux remembers the removal so a new pane
        // does not inherit the variable from its own parent.
        await server.Environment.SetAsync("LIBTMUX_GONE", "here", cancellationToken: token);
        await server.Environment.RemoveAsync("LIBTMUX_GONE", token);
        TmuxEnvironmentEntry removed = Assert.IsType<TmuxEnvironmentEntry>(
            await server.Environment.GetAsync("LIBTMUX_GONE", token));
        Assert.True(removed.IsRemoved);
        Assert.Null(removed.Value);

        // Unsetting leaves nothing at all, not even the removal.
        await server.Environment.UnsetAsync("LIBTMUX_GONE", token);
        Assert.Null(await server.Environment.GetAsync("LIBTMUX_GONE", token));

        // A name nobody set is an ordinary empty answer rather than a failure.
        Assert.Null(await server.Environment.GetAsync("LIBTMUX_NEVER_SET", token));

        // The server environment and a session's are separate tables.
        await session.Environment.SetAsync("LIBTMUX_LOCAL", "session", cancellationToken: token);
        Assert.Null(await server.Environment.GetAsync("LIBTMUX_LOCAL", token));
        Assert.Equal(
            "session",
            (await session.Environment.GetAsync("LIBTMUX_LOCAL", token))!.Value);

        // A name that is not a name never reaches tmux.
        await Assert.ThrowsAsync<ArgumentException>(
            () => server.Environment.GetAsync(" ", token));
        await Assert.ThrowsAsync<ArgumentException>(
            () => server.Environment.SetAsync(" ", "x", cancellationToken: token));
        await Assert.ThrowsAsync<ArgumentException>(
            () => server.Environment.RemoveAsync(" ", token));
        await Assert.ThrowsAsync<ArgumentException>(
            () => server.Environment.UnsetAsync(" ", token));
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task A_session_environment_reaches_the_panes_it_spawns()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        Session session = await TestHierarchy.RequireFirstSessionAsync(server, token);

        // The environment is worth setting only because new panes inherit it,
        // so the proof is a pane that can read the value back.
        await session.Environment.SetAsync("LIBTMUX_SEEN", "inherited", cancellationToken: token);

        // The window runs the reader itself. Typing into a shell would race the
        // shell's own start, and a pane that is not yet reading swallows what
        // it is sent.
        Window window = await session.CreateWindowAsync(
            new NewWindowRequest(
                name: "spawned",
                command: "sh -c 'printf \"value=%s\\n\" \"$LIBTMUX_SEEN\"; sleep 30'"),
            token);
        Pane pane = await TestHierarchy.RequireFirstPaneAsync(window, token);

        DateTimeOffset deadline = DateTimeOffset.UtcNow + TestBudget.Settle;
        string text = string.Empty;
        while (DateTimeOffset.UtcNow < deadline)
        {
            text = string.Join('\n', await pane.CaptureAsync(cancellationToken: token));
            if (text.Contains("value=inherited", StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), token);
        }

        Assert.Fail($"The pane never reported the inherited value: {text}");
    }

    private static Task<Server> ConnectAsync(RawTmuxTestContext raw, CancellationToken token) =>
        Server.ConnectAsync(
            new ServerConnectionOptions(
                tmuxBinaryPath: raw.TmuxBinaryPath,
                socketPath: raw.SocketPath,
                configurationFile: "/dev/null"),
            token);
}
