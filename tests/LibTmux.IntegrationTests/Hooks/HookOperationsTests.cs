using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Internal;

namespace LibTmux.IntegrationTests.Hooks;

[UnsupportedOSPlatform("windows")]
public sealed class HookOperationsTests
{
    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task Server_and_session_hooks_round_trip_without_global_state()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        Session session = await TestHierarchy.RequireFirstSessionAsync(server, token);

        // A hook is an array even with one entry, so it reads back indexed.
        TmuxHook set = await server.Hooks.SetAsync(
            new SetHookRequest("alert-bell", "display-message 'one'"),
            token);
        Assert.Equal("alert-bell", set.Name);
        TmuxHookEntry only = Assert.Single(set.Values);
        Assert.Equal(0, only.Index);

        // tmux normalises a command once, on the way in. What it prints
        // afterwards is what it will print forever, so handing that text back
        // is a fixed point rather than a slow drift.
        TmuxHook again = await server.Hooks.SetAsync(
            new SetHookRequest("alert-bell", only.Command),
            token);
        Assert.Equal(only.Command, Assert.Single(again.Values).Command);

        // The server's hooks are the global ones; a session's are its own, and
        // setting one leaves the other alone.
        Assert.Null(await session.Hooks.GetAsync(new HookRequest("alert-bell"), token));
        await session.Hooks.SetAsync(
            new SetHookRequest("alert-bell", "display-message 'session'"),
            token);
        TmuxHook sessionHook = Assert.IsType<TmuxHook>(
            await session.Hooks.GetAsync(new HookRequest("alert-bell"), token));
        Assert.Contains("session", Assert.Single(sessionHook.Values).Command, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "session",
            Assert.Single(
                Assert.IsType<TmuxHook>(
                    await server.Hooks.GetAsync(new HookRequest("alert-bell"), token)).Values)
                .Command,
            StringComparison.Ordinal);

        // Unsetting removes the hook rather than emptying it.
        await session.Hooks.UnsetAsync(new HookRequest("alert-bell"), token);
        Assert.Null(await session.Hooks.GetAsync(new HookRequest("alert-bell"), token));
        Assert.NotNull(await server.Hooks.GetAsync(new HookRequest("alert-bell"), token));
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task Set_show_unset_and_run_flags_emit_exact_argv()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        Session session = await TestHierarchy.RequireFirstSessionAsync(server, token);
        Window window = await TestHierarchy.RequireFirstWindowAsync(session, token);
        Pane pane = await TestHierarchy.RequireFirstPaneAsync(window, token);

        Assert.Equal(OptionScope.Server, server.Hooks.Scope);
        Assert.Equal(OptionScope.Session, session.Hooks.Scope);
        Assert.Equal(OptionScope.Window, window.Hooks.Scope);
        Assert.Equal(OptionScope.Pane, pane.Hooks.Scope);

        // Appending adds an entry rather than replacing the one there.
        await server.Hooks.SetAsync(new SetHookRequest("alert-bell", "display-message 'a'"), token);
        TmuxHook appended = await server.Hooks.SetAsync(
            new SetHookRequest("alert-bell", "display-message 'b'", append: true),
            token);
        Assert.Equal([0, 1], appended.Values.Select(entry => entry.Index).ToArray());

        // Writing a whole ordering at once lands every index, and clearing
        // first means nothing survives from before.
        TmuxHook rewritten = await server.Hooks.SetAsync(
            new SetHooksRequest(
                "alert-bell",
                new Dictionary<int, string>
                {
                    [0] = "display-message 'first'",
                    [3] = "display-message 'fourth'",
                },
                clearExisting: true),
            token);
        Assert.Equal([0, 3], rewritten.Values.Select(entry => entry.Index).ToArray());

        // Running a hook fires its commands without waiting for the event.
        await server.Hooks.RunAsync(new HookRequest("alert-bell"), token);

        // A hook set through a window is not one the session holds.
        await window.Hooks.SetAsync(new SetHookRequest("pane-focus-in", "display-message 'w'"), token);
        Assert.NotNull(await window.Hooks.GetAsync(new HookRequest("pane-focus-in"), token));
        Assert.Null(await session.Hooks.GetAsync(new HookRequest("pane-focus-in"), token));

