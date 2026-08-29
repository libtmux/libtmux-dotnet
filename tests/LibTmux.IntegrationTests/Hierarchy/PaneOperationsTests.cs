using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Internal;
using Microsoft.Extensions.Logging;

namespace LibTmux.IntegrationTests.Hierarchy;

[UnsupportedOSPlatform("windows")]
public sealed class PaneOperationsTests
{
    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task Send_keys_and_capture_preserve_literal_payloads()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Pane pane = await FirstPaneAsync(raw, token);

        // A trailing semicolon is what tmux would read as a command separator.
        // The transport already escapes it; escaping again would deliver a
        // backslash into the pane.
        await pane.SendKeysAsync(
            new SendKeysRequest("echo 'A;'", enter: false, literal: true),
            token);
        await pane.EnterAsync(token);
        Assert.Contains("A;", await ReadPaneAsync(pane, "A;", token), StringComparison.Ordinal);

        // Enter must be its own command: appended to the same literal send it
        // would type the five characters of the key's name.
        await pane.SendTextAsync("echo LITERALPAYLOAD", cancellationToken: token);
        string afterText = await ReadPaneAsync(pane, "LITERALPAYLOAD", token);
        Assert.Contains("LITERALPAYLOAD", afterText, StringComparison.Ordinal);
        Assert.DoesNotContain("echo LITERALPAYLOADEnter", afterText, StringComparison.Ordinal);

        await pane.SendTextAsync("Enter", enter: false, token);
        Assert.Contains("Enter", await ReadPaneAsync(pane, "Enter", token), StringComparison.Ordinal);
        await pane.SendKeysAsync(new SendKeysRequest("C-u"), token);

        // Keeping a line out of shell history is a leading space, not a flag.
        await pane.SendKeysAsync(
            new SendKeysRequest("echo hidden", suppressHistory: true, enter: false),
            token);
        Assert.Contains(
            " echo hidden",
            await ReadPaneAsync(pane, " echo hidden", token),
            StringComparison.Ordinal);
        await pane.EnterAsync(token);

        // The extremes render as a lone hyphen in their own token, so asking
        // for the whole history returns more than the visible pane alone.
        IReadOnlyList<string> visible = await pane.CaptureAsync(cancellationToken: token);
        IReadOnlyList<string> everything = await pane.CaptureAsync(
            new CapturePaneRequest(
                startLine: CapturePanePosition.BeginningOfHistory,
                endLine: CapturePanePosition.EndOfVisiblePane),
            token);
        Assert.True(
            everything.Count >= visible.Count,
            $"history {everything.Count} < visible {visible.Count}");
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task Capture_flags_emit_exact_argv_and_preserve_positions()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Pane pane = await FirstPaneAsync(raw, token);

        // A pane with no alternate screen refuses the request unless the
        // caller says a missing one is acceptable.
        await Assert.ThrowsAsync<TmuxCommandException>(
            () => pane.CaptureAsync(new CapturePaneRequest(alternateScreen: true), token));
        IReadOnlyList<string> quiet = await pane.CaptureAsync(
            new CapturePaneRequest(alternateScreen: true, quiet: true),
            token);
        Assert.Empty(quiet);

        // A numbered position rides its own token, so a capture bounded at the
        // top of the visible pane still reaches content written into it.
        await pane.SendTextAsync("echo CAPTUREMARKER", cancellationToken: token);
        string marked = await ReadPaneAsync(pane, "CAPTUREMARKER", token);
        Assert.Contains("CAPTUREMARKER", marked, StringComparison.Ordinal);
        IReadOnlyList<string> bounded = await pane.CaptureAsync(
            new CapturePaneRequest(startLine: new CapturePanePosition(0)),
            token);
        Assert.Contains(
            "CAPTUREMARKER",
            string.Join('\n', bounded),
            StringComparison.Ordinal);
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task Send_keys_flags_distinguish_literal_and_key_modes()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Pane pane = await FirstPaneAsync(raw, token);

        // A request that sends nothing is a caller mistake, not a tmux one.
        // The request itself is inert; the refusal belongs where it dispatches.
        await Assert.ThrowsAsync<ArgumentException>(
            () => pane.SendKeysAsync(new SendKeysRequest(), token));

