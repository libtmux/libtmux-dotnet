using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;

namespace LibTmux.IntegrationTests.Options;

[UnsupportedOSPlatform("windows")]
public sealed class TmuxOptionsTests
{
    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task Preserves_global_inherited_sparse_and_raw_values()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        Session session = await TestHierarchy.RequireFirstSessionAsync(server, token);
        Window window = await TestHierarchy.RequireFirstWindowAsync(session, token);

        // A value set globally is not in the session's own table, and shows up
        // there only when inherited values are asked for.
        await session.Options.SetAsync(new SetOptionRequest("status-keys", "vi") { Global = true }, token);
        Assert.Empty(await session.Options.GetAsync(new GetOptionRequest("status-keys") { Quiet = true }, token));
        IReadOnlyList<TmuxOption> inherited = await session.Options.GetAsync(
            new GetOptionRequest("status-keys") { IncludeInherited = true },
            token);
        Assert.Equal("vi", Assert.Single(inherited).Value.Raw);

        // A local value wins over the inherited one, and unsetting it gives the
        // inherited one back rather than leaving nothing.
        await session.Options.SetAsync(new SetOptionRequest("status-keys", "emacs"), token);
        Assert.Equal(
            "emacs",
            Assert.Single(await session.Options.GetAsync(new GetOptionRequest("status-keys"), token))
                .Value.Raw);
        await session.Options.UnsetAsync(new UnsetOptionRequest("status-keys"), token);
        Assert.Equal(
            "vi",
            Assert.Single(await session.Options.GetAsync(
                    new GetOptionRequest("status-keys") { IncludeInherited = true },
                    token))
                .Value.Raw);

        // A sparse array keeps the index tmux gave it, and nothing fills the gap.
        await server.Options.SetAsync(new SetOptionRequest("command-alias[40]", "zz=split-window"), token);
        IReadOnlyList<TmuxOption> aliases = await server.Options.GetAsync(
            new GetOptionRequest("command-alias"),
            token);
        Assert.Contains(aliases, alias => alias.Index == 40 && alias.Value.Raw == "zz=split-window");
        Assert.DoesNotContain(aliases, alias => alias.Index == 39);

        // Text tmux has to escape on the way out arrives as it went in.
        const string awkward = "a\"b\\c d\te";
        TmuxOptionValue stored = await window.Options.SetAsync(
            new SetOptionRequest("@awkward", awkward),
            token);
        Assert.Equal(awkward, stored.Raw);
        Assert.Equal(TmuxOptionState.Value, stored.State);
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task Global_inherited_and_unset_scopes_emit_exact_flags()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        Session session = await TestHierarchy.RequireFirstSessionAsync(server, token);
        Window first = await TestHierarchy.RequireFirstWindowAsync(session, token);
        Window second = await session.CreateWindowAsync(new NewWindowRequest { Name = "second" }, token);
        Pane pane = (await first.GetPanesAsync(token))[0];

        // Each accessor knows the table it stands for without being told.
        Assert.Equal(OptionScope.Server, server.Options.Scope);
        Assert.Equal(OptionScope.Session, session.Options.Scope);
        Assert.Equal(OptionScope.Window, first.Options.Scope);
        Assert.Equal(OptionScope.Pane, pane.Options.Scope);

        // A window option lands on the window it was set through, and its
        // neighbour is untouched.
        await first.Options.SetAsync(new SetOptionRequest("@marker", "first"), token);
        Assert.Equal(
            "first",
            Assert.Single(await first.Options.GetAsync(new GetOptionRequest("@marker"), token))
                .Value.Raw);
        Assert.Empty(await second.Options.GetAsync(new GetOptionRequest("@marker") { Quiet = true }, token));

        // A request may name a scope other than the accessor's own, which is
        // how a session reaches the global table it inherits from.
        await session.Options.SetAsync(
            new SetOptionRequest("@shared", "everywhere") { Global = true },
            token);
        Assert.Equal(
            "everywhere",
            Assert.Single(await session.Options.GetAsync(
                    new GetOptionRequest("@shared") { Global = true },
                    token))
                .Value.Raw);

        // Unsetting removes the entry rather than blanking it.
        await session.Options.UnsetAsync(
            new UnsetOptionRequest("@shared") { Global = true },
            token);
        Assert.Empty(await session.Options.GetAsync(
            new GetOptionRequest("@shared") { Global = true, Quiet = true },
            token));

        // Hooks live beside options and appear only when asked for.
        await raw.ExecuteAsync(["set-hook", "-g", "alert-bell", "display-message 'rang'"], token);
        IReadOnlyList<TmuxOption> withHooks = await session.Options.GetAllAsync(
            new GetOptionsRequest { Global = true, IncludeHooks = true },
            token);
        Assert.Contains(withHooks, option => option.Name == "alert-bell");
        Assert.DoesNotContain(
            await session.Options.GetAllAsync(new GetOptionsRequest { Global = true }, token),
            option => option.Name == "alert-bell");
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task Sparse_arrays_and_raw_values_round_trip()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        Session session = await TestHierarchy.RequireFirstSessionAsync(server, token);

