using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Internal;

namespace LibTmux.IntegrationTests.Parity;

[UnsupportedOSPlatform("windows")]
public sealed class Component15ParityTests
{
    public static TheoryData<string> OwnedRows =>
    [
        "libtmux.common:EnvironmentMixin",
        "libtmux.common:EnvironmentMixin.getenv",
        "libtmux.common:EnvironmentMixin.remove_environment",
        "libtmux.common:EnvironmentMixin.set_environment",
        "libtmux.common:EnvironmentMixin.show_environment",
        "libtmux.common:EnvironmentMixin.unset_environment",
        "libtmux.hooks:<module>",
        "libtmux.hooks:HookDict",
        "libtmux.hooks:HookValues",
        "libtmux.hooks:HooksMixin",
        "libtmux.hooks:HooksMixin.default_hook_scope",
        "libtmux.hooks:HooksMixin.hooks",
        "libtmux.hooks:HooksMixin.run_hook",
        "libtmux.hooks:HooksMixin.set_hook",
        "libtmux.hooks:HooksMixin.set_hooks",
        "libtmux.hooks:HooksMixin.show_hook",
        "libtmux.hooks:HooksMixin.show_hooks",
        "libtmux.hooks:HooksMixin.unset_hook",
        "libtmux.pane:Pane.default_hook_scope",
        "libtmux.server:Server.default_hook_scope",
        "libtmux.session:Session.default_hook_scope",
        "libtmux.window:Window.default_hook_scope",
    ];