        // A pane in no mode makes tmux itself refuse the copy-mode command;
        // this library does not pre-check pane mode before sending it.
        await Assert.ThrowsAsync<TmuxCommandException>(
            () => pane.SendKeysAsync(new SendKeysRequest(copyModeCommand: "cancel"), token));

        await pane.EnterCopyModeAsync(cancellationToken: token);
        Assert.Equal("copy-mode", await FormatAsync(pane, "#{pane_mode}", token));
        await pane.SendKeysAsync(new SendKeysRequest(copyModeCommand: "cancel"), token);
        Assert.NotEqual("copy-mode", await FormatAsync(pane, "#{pane_mode}", token));
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task Select_direction_last_keep_zoom_mark_and_input_flags_emit_exact_argv()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Pane pane = await FirstPaneAsync(raw, token);
        Pane second = await pane.SplitAsync(cancellationToken: token);

        Pane selected = await second.SelectAsync(cancellationToken: token);
        Assert.Equal("1", await FormatAsync(selected, "#{pane_active}", token));

        await second.SelectAsync(new SelectPaneRequest(mark: true), token);
        Assert.Equal("1", await FormatAsync(second, "#{pane_marked}", token));
        await second.SelectAsync(new SelectPaneRequest(mark: false), token);
        Assert.Equal("0", await FormatAsync(second, "#{pane_marked}", token));

        await second.SelectAsync(new SelectPaneRequest(inputEnabled: false), token);
        Assert.Equal("1", await FormatAsync(second, "#{pane_input_off}", token));
        await second.SelectAsync(new SelectPaneRequest(inputEnabled: true), token);
        Assert.Equal("0", await FormatAsync(second, "#{pane_input_off}", token));

        // Both spellings of "the last pane" collapse to one flag; sending it
        // twice would be a tmux usage error.
        await second.SelectAsync(
            new SelectPaneRequest(direction: PaneSelectDirection.Last, last: true),
            token);
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task New_split_move_join_paste_display_clear_and_break_flags_emit_exact_argv()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        Session session = await TestHierarchy.RequireFirstSessionAsync(server, token);
        Window window = await TestHierarchy.RequireFirstWindowAsync(session, token);
        Pane pane = await TestHierarchy.RequireFirstPaneAsync(window, token);

        // A pane identifier already names a pane; composing it with a
        // sub-target would ask tmux for a window that does not exist.
        Pane explicitTarget = await pane.SplitAsync(
            new SplitPaneRequest(target: pane.Id.ToString()),
            token);
        Assert.Equal(2, (await window.GetPanesAsync(token)).Count);

        // A percentage rides the size flag on every lane, because the
        // percentage flag itself is broken from 3.4 through 3.6.
        Window other = await session.CreateWindowAsync(new NewWindowRequest(name: "target"), token);
        await explicitTarget.MoveAsync(
            new MovePaneRequest(other.Id.ToString(), size: "30%"),
            token);
        Assert.Equal(2, (await other.GetPanesAsync(token)).Count);

        await Assert.ThrowsAsync<TmuxCommandException>(
            () => pane.PasteBufferAsync(new PasteBufferRequest("no-such-buffer"), token));

        // Clearing empties the scrollback but does not stop the pane's shell
        // writing to it, so a prompt drawn just after the clear leaves a line
        // behind. Clearing until it holds is what settles.
        await WaitForClearedHistoryAsync(pane, token);

        Pane spare = await pane.SplitAsync(cancellationToken: token);
        Window broken = await spare.BreakAsync("named", cancellationToken: token);
        Assert.Equal("named", broken.Name);
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task Copy_find_pipe_swap_resize_and_respawn_flags_emit_exact_argv()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Pane pane = await FirstPaneAsync(raw, token);

        await pane.EnterCopyModeAsync(cancellationToken: token);
        Assert.Equal("copy-mode", await FormatAsync(pane, "#{pane_mode}", token));
        await pane.EnterCopyModeAsync(new CopyModeRequest(cancel: true), token);
        Assert.NotEqual("copy-mode", await FormatAsync(pane, "#{pane_mode}", token));

