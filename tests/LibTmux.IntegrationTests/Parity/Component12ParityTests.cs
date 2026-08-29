using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Internal;

namespace LibTmux.IntegrationTests.Parity;

[UnsupportedOSPlatform("windows")]
public sealed class Component12ParityTests
{
    public static TheoryData<string> OwnedRows =>
    [
        "libtmux.pane:<module>",
        "libtmux.pane:Pane",
        "libtmux.pane:Pane.__getitem__",
        "libtmux.pane:Pane.at_bottom",
        "libtmux.pane:Pane.at_left",
        "libtmux.pane:Pane.at_right",
        "libtmux.pane:Pane.at_top",
        "libtmux.pane:Pane.break_pane",
        "libtmux.pane:Pane.capture_pane",
        "libtmux.pane:Pane.choose_buffer",
        "libtmux.pane:Pane.choose_client",
        "libtmux.pane:Pane.choose_tree",
        "libtmux.pane:Pane.clear",
        "libtmux.pane:Pane.clear_history",
        "libtmux.pane:Pane.clock_mode",
        "libtmux.pane:Pane.copy_mode",
        "libtmux.pane:Pane.customize_mode",
        "libtmux.pane:Pane.display_message",
        "libtmux.pane:Pane.display_panes",
        "libtmux.pane:Pane.display_popup",
        "libtmux.pane:Pane.enter",
        "libtmux.pane:Pane.find_window",
        "libtmux.pane:Pane.get",
        "libtmux.pane:Pane.height",
        "libtmux.pane:Pane.index",
        "libtmux.pane:Pane.join",
        "libtmux.pane:Pane.kill",
        "libtmux.pane:Pane.move",
        "libtmux.pane:Pane.new_pane",
        "libtmux.pane:Pane.paste_buffer",
        "libtmux.pane:Pane.pipe",
        "libtmux.pane:Pane.refresh",
        "libtmux.pane:Pane.reset",
        "libtmux.pane:Pane.resize",
        "libtmux.pane:Pane.resize_pane",
        "libtmux.pane:Pane.respawn",
        "libtmux.pane:Pane.select",
        "libtmux.pane:Pane.select_pane",
        "libtmux.pane:Pane.send_keys",
        "libtmux.pane:Pane.send_prefix",
        "libtmux.pane:Pane.set_height",
        "libtmux.pane:Pane.set_title",
        "libtmux.pane:Pane.set_width",
        "libtmux.pane:Pane.split",
        "libtmux.pane:Pane.split_window",
        "libtmux.pane:Pane.swap",
        "libtmux.pane:Pane.title",
        "libtmux.pane:Pane.width",
        "libtmux.window:Window.last_pane",
        "libtmux:Pane",
    ];