        // The same for a pane, which is a table below the window's.
        await pane.Hooks.SetAsync(new SetHookRequest("pane-focus-out", "display-message 'p'"), token);
        Assert.NotNull(await pane.Hooks.GetAsync(new HookRequest("pane-focus-out"), token));
        Assert.Null(await window.Hooks.GetAsync(new HookRequest("pane-focus-out"), token));

        // Listing answers what the scope holds and nothing from its neighbours.
        IReadOnlyList<TmuxHook> windowHooks = await window.Hooks.GetAllAsync(
            cancellationToken: token);
        Assert.Contains(windowHooks, hook => hook.Name == "pane-focus-in");
        Assert.DoesNotContain(windowHooks, hook => hook.Name == "pane-focus-out");

        // A hook name that is not a name never reaches tmux.
        Assert.Throws<ArgumentException>(() => new HookRequest(" "));
        Assert.Throws<ArgumentException>(() => new SetHookRequest(" ", "x"));
        Assert.Throws<ArgumentException>(
            () => new SetHooksRequest("alert-bell", new Dictionary<int, string>()));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SetHooksRequest("alert-bell", new Dictionary<int, string> { [-1] = "x" }));
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task HookScopePaneWindowSetVersionPolicy()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        Session session = await TestHierarchy.RequireFirstSessionAsync(server, token);
        Window window = await TestHierarchy.RequireFirstWindowAsync(session, token);
        Pane pane = await TestHierarchy.RequireFirstPaneAsync(window, token);

        // Every supported tmux carries the window and pane hook scopes, so
        // there is no older spelling to fall back to and nothing to warn about.
        Assert.True(
            TmuxCapabilities.IsSupported(
                server.Version!.Value,
                "hook_scope_pane_window_set"));
        Assert.Equal("-w", CommandFlagCatalog.GetHookScopeFlag(OptionScope.Window));
        Assert.Equal("-p", CommandFlagCatalog.GetHookScopeFlag(OptionScope.Pane));

        // tmux normalises a hook command as it stores it, dropping quotes it
        // does not need, so the markers are words that need none.
        await window.Hooks.SetAsync(
            new SetHookRequest("pane-focus-in", "display-message windowmark"),
            token);
        await pane.Hooks.SetAsync(
            new SetHookRequest("pane-focus-in", "display-message panemark"),
            token);

        // Each lands in its own table, which is what the flag decides.
        RawTmuxResult windowSide = await raw.ExecuteAsync(
            ["show-hooks", "-w", "-t", window.Id.ToString()],
            token);
        RawTmuxResult paneSide = await raw.ExecuteAsync(
            ["show-hooks", "-p", "-t", pane.Id.ToString()],
            token);
        Assert.Contains(
            windowSide.StandardOutputLines,
            line => line.Contains("windowmark", StringComparison.Ordinal));
        Assert.Contains(
            paneSide.StandardOutputLines,
            line => line.Contains("panemark", StringComparison.Ordinal));
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task HookScopePaneWindowShowVersionPolicy()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        Session session = await TestHierarchy.RequireFirstSessionAsync(server, token);
        Window window = await TestHierarchy.RequireFirstWindowAsync(session, token);
        Pane pane = await TestHierarchy.RequireFirstPaneAsync(window, token);

        Assert.True(
            TmuxCapabilities.IsSupported(
                server.Version!.Value,
                "hook_scope_pane_window_show"));

        // Reading a scope that holds nothing is an empty answer, not a failure,
        // on every supported version.
        Assert.Empty(await window.Hooks.GetAllAsync(cancellationToken: token));
        Assert.Empty(await pane.Hooks.GetAllAsync(cancellationToken: token));

        await raw.ExecuteAsync(
            ["set-hook", "-w", "-t", window.Id.ToString(), "pane-focus-in", "display-message windowmark"],
            token);
        TmuxHook read = Assert.Single(await window.Hooks.GetAllAsync(cancellationToken: token));
        Assert.Equal("pane-focus-in", read.Name);
        Assert.Empty(await pane.Hooks.GetAllAsync(cancellationToken: token));
    }

    private static Task<Server> ConnectAsync(RawTmuxTestContext raw, CancellationToken token) =>
        Server.ConnectAsync(
            new ServerConnectionOptions(
                tmuxBinaryPath: raw.TmuxBinaryPath,
                socketPath: raw.SocketPath,
                configurationFile: "/dev/null"),
            token);
}