        // Without a real mouse event tmux accepts the request and enters no
        // mode, so the absence is the assertion.
        await pane.EnterCopyModeAsync(new CopyModeRequest(mouseDrag: true), token);
        Assert.NotEqual("copy-mode", await FormatAsync(pane, "#{pane_mode}", token));

        await pane.PipeAsync(new PipePaneRequest("cat > /dev/null"), token);
        Assert.Equal("1", await FormatAsync(pane, "#{pane_pipe}", token));
        await pane.PipeAsync(cancellationToken: token);
        Assert.Equal("0", await FormatAsync(pane, "#{pane_pipe}", token));

        // tmux replaces a named source whenever a direction is given, so the
        // request refuses the pair rather than letting the name vanish.
        Assert.Throws<ArgumentException>(
            () => new SwapPaneRequest("%1", PaneSwapDirection.Up));
        Assert.Throws<ArgumentException>(() => new SwapPaneRequest());

        // tmux applies several sizing instructions and discards the losers.
        Assert.Throws<ArgumentException>(() => new ResizePaneRequest());
        Assert.Throws<ArgumentException>(
            () => new ResizePaneRequest(width: "20", zoom: true));

        Pane resized = await pane.SetWidthAsync(30, token);
        Assert.True(resized.Width > 0);

        await Assert.ThrowsAsync<TmuxCommandException>(
            () => pane.RespawnAsync(cancellationToken: token));
        await pane.RespawnAsync(new RespawnRequest(killExistingProcess: true), token);
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task Popup_menu_and_display_flags_emit_exact_argv()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Pane pane = await FirstPaneAsync(raw, token);

        // Both overlays need an attached client, which this test process lacks,
        // so only the exception type is asserted, not tmux's refusal text.
        await Assert.ThrowsAsync<TmuxCommandException>(
            () => pane.DisplayPopupAsync(cancellationToken: token));
        await Assert.ThrowsAsync<TmuxCommandException>(
            () => pane.DisplayPaneNumbersAsync(cancellationToken: token));

        IReadOnlyList<string>? printed = await pane.DisplayMessageAsync(
            new DisplayMessageRequest("#{pane_id}", returnText: true),
            token);
        Assert.Equal(pane.Id.ToString(), Assert.Single(printed!));

