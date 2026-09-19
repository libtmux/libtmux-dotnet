using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Internal;

namespace LibTmux.IntegrationTests.Parity;

[UnsupportedOSPlatform("windows")]
public sealed class Component14ParityTests
{
    public static TheoryData<string> OwnedRows =>
    [
        "libtmux.common:WindowOptionDict",
        "libtmux.options:<module>",
        "libtmux.options:CommandAliases",
        "libtmux.options:ConvertedValue",
        "libtmux.options:ConvertedValues",
        "libtmux.options:ExplodedComplexUntypedOptionsDict",
        "libtmux.options:ExplodedUntypedOptionsDict",
        "libtmux.options:OptionDict",
        "libtmux.options:OptionsMixin",
        "libtmux.options:OptionsMixin.default_option_scope",
        "libtmux.options:OptionsMixin.set_option",
        "libtmux.options:OptionsMixin.show_option",
        "libtmux.options:OptionsMixin.show_options",
        "libtmux.options:OptionsMixin.unset_option",
        "libtmux.options:TerminalOverride",
        "libtmux.options:TerminalOverrides",
        "libtmux.options:UntypedOptionsDict",
        "libtmux.options:convert_value",
        "libtmux.options:convert_values",
        "libtmux.options:explode_arrays",
        "libtmux.options:explode_complex",
        "libtmux.options:handle_option_error",
        "libtmux.options:parse_options_to_dict",
        "libtmux.pane:Pane.default_option_scope",
        "libtmux.server:Server.default_option_scope",
        "libtmux.session:Session.default_option_scope",
        "libtmux.window:Window.default_option_scope",
        "libtmux.window:Window.set_window_option",
        "libtmux.window:Window.show_window_option",
        "libtmux.window:Window.show_window_options",
    ];

    [Theory(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    [MemberData(nameof(OwnedRows))]
    public async Task Owned_parity_row_has_option_behavior(string pythonSymbolId)
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
            "libtmux.options:<module>" or "libtmux.options:OptionsMixin" =>
                await ProvesEveryScopeReadsAndWritesAsync(server, session, window, pane, token),
            "libtmux.options:OptionsMixin.default_option_scope"
                or "libtmux.server:Server.default_option_scope"
                or "libtmux.session:Session.default_option_scope"
                or "libtmux.window:Window.default_option_scope"
                or "libtmux.pane:Pane.default_option_scope" =>
                ProvesDefaultScopes(server, session, window, pane),
            "libtmux.options:OptionsMixin.set_option"
                or "libtmux.window:Window.set_window_option" =>
                await ProvesSetAsync(session, window, token),
            "libtmux.options:OptionsMixin.show_option"
                or "libtmux.window:Window.show_window_option" =>
                await ProvesShowOneAsync(session, window, token),
            "libtmux.options:OptionsMixin.show_options"
                or "libtmux.window:Window.show_window_options" =>
                await ProvesShowAllAsync(session, window, token),
            "libtmux.options:OptionsMixin.unset_option" => await ProvesUnsetAsync(session, token),
            "libtmux.options:handle_option_error" => await ProvesTypedFailureAsync(session, token),
            "libtmux.options:convert_value" or "libtmux.options:ConvertedValue" =>
                ProvesConvertOne(),
            "libtmux.options:convert_values" or "libtmux.options:ConvertedValues" =>
                ProvesConvertMany(),
            "libtmux.options:parse_options_to_dict" or "libtmux.options:UntypedOptionsDict"
                or "libtmux.options:OptionDict" =>
                ProvesRowParsing(),
            "libtmux.options:explode_arrays" or "libtmux.options:ExplodedUntypedOptionsDict"
                or "libtmux.common:WindowOptionDict" =>
                ProvesArrayExplosion(),
            "libtmux.options:explode_complex"
                or "libtmux.options:ExplodedComplexUntypedOptionsDict" =>
                ProvesComplexExplosion(),
            "libtmux.options:CommandAliases" => ProvesCommandAliases(),
            "libtmux.options:TerminalOverride" or "libtmux.options:TerminalOverrides" =>
                ProvesTerminalOverrides(),
            _ => false,
        };

        Assert.True(proved, $"Parity behavior was not proved for {pythonSymbolId}.");
    }