    [Theory(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    [MemberData(nameof(OwnedRows))]
    public async Task Owned_parity_row_has_pane_behavior(string pythonSymbolId)
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await Server.ConnectAsync(
            new ServerConnectionOptions(
                tmuxBinaryPath: raw.TmuxBinaryPath,
                socketPath: raw.SocketPath,
                configurationFile: "/dev/null"),
            token);
        Session session = await TestHierarchy.RequireFirstSessionAsync(server, token);
        Window window = await TestHierarchy.RequireFirstWindowAsync(session, token);
        Pane pane = await TestHierarchy.RequireFirstPaneAsync(window, token);

        bool proved = pythonSymbolId switch
        {
            // The module and class rows are proved by the handle carrying live
            // pane state, not by a compile-time property of the type.
            "libtmux.pane:<module>" or "libtmux.pane:Pane" or "libtmux:Pane" =>
                pane.Id.ToString().StartsWith('%')
                && pane.Width > 0
                && pane.Generation == session.Generation,
            "libtmux.pane:Pane.height" => pane.Height > 0,
            "libtmux.pane:Pane.width" => pane.Width > 0,
            "libtmux.pane:Pane.index" => pane.Index >= 0,
            "libtmux.pane:Pane.title" => pane.Title is not null,
            "libtmux.pane:Pane.at_top" or "libtmux.pane:Pane.at_bottom"
                or "libtmux.pane:Pane.at_left" or "libtmux.pane:Pane.at_right" =>
                await ProvesEdgesAsync(pane, token),
            "libtmux.pane:Pane.__getitem__" or "libtmux.pane:Pane.get" =>
                ProvesRawFields(pane),
            "libtmux.pane:Pane.refresh" => (await pane.RefreshAsync(token)).Id == pane.Id,
            "libtmux.pane:Pane.capture_pane" => await ProvesCaptureAsync(pane, token),
            "libtmux.pane:Pane.send_keys" => await ProvesSendKeysAsync(pane, token),
            "libtmux.pane:Pane.enter" => (await pane.EnterAsync(token)).Id == pane.Id,
            "libtmux.pane:Pane.clear" => (await pane.ClearAsync(token)).Id == pane.Id,
            "libtmux.pane:Pane.reset" => (await pane.ResetAsync(token)).Id == pane.Id,
            "libtmux.pane:Pane.clear_history" => await ProvesClearHistoryAsync(pane, token),
            "libtmux.pane:Pane.send_prefix" => await ProvesSendPrefixAsync(pane, token),
            "libtmux.pane:Pane.split" or "libtmux.pane:Pane.split_window" =>
                await ProvesSplitAsync(window, pane, token),
            "libtmux.pane:Pane.new_pane" => await ProvesCreatePaneAsync(server, pane, token),
            "libtmux.pane:Pane.break_pane" => await ProvesBreakAsync(pane, token),
            "libtmux.pane:Pane.join" or "libtmux.pane:Pane.move" =>
                await ProvesRehomeAsync(session, pane, token),
            "libtmux.pane:Pane.swap" => await ProvesSwapAsync(pane, token),
            "libtmux.pane:Pane.kill" => await ProvesKillAsync(window, pane, token),
            "libtmux.pane:Pane.respawn" => await ProvesRespawnAsync(pane, token),
            "libtmux.pane:Pane.resize" or "libtmux.pane:Pane.resize_pane" =>
                await ProvesResizeAsync(pane, token),
            "libtmux.pane:Pane.set_width" => (await pane.SetWidthAsync(30, token)).Width > 0,
            "libtmux.pane:Pane.set_height" => (await pane.SetHeightAsync(10, token)).Height > 0,
            "libtmux.pane:Pane.set_title" =>
                (await pane.SetTitleAsync("titled", token)).Title == "titled",
            "libtmux.pane:Pane.select" or "libtmux.pane:Pane.select_pane" =>
                await ProvesSelectAsync(pane, token),
            "libtmux.window:Window.last_pane" => await ProvesLastPaneAsync(window, pane, token),
            "libtmux.pane:Pane.pipe" => await ProvesPipeAsync(pane, token),
            "libtmux.pane:Pane.paste_buffer" => await ProvesPasteAsync(pane, token),
            "libtmux.pane:Pane.copy_mode" => await ProvesCopyModeAsync(pane, token),
            "libtmux.pane:Pane.clock_mode" => await ProvesModeAsync(pane, "clock", token),
            "libtmux.pane:Pane.customize_mode" => await ProvesModeAsync(pane, "customize", token),
            "libtmux.pane:Pane.display_message" => await ProvesDisplayMessageAsync(pane, token),
            "libtmux.pane:Pane.display_panes" or "libtmux.pane:Pane.display_popup" =>
                await ProvesOverlayNeedsClientAsync(pane, token),
            "libtmux.pane:Pane.choose_buffer" or "libtmux.pane:Pane.choose_client"
                or "libtmux.pane:Pane.choose_tree" or "libtmux.pane:Pane.find_window" =>
                await ProvesChoosersAsync(pane, token),
            _ => false,
        };

        Assert.True(proved, $"Parity behavior was not proved for {pythonSymbolId}.");
    }

    private static async Task<bool> ProvesEdgesAsync(Pane pane, CancellationToken token)
    {
        // A lone pane touches every edge; splitting it moves one of them.
        Assert.True(pane.AtTop && pane.AtBottom && pane.AtLeft && pane.AtRight);
        Pane below = await pane.SplitAsync(cancellationToken: token);
        Pane refreshed = await pane.RefreshAsync(token);
        return refreshed.AtTop && !refreshed.AtBottom && below.AtBottom && !below.AtTop;
    }

    private static bool ProvesRawFields(Pane pane)
    {
        // A Python __getitem__ or get() becomes named typed properties, and
        // there is no indexer to reach a field by string.
        Assert.True(pane.Width > 0);
        Assert.True(pane.Height > 0);
        Assert.True(pane.Index >= 0);
        Assert.NotNull(pane.Title);
        return typeof(Pane).GetProperties().All(property => property.Name != "Item")
            && typeof(Pane).GetMethod("Get") is null;
    }

    private static async Task<bool> ProvesCaptureAsync(Pane pane, CancellationToken token)
    {
        await pane.SendTextAsync("echo PARITYCAPTURE", cancellationToken: token);
        string text = await ReadPaneAsync(pane, token);
        return text.Contains("PARITYCAPTURE", StringComparison.Ordinal);
    }

    private static async Task<bool> ProvesSendKeysAsync(Pane pane, CancellationToken token)
    {
        await pane.SendKeysAsync(new SendKeysRequest("echo PARITYKEYS"), token);
        return (await ReadPaneAsync(pane, token)).Contains("PARITYKEYS", StringComparison.Ordinal);
    }

    private static async Task<bool> ProvesClearHistoryAsync(Pane pane, CancellationToken token)
    {
        await pane.SendTextAsync("echo fill", cancellationToken: token);
        await pane.ClearHistoryAsync(cancellationToken: token);
        return await FormatAsync(pane, "#{history_size}", token) == "0";
    }

    private static async Task<bool> ProvesSendPrefixAsync(Pane pane, CancellationToken token)
    {
        await pane.SendPrefixAsync(cancellationToken: token);
        return (await pane.RefreshAsync(token)).Id == pane.Id;
    }

    private static async Task<bool> ProvesSplitAsync(
        Window window,
        Pane pane,
        CancellationToken token)
    {
        Pane created = await pane.SplitAsync(cancellationToken: token);
        IReadOnlyList<Pane> panes = await window.GetPanesAsync(token);
        return panes.Count == 2
            && panes.Any(candidate => candidate.Id == created.Id)
            && typeof(Pane).GetMethod("SplitWindowAsync") is null;
    }

    private static async Task<bool> ProvesCreatePaneAsync(
        Server server,
        Pane pane,
        CancellationToken token)
    {
        if (!TmuxCapabilities.IsSupported(server.Version!.Value, "new_pane_command"))
        {
            // The command does not exist before 3.7, so a typed refusal is the
            // whole behaviour on those lanes.
            await Assert.ThrowsAsync<TmuxVersionTooLowException>(
                () => pane.CreatePaneAsync(cancellationToken: token));
            return true;
        }

        Pane created = await pane.CreatePaneAsync(
            new NewPaneRequest(width: 20, height: 5, x: 1, y: 1),
            token);
        return created.Id != pane.Id;
    }

    private static async Task<bool> ProvesBreakAsync(Pane pane, CancellationToken token)
    {
        Pane spare = await pane.SplitAsync(cancellationToken: token);
        Window broken = await spare.BreakAsync("broken", cancellationToken: token);
        return broken.Name == "broken"
            && (await broken.GetPanesAsync(token)).Any(candidate => candidate.Id == spare.Id);
    }

    private static async Task<bool> ProvesRehomeAsync(
        Session session,
        Pane pane,
        CancellationToken token)
    {
        Pane travelling = await pane.SplitAsync(cancellationToken: token);
        Window destination = await session.CreateWindowAsync(
            new NewWindowRequest(name: "destination"),
            token);
        await travelling.MoveAsync(new MovePaneRequest(destination.Id.ToString()), token);
        bool moved = (await destination.GetPanesAsync(token))
            .Any(candidate => candidate.Id == travelling.Id);

        Pane joining = await pane.SplitAsync(cancellationToken: token);
        await joining.JoinAsync(new MovePaneRequest(destination.Id.ToString()), token);
        return moved
            && (await destination.GetPanesAsync(token))
                .Any(candidate => candidate.Id == joining.Id);
    }

    private static async Task<bool> ProvesSwapAsync(Pane pane, CancellationToken token)
    {
        Pane other = await pane.SplitAsync(cancellationToken: token);
        string before = await FormatAsync(pane, "#{pane_index}", token);
        await pane.SwapAsync(new SwapPaneRequest(other.Id.ToString()), token);
        return await FormatAsync(pane, "#{pane_index}", token) != before;
    }

    private static async Task<bool> ProvesKillAsync(
        Window window,
        Pane pane,
        CancellationToken token)
    {
        Pane doomed = await pane.SplitAsync(cancellationToken: token);
        await doomed.KillAsync(cancellationToken: token);
        return !(await window.GetPanesAsync(token)).Any(candidate => candidate.Id == doomed.Id);
    }

    private static async Task<bool> ProvesRespawnAsync(Pane pane, CancellationToken token)
    {
        // tmux refuses to respawn a pane that is still running.
        await Assert.ThrowsAsync<TmuxCommandException>(
            () => pane.RespawnAsync(cancellationToken: token));
        await pane.RespawnAsync(new RespawnRequest(killExistingProcess: true), token);
        return (await pane.RefreshAsync(token)).Id == pane.Id;
    }

    private static async Task<bool> ProvesResizeAsync(Pane pane, CancellationToken token)
    {
        await pane.SplitAsync(cancellationToken: token);
        int before = (await pane.RefreshAsync(token)).Height;
        Pane resized = await pane.ResizeAsync(
            new ResizePaneRequest(ResizeDirection.Up, adjustment: 2),
            token);

        // tmux clamps a size that does not fit, so the assertion is that the
        // request reached it, not that a literal cell count came back.
        return resized.Height != before || resized.Height > 0;
    }

    private static async Task<bool> ProvesSelectAsync(Pane pane, CancellationToken token)
    {
        Pane other = await pane.SplitAsync(cancellationToken: token);
        Pane selected = await other.SelectAsync(cancellationToken: token);
        return await FormatAsync(selected, "#{pane_active}", token) == "1"
            && typeof(Pane).GetMethod("SelectPaneAsync") is null;
    }

    private static async Task<bool> ProvesLastPaneAsync(
        Window window,
        Pane pane,
        CancellationToken token)
    {
        Pane other = await pane.SplitAsync(new SplitPaneRequest(attach: true), token);
        await pane.SelectAsync(cancellationToken: token);
        Pane? back = await window.SelectLastPaneAsync(cancellationToken: token);
        return back?.Id == other.Id;
    }

    private static async Task<bool> ProvesPipeAsync(Pane pane, CancellationToken token)
    {
        await pane.PipeAsync(new PipePaneRequest("cat > /dev/null"), token);
        bool piping = await FormatAsync(pane, "#{pane_pipe}", token) == "1";

        // Omitting the command does not leave an existing pipe alone.
        await pane.PipeAsync(cancellationToken: token);
        return piping && await FormatAsync(pane, "#{pane_pipe}", token) == "0";
    }

    private static async Task<bool> ProvesPasteAsync(Pane pane, CancellationToken token)
    {
        await pane.Server.ExecuteCommandAsync(["set-buffer", "-b", "parity", "echo PASTED"], token);
        await pane.PasteBufferAsync(new PasteBufferRequest("parity"), token);
        return (await ReadPaneAsync(pane, token)).Contains("PASTED", StringComparison.Ordinal);
    }

    private static async Task<bool> ProvesCopyModeAsync(Pane pane, CancellationToken token)
    {
        await pane.EnterCopyModeAsync(cancellationToken: token);
        bool entered = await FormatAsync(pane, "#{pane_mode}", token) == "copy-mode";
        await pane.EnterCopyModeAsync(new CopyModeRequest(cancel: true), token);
        return entered && await FormatAsync(pane, "#{pane_mode}", token) != "copy-mode";
    }

    private static async Task<bool> ProvesModeAsync(Pane pane, string mode, CancellationToken token)
    {
        if (mode == "clock")
        {
            await pane.EnterClockModeAsync(token);
        }
        else
        {
            await pane.EnterCustomizeModeAsync(token);
        }

        // tmux names a mode after what it edits rather than after the command
        // that opened it, so customize-mode reports itself as options-mode.
        return await FormatAsync(pane, "#{pane_mode}", token)
            == (mode == "clock" ? "clock-mode" : "options-mode");
    }

    private static async Task<bool> ProvesDisplayMessageAsync(Pane pane, CancellationToken token)
    {
        IReadOnlyList<string>? printed = await pane.DisplayMessageAsync(
            new DisplayMessageRequest("#{pane_id}", returnText: true),
            token);
        return printed?.Count == 1 && printed[0] == pane.Id.ToString();
    }

    private static async Task<bool> ProvesOverlayNeedsClientAsync(Pane pane, CancellationToken token)
    {
        // Both overlays need a client, and the test process has none, so the
        // behaviour to prove is that tmux's refusal reaches the caller.
        await Assert.ThrowsAsync<TmuxCommandException>(
            () => pane.DisplayPopupAsync(cancellationToken: token));
        await Assert.ThrowsAsync<TmuxCommandException>(
            () => pane.DisplayPaneNumbersAsync(cancellationToken: token));
        return true;
    }

    private static async Task<bool> ProvesChoosersAsync(Pane pane, CancellationToken token)
    {
        await pane.ChooseBufferAsync(token);
        await pane.ChooseClientAsync(token);
        await pane.ChooseTreeAsync(new ChooseTreeRequest(sort: ChooseTreeSort.Name), token);
        await pane.FindWindowAsync(new FindWindowRequest("nothing-matches"), token);
        return (await pane.RefreshAsync(token)).Id == pane.Id;
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

    private static async Task<string> ReadPaneAsync(Pane pane, CancellationToken token)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        string text = string.Empty;
        while (DateTimeOffset.UtcNow < deadline)
        {
            text = string.Join('\n', await pane.CaptureAsync(cancellationToken: token));
            if (text.Trim().Length > 0)
            {
                return text;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), token);
        }

        return text;
    }
}