        await pane.ChooseBufferAsync(token);
        await pane.ChooseClientAsync(token);
        await pane.FindWindowAsync(new FindWindowRequest("nothing-matches"), token);
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public Task CapturePaneTrimTrailingVersionPolicy() =>
        GatedAsync(
            "capture_pane_trim_trailing",
            (pane, token) => pane.CaptureAsync(
                new CapturePaneRequest(trimTrailingSpaces: true),
                token));

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public Task ChooseTreeSortTimeVersionPolicy() =>
        GatedAsync(
            "choose_tree_sort_time",
            (pane, token) => pane.ChooseTreeAsync(
                new ChooseTreeRequest(sort: ChooseTreeSort.Time),
                token));

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public Task CapturePaneModeScreenVersionPolicy() =>
        GatedAsync(
            "capture_pane_mode_screen",
            (pane, token) => pane.CaptureAsync(new CapturePaneRequest(modeScreen: true), token));

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public Task CapturePane37MetadataVersionPolicy() =>
        GatedAsync(
            "capture_pane_3_7_metadata",
            (pane, token) => pane.CaptureAsync(
                new CapturePaneRequest(hyperlinks: true, lineNumbers: true, lineFlags: true),
                token));

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public Task ClearHistoryHyperlinksVersionPolicy() =>
        GatedAsync(
            "clear_history_hyperlinks",
            (pane, token) => pane.ClearHistoryAsync(resetHyperlinks: true, cancellationToken: token));

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public Task CopyModePageDownVersionPolicy() =>
        GatedAsync(
            "copy_mode_page_down",
            (pane, token) => pane.EnterCopyModeAsync(new CopyModeRequest(pageDown: true), token));

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public Task DisplayMessageLiteralVersionPolicy() =>
        GatedAsync(
            "display_message_literal",
            (pane, token) => pane.DisplayMessageAsync(
                new DisplayMessageRequest("#{pane_id}", returnText: true, noExpand: true),
                token));

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public Task DisplayMessageUpdatePaneVersionPolicy() =>
        GatedAsync(
            "display_message_update_pane",
            (pane, token) => pane.DisplayMessageAsync(
                new DisplayMessageRequest("x", returnText: true, updatePane: true),
                token));

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public Task PasteBufferNoVisVersionPolicy() =>
        GatedAsync(
            "paste_buffer_no_vis",
            async (pane, token) =>
            {
                await pane.Server.ExecuteCommandAsync(
                    ["set-buffer", "-b", "policy", "x"],
                    token);
                await pane.PasteBufferAsync(
                    new PasteBufferRequest("policy", rawBytes: true),
                    token);
            });

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public Task SendKeysClientKeysVersionPolicy() =>
        GatedAsync(
            "send_keys_client_keys",
            // Enter would make this two dispatches on both branches, which the
            // policy's single-dispatch boundary forbids.
            (pane, token) => pane.SendKeysAsync(
                new SendKeysRequest("x", enter: false, keyName: true),
                token));

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public Task SplitWindowEmptyVersionPolicy() =>
        GatedAsync(
            "split_window_empty",
            (pane, token) => pane.SplitAsync(new SplitPaneRequest(empty: true), token));

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public Task SplitWindowAppearanceVersionPolicy() =>
        GatedAsync(
            "split_window_appearance",
            (pane, token) => pane.SplitAsync(new SplitPaneRequest(style: "fg=red"), token));

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public Task DisplayPopup33OptionsVersionPolicy() =>
        // A popup needs a client, so both branches end in tmux's refusal; the
        // gate is proved by whether a warning was logged before dispatch.
        GatedAsync(
            "display_popup_3_3_options",
            (pane, token) => Assert.ThrowsAsync<TmuxCommandException>(
                () => pane.DisplayPopupAsync(new DisplayPopupRequest(title: "t"), token)));

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public Task DisplayPopup36KeyPolicyVersionPolicy() =>
        GatedAsync(
            "display_popup_3_6_key_policy",
            (pane, token) => Assert.ThrowsAsync<TmuxCommandException>(
                () => pane.DisplayPopupAsync(new DisplayPopupRequest(noKeys: true), token)));

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task NewPaneCommandVersionPolicy()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        RecordingLogger logger = new();
        Server server = await ConnectAsync(raw, token, logger);
        Pane pane = await FirstPaneAsync(server, token);
        bool supported = TmuxCapabilities.IsSupported(
            server.Version!.Value,
            "new_pane_command");

        if (supported)
        {
            Pane created = await pane.CreatePaneAsync(cancellationToken: token);
            Assert.NotEqual(pane.Id, created.Id);
        }
        else
        {
            // The command does not exist below 3.7, so there is nothing to omit
            // and nothing worth dispatching: a typed refusal, and no warning.
            await Assert.ThrowsAsync<TmuxVersionTooLowException>(
                () => pane.CreatePaneAsync(cancellationToken: token));
        }

        Assert.Empty(logger.Warnings);
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task BreakPane37WorkaroundVersionPolicy()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        Pane pane = await FirstPaneAsync(server, token);

        // tmux 3.7 alone dereferences a null window name here and crashes the
        // whole server, so that version always gets a placeholder name.
        bool workaround = TmuxCapabilities.IsSupported(
            server.Version!.Value,
            "break_pane_3_7_workaround");

        Pane named = await pane.SplitAsync(cancellationToken: token);
        Assert.Equal("wanted", (await named.BreakAsync("wanted", cancellationToken: token)).Name);

        Pane anonymous = await pane.SplitAsync(cancellationToken: token);
        Window unnamed = await anonymous.BreakAsync(cancellationToken: token);
        Assert.NotEmpty(await server.GetSessionsAsync(token));