    [Theory(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    [MemberData(nameof(OwnedRows))]
    public async Task Owned_parity_row_has_hook_or_environment_behavior(string pythonSymbolId)
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await Server.ConnectAsync(
            new ServerConnectionOptions
            {
                TmuxBinaryPath = raw.TmuxBinaryPath,
                SocketPath = raw.SocketPath,
                ConfigurationFile = "/dev/null",
            },
            token);
        Session session = await TestHierarchy.RequireFirstSessionAsync(server, token);
        Window window = await TestHierarchy.RequireFirstWindowAsync(session, token);
        Pane pane = await TestHierarchy.RequireFirstPaneAsync(window, token);

        bool proved = pythonSymbolId switch
        {
            "libtmux.hooks:<module>" or "libtmux.hooks:HooksMixin" =>
                await ProvesEveryScopeHasHooksAsync(server, session, window, pane, token),
            "libtmux.hooks:HooksMixin.default_hook_scope"
                or "libtmux.server:Server.default_hook_scope"
                or "libtmux.session:Session.default_hook_scope"
                or "libtmux.window:Window.default_hook_scope"
                or "libtmux.pane:Pane.default_hook_scope" =>
                ProvesDefaultScopes(server, session, window, pane),
            "libtmux.hooks:HooksMixin.hooks" or "libtmux.hooks:HookDict" =>
                await ProvesHookListingAsync(server, token),
            "libtmux.hooks:HookValues" => await ProvesHookIsAnArrayAsync(server, token),
            "libtmux.hooks:HooksMixin.set_hook" => await ProvesSetHookAsync(server, token),
            "libtmux.hooks:HooksMixin.set_hooks" => await ProvesSetHooksAsync(server, token),
            "libtmux.hooks:HooksMixin.show_hook" => await ProvesShowHookAsync(server, session, token),
            "libtmux.hooks:HooksMixin.show_hooks" => await ProvesShowHooksAsync(server, token),
            "libtmux.hooks:HooksMixin.unset_hook" => await ProvesUnsetHookAsync(server, token),
            "libtmux.hooks:HooksMixin.run_hook" => await ProvesRunHookAsync(server, token),
            "libtmux.common:EnvironmentMixin" =>
                await ProvesBothEnvironmentsAsync(server, session, token),
            "libtmux.common:EnvironmentMixin.set_environment" =>
                await ProvesSetEnvironmentAsync(server, session, token),
            "libtmux.common:EnvironmentMixin.getenv" =>
                await ProvesGetEnvironmentAsync(server, token),
            "libtmux.common:EnvironmentMixin.show_environment" =>
                await ProvesShowEnvironmentAsync(server, token),
            "libtmux.common:EnvironmentMixin.remove_environment" =>
                await ProvesRemoveEnvironmentAsync(server, token),
            "libtmux.common:EnvironmentMixin.unset_environment" =>
                await ProvesUnsetEnvironmentAsync(server, token),
            _ => false,
        };

        Assert.True(proved, $"Parity behavior was not proved for {pythonSymbolId}.");
    }

    private static async Task<bool> ProvesEveryScopeHasHooksAsync(
        Server server,
        Session session,
        Window window,
        Pane pane,
        CancellationToken token)
    {
        // Python mixes the hook methods into every object; here each accessor
        // owns its own table.
        (TmuxHooks Hooks, string Name)[] scopes =
        [
            // Each scope is exercised with a hook it owns: tmux keeps a window
            // hook set globally rather than placing it in the window's table.
            (server.Hooks, "alert-bell"),
            (session.Hooks, "alert-bell"),
            (window.Hooks, "window-renamed"),
            (pane.Hooks, "pane-focus-in"),
        ];

        foreach ((TmuxHooks hooks, string name) in scopes)
        {
            await hooks.SetAsync(new SetHookRequest(name, "display-message mixed"), token);
            Assert.NotNull(await hooks.GetAsync(new HookRequest(name), token));
            await hooks.UnsetAsync(new HookRequest(name), token);
            Assert.Null(await hooks.GetAsync(new HookRequest(name), token));
        }

        return true;
    }

    private static bool ProvesDefaultScopes(
        Server server,
        Session session,
        Window window,
        Pane pane)
    {
        Assert.Equal(OptionScope.Server, server.Hooks.Scope);
        Assert.Equal(OptionScope.Session, session.Hooks.Scope);
        Assert.Equal(OptionScope.Window, window.Hooks.Scope);
        Assert.Equal(OptionScope.Pane, pane.Hooks.Scope);

        // tmux has no server hook table of its own, so the server's hooks are
        // the global ones and carry the global flag rather than a server flag.
        Assert.Equal("-g", CommandFlagCatalog.GetHookScopeFlag(OptionScope.Server));
        return CommandFlagCatalog.GetHookScopeFlag(OptionScope.Session).Length == 0;
    }

    private static async Task<bool> ProvesHookListingAsync(Server server, CancellationToken token)
    {
        Assert.Empty(await server.Hooks.GetAllAsync(cancellationToken: token));
        await server.Hooks.SetAsync(new SetHookRequest("alert-bell", "display-message rang"), token);
        await server.Hooks.SetAsync(new SetHookRequest("alert-silence", "display-message quiet"), token);

        IReadOnlyList<TmuxHook> hooks = await server.Hooks.GetAllAsync(cancellationToken: token);
        Assert.Equal(2, hooks.Count);
        return hooks.Select(hook => hook.Name).Order(StringComparer.Ordinal)
            .SequenceEqual(["alert-bell", "alert-silence"], StringComparer.Ordinal);
    }

    private static async Task<bool> ProvesHookIsAnArrayAsync(Server server, CancellationToken token)
    {
        // Every hook is an array, so even one command carries an index.
        TmuxHook one = await server.Hooks.SetAsync(
            new SetHookRequest("alert-bell", "display-message first"),
            token);
        Assert.Equal(0, Assert.Single(one.Values).Index);

        TmuxHook two = await server.Hooks.SetAsync(
            new SetHookRequest("alert-bell", "display-message second", append: true),
            token);
        return two.Values.Select(entry => entry.Index).SequenceEqual([0, 1]);
    }

    private static async Task<bool> ProvesSetHookAsync(Server server, CancellationToken token)
    {
        TmuxHook hook = await server.Hooks.SetAsync(
            new SetHookRequest("alert-bell", "display-message rang"),
            token);
        Assert.Equal("alert-bell", hook.Name);
        return Assert.Single(hook.Values).Command == "display-message rang";
    }

    private static async Task<bool> ProvesSetHooksAsync(Server server, CancellationToken token)
    {
        await server.Hooks.SetAsync(new SetHookRequest("alert-bell", "display-message stale"), token);
        TmuxHook hook = await server.Hooks.SetAsync(
            new SetHooksRequest(
                "alert-bell",
                new Dictionary<int, string>
                {
                    [1] = "display-message second",
                    [4] = "display-message fifth",
                },
                clearExisting: true),
            token);

        // Clearing first means the entry that was at index zero is gone, and
        // the indices given are the indices kept.
        return hook.Values.Select(entry => entry.Index).SequenceEqual([1, 4]);
    }

    private static async Task<bool> ProvesShowHookAsync(
        Server server,
        Session session,
        CancellationToken token)
    {
        await server.Hooks.SetAsync(new SetHookRequest("alert-bell", "display-message rang"), token);
        Assert.NotNull(await server.Hooks.GetAsync(new HookRequest("alert-bell"), token));

        // A name nobody set is an empty answer, not a failure, and a session
        // does not see the server's.
        Assert.Null(await server.Hooks.GetAsync(new HookRequest("alert-silence"), token));
        return await session.Hooks.GetAsync(new HookRequest("alert-bell"), token) is null;
    }

    private static async Task<bool> ProvesShowHooksAsync(Server server, CancellationToken token)
    {
        await server.Hooks.SetAsync(new SetHookRequest("alert-bell", "display-message rang"), token);
        IReadOnlyList<TmuxHook> hooks = await server.Hooks.GetAllAsync(
            new ListHooksRequest { Global = true },
            token);
        return hooks.Any(hook => hook.Name == "alert-bell");
    }

    private static async Task<bool> ProvesUnsetHookAsync(Server server, CancellationToken token)
    {
        await server.Hooks.SetAsync(new SetHookRequest("alert-bell", "display-message rang"), token);
        await server.Hooks.UnsetAsync(new HookRequest("alert-bell"), token);
        return await server.Hooks.GetAsync(new HookRequest("alert-bell"), token) is null;
    }

    private static async Task<bool> ProvesRunHookAsync(Server server, CancellationToken token)
    {
        // Running a hook fires its commands now. The proof is the command's
        // effect, since running it leaves no trace of its own.
        await server.Hooks.SetAsync(
            new SetHookRequest("alert-bell", "set-option -g @hook-ran yes"),
            token);
        // The hook writes into the global session table, which is not the
        // server table the server's own accessor reads.
        GetOptionRequest written = new(
            "@hook-ran",
            OptionScope.Session,
            global: true,
            quiet: true);
        Assert.Empty(await server.Options.GetAsync(written, token));

        await server.Hooks.RunAsync(new HookRequest("alert-bell"), token);
        IReadOnlyList<TmuxOption> ran = await server.Options.GetAsync(written, token);
        return ran.Count == 1 && ran[0].Value.Raw == "yes";
    }

    private static async Task<bool> ProvesBothEnvironmentsAsync(
        Server server,
        Session session,
        CancellationToken token)
    {
        // Python mixes the environment methods into Server and Session. Here
        // each carries the table a new pane would inherit from it.
        await server.Environment.SetAsync("LIBTMUX_A", "server", cancellationToken: token);
        await session.Environment.SetAsync("LIBTMUX_B", "session", cancellationToken: token);
        Assert.Null(await server.Environment.GetAsync("LIBTMUX_B", token));
        return (await session.Environment.GetAsync("LIBTMUX_B", token))?.Value == "session";
    }

    private static async Task<bool> ProvesSetEnvironmentAsync(
        Server server,
        Session session,
        CancellationToken token)
    {
        TmuxEnvironmentEntry entry = await server.Environment.SetAsync(
            "LIBTMUX_SET",
            "value",
            cancellationToken: token);
        Assert.Equal("value", entry.Value);
        Assert.False(entry.IsRemoved);

        TmuxEnvironmentEntry expanded = await session.Environment.SetAsync(
            "LIBTMUX_FORMAT",
            "#{session_name}",
            expandFormats: true,
            cancellationToken: token);
        return expanded.Value == session.Name;
    }

    private static async Task<bool> ProvesGetEnvironmentAsync(
        Server server,
        CancellationToken token)
    {
        await server.Environment.SetAsync("LIBTMUX_GET", "here", cancellationToken: token);
        Assert.Equal("here", (await server.Environment.GetAsync("LIBTMUX_GET", token))?.Value);

        // Python raises for a name it does not hold. A name nobody set is an
        // ordinary absence, so it answers nothing instead.
        return await server.Environment.GetAsync("LIBTMUX_MISSING", token) is null;
    }

    private static async Task<bool> ProvesShowEnvironmentAsync(
        Server server,
        CancellationToken token)
    {
        await server.Environment.SetAsync("LIBTMUX_ONE", "1", cancellationToken: token);
        await server.Environment.SetAsync("LIBTMUX_TWO", "2", cancellationToken: token);
        IReadOnlyList<TmuxEnvironmentEntry> entries = await server.Environment.GetAllAsync(token);
        Assert.Contains(entries, entry => entry.Name == "LIBTMUX_ONE" && entry.Value == "1");
        return entries.Any(entry => entry.Name == "LIBTMUX_TWO" && entry.Value == "2");
    }

    private static async Task<bool> ProvesRemoveEnvironmentAsync(
        Server server,
        CancellationToken token)
    {
        await server.Environment.SetAsync("LIBTMUX_REMOVED", "here", cancellationToken: token);
        await server.Environment.RemoveAsync("LIBTMUX_REMOVED", token);

        // A removed variable is still an entry: it tells tmux to strip the name
        // from the panes it spawns, which absence would not.
        TmuxEnvironmentEntry? entry = await server.Environment.GetAsync("LIBTMUX_REMOVED", token);
        return entry is { IsRemoved: true, Value: null };
    }

    private static async Task<bool> ProvesUnsetEnvironmentAsync(
        Server server,
        CancellationToken token)
    {
        await server.Environment.SetAsync("LIBTMUX_UNSET", "here", cancellationToken: token);
        await server.Environment.UnsetAsync("LIBTMUX_UNSET", token);

        // Unsetting leaves nothing at all, which is what separates it from
        // removing.
        return await server.Environment.GetAsync("LIBTMUX_UNSET", token) is null;
    }
}