    private static async Task<bool> ProvesEveryScopeReadsAndWritesAsync(
        Server server,
        Session session,
        Window window,
        Pane pane,
        CancellationToken token)
    {
        // Python mixes the same four methods into every object. Here each
        // object carries an accessor that knows its own table.
        foreach (TmuxOptions options in (TmuxOptions[])
            [server.Options, session.Options, window.Options, pane.Options])
        {
            await options.SetAsync(new SetOptionRequest("@mixed", "in"), token);
            Assert.Equal(
                "in",
                Assert.Single(await options.GetAsync(new GetOptionRequest("@mixed"), token))
                    .Value.Raw);
            await options.UnsetAsync(new UnsetOptionRequest("@mixed"), token);
            Assert.Empty(await options.GetAsync(new GetOptionRequest("@mixed") { Quiet = true }, token));
        }

        return true;
    }

    private static bool ProvesDefaultScopes(
        Server server,
        Session session,
        Window window,
        Pane pane)
    {
        // Python asks each class for the scope its options default to. Here the
        // accessor is already the scope, so there is nothing to look up.
        Assert.Equal(OptionScope.Server, server.Options.Scope);
        Assert.Equal(OptionScope.Session, session.Options.Scope);
        Assert.Equal(OptionScope.Window, window.Options.Scope);
        Assert.Equal(OptionScope.Pane, pane.Options.Scope);
        return CommandFlagCatalog.DefaultOptionScope is null;
    }

    private static async Task<bool> ProvesSetAsync(
        Session session,
        Window window,
        CancellationToken token)
    {
        TmuxOptionValue stored = await window.Options.SetAsync(
            new SetOptionRequest("automatic-rename", "off"),
            token);
        Assert.Equal(TmuxOptionState.Off, stored.State);

        Assert.Empty(await session.Options.GetAsync(
            new GetOptionRequest("@window-only") { Quiet = true },
            token));
        return stored.Boolean == false;
    }

    private static async Task<bool> ProvesShowOneAsync(
        Session session,
        Window window,
        CancellationToken token)
    {
        await window.Options.SetAsync(new SetOptionRequest("@one", "value"), token);
        TmuxOption option = Assert.Single(
            await window.Options.GetAsync(new GetOptionRequest("@one"), token));
        Assert.Equal("@one", option.Name);

        // Asking the session for the same name finds nothing, which is what
        // makes the window flag load-bearing rather than decorative.
        Assert.Empty(await session.Options.GetAsync(new GetOptionRequest("@one") { Quiet = true }, token));
        return option.Value.Raw == "value";
    }

    private static async Task<bool> ProvesShowAllAsync(
        Session session,
        Window window,
        CancellationToken token)
    {
        await window.Options.SetAsync(new SetOptionRequest("@listed", "yes"), token);
        IReadOnlyList<TmuxOption> windowOptions = await window.Options.GetAllAsync(
            cancellationToken: token);
        Assert.Contains(windowOptions, option => option.Name == "@listed");

        IReadOnlyList<TmuxOption> sessionOptions = await session.Options.GetAllAsync(
            cancellationToken: token);
        return !sessionOptions.Any(option => option.Name == "@listed");
    }

    private static async Task<bool> ProvesUnsetAsync(Session session, CancellationToken token)
    {
        await session.Options.SetAsync(new SetOptionRequest("@gone", "here"), token);
        await session.Options.UnsetAsync(new UnsetOptionRequest("@gone"), token);
        IReadOnlyList<TmuxOption> after = await session.Options.GetAsync(
            new GetOptionRequest("@gone") { Quiet = true },
            token);

        // The option is missing, not present and empty.
        return after.Count == 0;
    }

    private static async Task<bool> ProvesTypedFailureAsync(
        Session session,
        CancellationToken token)
    {
        // Python raises one of four exception types depending on which words
        // tmux chose. The words are the version's, not the caller's, so they
        // are carried rather than classified.
        TmuxOptionException failure = await Assert.ThrowsAsync<TmuxOptionException>(
            () => session.Options.GetAsync(new GetOptionRequest("no-such-option"), token));
        Assert.Equal("no-such-option", failure.OptionName);
        Assert.IsAssignableFrom<LibTmuxException>(failure);

        TmuxOptionException ambiguous = await Assert.ThrowsAsync<TmuxOptionException>(
            () => session.Options.GetAsync(new GetOptionRequest("status-"), token));
        return ambiguous.Message.Contains("ambiguous", StringComparison.Ordinal);
    }

