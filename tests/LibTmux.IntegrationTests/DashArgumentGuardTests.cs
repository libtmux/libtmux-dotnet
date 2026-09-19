using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Testing;

namespace LibTmux.IntegrationTests;

/// <summary>
/// Caller-supplied positional text must reach tmux's argument parser as data,
/// never as a flag it happens to spell. Each test sends a value shaped like a
/// flag tmux does not recognize for that command; without a guard, tmux's own
/// parser refuses the whole command with "unknown flag", proving the value
/// was read as an option rather than delivered to the field it was meant for.
/// With the guard, the same call either succeeds, or fails at a later,
/// different check (an unknown key, an invalid option name, a missing file,
/// "no current client") that only a value already past the flag parser could
/// reach - the fix that removed one exact-argv pin
/// (<c>PaneSendKeysDispatchTests.Send_text_uses_literal_mode</c>) already
/// shows the argv difference directly.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class DashArgumentGuardTests
{
    /// <summary>
    /// Shaped like a flag no command in this file recognizes (verified against
    /// tmux 3.2a through 3.7d), so an unguarded call is refused with "unknown
    /// flag -z" and a guarded one reaches whatever check comes after parsing.
    /// </summary>
    private const string DashMarker = "-zqamarker";

    [UnixFact]
    public async Task Literal_send_keys_delivers_a_flag_shaped_value_as_typed_text()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            cancellationToken: token);

        // -R is send-keys's own reset flag. Unguarded, "send-keys -l -R"
        // reads it as that flag and types nothing; the pane stays exactly as
        // it was. Guarded, "-R" is delivered as the two literal characters.
        await scope.Pane.SendTextAsync("-R", enter: false, cancellationToken: token);

        IReadOnlyList<string> lines = await scope.Pane.CaptureAsync(cancellationToken: token);
        Assert.Contains(lines, line => line.Contains("-R", StringComparison.Ordinal));
    }

    [UnixFact]
    public async Task Session_window_and_pane_creation_commands_are_not_read_as_flags()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            cancellationToken: token);
        Server server = scope.Server;

        // A flag-shaped Command is not a real program, so the spawned pane
        // can die and take its throwaway session or window down before this
        // call reads it back - tolerated below. What must never happen is
        // tmux's own parser refusing "unknown flag" before dispatch.

        // Server.CreateSessionAsync -> Server.BuildNewSessionArguments
        // (shared helper).
        await AssertNotRejectedAsFlagAsync(() => server.CreateSessionAsync(
            new NewSessionRequest { Name = $"dash-{Guid.NewGuid():N}", Command = DashMarker },
            token));

        // Session.CreateWindowAsync -> Session.BuildNewWindowArguments
        // (shared helper).
        await AssertNotRejectedAsFlagAsync(() => scope.Session.CreateWindowAsync(
            new NewWindowRequest { Command = DashMarker },
            token));

        // Window.CreateWindowAsync builds its own argv independently of
        // Session's - a separate bug the survey found, fixed separately.
        await AssertNotRejectedAsFlagAsync(() => scope.Window.CreateWindowAsync(
            new NewWindowRequest { Command = DashMarker },
            token));

        // Pane.SplitAsync -> Pane.BuildSplitArguments (shared helper).
        await AssertNotRejectedAsFlagAsync(() => scope.Pane.SplitAsync(
            new SplitPaneRequest { Command = DashMarker },
            token));

        // Window.SplitPaneAsync builds its own argv independently of Pane's.
        await AssertNotRejectedAsFlagAsync(() => scope.Window.SplitPaneAsync(
            new SplitPaneRequest { Command = DashMarker },
            token));

        // Pane.RespawnAsync -> Pane.BuildRespawnPaneArguments (shared
        // helper). Kill first: a live pane refuses to respawn regardless.
        Pane toRespawn = await scope.Window.SplitPaneAsync(cancellationToken: token);
        await AssertNotRejectedAsFlagAsync(() => toRespawn.RespawnAsync(
            new RespawnRequest { Command = DashMarker, KillExistingProcess = true },
            token));

        // Window.RespawnAsync builds its own argv independently of Pane's.
        await AssertNotRejectedAsFlagAsync(() => scope.Window.RespawnAsync(
            new RespawnRequest { Command = DashMarker, KillExistingProcess = true },
            token));
    }

    [UnixFact]
    public async Task Renaming_a_session_or_window_accepts_a_leading_dash_name()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            cancellationToken: token);

        // Unguarded, "rename-session -t id -zqamarker" is refused as an
        // unknown flag before anything is renamed.
        Session renamed = await scope.Session.RenameAsync(DashMarker, token);
        Assert.Equal(DashMarker, renamed.Name);

        Window renamedWindow = await scope.Window.RenameAsync(DashMarker, token);
        Assert.Equal(DashMarker, renamedWindow.Snapshot!["window_name"]);
    }

    [UnixFact]
    public async Task Shell_commands_and_a_wait_for_channel_are_not_read_as_flags()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            cancellationToken: token);
        Server server = scope.Server;

        // run-shell waits for the command it runs, so an unguarded call fails
        // with tmux's own parse error; guarded, tmux runs "-zqamarker" as a
        // shell command, which the shell reports as not found - a different,
        // later failure than "unknown flag".
        TmuxCommandException runFailure = await Assert.ThrowsAsync<TmuxCommandException>(
            () => server.RunShellAsync(new RunShellRequest(DashMarker), token));
        Assert.DoesNotContain("unknown flag", runFailure.Message, StringComparison.Ordinal);

        // if-shell treats a failed condition as false and still succeeds
        // overall, so the guarded call returns normally.
        await server.IfShellAsync(
            new IfShellRequest(DashMarker, ["display-message", "then"])
            {
                ElseCommand = ["display-message", "else"],
            },
            token);

        // wait-for -S signals and returns immediately, so this proves the
        // channel name itself reached tmux rather than being read as a flag.
        await server.WaitForAsync(new WaitForRequest(DashMarker, TmuxWaitMode.Signal), token);
    }

    [UnixFact]
    public async Task Display_message_pipe_pane_and_find_window_are_not_read_as_flags()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            cancellationToken: token);
        Server server = scope.Server;

        // Server.DisplayMessageAsync -> Server.BuildDisplayMessageArguments
        // (shared helper). -p prints the expanded message back; guarded, the
        // marker itself comes back untouched.
        IReadOnlyList<string>? fromServer = await server.DisplayMessageAsync(
            new DisplayMessageRequest { Message = DashMarker, ReturnText = true, NoExpand = true },
            token);
        Assert.NotNull(fromServer);
        Assert.Contains(DashMarker, fromServer);

        // Window.DisplayMessageAsync builds its own argv independently.
        IReadOnlyList<string>? fromWindow = await scope.Window.DisplayMessageAsync(
            new DisplayMessageRequest { Message = DashMarker, ReturnText = true, NoExpand = true },
            token);
        Assert.NotNull(fromWindow);
        Assert.Contains(DashMarker, fromWindow);

        // Pane.DisplayMessageAsync builds its own argv independently too.
        IReadOnlyList<string>? fromPane = await scope.Pane.DisplayMessageAsync(
            new DisplayMessageRequest { Message = DashMarker, ReturnText = true, NoExpand = true },
            token);
        Assert.NotNull(fromPane);
        Assert.Contains(DashMarker, fromPane);

        // pipe-pane's command is a bare positional; unguarded, "-zqamarker"
        // is refused as a flag before anything is piped.
        await scope.Pane.PipeAsync(new PipePaneRequest { Command = DashMarker }, token);
        await scope.Pane.PipeAsync(cancellationToken: token);

        // find-window's pattern is a bare positional too.
        await scope.Pane.FindWindowAsync(new FindWindowRequest(DashMarker), token);

        // display-popup needs a client to actually open, so both the broken
        // and the fixed call fail - but at different layers. Unguarded, tmux
        // refuses before it ever asks whether a client is attached.
        TmuxCommandException popupFailure = await Assert.ThrowsAsync<TmuxCommandException>(
            () => scope.Pane.DisplayPopupAsync(
                new DisplayPopupRequest { Command = DashMarker },
                token));
        Assert.Contains("no current client", popupFailure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [UnixFact]
    public async Task Key_bindings_reach_key_name_validation_not_the_flag_parser()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TmuxTestFactory factory = new();
        // A server with no session exits on its own almost immediately, so a
        // real session keeps it alive for the whole test.
        await using TemporarySessionScope scope = await factory.CreateSessionAsync(
            cancellationToken: token);
        Server server = scope.Session.Server;

        // Unguarded, "bind-key -zqamarker command" is refused as an unknown
        // flag. Guarded, tmux reaches its own key-name table and refuses
        // "-zqamarker" for a different, later reason: it is not a key.
        TmuxCommandException bindFailure = await Assert.ThrowsAsync<TmuxCommandException>(
            () => server.Keys.BindAsync(
                new BindKeyRequest(DashMarker, ["display-message", "bound"]),
                token));
        Assert.Contains("unknown key", bindFailure.Message, StringComparison.OrdinalIgnoreCase);

        TmuxCommandException unbindFailure = await Assert.ThrowsAsync<TmuxCommandException>(
            () => server.Keys.UnbindAsync(new UnbindKeyRequest { Key = DashMarker }, token));
        Assert.Contains("unknown key", unbindFailure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [UnixFact]
    public async Task Interactive_prompts_reach_the_client_check_not_the_flag_parser()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TmuxTestFactory factory = new();
        await using TemporarySessionScope scope = await factory.CreateSessionAsync(
            cancellationToken: token);
        Server server = scope.Session.Server;

        // None of these have an attached client in this test, so every
        // guarded call still fails - but with "no current client" rather
        // than tmux's own "unknown flag", proving the value reached the
        // command's semantic layer intact.
        TmuxCommandException promptFailure = await Assert.ThrowsAsync<TmuxCommandException>(
            () => server.ShowCommandPromptAsync(new CommandPromptRequest(DashMarker), token));
        Assert.Contains("no current client", promptFailure.Message, StringComparison.OrdinalIgnoreCase);

        TmuxCommandException confirmFailure = await Assert.ThrowsAsync<TmuxCommandException>(
            () => server.ConfirmBeforeAsync(new ConfirmBeforeRequest([DashMarker]), token));
        Assert.Contains("no current client", confirmFailure.Message, StringComparison.OrdinalIgnoreCase);

        TmuxCommandException menuFailure = await Assert.ThrowsAsync<TmuxCommandException>(
            () => server.ShowMenuAsync(
                new DisplayMenuRequest([new TmuxMenuItem(DashMarker, "a", "display-message x")]),
                token));
        Assert.Contains("no current client", menuFailure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [UnixFact]
    public async Task Options_and_hooks_reach_name_validation_not_the_flag_parser()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            cancellationToken: token);
        TmuxOptions options = scope.Server.Options;
        TmuxHooks hooks = scope.Server.Hooks;

        // Unguarded, "set-option -g -zqamarker value" is refused as an
        // unknown flag. Guarded, tmux reaches its own option table and
        // refuses "-zqamarker" for a different, later reason: it names no
        // option.
        TmuxOptionException setFailure = await Assert.ThrowsAsync<TmuxOptionException>(
            () => options.SetAsync(
                new SetOptionRequest(DashMarker, "value") { Global = true },
                token));
        Assert.Contains("invalid option", setFailure.Message, StringComparison.OrdinalIgnoreCase);

        TmuxOptionException unsetFailure = await Assert.ThrowsAsync<TmuxOptionException>(
            () => options.UnsetAsync(
                new UnsetOptionRequest(DashMarker) { Global = true },
                token));
        Assert.Contains("invalid option", unsetFailure.Message, StringComparison.OrdinalIgnoreCase);

        // set-hook's single-entry path (TmuxHooks.BuildSetArguments).
        TmuxOptionException hookSetFailure = await Assert.ThrowsAsync<TmuxOptionException>(
            () => hooks.SetAsync(
                new SetHookRequest(DashMarker, "display-message x") { Global = true },
                token));
        Assert.Contains("invalid option", hookSetFailure.Message, StringComparison.OrdinalIgnoreCase);

        // set-hook's multi-entry path (TmuxHooks.SetAsync(SetHooksRequest))
        // builds its own argv independently and bypasses BuildSetAllArguments.
        TmuxOptionException hookSetAllFailure = await Assert.ThrowsAsync<TmuxOptionException>(
            () => hooks.SetAsync(
                new SetHooksRequest(DashMarker, new Dictionary<int, string> { [0] = "display-message x" })
                {
                    Global = true,
                },
                token));
        Assert.Contains("invalid option", hookSetAllFailure.Message, StringComparison.OrdinalIgnoreCase);

        // run-hook (TmuxHooks.BuildRunArguments). Running a hook with no
        // entries is a no-op tmux accepts for any name, so the guarded call
        // succeeds outright rather than failing at a later check.
        await AssertNotRejectedAsFlagAsync(
            () => hooks.RunAsync(new HookRequest(DashMarker) { Global = true }, token));

        // TmuxHooks.UnsetAsync(HookRequest) builds its own argv independently
        // and bypasses BuildUnsetArguments.
        TmuxOptionException hookUnsetFailure = await Assert.ThrowsAsync<TmuxOptionException>(
            () => hooks.UnsetAsync(new HookRequest(DashMarker) { Global = true }, token));
        Assert.Contains("invalid option", hookUnsetFailure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [UnixFact]
    public async Task Server_administration_commands_reach_their_own_validation()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TmuxTestFactory factory = new();
        await using TemporarySessionScope scope = await factory.CreateSessionAsync(
            cancellationToken: token);
        Server server = scope.Session.Server;

        // list-commands' name filter. tmux 3.6 added validating it against
        // the command table ("unknown command"); older releases just answer
        // nothing for a name matching no command, so only the dispatch -
        // never "unknown flag" - is asserted here.
        await AssertNotRejectedAsFlagAsync(() => server.GetCommandsAsync(DashMarker, token));

        // source-file's path. Guarded, tmux reaches the filesystem and
        // reports the marker was not found there, rather than refusing to
        // parse the command at all.
        TmuxCommandException sourceFailure = await Assert.ThrowsAsync<TmuxCommandException>(
            () => server.SourceFileAsync(DashMarker, cancellationToken: token));
        Assert.Contains("no such file", sourceFailure.Message, StringComparison.OrdinalIgnoreCase);

        // server-access needs tmux 3.3; older lanes in the matrix skip it
        // rather than fail on an unrelated version gate.
        if (server.Version is { } version && version >= TmuxVersion.Parse("3.3"))
        {
            TmuxCommandException accessFailure = await Assert.ThrowsAsync<TmuxCommandException>(
                () => server.ConfigureAccessAsync(
                    new ServerAccessRequest { AllowUser = DashMarker },
                    token));
            Assert.Contains("unknown user", accessFailure.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [UnixFact]
    public async Task Environment_and_buffers_store_a_flag_shaped_value_verbatim()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TmuxTestFactory factory = new();
        await using TemporarySessionScope scope = await factory.CreateSessionAsync(
            cancellationToken: token);
        Server server = scope.Session.Server;

        // set-environment's name. tmux's own show-environment output cannot
        // be read back exactly for a name starting with '-' - that spelling
        // is how it marks a removed variable - so this proves only the
        // dispatch, the same as the creation family above.
        await AssertNotRejectedAsFlagAsync(
            () => server.Environment.SetAsync(DashMarker, "value", cancellationToken: token));
        await AssertNotRejectedAsFlagAsync(() => server.Environment.RemoveAsync(DashMarker, token));
        await AssertNotRejectedAsFlagAsync(() => server.Environment.UnsetAsync(DashMarker, token));

        // set-buffer's data - arbitrary content, not a name, so this is the
        // clearest case: a real payload that merely starts with '-'.
        await server.Buffers.SetAsync(DashMarker, "dash-guard-buffer", cancellationToken: token);
        string buffer = await server.Buffers.GetAsync("dash-guard-buffer", token);
        Assert.Equal(DashMarker, buffer);

        string tempFile = Path.Combine(
            Directory.CreateTempSubdirectory("libtmux-dash-guard-").FullName,
            "buffer.txt");
        try
        {
            await server.Buffers.SaveAsync(tempFile, "dash-guard-buffer", cancellationToken: token);
            Assert.Equal(DashMarker, await File.ReadAllTextAsync(tempFile, token));

            // load-buffer's path - the file itself is unrelated to the
            // guard; only the argv shape matters, so its own name is enough.
            await File.WriteAllTextAsync(tempFile, "loaded", token);
            await server.Buffers.LoadAsync(tempFile, "dash-guard-buffer", token);
            Assert.Equal("loaded", await server.Buffers.GetAsync("dash-guard-buffer", token));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(tempFile)!, recursive: true);
        }
    }

    /// <summary>
    /// Runs a creation call whose flag-shaped <c>Command</c> spawns nothing
    /// real, tolerating the pane, window, or session dying before this call
    /// reads it back. What must never appear is tmux's own parse refusal,
    /// which would mean the command was never dispatched at all.
    /// </summary>
    private static async Task AssertNotRejectedAsFlagAsync(Func<Task> dispatch)
    {
        Exception? error = await Record.ExceptionAsync(dispatch);
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            Assert.DoesNotContain("unknown flag", current.Message, StringComparison.OrdinalIgnoreCase);
        }
    }
}