        // Writing scattered indices leaves them scattered.
        await server.Options.SetAsync(new SetOptionRequest("command-alias[7]", "a7=list-keys"), token);
        await server.Options.SetAsync(new SetOptionRequest("command-alias[99]", "a99=list-keys"), token);
        IReadOnlyList<TmuxOption> aliases = await server.Options.GetAsync(
            new GetOptionRequest("command-alias"),
            token);
        int[] written = [.. aliases.Where(alias => alias.Index is 7 or 99).Select(alias => alias.Index!.Value)];
        Assert.Equal([7, 99], written);

        // Appending joins the existing value rather than replacing it.
        await session.Options.SetAsync(new SetOptionRequest("@joined", "left"), token);
        TmuxOptionValue joined = await session.Options.SetAsync(
            new SetOptionRequest("@joined", "-right") { Append = true },
            token);
        Assert.Equal("left-right", joined.Raw);

        // Refusing to overwrite is a refusal, not a quiet no-op: tmux reports
        // that the option is already set and leaves it alone.
        TmuxOptionException occupied = await Assert.ThrowsAsync<TmuxOptionException>(
            () => session.Options.SetAsync(
                new SetOptionRequest("@joined", "ignored") { PreventOverwrite = true },
                token));
        Assert.Contains("already set", occupied.Message, StringComparison.Ordinal);
        Assert.Equal(
            "left-right",
            Assert.Single(await session.Options.GetAsync(new GetOptionRequest("@joined"), token))
                .Value.Raw);

        // A format is expanded before it is stored, so the value that lands is
        // not the one that was sent.
        TmuxOptionValue expanded = await session.Options.SetAsync(
            new SetOptionRequest("@expanded", "#{session_name}") { ExpandFormat = true },
            token);
        Assert.Equal(session.Name, expanded.Raw);

        // Values tmux has to quote survive the round trip unchanged. A dollar
        // sign does too, but only because the reader undoes what tmux 3.4 adds
        // when it stores one, so it is proven on its own below.
        foreach (string awkward in
            (string[])["", " leading", "trailing ", "a#b", "a;b", "a'b", "a\"b", "a\\b", "a\tb"])
        {
            TmuxOptionValue stored = await session.Options.SetAsync(
                new SetOptionRequest("@round", awkward),
                token);
            Assert.Equal(awkward, stored.Raw);
        }

        // Whatever tmux chose to store, the escaped listing is read back as the
        // same text tmux reports when it is not escaping at all. A dollar sign
        // is left out: tmux 3.4 stores it carrying a backslash the caller never
        // sent, so the two readings disagree by that backslash there.
        foreach (string awkward in (string[])["a%b", "a{b"])
        {
            TmuxOptionValue stored = await session.Options.SetAsync(
                new SetOptionRequest("@round", awkward),
                token);
            RawTmuxResult plain = await raw.ExecuteAsync(["show-options", "-v", "@round"], token);
            Assert.Equal(Assert.Single(plain.StandardOutputLines), stored.Raw);
        }
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task Invalid_ambiguous_and_unknown_options_map_to_typed_failures()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        Session session = await TestHierarchy.RequireFirstSessionAsync(server, token);

        // Which of unknown, invalid, and ambiguous tmux says depends on its
        // version, so all of them are the same failure carrying tmux's words.
        TmuxOptionException unknown = await Assert.ThrowsAsync<TmuxOptionException>(
            () => session.Options.GetAsync(new GetOptionRequest("no-such-option"), token));
        Assert.Equal("no-such-option", unknown.OptionName);
        Assert.Contains("option", unknown.Message, StringComparison.Ordinal);

        TmuxOptionException ambiguous = await Assert.ThrowsAsync<TmuxOptionException>(
            () => session.Options.GetAsync(new GetOptionRequest("status-"), token));
        Assert.Equal("status-", ambiguous.OptionName);
        Assert.Contains("ambiguous", ambiguous.Message, StringComparison.Ordinal);

        await Assert.ThrowsAsync<TmuxOptionException>(
            () => session.Options.SetAsync(new SetOptionRequest("no-such-option", "x"), token));
        await Assert.ThrowsAsync<TmuxOptionException>(
            () => session.Options.UnsetAsync(new UnsetOptionRequest("no-such-option"), token));

        // Asking quietly turns a missing option into no rows instead.
        Assert.Empty(await session.Options.GetAsync(
            new GetOptionRequest("@never-set") { Quiet = true },
            token));