    private static bool ProvesConvertOne()
    {
        Assert.True(OptionParser.ParseValue("on").Boolean);
        Assert.False(OptionParser.ParseValue("off").Boolean);
        Assert.Equal(50L, OptionParser.ParseValue("50").Integer);
        Assert.Null(OptionParser.ParseValue("%50").Integer);

        // Python returns None for a missing value and cannot tell it from an
        // option set to nothing. The state says which it was.
        Assert.Equal(TmuxOptionState.Absent, OptionParser.ParseValue(null).State);
        return OptionParser.ParseValue(string.Empty).State == TmuxOptionState.Value;
    }

    private static bool ProvesConvertMany()
    {
        IReadOnlyList<TmuxOptionValue> values = OptionParser.ParseValues(["on", "off", "1", "x"]);

        Assert.Equal(4, values.Count);
        Assert.True(values[0].Boolean);
        Assert.False(values[1].Boolean);
        Assert.Equal(1L, values[2].Integer);
        return values[3].Integer is null && values[3].Raw == "x";
    }

    private static bool ProvesRowParsing()
    {
        IReadOnlyList<TmuxOption> options = OptionParser.ParseRows([
            "status-keys vi",
            "message-limit 50",
            "user-keys",
            "command-alias[0] \"choose-session=choose-tree -s\"",
        ]);

        Assert.Equal(4, options.Count);
        Assert.Equal("vi", options[0].Value.Raw);
        Assert.Equal(50L, options[1].Value.Integer);
        Assert.Equal(TmuxOptionState.Absent, options[2].Value.State);
        return options[3].Value.Raw == "choose-session=choose-tree -s";
    }

    private static bool ProvesArrayExplosion()
    {
        IReadOnlyList<TmuxOption> options = OptionParser.ParseRows([
            "terminal-features[0] xterm*:clipboard",
            "terminal-features[1] screen*:title",
        ]);

        Assert.Equal([0, 1], options.Select(option => option.Index).ToArray());
        Assert.All(options, option => Assert.Equal("terminal-features", option.Name));

        // Hooks are arrays even with one entry, which is what reading them as
        // sparse rows says.
        IReadOnlyList<TmuxOption> sparse = OptionParser.ParseSparse(["alert-bell refresh-client"]);
        return sparse[0].Index == 0;
    }

    private static bool ProvesComplexExplosion()
    {
        IReadOnlyDictionary<string, object?> complex = OptionParser.ParseComplex(
            OptionParser.ParseRows([
                "terminal-features[0] xterm*:clipboard:ccolour",
                "status-keys vi",
            ]));

        IReadOnlyDictionary<string, IReadOnlyList<string>> features =
            Assert.IsAssignableFrom<IReadOnlyDictionary<string, IReadOnlyList<string>>>(
                complex["terminal-features"]);
        Assert.Equal(["clipboard", "ccolour"], features["xterm*"]);

        // Anything without a structure inside it is left as it was.
        return Assert.IsType<TmuxOptionValue>(complex["status-keys"]).Raw == "vi";
    }

    private static bool ProvesCommandAliases()
    {
        IReadOnlyDictionary<string, object?> complex = OptionParser.ParseComplex(
            OptionParser.ParseRows([
                "command-alias[0] split-pane=split-window",
                "command-alias[2] \"server-info=show-messages -JT\"",
            ]));

        IReadOnlyDictionary<string, string> aliases =
            Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(complex["command-alias"]);
        Assert.Equal("split-window", aliases["split-pane"]);
        return aliases["server-info"] == "show-messages -JT";
    }

    private static bool ProvesTerminalOverrides()
    {
        IReadOnlyDictionary<string, object?> complex = OptionParser.ParseComplex(
            OptionParser.ParseRows(["terminal-overrides[0] *256col*:colors=256:XT"]));

        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> overrides =
            Assert.IsAssignableFrom<
                IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>>>(
                complex["terminal-overrides"]);
        IReadOnlyDictionary<string, object?> capabilities = overrides["*256col*"];

        Assert.Equal(256L, capabilities["colors"]);
        return capabilities.ContainsKey("XT") && capabilities["XT"] is null;
    }
}
