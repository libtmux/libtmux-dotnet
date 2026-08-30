using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Internal;
using LibTmux.Testing;

namespace LibTmux.IntegrationTests.Chaining;

[UnsupportedOSPlatform("windows")]
public sealed class TmuxChainTests
{
    [UnixFact]
    public async Task A_chain_runs_every_command_in_one_invocation()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);

        await server.Chain()
            .Then("new-window", "-d", "-t", raw.SessionName, "-n", "first")
            .Then("new-window", "-d", "-t", raw.SessionName, "-n", "second")
            .Then("new-window", "-d", "-t", raw.SessionName, "-n", "third")
            .ExecuteAsync(token);

        string[] names =
        [
            .. (await server.GetWindowsAsync(token)).Select(static window => window.Name),
        ];

        Assert.Contains("first", names);
        Assert.Contains("second", names);
        Assert.Contains("third", names);
    }

    [UnixFact]
    public async Task Building_a_chain_reaches_nothing_until_it_is_executed()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);

        TmuxChain built = server.Chain()
            .Then("new-window", "-d", "-t", raw.SessionName, "-n", "unrun");

        // A chain is a description, so holding one changes nothing on the
        // server. Anything else would make building it a side effect.
        Assert.Single(built.Commands);
        Assert.Single(await server.GetWindowsAsync(token));

        await built.ExecuteAsync(token);
        Assert.Equal(2, (await server.GetWindowsAsync(token)).Count);
    }

    [UnixFact]
    public async Task A_chain_is_immutable_so_a_shared_prefix_stays_what_it_was()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);

        TmuxChain prefix = server.Chain()
            .Then("new-window", "-d", "-t", raw.SessionName, "-n", "kept");
        TmuxChain longer = prefix.Then(
            "new-window", "-d", "-t", raw.SessionName, "-n", "extra");

        Assert.Single(prefix.Commands);
        Assert.Equal(2, longer.Commands.Count);

        await prefix.ExecuteAsync(token);
        string[] names =
        [
            .. (await server.GetWindowsAsync(token)).Select(static window => window.Name),
        ];

        Assert.Contains("kept", names);
        Assert.DoesNotContain("extra", names);
    }

    [UnixFact]
    public async Task A_semicolon_argument_stays_data_rather_than_a_separator()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);

        // tmux separates grouped commands with a semicolon, so a semicolon a
        // caller passes as a value has to survive as one. Reading it back as a
        // window name is the only way to tell the two apart.
        TmuxCommandResult result = await server.Chain()
            .Then("new-window", "-d", "-t", raw.SessionName, "-n", "a;b")
            .Then("display-message", "-p", "chained")
            .ExecuteAsync(token);

        Assert.Contains("chained", result.StandardOutputLines);
        Assert.Contains(
            await server.GetWindowsAsync(token),
            window => window.Name == "a;b");
    }

    [UnixFact]
    public async Task An_empty_chain_refuses_rather_than_running_nothing()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.Chain().ExecuteAsync(token));
    }

    [UnixFact]
    public async Task A_failing_command_surfaces_as_a_failed_chain()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);

        await Assert.ThrowsAsync<TmuxCommandException>(
            () => server.Chain()
                .Then("new-window", "-d", "-t", raw.SessionName, "-n", "before")
                .Then("no-such-tmux-command")
                .ExecuteAsync(token));
    }

    [UnixFact]
    public async Task A_typed_request_chains_the_same_way_it_runs_alone()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        Session session = await TestHierarchy.RequireFirstSessionAsync(server, token);

        NewWindowRequest request = new(name: "typed", startDirectory: "/tmp");

        // The one-shot and chained paths build arguments from the same code,
        // so this compares the built command against what the wrapper sends.
        TmuxCommand command = request.ToCommand(session);

        // The session identifier travels into the chain as plain text, so the
        // command carries the generation that identifier belongs to.
        Assert.Equal(session.Generation, command.RequiredGeneration);
        Assert.Equal("new-window", command.Name);
        Assert.Contains("typed", command.Arguments);
        Assert.Contains("/tmp", command.Arguments);

        await server.Chain().Then(command).ExecuteAsync(token);

        Window chained = Assert.Single(
            await server.GetWindowsAsync(token),
            window => window.Name == "typed");
        Window direct = await session.CreateWindowAsync(
            new NewWindowRequest(name: "direct", startDirectory: "/tmp"),
            token);

        Assert.Equal("direct", direct.Name);
        Assert.NotEqual(direct.Id, chained.Id);
    }

    [UnixFact]
    public async Task Keys_chain_through_the_pane_that_knows_the_version()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        Pane pane = (await server.GetPanesAsync(token))[0];

        // Which send-keys flags tmux accepts depends on the server version, so
        // the command is built from the pane rather than from a target string:
        // a bare string could not have told the builder which tmux it is for.
        TmuxCommand keys = new SendKeysRequest("echo chained-keys", enter: false)
            .ToCommand(pane);

        Assert.Equal("send-keys", keys.Name);
        Assert.Contains("echo chained-keys", keys.Arguments);

        await server.Chain()
            .Then(keys)
            .Then("send-keys", "-t", pane.Id.ToString(), "Enter")
            .ExecuteAsync(token);

        string seen = await TmuxWait.UntilAsync(
            async inner => string.Join('\n', await pane.CaptureAsync(cancellationToken: inner)),
            text => text.Contains("chained-keys", StringComparison.Ordinal),
            TestBudget.Settle,
            TimeSpan.FromMilliseconds(20),
            token);

        Assert.Contains("chained-keys", seen, StringComparison.Ordinal);
    }

    [UnixFact]
    public async Task A_session_request_chains_and_reports_what_it_made()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);

        // Creating a session names no target, so this request needs nothing
        // but itself. It keeps the identifier-printing flags the one-shot path
        // uses, which is what makes the chain able to say what it made.
        TmuxCommand command = new NewSessionRequest(name: "chained-session")
            .ToCommand();

        Assert.Equal("new-session", command.Name);
        Assert.Contains("chained-session", command.Arguments);

        TmuxCommandResult result = await server.Chain().Then(command).ExecuteAsync(token);

        Assert.Contains(
            await server.GetSessionsAsync(token),
            session => session.Name == "chained-session");
        Assert.Single(result.StandardOutputLines);
        Assert.StartsWith("$", result.StandardOutputLines[0], StringComparison.Ordinal);
    }

    [UnixFact]
    public async Task A_request_runs_on_its_own_the_way_it_runs_in_a_chain()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        Session session = await TestHierarchy.RequireFirstSessionAsync(server, token);

        // Construct, configure, execute: a single request needs no chain to
        // run, and running it alone does what adding it to one would have.
        await new NewWindowRequest(name: "executed").ExecuteAsync(session, token);

        Assert.Contains(
            await server.GetWindowsAsync(token),
            window => window.Name == "executed");

        await new NewSessionRequest(name: "executed-session").ExecuteAsync(server, token);

        Assert.Contains(
            await server.GetSessionsAsync(token),
            other => other.Name == "executed-session");

        Pane pane = (await server.GetPanesAsync(token))[0];
        await new SendKeysRequest("echo executed-keys", enter: true).ExecuteAsync(pane, token);

        string seen = await TmuxWait.UntilAsync(
            async inner => string.Join('\n', await pane.CaptureAsync(cancellationToken: inner)),
            text => text.Contains("executed-keys", StringComparison.Ordinal),
            TestBudget.Settle,
            TimeSpan.FromMilliseconds(20),
            token);

        Assert.Contains("executed-keys", seen, StringComparison.Ordinal);
    }

    [UnixFact]
    public async Task Key_bindings_chain_and_run_alone()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);

        // Binding needs no target and no version check, so its command is the
        // request and nothing else.
        await server.Chain()
            .Then(new BindKeyRequest("F1", ["display-message", "chained-bind"]).ToCommand())
            .Then(new BindKeyRequest("F2", ["display-message", "second-bind"]).ToCommand())
            .ExecuteAsync(token);

        IReadOnlyList<string> bound = await server.GetKeysAsync(cancellationToken: token);
        Assert.Contains(bound, line => line.Contains("F1", StringComparison.Ordinal));
        Assert.Contains(bound, line => line.Contains("F2", StringComparison.Ordinal));

        await new UnbindKeyRequest("F1").ExecuteAsync(server, token);

        IReadOnlyList<string> after = await server.GetKeysAsync(cancellationToken: token);
        Assert.DoesNotContain(after, line => line.Contains("F1", StringComparison.Ordinal));
        Assert.Contains(after, line => line.Contains("F2", StringComparison.Ordinal));
    }

    [UnixFact]
    public async Task A_chained_layout_is_still_checked_before_it_is_sent()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        Window window = (await server.GetWindowsAsync(token))[0];

        await new SelectLayoutRequest(layout: "even-horizontal").ExecuteAsync(window, token);

        // An unrecognised layout name takes the whole tmux server down on
        // 3.3a, so the name is checked before anything is sent. Batching must
        // not be a way around that check.
        await Assert.ThrowsAsync<TmuxWindowException>(
            () => new SelectLayoutRequest(layout: "not-a-layout").ExecuteAsync(window, token));

        Assert.True(await server.IsAliveAsync(token));
    }

    [UnixFact]
    public async Task Conditional_and_channel_requests_chain()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);

        // Neither command needs a target or a version check, so both build
        // from the request alone.
        TmuxCommand conditional = new IfShellRequest(
            "true",
            ["display-message", "chained-if"]).ToCommand();

        Assert.Equal("if-shell", conditional.Name);
        Assert.Contains("true", conditional.Arguments);
        Assert.Contains("display-message chained-if", conditional.Arguments);

        TmuxCommand signal = new WaitForRequest("chained-channel", TmuxWaitMode.Signal)
            .ToCommand();

        Assert.Equal("wait-for", signal.Name);
        Assert.Contains("chained-channel", signal.Arguments);

        // Both are accepted in one grouped run. What if-shell then does
        // happens on tmux's own schedule, so this asserts the dispatch rather
        // than racing the scheduler.
        await server.Chain().Then(conditional).Then(signal).ExecuteAsync(token);

        Assert.True(await server.IsAliveAsync(token));
    }

    [UnixFact]
    public async Task Pane_selection_chains_through_its_pane()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        await raw.ExecuteAsync(
            ["split-window", "-d", "-t", $"{raw.SessionName}:0.0"],
            token);

        IReadOnlyList<Pane> panes = await server.GetPanesAsync(token);
        Assert.Equal(2, panes.Count);

        // Selecting names the pane it moves from, so the command is built off
        // that pane rather than from a bare string.
        await new SelectPaneRequest(direction: PaneSelectDirection.Down)
            .ExecuteAsync(panes[0], token);

        // tmux itself reports which pane is active, which is the only thing
        // that says the selection landed.
        RawTmuxResult active = await raw.ExecuteAsync(
            ["display-message", "-p", "-t", raw.SessionName, "#{pane_id}"],
            token);

        Assert.Equal(panes[1].Id.ToString(), active.StandardOutputLines[0]);
    }

    [UnixFact]
    public async Task Pane_resize_chains_through_its_pane()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        await raw.ExecuteAsync(
            ["split-window", "-d", "-t", $"{raw.SessionName}:0.0"],
            token);

        Pane first = (await server.GetPanesAsync(token))[0];
        int before = first.Height;

        await new ResizePaneRequest(height: "5").ExecuteAsync(first, token);

        Pane after = await first.RefreshAsync(token);
        Assert.NotEqual(before, after.Height);
        Assert.Equal(5, after.Height);
    }

    [UnixFact]
    public async Task A_window_search_chains_through_its_pane()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        Pane pane = (await server.GetPanesAsync(token))[0];

        TmuxCommand command = new FindWindowRequest(
            "needle",
            matchName: true,
            ignoreCase: true).ToCommand(pane);

        Assert.Equal("find-window", command.Name);
        Assert.Contains("-N", command.Arguments);
        Assert.Contains("-i", command.Arguments);
        Assert.Contains("needle", command.Arguments);

        // A search opens a chooser, which needs a client to open in. With none
        // attached there is nothing to observe afterwards, so this asserts the
        // command tmux accepted rather than an effect it cannot have had.
        await new FindWindowRequest("needle", matchName: true).ExecuteAsync(pane, token);

        Assert.True(await server.IsAliveAsync(token));
    }

    [UnixFact]
    public async Task Panes_swap_through_the_pane_being_moved()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        await raw.ExecuteAsync(
            ["split-window", "-d", "-t", $"{raw.SessionName}:0.0"],
            token);

        IReadOnlyList<Pane> before = await server.GetPanesAsync(token);
        Assert.Equal(2, before.Count);

        // Swapping exchanges the panes' positions, so the identifier sitting
        // at index zero afterwards is the one that used to be at index one.
        await new SwapPaneRequest(target: before[1].Id.ToString(), detach: true)
            .ExecuteAsync(before[0], token);

        IReadOnlyList<Pane> after = await server.GetPanesAsync(token);
        Assert.Equal(before[1].Id, after[0].Id);
        Assert.Equal(before[0].Id, after[1].Id);
    }

    [UnixFact]
    public async Task Pane_piping_chains_and_writes_what_the_pane_produced()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        Pane pane = (await server.GetPanesAsync(token))[0];
        string sink = Path.Combine(Path.GetTempPath(), $"ltpipe-{Guid.NewGuid():N}"[..20]);

        try
        {
            // Piping's effect is only observable in the sink; exit status says
            // nothing about whether the pane's output was actually routed.
            await new PipePaneRequest(command: $"cat >> {sink}", outputOnly: true)
                .ExecuteAsync(pane, token);
            await pane.SendTextAsync("echo piped-through", cancellationToken: token);
            await pane.EnterAsync(token);

            string written = await TmuxWait.UntilAsync(
                inner => Task.FromResult(File.Exists(sink) ? File.ReadAllText(sink) : string.Empty),
                text => text.Contains("piped-through", StringComparison.Ordinal),
                TimeSpan.FromSeconds(15),
                TimeSpan.FromMilliseconds(20),
                token);

            Assert.Contains("piped-through", written, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(sink);
        }
    }

    [UnixFact]
    public async Task A_capture_chains_and_drops_the_flags_this_tmux_lacks()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        Pane pane = (await server.GetPanesAsync(token))[0];

        await pane.SendTextAsync("echo captured-by-chain", cancellationToken: token);
        await pane.EnterAsync(token);
        await TmuxWait.UntilAsync(
            async inner => string.Join('\n', await pane.CaptureAsync(cancellationToken: inner)),
            text => text.Contains("captured-by-chain", StringComparison.Ordinal),
            TestBudget.Settle,
            TimeSpan.FromMilliseconds(20),
            token);

        // Trimming trailing space arrived in 3.4. Asking for it on an older
        // tmux must drop the flag rather than send one that server refuses,
        // which is why the command is built from the pane.
        TmuxCommandResult result = await new CapturePaneRequest(trimTrailingSpaces: true)
            .ExecuteAsync(pane, token);

        Assert.Contains(
            result.StandardOutputLines,
            line => line.Contains("captured-by-chain", StringComparison.Ordinal));
    }

    [UnixFact]
    public async Task A_message_chains_and_keeps_its_version_gates()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);

        // Naming a client is refused by 3.2a; literal expansion arrived in 3.4.
        // Both must still produce a message on every supported tmux.
        TmuxCommandResult result = await new DisplayMessageRequest(
            "chained-message",
            returnText: true,
            targetClient: "/dev/null").ExecuteAsync(server, token);

        Assert.Contains("chained-message", result.StandardOutputLines);
    }

    [UnixFact]
    public async Task A_shell_request_chains_and_keeps_its_version_gates()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);

        // Showing standard error arrived in 3.6 and passing arguments in 3.7,
        // so asking for both has to drop what the running tmux lacks rather
        // than send a flag it refuses.
        TmuxCommandResult result = await new RunShellRequest(
            "echo chained-shell",
            showStandardError: true).ExecuteAsync(server, token);

        // tmux 3.3a/3.4 accept run-shell but report nothing; 3.2a and 3.5+
        // return what it printed, so output is asserted only where sent.
        string reported = server.Version!.Value.ToString();
        if (reported is "3.3a" or "3.4")
        {
            Assert.Empty(result.StandardOutputLines);
            return;
        }

        Assert.Contains("chained-shell", result.StandardOutputLines);
    }

    [UnixFact]
    public async Task A_paste_chains_and_keeps_its_version_gate()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        Pane pane = (await server.GetPanesAsync(token))[0];

        await server.SetBufferAsync("chained-paste", "ltbuf", cancellationToken: token);

        // Pasting raw bytes arrived in 3.7, so asking for it on an older tmux
        // must drop the flag rather than send one that server refuses. The
        // buffer's text reaching the pane is what says the paste happened.
        await new PasteBufferRequest(name: "ltbuf", rawBytes: true).ExecuteAsync(pane, token);

        // Joined, because a paste lands at the prompt and a wide enough prompt
        // leaves it split across two stored lines.
        string seen = await TmuxWait.UntilAsync(
            async inner => string.Join(
                '\n',
                await pane.CaptureAsync(new CapturePaneRequest(joinWrappedLines: true), inner)),
            text => text.Contains("chained-paste", StringComparison.Ordinal),
            TestBudget.Settle,
            TimeSpan.FromMilliseconds(20),
            token);

        Assert.Contains("chained-paste", seen, StringComparison.Ordinal);
    }

    [UnixFact]
    public async Task A_menu_chains_and_drops_the_styles_this_tmux_lacks()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);

        // Styles arrived in 3.4 and the mouse flag in 3.5. A menu needs a
        // client to open in, so what is asserted is the command tmux built
        // rather than a menu nobody could see.
        TmuxCommand command = new DisplayMenuRequest(
            [new TmuxMenuItem("Build", "b", "display-message built")],
            title: "chained",
            mouse: true).ToCommand(server);

        Assert.Equal("display-menu", command.Name);
        Assert.Contains("Build", command.Arguments);
        Assert.Contains("chained", command.Arguments);

        bool carriesMouse = TmuxCapabilities.IsSupported(
            server.Version!.Value,
            "display_menu_mouse");

        Assert.Equal(carriesMouse, command.Arguments.Contains("-M"));
    }

    [UnixFact]
    public async Task A_popup_chains_and_drops_the_options_this_tmux_lacks()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        Pane pane = (await server.GetPanesAsync(token))[0];

        // Popup options arrived in 3.3 and the key policy in 3.6. A popup
        // needs a client to open in, so the assertion is the command tmux
        // built rather than a popup nobody could see.
        TmuxCommand command = new DisplayPopupRequest(
            command: "true",
            width: "40",
            height: "10",
            title: "chained-popup").ToCommand(pane);

        Assert.Equal("display-popup", command.Name);
        Assert.Contains("40", command.Arguments);

        bool carriesOptions = TmuxCapabilities.IsSupported(
            server.Version!.Value,
            "display_popup_3_3_options");

        // The close mode maps to a flag every supported tmux carries; what
        // 3.3 added is the titling and styling, so that is what tracks the
        // capability.
        Assert.Equal(carriesOptions, command.Arguments.Contains("-T"));
    }

    [UnixFact]
    public async Task Options_chain_in_the_scope_their_handle_names()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        Window window = (await server.GetWindowsAsync(token))[0];

        // The same request means different things through different handles,
        // so the command is built from the handle rather than the request
        // alone. Setting several at once is what a workspace script does.
        await server.Chain()
            .Then(new SetOptionRequest("@chained-one", "first").ToCommand(server.Options))
            .Then(new SetOptionRequest("@chained-two", "second").ToCommand(server.Options))
            .Then(new SetOptionRequest("@chained-window", "third").ToCommand(window.Options))
            .ExecuteAsync(token);

        Assert.Equal(
            "first",
            (await server.Options.GetAsync(new GetOptionRequest("@chained-one"), token))[0]
                .Value.Raw);
        Assert.Equal(
            "second",
            (await server.Options.GetAsync(new GetOptionRequest("@chained-two"), token))[0]
                .Value.Raw);
        Assert.Equal(
            "third",
            (await window.Options.GetAsync(new GetOptionRequest("@chained-window"), token))[0]
                .Value.Raw);
    }

    [UnixFact]
    public async Task An_unset_chains_beside_the_set_that_preceded_it()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);

        // Setting and unsetting in one invocation is how a script leaves the
        // server the way it found it, so the two belong in the same chain.
        await server.Chain()
            .Then(new SetOptionRequest("@chained-temp", "present").ToCommand(server.Options))
            .Then(new SetOptionRequest("@chained-kept", "stays").ToCommand(server.Options))
            .Then(new UnsetOptionRequest("@chained-temp").ToCommand(server.Options))
            .ExecuteAsync(token);

        Assert.Equal(
            "stays",
            (await server.Options.GetAsync(new GetOptionRequest("@chained-kept"), token))[0]
                .Value.Raw);

        IReadOnlyList<TmuxOption> gone = await server.Options.GetAsync(
            new GetOptionRequest("@chained-temp", quiet: true),
            token);

        Assert.True(gone.Count == 0 || gone[0].Value.State == TmuxOptionState.Absent);
    }

    [UnixFact]
    public async Task Hooks_chain_in_the_scope_their_handle_names()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);

        // Hooks carry the same scope rules options do, so the command is built
        // from the handle. Installing several at once is what a workspace
        // script does before it starts anything.
        await server.Chain()
            .Then(new SetHookRequest("after-new-window", "display-message hooked")
                .ToCommand(server.Hooks))
            .Then(new SetHookRequest("after-new-session", "display-message hooked-too")
                .ToCommand(server.Hooks))
            .ExecuteAsync(token);

        IReadOnlyList<TmuxHook> installed = await server.Hooks.GetAllAsync(
            cancellationToken: token);

        Assert.Contains(
            installed,
            hook => hook.Name.Contains("after-new-window", StringComparison.Ordinal));
        Assert.Contains(
            installed,
            hook => hook.Name.Contains("after-new-session", StringComparison.Ordinal));
    }

    [UnixFact]
    public async Task A_confirmation_chains_and_drops_the_keys_this_tmux_lacks()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);

        // Naming the accepting key arrived in 3.4. A confirmation needs a
        // client to ask, so what is asserted is the command tmux built rather
        // than an answer nobody could give.
        TmuxCommand command = new ConfirmBeforeRequest(
            ["display-message", "confirmed"],
            prompt: "sure?",
            confirmKey: "y").ToCommand(server);

        Assert.Equal("confirm-before", command.Name);
        Assert.Contains("sure?", command.Arguments);

        bool carriesKeys = TmuxCapabilities.IsSupported(
            server.Version!.Value,
            "confirm_before_acceptance");

        Assert.Equal(carriesKeys, command.Arguments.Contains("-c"));
    }

    [UnixFact]
    public async Task A_prompt_chains_and_is_still_refused_below_its_floor()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);

        bool carriesTypes = TmuxCapabilities.IsSupported(
            server.Version!.Value,
            "command_prompt_background");

        CommandPromptRequest typed = new("display-message %%", type: PromptType.Command);

        if (!carriesTypes)
        {
            // tmux 3.2a reads the type flag as a pair of booleans meaning
            // something else, so batching must not become a way to send one:
            // asking is refused here exactly as it is when run alone.
            Assert.Throws<TmuxVersionTooLowException>(() => typed.ToCommand(server));
            return;
        }

        TmuxCommand command = typed.ToCommand(server);

        // A prompt needs a client to ask, and tmux refuses it outright on a
        // server nobody is attached to, so what is asserted is the command
        // built rather than a question nobody could answer.
        Assert.Equal("command-prompt", command.Name);
        Assert.Contains("-T", command.Arguments);
        Assert.Contains(command, server.Chain().Then(command).Commands);
    }

    [UnixFact]
    public async Task Copy_mode_chains_and_is_observable_in_the_pane()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        Pane pane = (await server.GetPanesAsync(token))[0];

        // Paging down on entry arrived in 3.5, so asking for it on an older
        // tmux must drop the flag. Unlike a prompt or a menu, copy mode leaves
        // a mark on the pane, so the entry itself is observable.
        await new CopyModeRequest(pageDown: true).ExecuteAsync(pane, token);

        RawTmuxResult mode = await raw.ExecuteAsync(
            ["display-message", "-p", "-t", $"{raw.SessionName}:0.0", "#{pane_in_mode}"],
            token);

        Assert.Equal("1", mode.StandardOutputLines[0]);

        await new CopyModeRequest(cancel: true).ExecuteAsync(pane, token);

        RawTmuxResult left = await raw.ExecuteAsync(
            ["display-message", "-p", "-t", $"{raw.SessionName}:0.0", "#{pane_in_mode}"],
            token);

        Assert.Equal("0", left.StandardOutputLines[0]);
    }

    [UnixFact]
    public async Task Window_resize_and_pane_respawn_chain()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        Window window = (await server.GetWindowsAsync(token))[0];
        Pane pane = (await server.GetPanesAsync(token))[0];

        await new ResizeWindowRequest(width: 100, height: 30).ExecuteAsync(window, token);

        Window resized = await window.RefreshAsync(token);
        Assert.Equal(100, resized.Width);
        Assert.Equal(30, resized.Height);

        // Respawning replaces the pane's process, so the pane keeps its
        // identifier while what runs inside it changes.
        await new RespawnRequest(command: "cat", killExistingProcess: true)
            .ExecuteAsync(pane, token);

        string running = await TmuxWait.UntilAsync(
            async inner => (await raw.ExecuteAsync(
                ["display-message", "-p", "-t", pane.Id.ToString(), "#{pane_current_command}"],
                inner)).StandardOutputLines[0],
            command => command == "cat",
            TestBudget.Settle,
            TimeSpan.FromMilliseconds(20),
            token);

        Assert.Equal("cat", running);
    }

    [UnixFact]
    public async Task A_chooser_chains_and_keeps_the_dropped_sort_order_out()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        Pane pane = (await server.GetPanesAsync(token))[0];

        // tmux 3.7 rejects activity-time order by name and fails the whole
        // invocation, so the order is dropped there and kept everywhere else.
        TmuxCommand command = new ChooseTreeRequest(sort: ChooseTreeSort.Time).ToCommand(pane);

        Assert.Equal("choose-tree", command.Name);

        bool carriesTime = TmuxCapabilities.IsSupported(
            server.Version!.Value,
            "choose_tree_sort_time");

        Assert.Equal(carriesTime, command.Arguments.Contains("time"));

        // The chooser needs a client to open in, so the command being accepted
        // is what says the order was right for this tmux.
        await server.Chain().Then(command).ExecuteAsync(token);

        Assert.True(await server.IsAliveAsync(token));
    }

    [UnixFact]
    public async Task A_chain_refuses_commands_from_two_servers()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        Session session = await TestHierarchy.RequireFirstSessionAsync(server, token);

        // At most one of two generations names a running server, so a chain
        // carrying both cannot be valid however tmux answers it.
        TmuxCommand here = new NewWindowRequest(name: "here").ToCommand(session);
        TmuxCommand elsewhere = here with
        {
            RequiredGeneration = new ServerGeneration(
                session.Generation.ProcessId + 1,
                session.Generation.StartTime),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.Chain().Then(here).Then(elsewhere).ExecuteAsync(token));
    }

    [UnixFact]
    public async Task Buffer_listing_and_access_chain()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);

        await server.SetBufferAsync("listed", "ltlist", cancellationToken: token);

        TmuxCommandResult listed = await new ListBuffersRequest(format: "#{buffer_name}")
            .ExecuteAsync(server, token);

        Assert.Contains("ltlist", listed.StandardOutputLines);

        bool carriesAccess = TmuxCapabilities.IsSupported(
            server.Version!.Value,
            "server_access_command");

        ServerAccessRequest access = new(list: true);

        if (!carriesAccess)
        {
            // The command arrived in 3.3, so batching must not become a way to
            // send one an older server has never heard of.
            Assert.Throws<TmuxVersionTooLowException>(() => access.ToCommand(server));
            return;
        }

        Assert.Equal("server-access", access.ToCommand(server).Name);
    }

    [UnixFact]
    public async Task A_window_link_chains_through_the_window_that_knows_its_source()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        await server.CreateSessionAsync(new NewSessionRequest(name: "link-target"), token);

        Window window = (await server.GetWindowsAsync(token))
            .First(candidate => candidate.Name != "link-target");

        // Linking names its source as the session the window was read through,
        // which a window resolved by identifier alone does not know, so the
        // command is built from the handle.
        await new LinkWindowRequest("link-target", detach: true).ExecuteAsync(window, token);

        // The same window now appears under both sessions, which is what a
        // link is.
        IReadOnlyList<Window> linked = [.. (await server.GetWindowsAsync(token))
            .Where(candidate => candidate.Id == window.Id)];

        Assert.Equal(2, linked.Count);
    }

    [UnixFact]
    public async Task Window_and_pane_moves_chain()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        await raw.ExecuteAsync(
            ["new-window", "-d", "-t", raw.SessionName, "-n", "mover"],
            token);

        Window mover = (await server.GetWindowsAsync(token))
            .First(window => window.Name == "mover");

        await new MoveWindowRequest(destination: "9").ExecuteAsync(mover, token);

        Window moved = await mover.RefreshAsync(token);
        Assert.Equal(9, moved.Index);

        // Moving a pane re-homes it into another window, so the window it
        // belonged to is the thing that changes.
        Pane pane = (await moved.GetPanesAsync(token))[0];
        Window destination = (await server.GetWindowsAsync(token))
            .First(window => window.Id != moved.Id);

        await new MovePaneRequest(target: $"{destination.Id}").ExecuteAsync(pane, token);

        Window refreshed = await destination.RefreshAsync(token);
        Assert.Equal(2, (await refreshed.GetPanesAsync(token)).Count);
    }

    [UnixFact]
    public async Task Splits_chain_and_report_the_panes_they_made()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        Pane pane = (await server.GetPanesAsync(token))[0];

        // Splitting repeatedly is the case chaining exists for, and the split
        // keeps its identifier-printing flags so the invocation says which
        // panes it made.
        TmuxCommandResult result = await server.Chain()
            .Then(new SplitPaneRequest().ToCommand(pane))
            .Then(new SplitPaneRequest(direction: PaneDirection.Below).ToCommand(pane))
            .ExecuteAsync(token);

        Assert.Equal(3, (await server.GetPanesAsync(token)).Count);
        Assert.Equal(2, result.StandardOutputLines.Count);
        Assert.All(
            result.StandardOutputLines,
            line => Assert.StartsWith("%", line, StringComparison.Ordinal));
    }

    [UnixFact]
    public async Task Floating_panes_and_attachment_chain()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        Pane pane = (await server.GetPanesAsync(token))[0];
        Session session = await TestHierarchy.RequireFirstSessionAsync(server, token);

        // Attaching needs a terminal this process does not have, so what is
        // asserted is the command built rather than an attachment that cannot
        // happen here.
        TmuxCommand attach = new AttachSessionRequest(detachOthers: true).ToCommand(session);

        Assert.Equal("attach-session", attach.Name);
        Assert.Contains("-d", attach.Arguments);

        bool carriesFloats = TmuxCapabilities.IsSupported(
            server.Version!.Value,
            "new_pane_command");

        NewPaneRequest floating = new(width: 20, height: 5);

        if (!carriesFloats)
        {
            // new-pane arrived whole in 3.7, so batching must not become a way
            // to send a command older servers have never heard of.
            Assert.Throws<TmuxVersionTooLowException>(() => floating.ToCommand(pane));
            return;
        }

        TmuxCommandResult made = await floating.ExecuteAsync(pane, token);

        Assert.Single(made.StandardOutputLines);
        Assert.StartsWith("%", made.StandardOutputLines[0], StringComparison.Ordinal);
    }

    [UnixFact]
    public async Task A_read_chains_beside_the_changes_it_reports_on()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);

        // Reading inside a chain is for seeing what the same invocation just
        // did. A chain returns one combined stream, so this asks for one value
        // rather than several it could not tell apart afterwards.
        TmuxCommandResult result = await server.Chain()
            .Then(new SetOptionRequest("@chained-read", "written").ToCommand(server.Options))
            .Then(new GetOptionRequest("@chained-read").ToCommand(server.Options))
            .ExecuteAsync(token);

        Assert.Contains(
            result.StandardOutputLines,
            line => line.Contains("written", StringComparison.Ordinal));

        // The handle's own accessor answers the same question with the value
        // already parsed, which is what most callers want.
        Assert.Equal(
            "written",
            (await server.Options.GetAsync(new GetOptionRequest("@chained-read"), token))[0]
                .Value.Raw);
    }

    [UnixFact]
    public async Task Hook_entries_and_listings_chain()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);

        // One request, several tmux commands. The one-shot path sends these a
        // process at a time, so this is the case batching helps most.
        SetHooksRequest entries = new(
            "after-new-window",
            new Dictionary<int, string>
            {
                [0] = "display-message first",
                [1] = "display-message second",
            },
            clearExisting: true);

        Assert.Equal(3, entries.ToCommands(server.Hooks).Count);

        // A request answering several commands joins a chain whole, so the
        // clear and both entries reach tmux in one invocation.
        await server.Chain().Then(entries.ToCommands(server.Hooks)).ExecuteAsync(token);

        TmuxCommandResult listed = await new ListHooksRequest()
            .ExecuteAsync(server.Hooks, server, token);

        Assert.Contains(
            listed.StandardOutputLines,
            line => line.Contains("after-new-window[0]", StringComparison.Ordinal));
        Assert.Contains(
            listed.StandardOutputLines,
            line => line.Contains("after-new-window[1]", StringComparison.Ordinal));

        // Removing names the same hook without saying what to do with it, so
        // the command is asked for by name rather than guessed.
        await server.Chain()
            .Then(new HookRequest("after-new-window").ToUnsetCommand(server.Hooks))
            .ExecuteAsync(token);

        Assert.DoesNotContain(
            await server.Hooks.GetAllAsync(cancellationToken: token),
            hook => hook.Name.Contains("after-new-window", StringComparison.Ordinal));
    }

    private static Task<Server> ConnectAsync(
        RawTmuxTestContext raw,
        CancellationToken token) =>
        Server.ConnectAsync(
            new ServerConnectionOptions(
                tmuxBinaryPath: raw.TmuxBinaryPath,
                socketPath: raw.SocketPath,
                configurationFile: "/dev/null"),
            token);
}