        // A name that is not a name never reaches tmux at all.
        Assert.Throws<ArgumentException>(() => new GetOptionRequest(" "));
        Assert.Throws<ArgumentException>(() => new SetOptionRequest(" ", "x"));
        Assert.Throws<ArgumentNullException>(() => new SetOptionRequest("@x", null!));
        Assert.Throws<ArgumentException>(() => new UnsetOptionRequest(" "));
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task Window_option_aliases_resolve_to_the_window_scope()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        Session session = await TestHierarchy.RequireFirstSessionAsync(server, token);
        Window window = await TestHierarchy.RequireFirstWindowAsync(session, token);

        // tmux once had set-window-option and show-window-options as separate
        // commands. They are set-option and show-options with the window flag,
        // which is what the window's own accessor carries.
        Assert.Equal(OptionScope.Window, window.Options.Scope);
        await window.Options.SetAsync(new SetOptionRequest("automatic-rename", "off"), token);

        TmuxOption option = Assert.Single(
            await window.Options.GetAsync(new GetOptionRequest("automatic-rename"), token));
        Assert.False(option.Value.Boolean);
        Assert.Equal(TmuxOptionState.Off, option.Value.State);

        // The same value is what the window flag shows through raw tmux, so the
        // accessor wrote the window table and not the session's.
        RawTmuxResult direct = await raw.ExecuteAsync(
            ["show-options", "-w", "-t", window.Id.ToString(), "automatic-rename"],
            token);
        Assert.Equal("automatic-rename off", Assert.Single(direct.StandardOutputLines));

        // A window's own table holds only what was set on it, so listing it
        // returns that one option; the rest of what the window behaves by is
        // inherited, and asking for that brings the whole table.
        TmuxOption only = Assert.Single(
            await window.Options.GetAllAsync(cancellationToken: token));
        Assert.Equal("automatic-rename", only.Name);
        IReadOnlyList<TmuxOption> inherited = await window.Options.GetAllAsync(
            new GetOptionsRequest { IncludeInherited = true },
            token);
        Assert.Contains(inherited, entry => entry.Name == "automatic-rename");
        Assert.True(inherited.Count > 1);
    }

    private static Task<Server> ConnectAsync(RawTmuxTestContext raw, CancellationToken token) =>
        Server.ConnectAsync(
            new ServerConnectionOptions
            {
                TmuxBinaryPath = raw.TmuxBinaryPath,
                SocketPath = raw.SocketPath,
                ConfigurationFile = "/dev/null",
            },
            token);
    [UnixFact]
    public async Task A_dollar_sign_survives_the_option_round_trip()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);

        // tmux 3.4 alone escapes a dollar sign a second time on top of its own
        // escaping, showing a$b back as "a\\$b" where 3.5 shows "a\$b". One
        // decode leaves the extra backslash, so a caller reading the value
        // could tell which tmux answered, and the value would be wrong.
        await server.Options.SetAsync(new SetOptionRequest("@dollar", "a$b"), token);
        TmuxOption plain = (await server.Options.GetAsync(
            new GetOptionRequest("@dollar"),
            token))[0];

        Assert.Equal("a$b", plain.Value.Raw);

        // A backslash the caller really did write survives as one, so undoing
        // the extra level does not eat a real escape.
        await server.Options.SetAsync(new SetOptionRequest("@escaped", @"a\$b"), token);
        TmuxOption escaped = (await server.Options.GetAsync(
            new GetOptionRequest("@escaped"),
            token))[0];

        Assert.Equal(@"a\$b", escaped.Value.Raw);
    }

    [UnixFact]
    public async Task A_dollar_sign_survives_the_round_trip_from_an_unmaterialized_owner()
    {
        // CreateOwnedAsync hands back a handle that never materializes, even
        // after a session exists through it, so Options must discover the
        // dollar-escaping answer fresh rather than freeze it from the
        // handle's own permanently null Version -- wrong on tmux 3.4.
        CancellationToken token = TestContext.Current.CancellationToken;
        await using OwnedServerScope owned = await Server.CreateOwnedAsync(
            IsolatedOptions(),
            token);
        await owned.Value.CreateSessionAsync(new NewSessionRequest { Name = "main" }, token);
        Assert.False(owned.Value.IsMaterialized);

        await owned.Value.Options.SetAsync(new SetOptionRequest("@dollar", "a$b"), token);
        TmuxOption plain = (await owned.Value.Options.GetAsync(
            new GetOptionRequest("@dollar"),
            token))[0];

        Assert.Equal("a$b", plain.Value.Raw);
    }

    private static ServerConnectionOptions IsolatedOptions() =>
        new()
        {
            TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux",
            SocketName = $"ltcs-options-{Guid.NewGuid():N}",
            ConfigurationFile = "/dev/null",
        };
}
