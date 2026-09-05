using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Mcp;
using LibTmux.Testing;
using ModelContextProtocol;

namespace LibTmux.IntegrationTests;

/// <summary>What the tools do to a real tmux server.</summary>
[Collection("tmux control clients")]
[UnsupportedOSPlatform("windows")]
public sealed class TmuxToolsTests
{
    [UnixFact]
    public async Task A_synchronized_cohort_holds_only_the_panes_tmux_would_reach()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);
        string first = scope.Pane.Id.ToString();
        ActionResult second = await mcp.Capabilities.SplitWindowAsync(
            first, cancellationToken: token);
        ActionResult third = await mcp.Capabilities.SplitWindowAsync(
            first, cancellationToken: token);
        await mcp.Capabilities.SetSynchronizePanesAsync(true, cancellationToken: token);

        PaneInputResult all = await mcp.Capabilities.SendKeysAsync(
            "# all", first, enter: true, cancellationToken: token);
        Assert.Equal(3, all.TargetPaneIds.Count);
        Assert.Contains(second.PaneId!, all.TargetPaneIds);
        Assert.Contains(third.PaneId!, all.TargetPaneIds);

        // send_keys fans out; a run does not. Its payload travels through a
        // buffer, which tmux writes straight to the named pane, so the exit
        // status it reports is one pane's even with the cohort at three.
        RunResult scoped = await mcp.Capabilities.RunShellCommandAsync(
            "echo cohort-marker-7f3", first, timeoutSeconds: 20, cancellationToken: token);
        Assert.Equal(0, scoped.ExitStatus);
        foreach (string peer in new[] { second.PaneId!, third.PaneId! })
        {
            CaptureResult seen = await mcp.Read.CapturePaneAsync(
                peer, cancellationToken: token);
            Assert.DoesNotContain(
                seen.Content.Lines,
                line => line.Contains("cohort-marker-7f3", StringComparison.Ordinal));
        }

        // A zoomed window hides its other panes, and window.c skips every pane
        // that is not visible, so the cohort collapses to the zoomed one.
        Server server = scope.Server;
        _ = await server.ExecuteCommandAsync(["resize-pane", "-Z", "-t", first], token);

        PaneInputResult zoomed = await mcp.Capabilities.SendKeysAsync(
            "# zoomed", first, enter: true, cancellationToken: token);
        Assert.Equal([first], zoomed.TargetPaneIds);
        Assert.DoesNotContain("synchronize", zoomed.Changed, StringComparison.Ordinal);

        // Which is what makes the run refusal wrong: the outcome is singular.
        RunResult run = await mcp.Capabilities.RunShellCommandAsync(
            "echo zoomed-ran", first, timeoutSeconds: 20, cancellationToken: token);
        Assert.Equal(0, run.ExitStatus);

        _ = await server.ExecuteCommandAsync(["resize-pane", "-Z", "-t", first], token);
        PaneInputResult restored = await mcp.Capabilities.SendKeysAsync(
            "# restored", first, enter: true, cancellationToken: token);
        Assert.Equal(3, restored.TargetPaneIds.Count);
        Assert.Contains("synchronize", restored.Changed, StringComparison.Ordinal);
    }

    [UnixFact]
    public async Task An_empty_identifier_is_refused_rather_than_read_as_the_current_one()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);
        string session = scope.Session.Id.ToString();
        ActionResult extra = await mcp.Capabilities.CreateWindowAsync(
            session, cancellationToken: token);
        int before = (await mcp.Read.ListWindowsAsync(session, cancellationToken: token)).Count;

        // A caller that mangles an id into "" used to destroy the ACTIVE window
        // rather than be told the id was empty.
        McpException empty = await Assert.ThrowsAsync<McpException>(
            () => mcp.Capabilities.KillWindowAsync(string.Empty, token));
        Assert.Contains("empty windowId", empty.Message, StringComparison.Ordinal);
        Assert.Equal(
            before,
            (await mcp.Read.ListWindowsAsync(session, cancellationToken: token)).Count);

        McpException blank = await Assert.ThrowsAsync<McpException>(
            () => mcp.Capabilities.RenameWindowAsync("renamed", "   ", token));
        Assert.Contains("list_windows", blank.Message, StringComparison.Ordinal);

        // The pane-input tools resolve their own source pane, so the guard on
        // the shared resolver missed exactly the three that type into a shell.
        // find_pane_by_position takes its window as a required id and passes it
        // to a listing where empty means every window, so every window's pane
        // at index 0 matched and the pick threw onto the backstop.
        McpException everyWindow = await Assert.ThrowsAsync<McpException>(
            () => mcp.Capabilities.FindPaneByPositionAsync(string.Empty, 0, token));
        Assert.Contains("list_windows", everyWindow.Message, StringComparison.Ordinal);
        Assert.Null(await mcp.Capabilities.FindPaneByPositionAsync(
            scope.Window.Id.ToString(), 99, token));

        McpException typed = await Assert.ThrowsAsync<McpException>(
            () => mcp.Capabilities.SendKeysAsync("echo no", string.Empty, cancellationToken: token));
        Assert.Contains("empty paneId", typed.Message, StringComparison.Ordinal);
        McpException ran = await Assert.ThrowsAsync<McpException>(
            () => mcp.Capabilities.RunShellCommandAsync(
                "echo no", "   ", timeoutSeconds: 20, cancellationToken: token));
        Assert.Contains("list_panes", ran.Message, StringComparison.Ordinal);

        // Omission still means the current one, which is the behaviour the
        // empty string was being mistaken for.
        ActionResult renamed = await mcp.Capabilities.RenameWindowAsync(
            "still-works", cancellationToken: token);
        Assert.StartsWith("Renamed window", renamed.Changed, StringComparison.Ordinal);

        ActionResult killed = await mcp.Capabilities.KillWindowAsync(
            extra.WindowId!, token);
        Assert.Contains(extra.WindowId!, killed.Changed, StringComparison.Ordinal);
    }

    [UnixFact]
    public async Task Every_settable_option_is_one_this_tmux_refuses_a_command_for()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        int known = 0;

        // The allow-list was read off tmux's own option types at one revision.
        // This asks the tmux actually under test, on every matrix version, and
        // it has to answer in both directions: the option takes its own value
        // back, and refuses both a bare word and a real tmux command.
        foreach (string name in InertOptions.Names)
        {
            RawTmuxResult current = await raw.ExecuteAsync(
                ["show-options", "-gv", name], token);
            if (current.ExitCode != 0)
            {
                Assert.Contains("option", current.StandardErrorText, StringComparison.Ordinal);
                continue;
            }

            known++;
            // tmux prints "none" for an unset colour and refuses it as input,
            // so its own round-trip is lossy for exactly those two options.
            string value = current.StandardOutputText.TrimEnd('\n');
            if (value.Length > 0 && !string.Equals(value, "none", StringComparison.Ordinal))
            {
                RawTmuxResult restored = await raw.ExecuteAsync(
                    ["set-option", "-g", name, value], token);
                Assert.Equal(0, restored.ExitCode);
            }

            foreach (string probe in new[] { "zzz", "display-message zzz" })
            {
                RawTmuxResult refused = await raw.ExecuteAsync(
                    ["set-option", "-g", name, probe], token);

                // A refusal for "no such option" would prove nothing about the
                // option's type, which is the whole claim being tested.
                Assert.NotEqual(0, refused.ExitCode);
                Assert.DoesNotContain(
                    "option", refused.StandardErrorText, StringComparison.Ordinal);
            }
        }

        // 3.2a knows 52 of the 76 and 3.7a knows all of them, so a floor here
        // catches a list that stopped matching tmux without pinning a version.
        Assert.InRange(known, 50, InertOptions.Names.Count);
    }

    [UnixFact]
    public async Task A_wait_streams_from_tmux_rather_than_falling_back_to_polling()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);

        // The streaming design rests on tmux pushing pane output to a control
        // client. When one cannot start, waiting falls back to a 60ms poll and
        // nothing reports the difference — and every other test of this hub
        // injects a fake session, so only this one says the real client starts.
        Assert.False(mcp.Activity.IsStreaming);
        IAsyncDisposable lease = await mcp.Activity.WatchAsync(scope.Pane, token);
        Assert.True(mcp.Activity.IsStreaming);

        await lease.DisposeAsync();
        Assert.False(mcp.Activity.IsStreaming);
    }

    [UnixFact]
    public async Task A_read_sees_the_options_a_server_was_actually_configured_with()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);

        // set -g is where nearly all tmux configuration lives, and a read
        // without inherited values answers nothing for it at any narrower
        // scope, so a normally configured server looked unconfigured.
        _ = await scope.Server.ExecuteCommandAsync(
            ["set-option", "-g", "base-index", "5"], token);

        OptionEntry global = Assert.Single(await mcp.Read.ShowOptionsAsync(
            "base-index", OptionScope.Session, cancellationToken: token));
        Assert.Equal("5", global.Value);
        Assert.True(global.Inherited);

        // Set where it was asked, the same read reports it as this scope's own.
        await mcp.Capabilities.SetOptionAsync(
            "base-index", "7", OptionScope.Session, cancellationToken: token);
        OptionEntry own = Assert.Single(await mcp.Read.ShowOptionsAsync(
            "base-index", OptionScope.Session, cancellationToken: token));
        Assert.Equal("7", own.Value);
        Assert.False(own.Inherited);
    }

    [UnixFact]
    public async Task Setting_an_option_refuses_every_name_that_carries_code()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);

        await mcp.Capabilities.SetOptionAsync(
            "status-position", "top", OptionScope.Session, cancellationToken: token);
        IReadOnlyList<OptionEntry> read = await mcp.Read.ShowOptionsAsync(
            "status-position", OptionScope.Session, cancellationToken: token);
        Assert.Equal("top", Assert.Single(read).Value);

        // default-command is a command option and status-left is a format, so
        // either would run code on panes this call never named.
        foreach (string name in new[] { "default-command", "status-left", "after-new-session" })
        {
            McpException refused = await Assert.ThrowsAsync<McpException>(
                () => mcp.Capabilities.SetOptionAsync(name, "on", cancellationToken: token));
            Assert.Contains("show_option", refused.Message, StringComparison.Ordinal);
        }

        // An allowed name still cannot carry a format, so a misclassification
        // in the list alone is not enough to reach tmux's format engine.
        McpException format = await Assert.ThrowsAsync<McpException>(
            () => mcp.Capabilities.SetOptionAsync(
                "status-position", "#(id)", cancellationToken: token));
        Assert.Contains("inert", format.Message, StringComparison.Ordinal);

        McpException owned = await Assert.ThrowsAsync<McpException>(
            () => mcp.Capabilities.SetOptionAsync("mouse", "on", cancellationToken: token));
        Assert.Contains("set_mouse_enabled", owned.Message, StringComparison.Ordinal);

        // Excluding tmux's string and command types stops a value carrying
        // code and is blind to a flag tmux acts on itself: these three end
        // sessions or the server, so they answer to the teardown gate.
        McpException lethal = await Assert.ThrowsAsync<McpException>(
            () => mcp.Capabilities.SetOptionAsync(
                "exit-unattached", "on", OptionScope.Server, cancellationToken: token));
        Assert.Contains("end the server", lethal.Message, StringComparison.Ordinal);

        await using McpToolFixture managed = McpToolFixture.Create(
            registry: CapabilityRegistry.Select(CapabilitySelection.WithoutTeardown));
        McpException gated = await Assert.ThrowsAsync<McpException>(
            () => managed.Capabilities.SetOptionAsync(
                "destroy-unattached", "off", OptionScope.Session, cancellationToken: token));
        Assert.Contains("kill_session", gated.Message, StringComparison.Ordinal);

        // The gate reads the name, so the harmless value still proves it opens.
        ActionResult allowed = await mcp.Capabilities.SetOptionAsync(
            "destroy-unattached", "off", OptionScope.Session, cancellationToken: token);
        Assert.Contains("destroy-unattached", allowed.Changed, StringComparison.Ordinal);
    }

    [UnixFact]
    public async Task A_pane_that_never_existed_is_refused_by_name()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);

        McpException missing = await Assert.ThrowsAsync<McpException>(
            () => mcp.Read.CapturePaneAsync("%999", cancellationToken: token));
        Assert.Contains("%999", missing.Message, StringComparison.Ordinal);

        // The message has to say what to do next, or a model retries the same
        // call until it runs out of turn.
        Assert.Contains("list_panes", missing.Message, StringComparison.Ordinal);

        McpException malformed = await Assert.ThrowsAsync<McpException>(
            () => mcp.Read.CapturePaneAsync("not-a-pane", cancellationToken: token));
        Assert.Contains("%1", malformed.Message, StringComparison.Ordinal);
    }

    [UnixFact]
    public async Task Running_a_command_reports_its_real_exit_status()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);
        string pane = scope.Pane.Id.ToString();

        RunResult ok = await mcp.Write.RunAsync(
            "echo mcp-ran",
            pane,
            timeoutSeconds: 20,
            cancellationToken: token);
        Assert.Equal(0, ok.ExitStatus);
        Assert.False(ok.TimedOut);
        Assert.Contains(ok.Output.Lines, line => line.Contains("mcp-ran", StringComparison.Ordinal));

        // The status comes from the shell rather than from reading the screen,
        // so a command that prints nothing still reports what it did.
        RunResult failed = await mcp.Write.RunAsync(
            "exit 42",
            pane,
            timeoutSeconds: 20,
            cancellationToken: token);
        Assert.Equal(42, failed.ExitStatus);
    }

    [UnixFact]
    public async Task A_run_reports_what_the_command_printed_and_not_the_command()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);
        string pane = scope.Pane.Id.ToString();

        // A shell echoes what is pasted into it, and a multi-line command
        // echoes as several lines carrying none of this server's markers, so
        // the caller read its own command back as output — prompt, subshell
        // paren and continuation lines included.
        RunResult many = await mcp.Write.RunAsync(
            "for i in 1 2 3\ndo\n  echo \"line $i\"\ndone",
            pane,
            timeoutSeconds: 20,
            cancellationToken: token);
        Assert.Equal(0, many.ExitStatus);
        Assert.Equal(
            ["line 1", "line 2", "line 3"],
            many.Output.Lines.Where(line => line.StartsWith("line ", StringComparison.Ordinal)));
        Assert.DoesNotContain(
            many.Output.Lines,
            line => line.Contains("for i in", StringComparison.Ordinal));

        // A command that prints nothing answers nothing, rather than two lines
        // a caller cannot tell from output.
        RunResult silent = await mcp.Write.RunAsync(
            "true", pane, timeoutSeconds: 20, cancellationToken: token);
        Assert.Equal(0, silent.ExitStatus);
        Assert.Empty(silent.Output.Lines);

    }

    [UnixFact]
    public async Task A_run_leaves_none_of_its_own_bookkeeping_on_screen()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);
        string pane = scope.Pane.Id.ToString();

        await mcp.Write.RunAsync("echo first", pane, timeoutSeconds: 20, cancellationToken: token);
        RunResult second = await mcp.Write.RunAsync(
            "echo second",
            pane,
            timeoutSeconds: 20,
            cancellationToken: token);

        // The shell echoes the rendezvous like anything else typed, and it
        // stays on screen. A later read must not show an earlier run's, or a
        // reader concludes the command printed tmux commands it never ran.
        Assert.DoesNotContain(second.Output.Lines, line => line.Contains("lt_r_", StringComparison.Ordinal));

        CaptureResult captured = await mcp.Read.CapturePaneAsync(
            pane,
            includeHistory: true,
            cancellationToken: token);
        Assert.DoesNotContain(captured.Content.Lines, line => line.Contains("lt_r_", StringComparison.Ordinal));
        Assert.DoesNotContain(captured.Content.Lines, line => line.Contains("@lt_s_", StringComparison.Ordinal));
    }

    [UnixFact]
    public async Task A_wide_prompt_does_not_swallow_what_the_command_printed()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);
        string pane = scope.Pane.Id.ToString();

        // A prompt this wide leaves the run's own bookkeeping wrapping across
        // rows, which is the shape that made an earlier scrubber read one long
        // joined line as continued and take the next line with it. A macOS
        // runner reaches it without being asked: its hostname is 61 characters.
        await mcp.Write.SendKeysAsync(
            "PS1=$(printf 'x%.0s' $(seq 70))",
            pane,
            enter: true,
            cancellationToken: token);

        RunResult ran = await mcp.Write.RunAsync(
            "echo wide-prompt-marker",
            pane,
            timeoutSeconds: 20,
            cancellationToken: token);

        Assert.Equal(0, ran.ExitStatus);
        Assert.Contains(
            ran.Output.Lines,
            line => line.Contains("wide-prompt-marker", StringComparison.Ordinal));
        Assert.DoesNotContain(ran.Output.Lines, line => line.Contains("lt_r_", StringComparison.Ordinal));
    }

    [UnixFact]
    public async Task Tailing_answers_only_what_is_new()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);
        string pane = scope.Pane.Id.ToString();

        // A first read establishes a position and spends nothing.
        TailResult start = await mcp.Read.TailPaneAsync(pane, cancellationToken: token);
        Assert.Empty(start.Content.Lines);
        Assert.False(start.LinesMissed);

        await mcp.Write.RunAsync("echo tail-one", pane, timeoutSeconds: 20, cancellationToken: token);
        TailResult first = await mcp.Read.TailPaneAsync(
            pane,
            start.Cursor,
            cancellationToken: token);
        Assert.Contains(first.Content.Lines, line => line.Contains("tail-one", StringComparison.Ordinal));

        // The same cursor advanced past that text, so asking again answers
        // nothing rather than the same lines a second time.
        TailResult again = await mcp.Read.TailPaneAsync(
            pane,
            first.Cursor,
            cancellationToken: token);
        Assert.DoesNotContain(again.Content.Lines, line => line.Contains("tail-one", StringComparison.Ordinal));
    }

    [UnixFact]
    public async Task A_cursor_from_another_pane_is_refused_rather_than_guessed_at()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);

        await Assert.ThrowsAsync<McpException>(
            () => mcp.Read.TailPaneAsync(
                scope.Pane.Id.ToString(),
                "not-a-cursor",
                cancellationToken: token));
    }

    [UnixFact]
    public async Task Waiting_ends_on_the_text_it_was_told_to_wait_for()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);
        string pane = scope.Pane.Id.ToString();

        await mcp.Write.SendKeysAsync(
            "(sleep 1; echo READY_MARKER)",
            pane,
            enter: true,
            cancellationToken: token);

        WaitResult matched = await mcp.Read.WaitForTextAsync(
            pane,
            ["READY_MARKER"],
            timeoutSeconds: 20,
            cancellationToken: token);
        Assert.Equal(WaitOutcome.Matched, matched.Outcome);
        Assert.Equal("READY_MARKER", matched.MatchedPattern);
    }

    [UnixFact]
    public async Task A_wait_that_finds_nothing_says_so_and_says_how_long_it_waited()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);

        WaitResult timedOut = await mcp.Read.WaitForTextAsync(
            scope.Pane.Id.ToString(),
            ["NEVER_APPEARS_ANYWHERE"],
            timeoutSeconds: 2,
            cancellationToken: token);

        Assert.Equal(WaitOutcome.Timeout, timedOut.Outcome);

        // An over-large request is lowered rather than refused, so the result
        // has to report what was actually used or the policy is invisible.
        Assert.Equal(2, timedOut.EffectiveTimeoutSeconds);
    }

    [UnixFact]
    public async Task An_over_large_timeout_is_lowered_to_the_ceiling()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create(new ServerPolicy
        {
            WaitCeiling = TimeSpan.FromSeconds(2),
        });
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);

        WaitResult capped = await mcp.Read.WaitForTextAsync(
            scope.Pane.Id.ToString(),
            ["NEVER_APPEARS_ANYWHERE"],
            timeoutSeconds: 600,
            cancellationToken: token);

        Assert.Equal(2, capped.EffectiveTimeoutSeconds);
    }

    [UnixFact]
    public async Task Searching_finds_a_pane_by_what_it_is_showing()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);
        string pane = scope.Pane.Id.ToString();

        await mcp.Write.RunAsync(
            "echo NEEDLE_IN_PANE",
            pane,
            timeoutSeconds: 20,
            cancellationToken: token);

        SearchResult found = await mcp.Read.SearchPanesAsync(
            "NEEDLE_IN_PANE",
            cancellationToken: token);
        Assert.Contains(found.Panes, match => match.PaneId == pane);
    }

    [UnixFact]
    public async Task A_pattern_that_cannot_be_compiled_is_refused_before_it_runs()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);

        McpException bad = await Assert.ThrowsAsync<McpException>(
            () => mcp.Read.SearchPanesAsync("([unclosed", cancellationToken: token));
        Assert.Contains("regular expression", bad.Message, StringComparison.Ordinal);
    }

    [UnixFact]
    public async Task Building_a_workspace_answers_the_ids_the_next_call_needs()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);

        ActionResult split = await mcp.Write.SplitPaneAsync(
            scope.Pane.Id.ToString(),
            PaneDirection.Below,
            cancellationToken: token);

        // The new pane's id comes back, so laying out a workspace does not
        // need a listing between every step.
        Assert.NotNull(split.PaneId);
        Assert.NotEqual(scope.Pane.Id.ToString(), split.PaneId);

        IReadOnlyList<PaneInfo> panes = await mcp.Read.ListPanesAsync(
            windowId: scope.Window.Id.ToString(),
            cancellationToken: token);
        Assert.Equal(2, panes.Count);
    }

    [UnixFact]
    public async Task Killing_removes_what_it_names_and_nothing_else()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);

        ActionResult split = await mcp.Write.SplitPaneAsync(
            scope.Pane.Id.ToString(),
            cancellationToken: token);
        await mcp.Capabilities.KillPaneAsync(split.PaneId!, cancellationToken: token);

        IReadOnlyList<PaneInfo> left = await mcp.Read.ListPanesAsync(
            windowId: scope.Window.Id.ToString(),
            cancellationToken: token);
        Assert.Single(left);
        Assert.Equal(scope.Pane.Id.ToString(), left[0].PaneId);
    }

    [UnixFact]
    public async Task A_snapshot_describes_the_cursor_and_the_text_together()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);

        PaneSnapshot snapshot = await mcp.Read.SnapshotPaneAsync(
            scope.Pane.Id.ToString(),
            cancellationToken: token);

        Assert.Equal(scope.Pane.Id.ToString(), snapshot.Pane.PaneId);
        Assert.NotNull(snapshot.CursorY);
        Assert.False(snapshot.AlternateScreen);
    }

    [UnixFact]
    public async Task A_capture_keeps_the_newest_lines_and_reports_the_rest()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);
        string pane = scope.Pane.Id.ToString();

        await mcp.Write.RunAsync("seq 1 300", pane, timeoutSeconds: 30, cancellationToken: token);

        CaptureResult small = await mcp.Read.CapturePaneAsync(
            pane,
            includeHistory: true,
            maxLines: 10,
            cancellationToken: token);

        Assert.Equal(10, small.Content.Lines.Count);
        Assert.True(small.Content.Truncated);
        Assert.True(small.Content.DroppedLines > 0);

        // A terminal's newest line is the one that says what happened, so a
        // budget that kept the oldest would answer the wrong question.
        Assert.Contains(small.Content.Lines, line => line.Contains("300", StringComparison.Ordinal));
    }

    [UnixFact]
    public async Task The_environment_answers_names_and_withholds_credential_values()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);

        Server server = await mcp.Connection.GetAsync(cancellationToken: token);
        await server.Environment.SetAsync("LIBTMUX_PROBE_PLAIN", "plain-value", cancellationToken: token);
        await server.Environment.SetAsync("LIBTMUX_PROBE_API_KEY", "not-a-real-key", cancellationToken: token);

        IReadOnlyList<EnvironmentEntry> listed = await mcp.Read.ShowEnvironmentAsync(
            cancellationToken: token);

        // A listing is the easy call, so it is the one that must never carry a
        // value: the server environment is where exported API keys accumulate.
        Assert.All(listed, entry => Assert.Null(entry.Value));
        EnvironmentEntry plain = Assert.Single(listed, entry => entry.Name == "LIBTMUX_PROBE_PLAIN");
        Assert.True(plain.HasValue);
        Assert.True(plain.Withheld);

        IReadOnlyList<EnvironmentEntry> named = await mcp.Read.ShowEnvironmentAsync(
            "LIBTMUX_PROBE_PLAIN",
            cancellationToken: token);
        Assert.Equal("plain-value", Assert.Single(named).Value);

        IReadOnlyList<EnvironmentEntry> secret = await mcp.Read.ShowEnvironmentAsync(
            "LIBTMUX_PROBE_API_KEY",
            cancellationToken: token);
        EnvironmentEntry withheld = Assert.Single(secret);
        Assert.Null(withheld.Value);
        Assert.True(withheld.HasValue);
        Assert.True(withheld.Withheld);
    }

    [UnixFact]
    public async Task An_empty_but_running_server_answers_zero_rather_than_refusing()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        Server setup = Server.Open(mcp.Options.ConnectionOptions);
        try
        {
            // The state between the last kill and the next create. tmux reaps a
            // server that holds nothing, so exit-empty off is what holds it in
            // the state a caller reaches by closing their last session.
            await setup.ExecuteCommandAsync(["new-session", "-d", "-s", "seed"], token);
            await setup.ExecuteCommandAsync(["set-option", "-s", "exit-empty", "off"], token);
            await setup.ExecuteCommandAsync(["kill-session", "-t", "seed"], token);

            TmuxServerInfo info = await mcp.Read.ServerInfoAsync(cancellationToken: token);
            Assert.Equal(0, info.SessionCount);
            Assert.Equal(0, info.WindowCount);
            Assert.Equal(0, info.PaneCount);

            // "Is anything running, and what am I talking to?" is the question
            // asked against an empty server, so the half that stays knowable
            // has to survive the zero counts.
            Assert.NotNull(info.Version);
            Assert.NotNull(info.SocketName);

            Assert.Empty(await mcp.Read.ListSessionsAsync(cancellationToken: token));
            Assert.Empty(await mcp.Read.ListWindowsAsync(cancellationToken: token));
            Assert.Empty(await mcp.Read.ListPanesAsync(cancellationToken: token));

            // tmux refuses a server-wide listing here with "no current target",
            // which must not reach a caller as a raw command failure.
            McpException absent = await Assert.ThrowsAsync<McpException>(
                () => mcp.Read.CapturePaneAsync(cancellationToken: token));
            Assert.Contains("create_session", absent.Message, StringComparison.Ordinal);
        }
        finally
        {
            await setup.ExecuteCommandAsync(["kill-server"], token);
        }
    }

    [UnixFact]
    public async Task The_caller_pane_is_only_recognised_on_its_own_socket()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);
        string pane = scope.Pane.Id.ToString();

        Server server = await mcp.Connection.GetAsync(cancellationToken: token);
        TmuxCommandResult where = await server.ExecuteCommandAsync(
            ["display-message", "-p", "#{socket_path}"],
            token);
        string socketPath = where.StandardOutputLines[0];

        string? priorServer = System.Environment.GetEnvironmentVariable("TMUX");
        string? priorPane = System.Environment.GetEnvironmentVariable("TMUX_PANE");
        try
        {
            System.Environment.SetEnvironmentVariable("TMUX_PANE", pane);

            System.Environment.SetEnvironmentVariable("TMUX", $"{socketPath},1,0");
            Assert.True((await mcp.Read.ListPanesAsync(cancellationToken: token))
                .Single(each => each.PaneId == pane).IsCaller);
            Assert.Null((await mcp.Read.ServerInfoAsync(cancellationToken: token))
                .CallerPaneSocket);

            // tmux numbers panes per server, so the same id on another socket
            // is an unrelated pane. Believing it would mark a scratch pane as
            // the terminal the conversation runs through.
            System.Environment.SetEnvironmentVariable("TMUX", "/tmp/tmux-1000/not-this-one,1,0");
            Assert.False((await mcp.Read.ListPanesAsync(cancellationToken: token))
                .Single(each => each.PaneId == pane).IsCaller);

            TmuxServerInfo info = await mcp.Read.ServerInfoAsync(cancellationToken: token);
            Assert.Null(info.CallerPaneId);

            // A bare null reads as "could not determine". Naming the socket the
            // caller is actually on says the stronger and more useful thing:
            // no listing from this server can ever contain that pane.
            Assert.Equal("/tmp/tmux-1000/not-this-one", info.CallerPaneSocket);
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("TMUX", priorServer);
            System.Environment.SetEnvironmentVariable("TMUX_PANE", priorPane);
        }
    }

    [UnixFact]
    public async Task Replacing_a_window_answers_to_the_teardown_gate()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create(
            registry: CapabilityRegistry.Select(CapabilitySelection.WithoutTeardown));
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);

        // move-window -k kills whatever holds the destination index, so the
        // default toolset could destroy a window without kill_window.
        McpException refused = await Assert.ThrowsAsync<McpException>(
            () => mcp.Capabilities.MoveWindowAsync(
                scope.Window.Id.ToString(),
                destination: "0",
                replaceExisting: true,
                cancellationToken: token));
        Assert.Contains("kill_window", refused.Message, StringComparison.Ordinal);
        Assert.Contains("teardown", refused.Message, StringComparison.Ordinal);

        // The move itself stays available without teardown.
        ActionResult moved = await mcp.Capabilities.MoveWindowAsync(
            scope.Window.Id.ToString(),
            cancellationToken: token);
        Assert.Contains("Moved window", moved.Changed, StringComparison.Ordinal);
    }

    [UnixFact]
    public async Task A_spawn_says_where_it_landed_when_tmux_ignored_the_directory()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);

        // tmux does not refuse a directory it cannot enter; it falls back to
        // HOME and reports success, so the caller has to be told.
        ActionResult missing = await mcp.Write.CreateWindowAsync(
            scope.Session.Id.ToString(),
            startDirectory: "/nonexistent-libtmux-probe",
            cancellationToken: token);
        Assert.Contains("It started in ", missing.Changed, StringComparison.Ordinal);

        // The same directory spelled with . and .. segments is still honoured.
        ActionResult honoured = await mcp.Write.CreateWindowAsync(
            scope.Session.Id.ToString(),
            startDirectory: "/tmp/../tmp/./",
            cancellationToken: token);
        Assert.Equal("Created window ", honoured.Changed[..15]);
        Assert.DoesNotContain("It started in", honoured.Changed, StringComparison.Ordinal);
    }

    [UnixFact]
    public async Task Every_declared_format_literalization_is_proven_to_bite()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);
        Server server = await mcp.Connection.GetAsync(cancellationToken: token);

        const string Injection = "INJ#{session_name}END";
        string root = Path.Combine(Path.GetTempPath(), $"ltm-fmt-{Guid.NewGuid():N}"[..24]);
        string literalDirectory = Path.Combine(root, Injection);
        Directory.CreateDirectory(literalDirectory);

        async Task<string?> ReadAsync(string target, string format) =>
            (await server.ExecuteCommandAsync(
                ["display-message", "-p", "-t", target, format],
                token)).StandardOutputLines[0];

        // A pane that starts in a directory whose NAME contains a format is the
        // decisive proof: tmux can only chdir there if the request was never
        // expanded. An expanded one silently lands in HOME instead.
        async Task StartsInLiteralDirectoryAsync(Func<string, Task<ActionResult>> spawn)
        {
            ActionResult spawned = await spawn(literalDirectory);
            Assert.Equal(
                literalDirectory,
                await ReadAsync(spawned.PaneId!, "#{pane_current_path}"));
        }

        Dictionary<(string Tool, string Field), Func<Task>> proofs = new()
        {
            [("rename_session", "name")] = async () =>
            {
                await mcp.Capabilities.RenameSessionAsync(Injection, cancellationToken: token);
                Assert.Equal(Injection, await ReadAsync(scope.Session.Id.ToString(), "#{session_name}"));
            },
            [("rename_window", "name")] = async () =>
            {
                await mcp.Capabilities.RenameWindowAsync(Injection, cancellationToken: token);
                Assert.Equal(Injection, await ReadAsync(scope.Window.Id.ToString(), "#{window_name}"));
            },
            [("set_pane_title", "title")] = async () =>
            {
                await mcp.Capabilities.SetPaneTitleAsync(Injection, cancellationToken: token);
                Assert.Equal(Injection, await ReadAsync(scope.Pane.Id.ToString(), "#{pane_title}"));
            },
            [("create_session", "name")] = async () =>
            {
                ActionResult made = await mcp.Capabilities.CreateSessionAsync(
                    $"{Injection}-s", cancellationToken: token);
                Assert.Equal($"{Injection}-s", await ReadAsync(made.SessionId!, "#{session_name}"));
            },
            [("create_window", "name")] = async () =>
            {
                ActionResult made = await mcp.Capabilities.CreateWindowAsync(
                    scope.Session.Id.ToString(), $"{Injection}-w", cancellationToken: token);
                Assert.Equal($"{Injection}-w", await ReadAsync(made.WindowId!, "#{window_name}"));
            },
            [("create_session", "startDirectory")] = () => StartsInLiteralDirectoryAsync(
                directory => mcp.Capabilities.CreateSessionAsync(
                    null, directory, cancellationToken: token)),
            [("create_window", "startDirectory")] = () => StartsInLiteralDirectoryAsync(
                directory => mcp.Capabilities.CreateWindowAsync(
                    scope.Session.Id.ToString(), null, directory, cancellationToken: token)),
            [("split_window", "startDirectory")] = () => StartsInLiteralDirectoryAsync(
                directory => mcp.Capabilities.SplitWindowAsync(
                    scope.Pane.Id.ToString(), startDirectory: directory, cancellationToken: token)),
            [("respawn_pane", "startDirectory")] = () => StartsInLiteralDirectoryAsync(
                async directory =>
                {
                    ActionResult host = await mcp.Capabilities.SplitWindowAsync(
                        scope.Pane.Id.ToString(), cancellationToken: token);
                    return await mcp.Capabilities.RespawnPaneAsync(
                        host.PaneId, directory, killExistingProcess: true, cancellationToken: token);
                }),
            [("get_tmux_variables", "names")] = async () =>
            {
                // Validated rather than escaped: a name that is not a variable
                // name never reaches a format string at all.
                McpException refused = await Assert.ThrowsAsync<McpException>(
                    () => mcp.Capabilities.GetTmuxVariablesAsync(
                        [Injection], cancellationToken: token));
                Assert.Contains("session_name", refused.Message, StringComparison.Ordinal);
            },
        };

        try
        {
            // The capability model is the source of truth: a field that declares
            // a literalization without a proof here fails rather than shipping
            // an injection defence nobody ever exercised.
            Assert.Equal(
                CapabilityRegistry.Manifest
                    .SelectMany(tool => tool.InputLiteralization.Keys
                        .Select(field => $"{tool.Name}.{field}"))
                    .Order(StringComparer.Ordinal),
                proofs.Keys.Select(key => $"{key.Tool}.{key.Field}").Order(StringComparer.Ordinal));

            foreach (Func<Task> proof in proofs.Values)
            {
                await proof();
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [UnixFact]
    public async Task A_capture_never_carries_raw_terminal_control_bytes()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);
        string pane = scope.Pane.Id.ToString();

        await mcp.Write.RunAsync(
            @"printf 'ESCPROBE_\033[31mRED\033[0m_END\n'",
            pane,
            timeoutSeconds: 20,
            cancellationToken: token);

        CaptureResult captured = await mcp.Read.CapturePaneAsync(
            pane,
            includeHistory: true,
            cancellationToken: token);

        // Pane text reaches a model's context. A capture that preserved escape
        // sequences would let anything running in a pane write terminal
        // control codes straight into it.
        Assert.Contains(
            captured.Content.Lines,
            line => line.Contains("ESCPROBE_", StringComparison.Ordinal)
                && line.Contains("RED", StringComparison.Ordinal));
        void HasNoControlBytes(IReadOnlyList<string> lines) =>
            Assert.All(lines, line => Assert.DoesNotContain('\u001b', line));

        HasNoControlBytes(captured.Content.Lines);

        // capture_pane builds its own request; snapshot_pane and capture_since
        // go through PaneReader, which builds a second one. Two constructions
        // of the same defence can drift, so both are pinned here.
        PaneSnapshot snapshot = await mcp.Read.SnapshotPaneAsync(
            pane,
            cancellationToken: token);
        HasNoControlBytes(snapshot.Content.Lines);

        TailResult baseline = await mcp.Read.TailPaneAsync(pane, cancellationToken: token);
        HasNoControlBytes(baseline.Content.Lines);

        await mcp.Write.RunAsync(
            @"printf 'DELTAPROBE_\033[35mMAG\033[0m_END\n'",
            pane,
            timeoutSeconds: 20,
            cancellationToken: token);
        TailResult delta = await mcp.Read.TailPaneAsync(
            pane,
            baseline.Cursor,
            cancellationToken: token);

        // A delta returning nothing would also contain no control bytes, so
        // the new text has to be present for this to mean anything.
        Assert.Contains(
            delta.Content.Lines,
            line => line.Contains("DELTAPROBE_", StringComparison.Ordinal)
                && line.Contains("MAG", StringComparison.Ordinal));
        HasNoControlBytes(delta.Content.Lines);
    }

    [UnixFact]
    public async Task A_read_batch_holds_its_declared_bounds_and_its_declared_tools()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);

        ReadToolCall Listing() => new("list_sessions", null);
        Task<ReadToolBatchResult> RunAsync(int count) =>
            mcp.Capabilities.CallReadToolsBatchAsync(
                [.. Enumerable.Range(0, count).Select(_ => Listing())],
                cancellationToken: token);

        // Both sides of both bounds, because an off-by-one here either refuses
        // a legal batch or accepts an unbounded one.
        await Assert.ThrowsAsync<McpException>(() => RunAsync(0));
        Assert.Equal(1, (await RunAsync(1)).Succeeded);
        Assert.Equal(16, (await RunAsync(16)).Succeeded);
        await Assert.ThrowsAsync<McpException>(() => RunAsync(17));

        // Every tool the batch declares it can nest must actually dispatch.
        // A name in that set that the dispatcher rejects is a surface that
        // advertises more than it can do.
        ToolDefinition batch = CapabilityRegistry.All().ByName["call_read_tools_batch"];
        ReadToolBatchResult every = await mcp.Capabilities.CallReadToolsBatchAsync(
            [.. batch.NestedAuthority.Order(StringComparer.Ordinal)
                .Select(name => new ReadToolCall(name, null))],
            onError: "continue",
            cancellationToken: token);

        Assert.Equal(batch.NestedAuthority.Count, every.Results.Count);
        Assert.All(
            every.Results,
            result => Assert.DoesNotContain(
                "not an enabled batch-eligible inspect tool",
                result.Error ?? string.Empty,
                StringComparison.Ordinal));
    }

    [UnixFact]
    public async Task Every_declared_input_bound_is_enforced_on_both_sides()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);
        string pane = scope.Pane.Id.ToString();

        static string Filler(int bytes) => new('a', bytes);
        static Task Refuses(Func<Task> call) => Assert.ThrowsAsync<McpException>(call);

        // A bound is only a bound if the value at the limit is accepted and
        // the next one is not. Half of that is a limit nobody can rely on.
        Task Wait(IReadOnlyList<string> patterns) =>
            mcp.Capabilities.WaitForTextAsync(pane, patterns, timeoutSeconds: 0.2, cancellationToken: token);

        await Wait([.. Enumerable.Repeat("zz", 32)]);
        await Refuses(() => Wait([.. Enumerable.Repeat("zz", 33)]));
        await Wait([Filler(999)]);
        await Refuses(() => Wait([Filler(1000)]));
        string[] totalPatternBoundary =
            [.. Enumerable.Repeat(Filler(999), 16), Filler(400)];
        await Wait(totalPatternBoundary);
        await Refuses(() => Wait([.. totalPatternBoundary, "z"]));

        Task Variables(int count) => mcp.Capabilities.GetTmuxVariablesAsync(
            [.. Enumerable.Repeat("session_name", count)], pane, cancellationToken: token);

        await Variables(1);
        await Variables(64);
        await Refuses(() => Variables(0));
        await Refuses(() => Variables(65));

        Task Keys(int steps, int delay) => mcp.Capabilities.SendKeysBatchAsync(
            [.. Enumerable.Range(0, steps).Select(_ =>
                new PaneInputOperation("", pane, DelayMilliseconds: delay))],
            cancellationToken: token);

        await Keys(64, 0);
        await Refuses(() => Keys(65, 0));
        await Refuses(() => Keys(1, 2001));

        await mcp.Capabilities.SearchPanesAsync(Filler(999), cancellationToken: token);
        await Refuses(() => mcp.Capabilities.SearchPanesAsync(
            Filler(1000), cancellationToken: token));

        await Refuses(() => mcp.Capabilities.WaitForChannelAsync(
            Filler(4097), cancellationToken: token));
    }

    [UnixFact]
    public async Task Every_enumerated_parameter_value_is_answerable()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);
        string pane = scope.Pane.Id.ToString();

        // Driven off the enums themselves, so a value added later is swept
        // without anybody remembering to add it here.
        foreach (OptionScope level in Enum.GetValues<OptionScope>())
        {
            Assert.NotNull(await mcp.Capabilities.ShowOptionAsync(
                "history-limit", level, pane, cancellationToken: token));
            Assert.NotNull(await mcp.Capabilities.ShowHooksAsync(
                level, pane, cancellationToken: token));
        }

        HashSet<string> made = [];
        foreach (PaneDirection direction in Enum.GetValues<PaneDirection>())
        {
            ActionResult split = await mcp.Capabilities.SplitWindowAsync(
                pane, direction, cancellationToken: token);
            Assert.True(made.Add(split.PaneId!), $"{direction} reused a pane id");
        }

        foreach (bool history in (bool[])[false, true])
        {
            foreach (bool joined in (bool[])[false, true])
            {
                CaptureResult read = await mcp.Read.CapturePaneAsync(
                    pane,
                    includeHistory: history,
                    joinWrappedLines: joined,
                    cancellationToken: token);
                Assert.Equal(pane, read.PaneId);
            }
        }

        foreach (bool enter in (bool[])[false, true])
        {
            foreach (bool literal in (bool[])[false, true])
            {
                PaneInputResult sent = await mcp.Capabilities.SendKeysAsync(
                    literal ? "x" : "Escape",
                    pane,
                    enter: enter,
                    literal: literal,
                    cancellationToken: token);
                Assert.Contains(pane, sent.Changed, StringComparison.Ordinal);
            }
        }
    }

    [UnixFact]
    public async Task Modal_panes_refuse_input_without_reaching_the_workload()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);
        string paneId = scope.Pane.Id.ToString();
        string marker = $"modal-input-{Guid.NewGuid():N}";

        await scope.Pane.EnterCopyModeAsync(cancellationToken: token);
        try
        {
            List<(string Tool, Exception? Error)> refusals = [];
            foreach ((string tool, Func<Task> call) in new (string, Func<Task>)[]
            {
                ("send_keys", async () => _ = await mcp.Capabilities.SendKeysAsync(
                    $"echo {marker}-send", paneId, enter: true, cancellationToken: token)),
                ("paste_text", async () => _ = await mcp.Capabilities.PasteTextAsync(
                    $"echo {marker}-paste\n", paneId, bracketed: false, cancellationToken: token)),
                ("run_shell_command", async () => _ = await mcp.Capabilities.RunShellCommandAsync(
                    $"echo {marker}-run", paneId, timeoutSeconds: 0.2, cancellationToken: token)),
            })
            {
                refusals.Add((tool, await Record.ExceptionAsync(call)));
            }

            PaneInputBatchResult batch = await mcp.Capabilities.SendKeysBatchAsync(
                [new PaneInputOperation($"echo {marker}-batch", paneId, Enter: true)],
                cancellationToken: token);

            foreach ((string tool, Exception? error) in refusals)
            {
                McpException refused = Assert.IsType<McpException>(error);
                Assert.Contains(tool, refused.Message, StringComparison.Ordinal);
                Assert.Contains(paneId, refused.Message, StringComparison.Ordinal);
                Assert.Contains("human-owned", refused.Message, StringComparison.Ordinal);
            }

            PaneInputOperationResult refusal = Assert.Single(batch.Results);
            Assert.False(refusal.Success);
            Assert.Contains("send_keys_batch", refusal.Error, StringComparison.Ordinal);
            Assert.Contains(paneId, refusal.Error, StringComparison.Ordinal);
            Assert.Contains("human-owned", refusal.Error, StringComparison.Ordinal);
            Assert.Contains("capture_pane", refusal.Error, StringComparison.Ordinal);
            Assert.Contains("snapshot_pane", refusal.Error, StringComparison.Ordinal);
            Assert.Contains("wait", refusal.Error, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("exit", refusal.Error, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cancel", refusal.Error, StringComparison.OrdinalIgnoreCase);
            Pane fresh = await FreshPaneAsync(scope.Pane, token);
            Assert.Equal("1", fresh.RawFormatFields["pane_in_mode"]);
            await AssertMarkerAbsentAsync(mcp, paneId, marker, token);
        }
        finally
        {
            await scope.Pane.EnterCopyModeAsync(new CopyModeRequest(cancel: true), token);
        }
    }

    [UnixFact]
    public async Task Synchronized_input_refuses_when_a_sibling_is_human_owned()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);
        string target = scope.Pane.Id.ToString();
        ActionResult split = await mcp.Capabilities.SplitWindowAsync(target, cancellationToken: token);
        string sibling = split.PaneId!;
        Server server = await mcp.Connection.GetAsync(cancellationToken: token);
        Pane modal = await TmuxTargets.PaneAsync(server, sibling, token);
        string marker = $"synchronized-modal-{Guid.NewGuid():N}";

        await mcp.Capabilities.SetSynchronizePanesAsync(
            true,
            scope.Window.Id.ToString(),
            token);
        await modal.EnterCopyModeAsync(cancellationToken: token);
        try
        {
            List<(string Tool, Exception? Error)> refusals = [];
            foreach ((string tool, Func<Task> call) in new (string, Func<Task>)[]
            {
                ("send_keys", async () => _ = await mcp.Capabilities.SendKeysAsync(
                    $"echo {marker}-send", target, enter: true, cancellationToken: token)),
                ("run_shell_command", async () => _ = await mcp.Capabilities.RunShellCommandAsync(
                    $"echo {marker}-run", target, timeoutSeconds: 0.2, cancellationToken: token)),
            })
            {
                refusals.Add((tool, await Record.ExceptionAsync(call)));
            }

            foreach ((string tool, Exception? error) in refusals)
            {
                McpException refused = Assert.IsType<McpException>(error);
                Assert.Contains(tool, refused.Message, StringComparison.Ordinal);
                Assert.Contains(sibling, refused.Message, StringComparison.Ordinal);
                Assert.Contains("human-owned", refused.Message, StringComparison.Ordinal);
            }

            await AssertMarkerAbsentAsync(mcp, target, marker, token);
            await AssertMarkerAbsentAsync(mcp, sibling, marker, token);
        }
        finally
        {
            await modal.EnterCopyModeAsync(new CopyModeRequest(cancel: true), token);
        }
    }

    [UnixFact]
    public async Task An_effective_on_pane_cohort_refuses_modal_members_before_input()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);
        string source = scope.Pane.Id.ToString();
        ActionResult split = await mcp.Capabilities.SplitWindowAsync(source, cancellationToken: token);
        string peerId = split.PaneId!;
        Server server = await mcp.Connection.GetAsync(cancellationToken: token);
        Pane peer = await TmuxTargets.PaneAsync(server, peerId, token);
        string marker = $"effective-on-modal-{Guid.NewGuid():N}";
        string deliveredMarker = $"effective-on-delivered-{Guid.NewGuid():N}";

        _ = await scope.Window.Options.SetAsync(
            new SetOptionRequest("synchronize-panes", "off"), token);
        await SetPaneSynchronizationAsync(scope.Pane, "on", token);
        await SetPaneSynchronizationAsync(peer, "on", token);
        PaneInputResult delivered = await mcp.Capabilities.SendKeysAsync(
            $"echo {deliveredMarker}", source, enter: true, cancellationToken: token);
        Assert.Equal(new[] { source, peerId }.Order(StringComparer.Ordinal), delivered.TargetPaneIds);
        _ = await mcp.Capabilities.WaitForTextAsync(
            source, [deliveredMarker], timeoutSeconds: 5, cancellationToken: token);
        _ = await mcp.Capabilities.WaitForTextAsync(
            peerId, [deliveredMarker], timeoutSeconds: 5, cancellationToken: token);

        await peer.EnterCopyModeAsync(cancellationToken: token);
        try
        {
            List<(string Tool, Exception? Error)> refusals = [];
            foreach ((string tool, Func<Task> call) in new (string, Func<Task>)[]
            {
                ("send_keys", async () => _ = await mcp.Capabilities.SendKeysAsync(
                    $"echo {marker}-send", source, enter: true, cancellationToken: token)),
                ("run_shell_command", async () => _ = await mcp.Capabilities.RunShellCommandAsync(
                    $"echo {marker}-run", source, timeoutSeconds: 0.2, cancellationToken: token)),
            })
            {
                refusals.Add((tool, await Record.ExceptionAsync(call)));
            }

            PaneInputBatchResult batch = await mcp.Capabilities.SendKeysBatchAsync(
                [new PaneInputOperation($"echo {marker}-batch", source, Enter: true)],
                cancellationToken: token);

            foreach ((string tool, Exception? error) in refusals)
            {
                McpException refusal = Assert.IsType<McpException>(error);
                Assert.Contains(tool, refusal.Message, StringComparison.Ordinal);
                Assert.Contains(peerId, refusal.Message, StringComparison.Ordinal);
                Assert.Contains("human-owned", refusal.Message, StringComparison.Ordinal);
            }

            PaneInputOperationResult batchRefusal = Assert.Single(batch.Results);
            Assert.False(batchRefusal.Success);
            Assert.Contains("send_keys_batch", batchRefusal.Error, StringComparison.Ordinal);
            Assert.Contains(peerId, batchRefusal.Error, StringComparison.Ordinal);
            await AssertMarkerAbsentAsync(mcp, source, marker, token);
            await AssertMarkerAbsentAsync(mcp, peerId, marker, token);
            Pane freshPeer = await FreshPaneAsync(peer, token);
            Assert.Equal("1", freshPeer.RawFormatFields["pane_in_mode"]);
        }
        finally
        {
            await peer.EnterCopyModeAsync(new CopyModeRequest(cancel: true), token);
        }
    }

    [UnixFact]
    public async Task Pane_overrides_limit_input_to_the_source_effective_cohort()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);
        string source = scope.Pane.Id.ToString();
        ActionResult split = await mcp.Capabilities.SplitWindowAsync(source, cancellationToken: token);
        string peerId = split.PaneId!;
        Server server = await mcp.Connection.GetAsync(cancellationToken: token);
        Pane peer = await TmuxTargets.PaneAsync(server, peerId, token);

        _ = await scope.Window.Options.SetAsync(
            new SetOptionRequest("synchronize-panes", "on"), token);

        await SetPaneSynchronizationAsync(scope.Pane, "on", token);
        await SetPaneSynchronizationAsync(peer, "off", token);
        await AssertSourceOnlyInputAsync("source-on-peer-off", source, peerId, mcp, token);

        await SetPaneSynchronizationAsync(scope.Pane, "off", token);
        await peer.Options.UnsetAsync(new UnsetOptionRequest("synchronize-panes"), token);
        await AssertSourceOnlyInputAsync("source-off-peer-on", source, peerId, mcp, token);
    }

    [UnixFact]
    public async Task Paste_text_remains_target_only_when_a_synchronized_sibling_is_human_owned()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);
        string target = scope.Pane.Id.ToString();
        ActionResult split = await mcp.Capabilities.SplitWindowAsync(target, cancellationToken: token);
        string sibling = split.PaneId!;
        Server server = await mcp.Connection.GetAsync(cancellationToken: token);
        Pane modal = await TmuxTargets.PaneAsync(server, sibling, token);
        string marker = $"target-only-paste-{Guid.NewGuid():N}";

        _ = await scope.Window.Options.SetAsync(
            new SetOptionRequest("synchronize-panes", "off"), token);
        await SetPaneSynchronizationAsync(scope.Pane, "on", token);
        await SetPaneSynchronizationAsync(modal, "on", token);
        await modal.EnterCopyModeAsync(cancellationToken: token);
        try
        {
            _ = await mcp.Capabilities.PasteTextAsync(
                $"echo {marker}\n",
                target,
                bracketed: false,
                cancellationToken: token);

            CaptureResult captured = await mcp.Read.CapturePaneAsync(
                target,
                includeHistory: true,
                cancellationToken: token);
            Assert.Contains(captured.Content.Lines, line => line.Contains(marker, StringComparison.Ordinal));
            await AssertMarkerAbsentAsync(mcp, sibling, marker, token);
        }
        finally
        {
            await modal.EnterCopyModeAsync(new CopyModeRequest(cancel: true), token);
        }
    }

    [UnixFact]
    public async Task Batch_dispatch_rechecks_a_pane_that_enters_mode_after_preflight()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);
        string paneId = scope.Pane.Id.ToString();
        Server server = await mcp.Connection.GetAsync(cancellationToken: token);
        Pane preflight = await TmuxTargets.PaneAsync(server, paneId, token);
        Assert.Equal("0", preflight.RawFormatFields["pane_in_mode"]);

        await scope.Pane.EnterCopyModeAsync(cancellationToken: token);
        try
        {
            McpException refusal = await Assert.ThrowsAsync<McpException>(
                () => WriteTools.SendKeysToPaneAsync(
                    preflight,
                    "echo stale-snapshot-marker",
                    enter: true,
                    literal: true,
                    suppressHistory: false,
                    toolName: "send_keys_batch",
                    cancellationToken: token));

            Assert.Contains("send_keys_batch", refusal.Message, StringComparison.Ordinal);
            Assert.Contains(paneId, refusal.Message, StringComparison.Ordinal);
            Assert.Contains("human-owned", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("capture_pane", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("snapshot_pane", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("wait", refusal.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("exit", refusal.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cancel", refusal.Message, StringComparison.OrdinalIgnoreCase);
            await AssertMarkerAbsentAsync(mcp, paneId, "stale-snapshot-marker", token);
        }
        finally
        {
            await scope.Pane.EnterCopyModeAsync(new CopyModeRequest(cancel: true), token);
        }
    }

    private static async Task<Pane> FreshPaneAsync(Pane pane, CancellationToken cancellationToken) =>
        (await pane.Window.GetPanesAsync(cancellationToken).ConfigureAwait(false))
            .Single(candidate => candidate.Id == pane.Id);

    private static async Task SetPaneSynchronizationAsync(
        Pane pane,
        string value,
        CancellationToken cancellationToken) =>
        _ = await pane.Options.SetAsync(
            new SetOptionRequest("synchronize-panes", value), cancellationToken);

    private static async Task AssertSourceOnlyInputAsync(
        string name,
        string source,
        string peer,
        McpToolFixture mcp,
        CancellationToken cancellationToken)
    {
        string marker = $"{name}-{Guid.NewGuid():N}";
        PaneInputResult sent = await mcp.Capabilities.SendKeysAsync(
            $"echo {marker}", source, enter: true, cancellationToken: cancellationToken);
        Assert.Equal([source], sent.TargetPaneIds);
        _ = await mcp.Capabilities.WaitForTextAsync(
            source, [marker], timeoutSeconds: 5, cancellationToken: cancellationToken);
        await AssertMarkerAbsentAsync(mcp, peer, marker, cancellationToken);
    }

    private static async Task AssertMarkerAbsentAsync(
        McpToolFixture mcp,
        string paneId,
        string marker,
        CancellationToken cancellationToken)
    {
        CaptureResult captured = await mcp.Read.CapturePaneAsync(
            paneId,
            includeHistory: true,
            cancellationToken: cancellationToken);
        Assert.DoesNotContain(
            captured.Content.Lines,
            line => line.Contains(marker, StringComparison.Ordinal));
    }
}