        // On the affected version tmux discards the given name outright, so
        // the placeholder only stops the crash; the resulting name is tmux's own.
        Assert.NotEqual("wanted", unnamed.Name);
        Assert.True(workaround || unnamed.Name != "libtmux");
    }

    private static async Task GatedAsync(
        string capability,
        Func<Pane, CancellationToken, Task> operation)
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        RecordingLogger logger = new();
        Server server = await ConnectAsync(raw, token, logger);
        Pane pane = await FirstPaneAsync(server, token);
        bool supported = TmuxCapabilities.IsSupported(server.Version!.Value, capability);

        await operation(pane, token);

        if (supported)
        {
            // A gate that warned unconditionally, or one that sent a flag the
            // server lacks, would fail here rather than pass quietly.
            Assert.Empty(logger.Warnings);
        }
        else
        {
            Assert.Single(logger.Warnings);
        }
    }

    private static Task<Server> ConnectAsync(
        RawTmuxTestContext raw,
        CancellationToken token,
        ILogger? logger = null) =>
        Server.ConnectAsync(
            new ServerConnectionOptions(
                tmuxBinaryPath: raw.TmuxBinaryPath,
                socketPath: raw.SocketPath,
                configurationFile: "/dev/null",
                logger: logger),
            token);

    private static async Task<Pane> FirstPaneAsync(
        RawTmuxTestContext raw,
        CancellationToken token) =>
        await FirstPaneAsync(await ConnectAsync(raw, token), token);

    private static async Task<Pane> FirstPaneAsync(Server server, CancellationToken token)
    {
        Session session = await TestHierarchy.RequireFirstSessionAsync(server, token);
        Window window = await TestHierarchy.RequireFirstWindowAsync(session, token);
        return await TestHierarchy.RequireFirstPaneAsync(window, token);
    }

    private static async Task WaitForClearedHistoryAsync(Pane pane, CancellationToken token)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        string size = string.Empty;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await pane.ClearHistoryAsync(cancellationToken: token);
            size = await FormatAsync(pane, "#{history_size}", token);
            if (string.Equals(size, "0", StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), token);
        }

        Assert.Fail($"The pane's history settled at {size} rather than empty.");
    }

    private static async Task<string> FormatAsync(
        Pane pane,
        string format,
        CancellationToken token)
    {
        IReadOnlyList<string>? lines = await pane.DisplayMessageAsync(
            new DisplayMessageRequest(format, returnText: true),
            token);
        return lines is { Count: > 0 } ? lines[0] : string.Empty;
    }

    [UnixFact]
    public async Task Capture_joins_what_the_pane_wrapped()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Pane pane = await FirstPaneAsync(raw, token);

        // A prompt this wide leaves two columns, so the payload wraps. It is
        // built in the shell so the command that sets it cannot match it.
        string prompt = new('x', 78);
        await pane.SendTextAsync(
            "PS1=$(printf 'x%.0s' $(seq 78))",
            cancellationToken: token);
        await ReadPaneAsync(pane, prompt, token);

        await pane.SendKeysAsync(
            new SendKeysRequest("echo WRAPPED", enter: false),
            token);
        await ReadPaneAsync(pane, "WRAPPED", token);

        string split = string.Join(
            '\n',
            await pane.CaptureAsync(cancellationToken: token));
        string joined = string.Join(
            '\n',
            await pane.CaptureAsync(new CapturePaneRequest(joinWrappedLines: true), token));

        Assert.DoesNotContain($"{prompt}echo WRAPPED", split, StringComparison.Ordinal);
        Assert.Contains($"{prompt}echo WRAPPED", joined, StringComparison.Ordinal);
    }

    private static async Task<string> ReadPaneAsync(
        Pane pane,
        string expected,
        CancellationToken token)
    {
        // Joined: a wide prompt leaves typed text split across two stored
        // lines, since tmux stores the wrap as a real line break.
        //
        // The generous budget covers a loaded machine redrawing the prompt;
        // timing out throws instead of returning stale text, so a failure
        // here reads as a timeout, not a wrong content assertion.
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(45);
        string text = string.Empty;
        while (DateTimeOffset.UtcNow < deadline)
        {
            text = string.Join(
                '\n',
                await pane.CaptureAsync(new CapturePaneRequest(joinWrappedLines: true), token));
            if (text.Contains(expected, StringComparison.Ordinal))
            {
                return text;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), token);
        }

        throw new TimeoutException(
            $"The pane never showed '{expected}' within 45s. It last held:\n{text}");
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly List<string> _warnings = [];

        public IReadOnlyList<string> Warnings => _warnings;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            // The dispatcher logs command failures at error level; these tests
            // only care about the warning a dropped flag produces.
            if (logLevel == LogLevel.Warning)
            {
                _warnings.Add(formatter(state, exception));
            }
        }
    }
}
