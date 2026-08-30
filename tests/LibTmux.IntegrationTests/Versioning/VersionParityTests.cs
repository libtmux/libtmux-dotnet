using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Internal;

namespace LibTmux.IntegrationTests.Versioning;

// A terminal is attached and keys are typed, so timing is part of what these
// proofs assert; running them against each other starves the waits under test.
[CollectionDefinition("Version policy proofs", DisableParallelization = true)]
public sealed class VersionPolicyProofs;

[Collection("Version policy proofs")]
[UnsupportedOSPlatform("windows")]
public sealed class VersionParityTests
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);
    private static readonly string[] ClipboardModes = ["off", "external", "on", "buffer"];
    private static readonly string[] TransitionFrameworks = ["net8.0", "net10.0"];
    private static readonly string[] TransitionVersions = ["3.7", "3.7a"];
    private static readonly Gate[] Gates =
    [
        new("break_pane_3_7_workaround", "break-pane", ["-n"]),
        new("capture_pane_3_7_metadata", "capture-pane", ["-H", "-L", "-F"]),
        new("capture_pane_mode_screen", "capture-pane", ["-M"]),
        new("capture_pane_trim_trailing", "capture-pane", ["-T"]),
        new("clear_history_hyperlinks", "clear-history", ["-H"]),
        new("choose_tree_sort_time", "choose-tree", ["-O"]),
        new("clear_prompt_history_command", "clear-prompt-history", []),
        new("command_prompt_3_7_behavior", "command-prompt", ["-e", "-C"]),
        new("command_prompt_background", "command-prompt", ["-b", "-F"]),
        new("command_prompt_literal", "command-prompt", ["-l"]),
        new("confirm_before_acceptance", "confirm-before", ["-c", "-y"]),
        new("confirm_before_background", "confirm-before", ["-b"]),
        new("copy_mode_page_down", "copy-mode", ["-d"]),
        new("display_menu_mouse", "display-menu", ["-M"]),
        new("display_menu_styles", "display-menu", ["-C", "-b", "-s", "-S", "-H"]),
        new("display_message_client", "display-message", ["-c"]),
        new("display_message_literal", "display-message", ["-l"]),
        new("display_message_update_pane", "display-message", ["-C"]),
        new("display_popup_3_3_options", "display-popup", ["-T", "-b", "-s", "-S", "-e", "-B"]),
        new("display_popup_3_6_key_policy", "display-popup", ["-k", "-N"]),
        new("hook_scope_pane_window_set", "set-hook", ["-p", "-w"]),
        new("hook_scope_pane_window_show", "show-hooks", ["-p", "-w"]),
        new("kill_session_group", "kill-session", ["-g"]),
        new("list_keys_format", "list-keys", ["-F"]),
        new("new_pane_command", "new-pane", []),
        new("paste_buffer_no_vis", "paste-buffer", ["-S"]),
        new("refresh_client_clipboard_query", "refresh-client", ["-l"]),
        new("run_shell_arguments", "run-shell", [], PositionalArguments: true),
        new("run_shell_show_stderr", "run-shell", ["-E"]),
        new("run_shell_working_directory", "run-shell", ["-c"]),
        new("send_keys_client_keys", "send-keys", ["-K", "-c"]),
        new("server_access_command", "server-access", []),
        new("show_prompt_history_command", "show-prompt-history", []),
        new("split_window_appearance", "split-window", ["-s", "-S", "-R", "-m", "-k"]),
        new("split_window_empty", "split-window", ["-E"]),
    ];

    [UnixFact]
    public async Task AttachmentAccounting()
    {
        await using RawTmuxTestContext context = await StartAsync();
        TmuxVersion version = await GetVersionAsync(context);
        Assert.True(TmuxCapabilities.IsSupported(version, "attachment_accounting"));

        RawTmuxResult before = await ExecuteAsync(
            context,
            ["list-clients", "-F", "#{client_tty}"]);
        Assert.Equal(0, before.ExitCode);
        Assert.Empty(before.StandardOutputLines);

        await using (PtyAttachedClientScope client = await PtyAttachedClientScope.StartAsync(
            context,
            TestContext.Current.CancellationToken))
        {
            RawTmuxResult attached = await ExecuteAsync(
                context,
                ["list-clients", "-F", "#{client_tty}:#{client_control_mode}"]);
            Assert.Equal(0, attached.ExitCode);
            Assert.Contains($"{client.Tty}:0", attached.StandardOutputLines);
        }

        RawTmuxResult detached = await ExecuteAsync(
            context,
            ["list-clients", "-F", "#{client_tty}"]);
        Assert.Equal(0, detached.ExitCode);
        Assert.Empty(detached.StandardOutputLines);
        await WriteProtocolTranscriptAsync(
            "pty.txt",
            [
                "event=pty-attach state=visible",
                "event=pty-detach state=gone",
            ]);
    }

    [UnixFact]
    public async Task ByteLengthFraming()
    {
        await using RawTmuxTestContext context = await StartAsync();
        TmuxVersion version = await GetVersionAsync(context);
        Assert.True(TmuxCapabilities.IsSupported(version, "byte_length_framing"));

        RawTmuxResult result = await ExecuteAsync(
            context,
            ["display-message", "-p", "é"]);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("é\n"u8.ToArray(), result.StandardOutput);
        Assert.Equal(3, result.StandardOutput.Length);
    }

    [UnixFact]
    public async Task ControlNotifications()
    {
        await using RawTmuxTestContext context = await StartAsync();
        TmuxVersion version = await GetVersionAsync(context);
        Assert.True(TmuxCapabilities.IsSupported(version, "control_notifications"));
        await using ControlModeClientScope client = await ControlModeClientScope.StartAsync(
            context,
            TestContext.Current.CancellationToken);

        const string command = "display-message -p libtmux-control-marker";
        await client.WriteLineAsync(command, TestContext.Current.CancellationToken);
        using CancellationTokenSource responseTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
        responseTimeout.CancelAfter(CommandTimeout);
        string begin = await ReadUntilAsync(client, "%begin ", responseTimeout.Token);
        string output = await ReadUntilAsync(
            client,
            "libtmux-control-marker",
            responseTimeout.Token);
        string end = await ReadUntilAsync(client, "%end ", responseTimeout.Token);
        Assert.StartsWith("%begin ", begin, StringComparison.Ordinal);
        Assert.Equal("libtmux-control-marker", output);
        Assert.StartsWith("%end ", end, StringComparison.Ordinal);

        RawTmuxResult created = await ExecuteAsync(
            context,
            ["new-window", "-d", "-t", context.SessionName]);
        Assert.Equal(0, created.ExitCode);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        string notification = await ReadUntilAsync(client, "%window-add ", timeout.Token);
        Assert.StartsWith("%window-add ", notification, StringComparison.Ordinal);
        await WriteProtocolTranscriptAsync(
            "control.txt",
            [
                $"event=control-send sequence=1 bytes={Encoding.UTF8.GetByteCount(command + "\n")}",
                "event=control-receive sequence=1 marker=%begin",
                "event=control-receive sequence=1 marker=%end",
            ]);
    }

    [UnixFact]
    public async Task FormatFieldsAndOperators()
    {
        await using RawTmuxTestContext context = await StartAsync();
        TmuxVersion version = await GetVersionAsync(context);
        Assert.True(TmuxCapabilities.IsSupported(version, "format_fields_and_operators"));

        RawTmuxResult result = await ExecuteAsync(
            context,
            [
                "display-message",
                "-p",
                $"#{{==:#{{session_name}},{context.SessionName}}}:#{{pane_id}}",
            ]);
        Assert.Equal(0, result.ExitCode);
        Assert.Single(result.StandardOutputLines);
        Assert.Matches("^1:%[0-9]+$", result.StandardOutputLines[0]);
    }

    [UnixFact]
    public async Task MissingTargetFormatSafety()
    {
        await using RawTmuxTestContext context = await StartAsync();
        TmuxVersion version = await GetVersionAsync(context);
        bool safe = TmuxCapabilities.IsSupported(version, "missing_target_format_safety");

        RawTmuxResult result = await ExecuteAsync(
            context,
            ["display-message", "-p", "-t", "%99999", "#{pane_bg}"]);

        if (version != TmuxVersion.Parse("3.2a"))
        {
            if (version.IsStableRelease)
            {
                Assert.True(safe);
            }

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("\n", result.StandardOutputText);
            Assert.Empty(result.StandardOutputLines);
            Assert.Empty(result.StandardErrorLines);
        }
        else
        {
            Assert.False(safe);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("server exited unexpectedly", result.StandardErrorText);
        }
    }

    [UnixFact]
    public async Task OptionDollarDoubleEscape()
    {
        await using RawTmuxTestContext context = await StartAsync();
        TmuxVersion version = await GetVersionAsync(context);
        bool doubled = TmuxCapabilities.IsSupported(version, "option_dollar_double_escape");

        RawTmuxResult stored = await ExecuteAsync(
            context,
            ["set-option", "-g", "@libtmux_dollar", "a$b"]);
        Assert.Equal(0, stored.ExitCode);

        // tmux 3.4 alone hands a stored dollar sign back escaped a second
        // time, so the version decides which of the two spellings is right
        // and the read is only ambiguous to a reader that ignores it.
        Assert.Equal(
            doubled ? @"a\$b" : "a$b",
            await ShowOptionAsync(context, "@libtmux_dollar"));
    }

    [UnixFact]
    public async Task SemicolonGrouping()
    {
        await using RawTmuxTestContext context = await StartAsync();
        TmuxVersion version = await GetVersionAsync(context);
        Assert.True(TmuxCapabilities.IsSupported(version, "semicolon_grouping"));

        RawTmuxResult grouped = await ExecuteAsync(
            context,
            [
                "set-option", "-g", "@libtmux_before", "yes", ";",
                "select-pane", "-t", "missing:0.0", ";",
                "set-option", "-g", "@libtmux_after", "yes",
            ]);
        Assert.NotEqual(0, grouped.ExitCode);
        Assert.Equal("yes", await ShowOptionAsync(context, "@libtmux_before"));
        Assert.Null(await ShowOptionAsync(context, "@libtmux_after"));
    }

    [UnixFact]
    public async Task BreakPane37Workaround()
    {
        await using RawTmuxTestContext context = await StartAsync();
        TmuxVersion version = await GetVersionAsync(context);
        bool workaround = TmuxCapabilities.IsSupported(
            version,
            "break_pane_3_7_workaround");
        string sourcePane = await SplitPaneAsync(context, []);
        List<string> arguments = ["break-pane", "-d", "-P", "-F", "#{window_name}"];
        if (workaround)
        {
            arguments.AddRange(["-n", "libtmux-transition"]);
        }

        arguments.AddRange(["-s", sourcePane]);
        RawTmuxResult result = await ExecuteAsync(context, arguments);
        Assert.Equal(0, result.ExitCode);
        Assert.Single(result.StandardOutputLines);
        Assert.False(string.IsNullOrWhiteSpace(result.StandardOutputLines[0]));
        Assert.NotEqual("libtmux-transition", result.StandardOutputLines[0]);

        await WriteTransitionRecordAsync(version, workaround);
    }

    [UnixFact]
    public Task CapturePane37Metadata() => ExerciseGateAsync("capture_pane_3_7_metadata");

    [UnixFact]
    public Task CapturePaneModeScreen() => ExerciseGateAsync("capture_pane_mode_screen");

    [UnixFact]
    public Task CapturePaneTrimTrailing() => ExerciseGateAsync("capture_pane_trim_trailing");

    [UnixFact]
    public Task ClearHistoryHyperlinks() => ExerciseGateAsync("clear_history_hyperlinks");

    [UnixFact]
    public Task ChooseTreeSortTime() => ExerciseGateAsync("choose_tree_sort_time");

    [UnixFact]
    public Task ClearPromptHistoryCommand() => ExerciseGateAsync("clear_prompt_history_command");

    [UnixFact]
    public Task CommandPrompt37Behavior() => ExerciseGateAsync("command_prompt_3_7_behavior");

    [UnixFact]
    public Task CommandPromptBackground() => ExerciseGateAsync("command_prompt_background");

    [UnixFact]
    public Task CommandPromptLiteral() => ExerciseGateAsync("command_prompt_literal");

    [UnixFact]
    public Task ConfirmBeforeAcceptance() => ExerciseGateAsync("confirm_before_acceptance");

    [UnixFact]
    public Task ConfirmBeforeBackground() => ExerciseGateAsync("confirm_before_background");

    [UnixFact]
    public Task CopyModePageDown() => ExerciseGateAsync("copy_mode_page_down");

    [UnixFact]
    public Task DisplayMenuMouse() => ExerciseGateAsync("display_menu_mouse");

    [UnixFact]
    public Task DisplayMenuStyles() => ExerciseGateAsync("display_menu_styles");

    [UnixFact]
    public Task DisplayMessageClient() => ExerciseGateAsync("display_message_client");

    [UnixFact]
    public Task DisplayMessageLiteral() => ExerciseGateAsync("display_message_literal");

    [UnixFact]
    public Task DisplayMessageUpdatePane() => ExerciseGateAsync("display_message_update_pane");

    [UnixFact]
    public Task DisplayPopup33Options() => ExerciseGateAsync("display_popup_3_3_options");

    [UnixFact]
    public Task DisplayPopup36KeyPolicy() => ExerciseGateAsync("display_popup_3_6_key_policy");

    [UnixFact]
    public Task HookScopePaneWindowSet() => ExerciseGateAsync("hook_scope_pane_window_set");

    [UnixFact]
    public Task HookScopePaneWindowShow() => ExerciseGateAsync("hook_scope_pane_window_show");

    [UnixFact]
    public Task KillSessionGroup() => ExerciseGateAsync("kill_session_group");

    [UnixFact]
    public Task ListKeysFormat() => ExerciseGateAsync("list_keys_format");

    [UnixFact]
    public Task NewPaneCommand() => ExerciseGateAsync("new_pane_command");

    [UnixFact]
    public Task PasteBufferNoVis() => ExerciseGateAsync("paste_buffer_no_vis");

    [UnixFact]
    public Task RefreshClientClipboardQuery() =>
        ExerciseGateAsync("refresh_client_clipboard_query");

    [UnixFact]
    public Task RunShellArguments() => ExerciseGateAsync("run_shell_arguments");

    [UnixFact]
    public Task RunShellShowStderr() => ExerciseGateAsync("run_shell_show_stderr");

    [UnixFact]
    public Task RunShellWorkingDirectory() => ExerciseGateAsync("run_shell_working_directory");

    [UnixFact]
    public Task SendKeysClientKeys() => ExerciseGateAsync("send_keys_client_keys");

    [UnixFact]
    public Task ServerAccessCommand() => ExerciseGateAsync("server_access_command");

    [UnixFact]
    public Task ShowPromptHistoryCommand() => ExerciseGateAsync("show_prompt_history_command");

    [UnixFact]
    public Task SplitWindowAppearance() => ExerciseGateAsync("split_window_appearance");

    [UnixFact]
    public Task SplitWindowEmpty() => ExerciseGateAsync("split_window_empty");

    [UnixFact]
    public async Task CommandFlags()
    {
        Assert.True(SyntaxSupportsFlag("example [-ab] [-t=client]", "-a"));
        Assert.False(SyntaxSupportsFlag("example [-ab] [-t=client]", "-c"));
        Assert.True(SyntaxSupportsFlag("command-prompt [-1CbeFiklN]", "-F"));
        await using RawTmuxTestContext context = await StartAsync();
        TmuxVersion version = await GetVersionAsync(context);
        foreach (Gate gate in Gates)
        {
            await AssertCommandSurfaceAsync(context, version, gate);
        }
    }

    private static async Task ExerciseGateAsync(string capability)
    {
        Gate gate = Gates.Single(
            candidate => string.Equals(
                candidate.Capability,
                capability,
                StringComparison.Ordinal));
        await using RawTmuxTestContext context = await StartAsync();
        TmuxVersion version = await GetVersionAsync(context);
        await AssertCommandSurfaceAsync(context, version, gate);
        if (TmuxCapabilities.IsSupported(version, capability))
        {
            await ExerciseSupportedBehaviorAsync(context, capability);
        }
    }

    private static async Task AssertCommandSurfaceAsync(
        RawTmuxTestContext context,
        TmuxVersion version,
        Gate gate)
    {
        RawTmuxResult syntax = await ExecuteAsync(
            context,
            ["list-commands", gate.Command]);
        bool isPresent;
        if (string.Equals(gate.Capability, "display_message_client", StringComparison.Ordinal))
        {
            // tmux 3.2a lists -c in its own usage text and then refuses the
            // command carrying it, so the usage string is not evidence and
            // only running it is.
            RawTmuxResult targeted = await ExecuteAsync(
                context,
                ["display-message", "-c", UnattachedClient, "-p", "gate"]);
            isPresent = targeted.ExitCode == 0;
        }
        else if (string.Equals(gate.Capability, "choose_tree_sort_time", StringComparison.Ordinal))
        {
            // Every supported tmux carries -O. What 3.7 dropped is one value
            // it accepts, so the usage text cannot tell them apart and only
            // offering the order can.
            RawTmuxResult sorted = await ExecuteAsync(
                context,
                ["choose-tree", "-t", TargetPane(context), "-O", "time"]);
            isPresent = !sorted.StandardErrorText.Contains(
                "invalid sort order",
                StringComparison.Ordinal);
        }
        else if (string.Equals(gate.Capability, "split_window_empty", StringComparison.Ordinal))
        {
            RawTmuxResult emptyPane = await ExecuteAsync(
                context,
                [
                    "split-window", "-d", "-P", "-F", "#{pane_id}", "-t",
                    TargetPane(context), "-E",
                ]);
            isPresent = emptyPane.ExitCode == 0;
        }
        else if (string.Equals(
            gate.Capability,
            "refresh_client_clipboard_query",
            StringComparison.Ordinal))
        {
            Assert.True(
                HasSingleNonemptyLine(syntax)
                && SyntaxSupportsFlag(syntax.StandardOutputLines[0], "-l"),
                $"tmux {version} must expose the historical refresh-client -l surface.");
            RawTmuxResult getClipboard = await ExecuteAsync(
                context,
                ["show-options", "-s", "-v", "get-clipboard"]);
            isPresent = HasSingleNonemptyLine(getClipboard);
        }
        else
        {
            isPresent = HasSingleNonemptyLine(syntax)
                && gate.Flags.All(flag => SyntaxSupportsFlag(syntax.StandardOutputLines[0], flag))
                && (!gate.PositionalArguments
                    || syntax.StandardOutputLines[0].Contains(
                        "[argument ...]",
                        StringComparison.Ordinal));
        }
        bool expected = string.Equals(
            gate.Capability,
            "break_pane_3_7_workaround",
            StringComparison.Ordinal)
            || TmuxCapabilities.IsSupported(version, gate.Capability);
        Assert.True(
            expected == isPresent,
            $"tmux {version} capability '{gate.Capability}' expected surface "
            + $"presence {expected}, observed {isPresent}: {syntax.StandardOutputText}");
    }

    private static async Task ExerciseSupportedBehaviorAsync(
        RawTmuxTestContext context,
        string capability)
    {
        switch (capability)
        {
            case "capture_pane_3_7_metadata":
                await SeedHyperlinkAsync(
                    context,
                    "https://example.invalid/libtmux",
                    "label");
                RawTmuxResult hyperlinks = await RequireSuccessAsync(
                    context,
                    ["capture-pane", "-p", "-H", "-S", "-", "-t", TargetPane(context)]);
                Assert.Contains(
                    "https://example.invalid/libtmux",
                    hyperlinks.StandardOutputText,
                    StringComparison.Ordinal);
                RawTmuxResult lineNumber = await RequireSuccessAsync(
                    context,
                    ["capture-pane", "-p", "-L", "-S", "0", "-E", "0", "-t", TargetPane(context)]);
                Assert.StartsWith("0 ", lineNumber.StandardOutputText, StringComparison.Ordinal);
                RawTmuxResult flags = await RequireSuccessAsync(
                    context,
                    ["capture-pane", "-p", "-F", "-S", "0", "-E", "0", "-t", TargetPane(context)]);
                Assert.Matches("^[DOPXH-]+ ", flags.StandardOutputText);
                break;
            case "capture_pane_mode_screen":
                await ExerciseCaptureModeScreenAsync(context);
                break;
            case "capture_pane_trim_trailing":
                await SeedPaneCommandAsync(
                    context,
                    "printf '\\033[2J\\033[HX'; sleep 2");

                // The shell reaching the sleep is what says the printf before
                // it has run. Reading the pane for the X it wrote would match
                // the echoed command line, which also contains one.
                await WaitForFormatAsync(
                    context,
                    TargetPane(context),
                    "#{pane_current_command}",
                    "sleep");
                RawTmuxResult untrimmed = await RequireSuccessAsync(
                    context,
                    ["capture-pane", "-p", "-N", "-S", "0", "-E", "0", "-t", TargetPane(context)]);
                RawTmuxResult trimmed = await RequireSuccessAsync(
                    context,
                    ["capture-pane", "-p", "-N", "-T", "-S", "0", "-E", "0", "-t", TargetPane(context)]);
                Assert.True(untrimmed.StandardOutput.Length > trimmed.StandardOutput.Length);
                Assert.Equal(["X"], trimmed.StandardOutputLines);
                break;
            case "clear_history_hyperlinks":
                await SeedHyperlinkAsync(
                    context,
                    "https://example.invalid/clear",
                    "linked-text");
                RawTmuxResult beforeClear = await RequireSuccessAsync(
                    context,
                    ["capture-pane", "-p", "-e", "-S", "-", "-t", TargetPane(context)]);
                Assert.Contains("https://example.invalid/clear", beforeClear.StandardOutputText);
                await RequireSuccessAsync(
                    context,
                    ["clear-history", "-H", "-t", TargetPane(context)]);
                RawTmuxResult afterClear = await RequireSuccessAsync(
                    context,
                    ["capture-pane", "-p", "-e", "-S", "-", "-t", TargetPane(context)]);
                Assert.DoesNotContain("https://example.invalid/clear", afterClear.StandardOutputText);
                RawTmuxResult visibleText = await RequireSuccessAsync(
                    context,
                    ["capture-pane", "-p", "-S", "-", "-t", TargetPane(context)]);
                Assert.Contains("linked-text", visibleText.StandardOutputText);
                break;
            case "clear_prompt_history_command":
                await ExercisePromptHistoryAsync(context, clear: true);
                break;
            case "command_prompt_3_7_behavior":
                await ExerciseCommandPrompt37Async(context);
                break;
            case "command_prompt_background":
                await ExerciseCommandPromptBackgroundAsync(context);
                break;
            case "command_prompt_literal":
                await ExerciseLiteralPromptAsync(context);
                break;
            case "confirm_before_acceptance":
                await ExerciseConfirmAcceptanceAsync(context);
                break;
            case "confirm_before_background":
                await ExerciseConfirmBackgroundAsync(context);
                break;
            case "copy_mode_page_down":
                await SeedScrollbackAsync(context);
                await RequireSuccessAsync(context, ["copy-mode", "-u", "-t", TargetPane(context)]);
                int beforePageDown = await DisplayIntegerAsync(context, "#{scroll_position}");
                Assert.True(beforePageDown > 0);
                await RequireSuccessAsync(context, ["copy-mode", "-d", "-t", TargetPane(context)]);
                int afterPageDown = await DisplayIntegerAsync(context, "#{scroll_position}");
                Assert.True(afterPageDown < beforePageDown);
                break;
            case "display_menu_mouse":
                await ExerciseMenuMouseAsync(context);
                break;
            case "display_menu_styles":
                await ExerciseMenuStylesAsync(context);
                break;
            case "display_message_literal":
                {
                    RawTmuxResult literal = await RequireSuccessAsync(
                        context,
                        ["display-message", "-p", "-l", "#{pane_id}"]);
                    Assert.Equal(["#{pane_id}"], literal.StandardOutputLines);
                    break;
                }
            case "display_message_update_pane":
                await ExerciseDisplayMessageUpdateAsync(context);
                break;
            case "display_popup_3_3_options":
                await ExercisePopupOptionsAsync(context);
                break;
            case "display_popup_3_6_key_policy":
                await ExercisePopupKeyPolicyAsync(context);
                break;
            case "hook_scope_pane_window_set":
                await ExerciseHookScopesAsync(context);
                break;
            case "hook_scope_pane_window_show":
                await ExerciseHookScopesAsync(context);
                break;
            case "kill_session_group":
                await RequireSuccessAsync(
                    context,
                    ["new-session", "-d", "-s", "libtmux-group-seed"]);
                await RequireSuccessAsync(
                    context,
                    [
                        "new-session", "-d", "-t", "libtmux-group-seed",
                        "-s", "libtmux-group-peer",
                    ]);
                await RequireSuccessAsync(
                    context,
                    ["kill-session", "-g", "-t", "libtmux-group-peer"]);
                RawTmuxResult sessions = await RequireSuccessAsync(
                    context,
                    ["list-sessions", "-F", "#{session_name}"]);
                Assert.Contains(context.SessionName, sessions.StandardOutputLines);
                Assert.DoesNotContain("libtmux-group-seed", sessions.StandardOutputLines);
                Assert.DoesNotContain("libtmux-group-peer", sessions.StandardOutputLines);
                break;
            case "list_keys_format":
                {
                    RawTmuxResult keys = await RequireSuccessAsync(
                        context,
                        ["list-keys", "-F", "libtmux-format-marker"]);
                    Assert.NotEmpty(keys.StandardOutputLines);
                    Assert.All(
                        keys.StandardOutputLines,
                        static line => Assert.Equal("libtmux-format-marker", line));
                    break;
                }
            case "new_pane_command":
                RawTmuxResult floatingPane = await RequireSuccessAsync(
                    context,
                    [
                        "new-pane", "-d", "-P", "-F", "#{pane_id}:#{pane_floating_flag}",
                        "-t", TargetPane(context), "sleep 2",
                    ]);
                Assert.Single(floatingPane.StandardOutputLines);
                Assert.Matches("^%[0-9]+:1$", floatingPane.StandardOutputLines[0]);
                string floatingPaneId = floatingPane.StandardOutputLines[0].Split(':')[0];
                RawTmuxResult floatingIdentity = await RequireSuccessAsync(
                    context,
                    [
                        "display-message", "-p", "-t", floatingPaneId,
                        "#{pane_id}:#{pane_floating_flag}",
                    ]);
                Assert.Equal([$"{floatingPaneId}:1"], floatingIdentity.StandardOutputLines);
                break;
            case "paste_buffer_no_vis":
                await ExercisePasteBufferNoVisAsync(context);
                break;
            case "refresh_client_clipboard_query":
                await ExerciseClipboardQueryAsync(context);
                break;
            case "run_shell_arguments":
                await ExerciseRunShellArgumentsAsync(context);
                break;
            case "run_shell_show_stderr":
                await ExerciseRunShellStderrAsync(context);
                break;
            case "run_shell_working_directory":
                await ExerciseRunShellWorkingDirectoryAsync(context);
                break;
            case "send_keys_client_keys":
                await ExerciseSendClientKeysAsync(context);
                break;
            case "server_access_command":
                RawTmuxResult access = await RequireSuccessAsync(context, ["server-access", "-l"]);
                Assert.Single(access.StandardOutputLines);
                Assert.Matches("^[^ ]+ \\(W\\)$", access.StandardOutputLines[0]);
                break;
            case "show_prompt_history_command":
                await ExercisePromptHistoryAsync(context, clear: false);
                break;
            case "split_window_appearance":
                string styledPane = await SplitPaneAsync(
                    context,
                    [
                        "-s", "fg=red", "-S", "fg=green", "-R", "fg=blue",
                        "-m", "libtmux-dead", "-k", "true",
                    ]);
                Assert.Equal("fg=red", await ShowPaneOptionAsync(context, styledPane, "window-style"));
                Assert.Equal(
                    "fg=green",
                    await ShowPaneOptionAsync(context, styledPane, "pane-active-border-style"));
                Assert.Equal(
                    "fg=blue",
                    await ShowPaneOptionAsync(context, styledPane, "pane-border-style"));
                Assert.Equal(
                    "libtmux-dead",
                    await ShowPaneOptionAsync(context, styledPane, "remain-on-exit-format"));
                Assert.Equal("key", await ShowPaneOptionAsync(context, styledPane, "remain-on-exit"));
                await WaitForFormatAsync(context, styledPane, "#{pane_dead}", "1");
                break;
            case "split_window_empty":
                string emptyPane = await SplitPaneAsync(context, ["-E"]);
                RawTmuxResult emptyIdentity = await RequireSuccessAsync(
                    context,
                    ["display-message", "-p", "-t", emptyPane, "#{pane_pid}"]);
                Assert.Equal(["0"], emptyIdentity.StandardOutputLines);
                await WriteEmptyPaneInputAsync(context, emptyPane, "libtmux-empty-input\n");
                RawTmuxResult emptyContents = await RequireSuccessAsync(
                    context,
                    ["capture-pane", "-p", "-t", emptyPane]);
                Assert.Contains("libtmux-empty-input", emptyContents.StandardOutputLines);
                RawTmuxResult rejectedCommand = await ExecuteAsync(
                    context,
                    ["split-window", "-d", "-t", TargetPane(context), "-E", "true"]);
                Assert.NotEqual(0, rejectedCommand.ExitCode);
                break;
            case "display_message_client":
                RawTmuxResult addressed = await RequireSuccessAsync(
                    context,
                    ["display-message", "-c", UnattachedClient, "-p", "addressed"]);
                Assert.Equal(["addressed"], addressed.StandardOutputLines);
                break;
            case "choose_tree_sort_time":
                // Where tmux still carries the order it takes it without
                // complaint, and the other orders keep working beside it, so
                // the version that drops one is the only thing that changed.
                foreach (string order in new[] { "time", "index", "name" })
                {
                    RawTmuxResult ordered = await ExecuteAsync(
                        context,
                        ["choose-tree", "-t", TargetPane(context), "-O", order]);
                    Assert.DoesNotContain(
                        "invalid sort order",
                        ordered.StandardErrorText,
                        StringComparison.Ordinal);
                }

                break;
            default:
                throw new InvalidOperationException(
                    $"No behavioral exercise exists for capability '{capability}'.");
        }
    }

    private static async Task ExerciseCommandPromptBackgroundAsync(
        RawTmuxTestContext context)
    {
        await using PtyAttachedClientScope client = await PtyAttachedClientScope.StartAsync(
            context,
            TestContext.Current.CancellationToken);
        Task<RawTmuxResult> prompt = ExecuteAsync(
            context,
            [
                "command-prompt", "-b", "-F", "-I", "prompt-value", "-t", client.Tty,
                "set-option -g @libtmux_prompt '#{session_name}:%%'",
            ]);
        RawTmuxResult opened = await prompt.WaitAsync(
            CommandTimeout,
            TestContext.Current.CancellationToken);
        Assert.Equal(0, opened.ExitCode);
        Assert.Null(await ShowOptionAsync(context, "@libtmux_prompt"));
        int outputOffset = client.ReadOutputSnapshot().Length;
        await client.WriteAsync("\r"u8.ToArray(), TestContext.Current.CancellationToken);
        Assert.Equal(
            $"{context.SessionName}:prompt-value",
            await WaitForOptionAsync(context, "@libtmux_prompt"));
        Assert.True(client.ReadOutputSnapshot().Length >= outputOffset);
    }

    private static async Task ExerciseCaptureModeScreenAsync(RawTmuxTestContext context)
    {
        await using PtyAttachedClientScope client = await PtyAttachedClientScope.StartAsync(
            context,
            TestContext.Current.CancellationToken);
        await RequireSuccessAsync(
            context,
            [
                "bind-key", "-n", "F11", "run-shell",
                "printf 'libtmux-mode-only-marker\\n'",
            ]);
        await RequireSuccessAsync(context, ["send-keys", "-K", "-c", client.Tty, "F11"]);
        await WaitForFormatAsync(context, TargetPane(context), "#{pane_in_mode}", "1");

        RawTmuxResult baseScreen = await RequireSuccessAsync(
            context,
            ["capture-pane", "-p", "-t", TargetPane(context)]);
        RawTmuxResult modeScreen = await RequireSuccessAsync(
            context,
            ["capture-pane", "-p", "-M", "-t", TargetPane(context)]);
        Assert.DoesNotContain(
            "libtmux-mode-only-marker",
            baseScreen.StandardOutputText,
            StringComparison.Ordinal);
        Assert.Contains(
            "libtmux-mode-only-marker",
            modeScreen.StandardOutputText,
            StringComparison.Ordinal);
    }

    private static async Task ExerciseCommandPrompt37Async(RawTmuxTestContext context)
    {
        await using PtyAttachedClientScope client = await PtyAttachedClientScope.StartAsync(
            context,
            TestContext.Current.CancellationToken);
        await RequireSuccessAsync(
            context,
            [
                "command-prompt", "-b", "-e", "-C", "-I", "", "-t", client.Tty,
                "set-option -g @libtmux_prompt37 executed",
            ]);
        await client.WriteAsync(new byte[] { 0x7f }, TestContext.Current.CancellationToken);

        // What is being proven is that the prompt did not execute, and nothing
        // to wait for ever arrives when nothing happens. A delay is the shape
        // this assertion has: too short and it proves less, never wrong.
        await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        Assert.Null(await ShowOptionAsync(context, "@libtmux_prompt37"));

        int offset = client.ReadOutputSnapshot().Length;
        await RequireSuccessAsync(
            context,
            ["command-prompt", "-b", "-C", "-I", "", "-t", client.Tty, "display-message %%"]);
        await SeedPaneCommandAsync(context, "printf 'libtmux-live-update\\n'");
        await WaitForClientOutputAsync(client, offset, "libtmux-live-update");
        await client.WriteAsync(new byte[] { 0x1b }, TestContext.Current.CancellationToken);
    }

    private static async Task ExerciseLiteralPromptAsync(RawTmuxTestContext context)
    {
        await using PtyAttachedClientScope client = await PtyAttachedClientScope.StartAsync(
            context,
            TestContext.Current.CancellationToken);
        await RequireSuccessAsync(
            context,
            [
                "command-prompt", "-b", "-l", "-p", "literal,prompt", "-I", "one,two",
                "-t", client.Tty, "set-option -g @libtmux_literal '%%'",
            ]);
        await client.WriteAsync("\r"u8.ToArray(), TestContext.Current.CancellationToken);
        Assert.Equal("one,two", await WaitForOptionAsync(context, "@libtmux_literal"));
    }

    private static async Task ExerciseConfirmAcceptanceAsync(RawTmuxTestContext context)
    {
        await using PtyAttachedClientScope client = await PtyAttachedClientScope.StartAsync(
            context,
            TestContext.Current.CancellationToken);
        await RequireSuccessAsync(
            context,
            [
                "confirm-before", "-b", "-c", "x", "-t", client.Tty,
                "set-option -g @libtmux_confirm custom",
            ]);
        await client.WriteAsync("x"u8.ToArray(), TestContext.Current.CancellationToken);
        Assert.Equal("custom", await WaitForOptionAsync(context, "@libtmux_confirm"));
        await RequireSuccessAsync(
            context,
            [
                "confirm-before", "-b", "-y", "-t", client.Tty,
                "set-option -g @libtmux_confirm enter",
            ]);
        await client.WriteAsync("\r"u8.ToArray(), TestContext.Current.CancellationToken);
        Assert.Equal("enter", await WaitForOptionValueAsync(context, "@libtmux_confirm", "enter"));
    }

    private static async Task ExerciseConfirmBackgroundAsync(RawTmuxTestContext context)
    {
        await using PtyAttachedClientScope client = await PtyAttachedClientScope.StartAsync(
            context,
            TestContext.Current.CancellationToken);
        RawTmuxResult opened = await RequireSuccessAsync(
            context,
            [
                "confirm-before", "-b", "-t", client.Tty,
                "set-option -g @libtmux_confirm_background accepted",
            ]);
        Assert.Equal(0, opened.ExitCode);
        Assert.Null(await ShowOptionAsync(context, "@libtmux_confirm_background"));
        await client.WriteAsync("y"u8.ToArray(), TestContext.Current.CancellationToken);
        Assert.Equal(
            "accepted",
            await WaitForOptionAsync(context, "@libtmux_confirm_background"));
    }

    private static async Task ExerciseDisplayMessageUpdateAsync(RawTmuxTestContext context)
    {
        await using PtyAttachedClientScope client = await PtyAttachedClientScope.StartAsync(
            context,
            TestContext.Current.CancellationToken);
        await WaitForClientOutputToSettleAsync(client);
        int offset = client.ReadOutputSnapshot().Length;
        await RequireSuccessAsync(
            context,
            ["display-message", "-C", "-d", "2000", "-t", client.Tty, "updating"]);
        await SeedPaneCommandAsync(context, "printf 'libtmux-message-update\\n'");
        await WaitForClientOutputAsync(client, offset, "libtmux-message-update");
    }

    private static async Task ExerciseMenuMouseAsync(RawTmuxTestContext context)
    {
        await using PtyAttachedClientScope client = await PtyAttachedClientScope.StartAsync(
            context,
            TestContext.Current.CancellationToken);
        await WaitForClientOutputToSettleAsync(client);
        int offset = client.ReadOutputSnapshot().Length;
        Task<RawTmuxResult> menu = ExecuteAsync(
            context,
            [
                "display-menu", "-M", "-t", TargetPane(context), "-x", "0", "-y", "0",
                "mouse-item", "x", "set-option -g @libtmux_menu_mouse selected",
            ]);

        // A click sent before the menu is drawn lands on the pane underneath
        // it. Its own item arriving at the client is what says it is there.
        await WaitForClientOutputAsync(client, offset, "mouse-item");
        await client.WriteAsync(
            "\u001b[<0;2;2M\u001b[<0;2;2m"u8.ToArray(),
            TestContext.Current.CancellationToken);
        Assert.Equal("selected", await WaitForOptionAsync(context, "@libtmux_menu_mouse"));
        Assert.Equal(
            0,
            (await menu.WaitAsync(CommandTimeout, TestContext.Current.CancellationToken)).ExitCode);
    }

    private static async Task ExerciseMenuStylesAsync(RawTmuxTestContext context)
    {
        await using PtyAttachedClientScope client = await PtyAttachedClientScope.StartAsync(
            context,
            TestContext.Current.CancellationToken);
        await WaitForClientOutputToSettleAsync(client);
        int offset = client.ReadOutputSnapshot().Length;
        Task<RawTmuxResult> menu = ExecuteAsync(
            context,
            [
                "display-menu", "-C", "1", "-b", "simple", "-s", "fg=red", "-S",
                "fg=green", "-H", "fg=blue", "-t", TargetPane(context), "-x", "0", "-y",
                "0", "first", "a", "set-option -g @libtmux_menu_style first",
                "second", "b", "set-option -g @libtmux_menu_style second",
            ]);
        // The last item proves the whole menu reached the client, which is what
        // the colors and the border below are read out of.
        await WaitForClientOutputAsync(client, offset, "second");
        byte[] rendered = client.ReadOutputSnapshot()[offset..];
        string renderedText = Encoding.UTF8.GetString(rendered);
        AssertSgrColor(renderedText, ansiColor: 31, paletteColor: 1);
        AssertSgrColor(renderedText, ansiColor: 32, paletteColor: 2);
        AssertSgrColor(renderedText, ansiColor: 34, paletteColor: 4);
        AssertSimpleBorder(renderedText);
        await client.WriteAsync("\r"u8.ToArray(), TestContext.Current.CancellationToken);
        Assert.Equal("second", await WaitForOptionAsync(context, "@libtmux_menu_style"));
        Assert.Equal(
            0,
            (await menu.WaitAsync(CommandTimeout, TestContext.Current.CancellationToken)).ExitCode);
    }

    private static async Task ExercisePopupOptionsAsync(RawTmuxTestContext context)
    {
        await using PtyAttachedClientScope client = await PtyAttachedClientScope.StartAsync(
            context,
            TestContext.Current.CancellationToken);
        await WaitForClientOutputToSettleAsync(client);
        int offset = client.ReadOutputSnapshot().Length;
        RawTmuxResult popup = await RequireSuccessAsync(
            context,
            [
                "display-popup", "-T", "libtmux-title", "-b", "simple", "-s", "fg=red",
                "-S", "fg=green", "-e", "LIBTMUX_POPUP=observed", "-t", TargetPane(context),
                "-E", "tmux set-option -g @libtmux_popup \"$LIBTMUX_POPUP\"; sleep 0.1",
            ]);
        Assert.Equal(0, popup.ExitCode);
        Assert.Equal("observed", await WaitForOptionAsync(context, "@libtmux_popup"));
        byte[] rendered = client.ReadOutputSnapshot()[offset..];
        string renderedText = Encoding.UTF8.GetString(rendered);
        Assert.Contains(
            "libtmux-title",
            renderedText,
            StringComparison.Ordinal);
        AssertSgrColor(renderedText, ansiColor: 31, paletteColor: 1);
        AssertSgrColor(renderedText, ansiColor: 32, paletteColor: 2);
        AssertSimpleBorder(renderedText);

        await WaitForClientOutputToSettleAsync(client);
        int noBorderOffset = client.ReadOutputSnapshot().Length;
        await RequireSuccessAsync(
            context,
            [
                "display-popup", "-B", "-T", "libtmux-no-border-title", "-s", "fg=blue",
                "-t", TargetPane(context), "-E", "sleep 0.1",
            ]);
        string noBorderRendered = Encoding.UTF8.GetString(
            client.ReadOutputSnapshot()[noBorderOffset..]);
        AssertSgrColor(noBorderRendered, ansiColor: 34, paletteColor: 4);
        Assert.False(
            HasSimpleBorder(noBorderRendered),
            "display-popup -B rendered a simple border inside the isolated PTY slice.");
        Assert.DoesNotContain(
            "libtmux-no-border-title",
            noBorderRendered,
            StringComparison.Ordinal);
    }

    private static async Task ExercisePopupKeyPolicyAsync(RawTmuxTestContext context)
    {
        await using PtyAttachedClientScope client = await PtyAttachedClientScope.StartAsync(
            context,
            TestContext.Current.CancellationToken);
        // A key sent before the popup is drawn lands on the pane underneath it
        // instead, so the key is retried until it is the one that closes the popup.
        Task<RawTmuxResult> closeAny = ExecuteAsync(
            context,
            ["display-popup", "-k", "-t", TargetPane(context), "true"]);
        await SendUntilCompletedAsync(client, closeAny, "x"u8.ToArray());
        Assert.Equal(
            0,
            (await closeAny.WaitAsync(CommandTimeout, TestContext.Current.CancellationToken)).ExitCode);

        // -N changes an open popup but blocks opening one when none exists, so
        // retrying it would wait on the popup it just made. The popup signals a
        // channel from inside instead; since -N and that signal are both
        // commands, not keys, applying -N after the wait cannot race it.
        Task<RawTmuxResult> reset = ExecuteAsync(
            context,
            [
                "display-popup", "-k", "-t", TargetPane(context),
                PopupOpenedCommand(context, "libtmux-popup-reset"),
            ]);
        await RequireSuccessAsync(context, ["wait-for", "libtmux-popup-reset"]);
        await RequireSuccessAsync(context, ["display-popup", "-N", "-t", TargetPane(context)]);
        await client.WriteAsync("x"u8.ToArray(), TestContext.Current.CancellationToken);

        // As above: the proof is that the popup stayed open, and a popup that
        // stays open produces nothing to wait for.
        await Task.Delay(TimeSpan.FromMilliseconds(75), TestContext.Current.CancellationToken);
        Assert.False(reset.IsCompleted);
        await client.WriteAsync(new byte[] { 0x1b }, TestContext.Current.CancellationToken);
        Assert.Equal(
            0,
            (await reset.WaitAsync(CommandTimeout, TestContext.Current.CancellationToken)).ExitCode);
    }

    private static async Task ExerciseHookScopesAsync(RawTmuxTestContext context)
    {
        await RequireSuccessAsync(
            context,
            [
                "set-hook", "-p", "-t", TargetPane(context), "pane-focus-in",
                "display-message pane-scope",
            ]);
        await RequireSuccessAsync(
            context,
            [
                "set-hook", "-w", "-t", TargetPane(context), "pane-focus-in",
                "display-message window-scope",
            ]);
        RawTmuxResult pane = await RequireSuccessAsync(
            context,
            ["show-hooks", "-p", "-t", TargetPane(context), "pane-focus-in"]);
        RawTmuxResult window = await RequireSuccessAsync(
            context,
            ["show-hooks", "-w", "-t", TargetPane(context), "pane-focus-in"]);
        Assert.Contains("pane-scope", pane.StandardOutputText, StringComparison.Ordinal);
        Assert.DoesNotContain("window-scope", pane.StandardOutputText, StringComparison.Ordinal);
        Assert.Contains("window-scope", window.StandardOutputText, StringComparison.Ordinal);
        Assert.DoesNotContain("pane-scope", window.StandardOutputText, StringComparison.Ordinal);
    }

    private static async Task ExercisePasteBufferNoVisAsync(RawTmuxTestContext context)
    {
        IReadOnlyList<string> escaped = await ReadPastedByteAsync(context, noVis: false);
        Assert.Contains(escaped, static line => string.Equals(line.Trim(), "94", StringComparison.Ordinal));
        IReadOnlyList<string> raw = await ReadPastedByteAsync(context, noVis: true);
        Assert.Contains(raw, static line => string.Equals(line.Trim(), "1", StringComparison.Ordinal));
        Assert.DoesNotContain(
            raw,
            static line => string.Equals(line.Trim(), "94", StringComparison.Ordinal));
    }

    private static async Task<IReadOnlyList<string>> ReadPastedByteAsync(
        RawTmuxTestContext context,
        bool noVis)
    {
        await RespawnShellAsync(context);
        await RequireSuccessAsync(
            context,
            [
                "send-keys", "-t", TargetPane(context), "-l",
                "stty raw -echo; od -An -tu1 -N1; stty sane",
            ]);
        await RequireSuccessAsync(context, ["send-keys", "-t", TargetPane(context), "Enter"]);

        // The byte has to arrive while od owns the terminal. An echoed command
        // line only says the shell read it, so the wait is for od itself to be
        // the pane's running command; pasting before that feeds the shell and
        // od then waits for input that never comes.
        await WaitForPaneCommandAsync(context, "od");
        await RequireSuccessAsync(context, ["set-buffer", "-b", "libtmux-byte", "\u0001"]);
        List<string> paste = ["paste-buffer"];
        if (noVis)
        {
            paste.Add("-S");
        }

        paste.AddRange(["-b", "libtmux-byte", "-t", TargetPane(context)]);
        await RequireSuccessAsync(context, paste);
        return await WaitForPaneAsync(
            context,
            static lines => lines.Any(line => line.Trim() is "1" or "94"));
    }

    /// <summary>Waits for the pane to be running any of the names given.</summary>
    /// <remarks>
    /// More than one name is accepted because what tmux reports for a shell is
    /// the process, not the path that was asked for. On macOS <c>/bin/sh</c> is
    /// bash in sh-compatibility mode, so respawning with <c>/bin/sh</c> settles
    /// at <c>bash</c>; on Linux it settles at <c>sh</c>. Both are the shell that
    /// was asked for.
    /// </remarks>
    private static async Task WaitForPaneCommandAsync(
        RawTmuxTestContext context,
        params string[] accepted)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TestBudget.Settle;
        string running = string.Empty;
        while (DateTimeOffset.UtcNow < deadline)
        {
            RawTmuxResult capture = await RequireSuccessAsync(
                context,
                ["display-message", "-p", "-t", TargetPane(context), "#{pane_current_command}"]);
            running = capture.StandardOutputLines.Count > 0
                ? capture.StandardOutputLines[0].Trim()
                : string.Empty;
            if (accepted.Contains(running, StringComparer.Ordinal))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken);
        }

        throw new InvalidOperationException(
            $"The pane runs '{running}' rather than any of "
            + $"'{string.Join("', '", accepted)}'.");
    }

    private static async Task<IReadOnlyList<string>> WaitForPaneAsync(
        RawTmuxTestContext context,
        Func<IReadOnlyList<string>, bool> settled)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TestBudget.Settle;
        IReadOnlyList<string> lines = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            RawTmuxResult capture = await RequireSuccessAsync(
                context,
                ["capture-pane", "-p", "-S", "-", "-t", TargetPane(context)]);
            lines = capture.StandardOutputLines;
            if (settled(lines))
            {
                return lines;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken);
        }

        return lines;
    }

    private static async Task ExerciseClipboardQueryAsync(RawTmuxTestContext context)
    {
        await using PtyAttachedClientScope client = await PtyAttachedClientScope.StartAsync(
            context,
            TestContext.Current.CancellationToken);
        int offset = client.ReadOutputSnapshot().Length;
        await RequireSuccessAsync(context, ["refresh-client", "-l", "-t", client.Tty]);
        await WaitForClientOutputAsync(client, offset, "\u001b]52;;?");
        RawTmuxResult original = await RequireSuccessAsync(
            context,
            ["show-options", "-s", "-v", "get-clipboard"]);
        Assert.Single(original.StandardOutputLines);
        Assert.Contains(original.StandardOutputLines[0], ClipboardModes);
        await RequireSuccessAsync(context, ["set-option", "-s", "get-clipboard", "off"]);
        RawTmuxResult changed = await RequireSuccessAsync(
            context,
            ["show-options", "-s", "-v", "get-clipboard"]);
        Assert.Equal(["off"], changed.StandardOutputLines);
        await RequireSuccessAsync(
            context,
            ["set-option", "-s", "get-clipboard", original.StandardOutputLines[0]]);
    }

    private static async Task ExerciseRunShellArgumentsAsync(RawTmuxTestContext context)
    {
        await RequireSuccessAsync(
            context,
            [
                "run-shell", "tmux set-option -g @libtmux_args '#{1}-#{2}'",
                "alpha", "beta",
            ]);
        Assert.Equal("alpha-beta", await WaitForOptionAsync(context, "@libtmux_args"));
    }

    private static async Task ExerciseRunShellStderrAsync(RawTmuxTestContext context)
    {
        const string marker = "libtmux-stderr-marker";
        await RequireSuccessAsync(context, ["run-shell", $"printf {marker} >&2"]);
        RawTmuxResult hidden = await RequireSuccessAsync(
            context,
            ["capture-pane", "-p", "-t", TargetPane(context)]);
        Assert.DoesNotContain(marker, hidden.StandardOutputText, StringComparison.Ordinal);
        await RequireSuccessAsync(
            context,
            ["run-shell", "-E", "-t", TargetPane(context), $"printf {marker} >&2"]);
        RawTmuxResult shown = await RequireSuccessAsync(
            context,
            ["capture-pane", "-p", "-M", "-t", TargetPane(context)]);
        Assert.Equal(1, CountOccurrences(shown.StandardOutputText, marker));
    }

    private static async Task ExerciseRunShellWorkingDirectoryAsync(
        RawTmuxTestContext context)
    {
        await RequireSuccessAsync(
            context,
            [
                "run-shell", "-c", "/",
                "tmux set-option -g @libtmux_cwd \"$PWD\"",
            ]);
        Assert.Equal("/", await WaitForOptionAsync(context, "@libtmux_cwd"));
    }

    private static async Task ExerciseSendClientKeysAsync(RawTmuxTestContext context)
    {
        await using PtyAttachedClientScope client = await PtyAttachedClientScope.StartAsync(
            context,
            TestContext.Current.CancellationToken);
        await RequireSuccessAsync(
            context,
            ["bind-key", "-n", "F12", "set-option", "-g", "@libtmux_client_key", "seen"]);
        await RequireSuccessAsync(context, ["send-keys", "-K", "-c", client.Tty, "F12"]);
        Assert.Equal("seen", await WaitForOptionAsync(context, "@libtmux_client_key"));
    }

    private static async Task ExercisePromptHistoryAsync(
        RawTmuxTestContext context,
        bool clear)
    {
        await using PtyAttachedClientScope client = await PtyAttachedClientScope.StartAsync(
            context,
            TestContext.Current.CancellationToken);
        await RequireSuccessAsync(
            context,
            [
                "command-prompt", "-b", "-I", "libtmux-history-value", "-t", client.Tty,
                "display-message %%",
            ]);
        await client.WriteAsync("\r"u8.ToArray(), TestContext.Current.CancellationToken);

        // The prompt records what was entered when it accepts it, which is not
        // when the return key was written to the client's terminal.
        RawTmuxResult shown = await WaitForResultAsync(
            context,
            ["show-prompt-history", "-T", "command"],
            result => result.StandardOutputLines.Count == 3);
        Assert.Equal(
            ["History for command:", "", "1: libtmux-history-value"],
            shown.StandardOutputLines);
        if (!clear)
        {
            return;
        }

        await RequireSuccessAsync(context, ["clear-prompt-history", "-T", "command"]);
        RawTmuxResult cleared = await RequireSuccessAsync(
            context,
            ["show-prompt-history", "-T", "command"]);
        Assert.Equal(["History for command:"], cleared.StandardOutputLines);
    }

    private static void AssertSgrColor(string rendered, int ansiColor, int paletteColor)
    {
        bool usesAnsiColor = rendered.Contains(
            $"\u001b[{ansiColor}m",
            StringComparison.Ordinal);
        bool usesPaletteColor = rendered.Contains(
            $"\u001b[38;5;{paletteColor}m",
            StringComparison.Ordinal);
        Assert.True(
            usesAnsiColor || usesPaletteColor,
            $"PTY output did not render ANSI color {ansiColor} or palette color {paletteColor}.");
    }

    private static void AssertSimpleBorder(string rendered)
    {
        Assert.True(
            HasSimpleBorder(rendered),
            "PTY output did not contain a complete simple-border glyph set. Observed: "
            + string.Join(
                ',',
                rendered.Where(static character => !char.IsControl(character)).Distinct()
                    .Select(static character => $"U+{(int)character:X4}")));
    }

    private static bool HasSimpleBorder(string rendered)
    {
        bool usesUnicode = rendered.Contains('┌')
            && rendered.Contains('─')
            && rendered.Contains('│')
            && rendered.Contains('┘');
        bool usesAscii = rendered.Contains('+')
            && rendered.Contains('-')
            && rendered.Contains('|');
        bool usesDecDrawing = rendered.Contains("\u001b(0", StringComparison.Ordinal)
            && rendered.Contains('q')
            && rendered.Contains('x');
        return usesUnicode || usesAscii || usesDecDrawing;
    }

    private static async Task WriteEmptyPaneInputAsync(
        RawTmuxTestContext context,
        string pane,
        string input)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(CommandTimeout);
        using Process process = Process.Start(
            context.CreateStartInfo(
                ["display-message", "-I", "-t", pane],
                redirectStandardInput: true))
            ?? throw new InvalidOperationException("tmux display-message did not start.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token);
        process.StandardInput.Close();
        await process.WaitForExitAsync(timeout.Token);
        Assert.True(
            process.ExitCode == 0,
            $"tmux display-message -I failed: {await stderr}");
        Assert.Empty(await stdout);
    }

    private static async Task RespawnShellAsync(RawTmuxTestContext context)
    {
        await RequireSuccessAsync(
            context,
            ["respawn-pane", "-k", "-t", TargetPane(context), "/bin/sh"]);

        // Killing and restarting a pane's process is not instant, and keys
        // typed into the gap reach nothing.
        await WaitForPaneCommandAsync(context, "sh", "bash");
    }

    private static async Task SeedPaneCommandAsync(
        RawTmuxTestContext context,
        string command)
    {
        await RespawnShellAsync(context);
        await RequireSuccessAsync(
            context,
            ["send-keys", "-t", TargetPane(context), "-l", command]);
        await RequireSuccessAsync(context, ["send-keys", "-t", TargetPane(context), "Enter"]);

        // Sending a command is not running it. What running it produces differs
        // per caller, so each waits for its own result rather than being given
        // a fixed number of milliseconds here.
    }

    /// <summary>Builds a popup command that signals once the popup is open.</summary>
    /// <remarks>
    /// tmux remembers a signal that arrives before anyone waits, so signalling
    /// from inside the popup and waiting for it afterwards cannot race. This
    /// says the popup exists, which is what a command needs; it does not say
    /// the command inside has finished, so a key sent on the strength of it can
    /// still land in that command rather than in the popup.
    /// </remarks>
    private static string PopupOpenedCommand(RawTmuxTestContext context, string channel) =>
        $"'{context.TmuxBinaryPath}' -S '{context.SocketPath}' wait-for -S {channel}";

    /// <summary>Sends one input until the work it is meant to finish has.</summary>
    /// <remarks>
    /// Some of what tmux does cannot be observed until it has already happened,
    /// and a popup that has not been drawn yet is one of them. Sending until
    /// the effect arrives replaces guessing how long drawing takes, and costs
    /// only the inputs that missed.
    /// </remarks>
    private static async Task SendUntilCompletedAsync(
        PtyAttachedClientScope client,
        Task completed,
        byte[] input)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + CommandTimeout;
        while (!completed.IsCompleted && DateTimeOffset.UtcNow < deadline)
        {
            await client.WriteAsync(input, TestContext.Current.CancellationToken);
            await Task.Delay(
                TimeSpan.FromMilliseconds(10),
                TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Runs a command until its result is the one being waited for.</summary>
    /// <remarks>
    /// A command that answers the wrong thing once has not necessarily failed:
    /// tmux may not have got to the work yet. Asking again until the deadline
    /// is what tells those apart.
    /// </remarks>
    private static async Task<RawTmuxResult> WaitForResultAsync(
        RawTmuxTestContext context,
        IReadOnlyList<string> arguments,
        Func<RawTmuxResult, bool> settled)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + CommandTimeout;
        RawTmuxResult result = await RequireSuccessAsync(context, arguments);
        while (!settled(result) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(
                TimeSpan.FromMilliseconds(10),
                TestContext.Current.CancellationToken);
            result = await RequireSuccessAsync(context, arguments);
        }

        return result;
    }

    /// <summary>Waits until a pane's scrollback shows some text.</summary>
    /// <remarks>
    /// A shell handed a command has not necessarily run it, and a sequence
    /// written to a pane's terminal has not necessarily been parsed. Reading
    /// until what was seeded is there is what separates proving tmux's behavior
    /// from measuring how busy the machine is.
    /// </remarks>
    private static async Task WaitForPaneContentAsync(
        RawTmuxTestContext context,
        string expected)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + CommandTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            RawTmuxResult result = await ExecuteAsync(
                context,
                ["capture-pane", "-p", "-S", "-", "-t", TargetPane(context)]);
            if (result.ExitCode == 0
                && result.StandardOutputText.Contains(expected, StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(10),
                TestContext.Current.CancellationToken);
        }

        Assert.Fail($"The pane never showed '{expected}'.");
    }

    private static async Task SeedHyperlinkAsync(
        RawTmuxTestContext context,
        string url,
        string text)
    {
        await RespawnShellAsync(context);
        await RequireSuccessAsync(
            context,
            [
                "run-shell",
                $"printf '\\033]8;;{url}\\033\\\\{text}\\033]8;;\\033\\\\\\n' "
                + "> '#{pane_tty}'",
            ]);

        // run-shell returning says the sequence was written to the terminal,
        // not that the pane has parsed it. The text it carries showing up does.
        await WaitForPaneContentAsync(context, text);
    }

    private static async Task SeedScrollbackAsync(RawTmuxTestContext context)
    {
        await SeedPaneCommandAsync(
            context,
            "i=0; while [ $i -lt 80 ]; do echo old-marker-$i; i=$((i+1)); done");
        await WaitForFormatAsync(context, TargetPane(context), "#{history_size}", "58");
    }

    private static async Task<int> DisplayIntegerAsync(
        RawTmuxTestContext context,
        string format)
    {
        RawTmuxResult result = await RequireSuccessAsync(
            context,
            ["display-message", "-p", "-t", TargetPane(context), format]);
        Assert.Single(result.StandardOutputLines);
        Assert.True(int.TryParse(result.StandardOutputLines[0], out int value));
        return value;
    }

    private static async Task<string> ShowPaneOptionAsync(
        RawTmuxTestContext context,
        string pane,
        string option)
    {
        RawTmuxResult result = await RequireSuccessAsync(
            context,
            ["show-options", "-p", "-v", "-t", pane, option]);
        Assert.Single(result.StandardOutputLines);
        return result.StandardOutputLines[0];
    }

    private static async Task WaitForFormatAsync(
        RawTmuxTestContext context,
        string pane,
        string format,
        string expected)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + CommandTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            RawTmuxResult result = await ExecuteAsync(
                context,
                ["display-message", "-p", "-t", pane, format]);
            if (result.ExitCode == 0
                && result.StandardOutputLines is [string value]
                && string.Equals(value, expected, StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(10),
                TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"tmux format {format} did not become {expected}.");
    }

    private static async Task WaitForClientOutputAsync(
        PtyAttachedClientScope client,
        int offset,
        string expected)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + CommandTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            byte[] snapshot = client.ReadOutputSnapshot();
            if (snapshot.Length >= offset
                && Encoding.UTF8.GetString(snapshot[offset..]).Contains(
                    expected,
                    StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(10),
                TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"PTY output did not contain {expected}.");
    }

    private static async Task WaitForClientOutputToSettleAsync(PtyAttachedClientScope client)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + CommandTimeout;
        int previousLength = -1;
        int stableSamples = 0;
        while (DateTimeOffset.UtcNow < deadline)
        {
            int currentLength = client.ReadOutputSnapshot().Length;
            if (currentLength == previousLength)
            {
                stableSamples++;
                if (stableSamples == 3)
                {
                    return;
                }
            }
            else
            {
                previousLength = currentLength;
                stableSamples = 0;
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(10),
                TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("PTY output did not settle before the observation window.");
    }

    private static async Task<string> WaitForOptionValueAsync(
        RawTmuxTestContext context,
        string option,
        string expected)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + CommandTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            string? value = await ShowOptionAsync(context, option);
            if (string.Equals(value, expected, StringComparison.Ordinal))
            {
                return expected;
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(10),
                TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"tmux option {option} did not become {expected}.");
    }

    private static int CountOccurrences(string value, string expected)
    {
        int count = 0;
        int offset = 0;
        while ((offset = value.IndexOf(expected, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += expected.Length;
        }

        return count;
    }

    private static async Task<string> SplitPaneAsync(
        RawTmuxTestContext context,
        IReadOnlyList<string> capabilityArguments)
    {
        List<string> arguments =
        [
            "split-window", "-d", "-P", "-F", "#{pane_id}", "-t", TargetPane(context),
            .. capabilityArguments,
        ];
        RawTmuxResult result = await RequireSuccessAsync(context, arguments);
        Assert.Single(result.StandardOutputLines);
        Assert.Matches("^%[0-9]+$", result.StandardOutputLines[0]);
        return result.StandardOutputLines[0];
    }

    private static async Task<RawTmuxResult> RequireSuccessAsync(
        RawTmuxTestContext context,
        IReadOnlyList<string> arguments)
    {
        RawTmuxResult result = await ExecuteAsync(context, arguments);
        Assert.True(
            result.ExitCode == 0,
            $"tmux command failed: {string.Join(' ', arguments)}: {result.StandardErrorText}");
        return result;
    }

    private static async Task<RawTmuxResult> ExecuteAsync(
        RawTmuxTestContext context,
        IReadOnlyList<string> arguments)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(CommandTimeout);
        try
        {
            return await context.ExecuteAsync(arguments, timeout.Token);
        }
        catch (OperationCanceledException) when (
            timeout.IsCancellationRequested
            && !TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"tmux command did not finish within {CommandTimeout.TotalSeconds} seconds: "
                + string.Join(' ', arguments));
        }
    }

    private static Task<RawTmuxTestContext> StartAsync() =>
        RawTmuxTestContext.StartAsync(TestContext.Current.CancellationToken);

    private static async Task<TmuxVersion> GetVersionAsync(
        RawTmuxTestContext context)
    {
        TmuxVersion version = await TmuxVersion.DetectAsync(
            context.TmuxBinaryPath,
            TestContext.Current.CancellationToken);
        string? expected = Environment.GetEnvironmentVariable("LIBTMUX_EXPECTED_TMUX_VERSION");
        if (!string.IsNullOrEmpty(expected))
        {
            Assert.Equal(TmuxVersion.Parse(expected), version);
        }

        RawTmuxResult serverVersion = await RequireSuccessAsync(
            context,
            ["display-message", "-p", "#{version}"]);
        Assert.Equal([version.Raw], serverVersion.StandardOutputLines);
        return version;
    }

    private static async Task<string?> ShowOptionAsync(
        RawTmuxTestContext context,
        string option)
    {
        RawTmuxResult result = await ExecuteAsync(
            context,
            ["show-options", "-gv", option]);
        return HasSingleNonemptyLine(result) ? result.StandardOutputLines[0] : null;
    }

    private static async Task<string> WaitForOptionAsync(
        RawTmuxTestContext context,
        string option)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + CommandTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            string? value = await ShowOptionAsync(context, option);
            if (value is not null)
            {
                return value;
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(10),
                TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"tmux option {option} did not appear in time.");
    }

    private static bool HasSingleNonemptyLine(RawTmuxResult result) =>
        result.ExitCode == 0
        && result.StandardOutputLines is [string line]
        && !string.IsNullOrWhiteSpace(line);

    private static async Task<string> ReadUntilAsync(
        ControlModeClientScope client,
        string prefix,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            string? line = await client.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                throw new InvalidOperationException(
                    "The control client closed before the expected notification.");
            }

            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return line;
            }
        }
    }

    private static bool SyntaxSupportsFlag(string syntax, string flag)
    {
        if (flag.Length != 2 || flag[0] != '-')
        {
            throw new ArgumentException("Only canonical single-letter tmux flags are supported.", nameof(flag));
        }

        char option = flag[1];
        int searchFrom = 0;
        while (true)
        {
            int clusterStart = syntax.IndexOf("[-", searchFrom, StringComparison.Ordinal);
            if (clusterStart < 0)
            {
                return false;
            }

            int optionIndex = clusterStart + 2;
            while (optionIndex < syntax.Length && char.IsAsciiLetterOrDigit(syntax[optionIndex]))
            {
                if (syntax[optionIndex] == option)
                {
                    return true;
                }

                optionIndex++;
            }

            searchFrom = optionIndex;
        }
    }

    // From 3.3 tmux accepts -c and simply prints for a client it cannot
    // resolve, while 3.2a refuses the flag outright. That difference is the
    // gate, so the probe needs no attached client of its own.
    private const string UnattachedClient = "/dev/null";

    private static string TargetPane(RawTmuxTestContext context) =>
        $"{context.SessionName}:0.0";

    private static async Task WriteTransitionRecordAsync(
        TmuxVersion version,
        bool workaround)
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable("LIBTMUX_BREAK_PANE_TRANSITION_PROOF"),
            "1",
            StringComparison.Ordinal))
        {
            return;
        }

        Assert.Contains(version.Raw, TransitionVersions);
        string framework = RequiredEnvironment("LIBTMUX_TEST_FRAMEWORK");
        Assert.Contains(framework, TransitionFrameworks);
        string sourceCommit = RequiredEnvironment("LIBTMUX_TMUX_SOURCE_COMMIT");
        Assert.Matches("^[0-9a-f]{40}$", sourceCommit);
        string transcriptDirectory = RequiredEnvironment("LIBTMUX_PROTOCOL_TRANSCRIPT_DIR");
        string workaroundState = workaround ? "applied" : "omitted";
        string record = string.Join(
            ' ',
            "event=break-pane-transition",
            $"framework={framework}",
            $"tmux-source-commit={sourceCommit}",
            $"tmux-version={version.Raw}",
            $"workaround={workaroundState}",
            "outcome=passed");
        Directory.CreateDirectory(transcriptDirectory);
        await File.AppendAllTextAsync(
            Path.Combine(transcriptDirectory, "break-pane-transition.txt"),
            record + "\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            TestContext.Current.CancellationToken);
    }

    private static async Task WriteProtocolTranscriptAsync(
        string filename,
        IReadOnlyList<string> records)
    {
        string? transcriptDirectory = Environment.GetEnvironmentVariable(
            "LIBTMUX_PROTOCOL_TRANSCRIPT_DIR");
        if (string.IsNullOrEmpty(transcriptDirectory))
        {
            return;
        }

        Directory.CreateDirectory(transcriptDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(transcriptDirectory, filename),
            string.Join('\n', records) + "\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            TestContext.Current.CancellationToken);
    }

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} is required for transition proof.");

    private sealed record Gate(
        string Capability,
        string Command,
        IReadOnlyList<string> Flags,
        bool PositionalArguments = false);
}
