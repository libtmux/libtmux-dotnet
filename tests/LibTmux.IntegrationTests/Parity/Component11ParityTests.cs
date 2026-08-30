using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Internal;
using LibTmux.Query;

namespace LibTmux.IntegrationTests.Parity;

[UnsupportedOSPlatform("windows")]
public sealed class Component11ParityTests
{
    private sealed record PaneRow(string PaneId);

    public static TheoryData<string> OwnedRows =>
    [
        "libtmux.session:Session.last_window",
        "libtmux.window:<module>",
        "libtmux.window:Window",
        "libtmux.window:Window.__getitem__",
        "libtmux.window:Window.display_message",
        "libtmux.window:Window.find_where",
        "libtmux.window:Window.get",
        "libtmux.window:Window.get_by_id",
        "libtmux.window:Window.height",
        "libtmux.window:Window.index",
        "libtmux.window:Window.kill",
        "libtmux.window:Window.kill_window",
        "libtmux.window:Window.link",
        "libtmux.window:Window.list_panes",
        "libtmux.window:Window.move_window",
        "libtmux.window:Window.name",
        "libtmux.window:Window.new_pane",
        "libtmux.window:Window.new_window",
        "libtmux.window:Window.next_layout",
        "libtmux.window:Window.previous_layout",
        "libtmux.window:Window.refresh",
        "libtmux.window:Window.rename_window",
        "libtmux.window:Window.resize",
        "libtmux.window:Window.respawn",
        "libtmux.window:Window.rotate",
        "libtmux.window:Window.search_panes",
        "libtmux.window:Window.select",
        "libtmux.window:Window.select_layout",
        "libtmux.window:Window.select_pane",
        "libtmux.window:Window.select_window",
        "libtmux.window:Window.server",
        "libtmux.window:Window.session",
        "libtmux.window:Window.split",
        "libtmux.window:Window.split_window",
        "libtmux.window:Window.swap",
        "libtmux.window:Window.unlink",
        "libtmux.window:Window.where",
        "libtmux.window:Window.width",
        "libtmux:Window",
    ];

    [Theory(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    [MemberData(nameof(OwnedRows))]
    public async Task Owned_parity_row_has_topology_behavior(string pythonSymbolId)
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

        bool proved = pythonSymbolId switch
        {
            // The module and class rows are proved by the handle carrying live
            // window state, not by a compile-time property of the type.
            "libtmux.window:<module>" or "libtmux.window:Window" or "libtmux:Window" =>
                window.Id.ToString().StartsWith('@')
                && window.Name.Length > 0
                && window.Generation == session.Generation,
            "libtmux.window:Window.name" => window.Name.Length > 0,
            "libtmux.window:Window.index" => window.Index >= 0,
            "libtmux.window:Window.height" => window.Height > 0,
            "libtmux.window:Window.width" => window.Width > 0,
            "libtmux.window:Window.server" => ReferenceEquals(window.Server, server),
            "libtmux.window:Window.session" => window.Session.Id == session.Id,
            "libtmux.window:Window.refresh" =>
                (await window.RefreshAsync(token)).Id == window.Id,
            "libtmux.window:Window.rename_window" =>
                (await window.RenameAsync("renamed", token)).Name == "renamed",
            "libtmux.window:Window.select" or "libtmux.window:Window.select_window" =>
                await ProvesSelectAsync(session, window, token),
            "libtmux.window:Window.kill" or "libtmux.window:Window.kill_window" =>
                await ProvesKillAsync(session, token),
            "libtmux.window:Window.new_window" => await ProvesCreateWindowAsync(window, token),
            "libtmux.window:Window.split" or "libtmux.window:Window.split_window" =>
                await ProvesSplitAsync(window, token),
            "libtmux.window:Window.list_panes" =>
                (await window.GetPanesAsync(token)).Count == 1,
            "libtmux.window:Window.get_by_id"
                or "libtmux.window:Window.__getitem__"
                or "libtmux.window:Window.get" =>
                await ProvesPaneLookupAsync(window, token),
            "libtmux.window:Window.where" or "libtmux.window:Window.find_where" =>
                await ProvesMatchingAsync(window, token),
            "libtmux.window:Window.select_pane" => await ProvesSelectPaneAsync(window, token),
            "libtmux.window:Window.search_panes" =>
                (await window.SearchPanesAsync(new UnsafeTmuxFilter("1"), token)).Count == 1,
            "libtmux.window:Window.link" or "libtmux.window:Window.unlink" =>
                await ProvesLinkAsync(server, window, token),
            "libtmux.window:Window.move_window" => await ProvesMoveAsync(window, token),
            "libtmux.window:Window.swap" => await ProvesSwapAsync(session, window, token),
            "libtmux.window:Window.resize" => await ProvesResizeAsync(window, token),
            "libtmux.window:Window.rotate" => await ProvesRotateAsync(window, token),
            "libtmux.window:Window.respawn" => await ProvesRespawnAsync(window, token),
            "libtmux.window:Window.select_layout"
                or "libtmux.window:Window.next_layout"
                or "libtmux.window:Window.previous_layout" =>
                await ProvesLayoutAsync(window, token),
            "libtmux.window:Window.new_pane" => await ProvesCreatePaneAsync(server, window, token),
            "libtmux.window:Window.display_message" =>
                await ProvesDisplayMessageAsync(window, token),
            "libtmux.session:Session.last_window" => await ProvesLastWindowAsync(session, token),
            _ => false,
        };

        Assert.True(proved, $"Parity behavior was not proved for {pythonSymbolId}.");
    }

    private static async Task<bool> ProvesSelectAsync(
        Session session,
        Window window,
        CancellationToken token)
    {
        await session.CreateWindowAsync(new NewWindowRequest(name: "other"), token);
        Window selected = await window.SelectAsync(token);

        // The deprecated Python spelling has no C# member; selection is one
        // verb on the window itself.
        return selected.Snapshot?["window_active"] == "1"
            && typeof(Window).GetMethod("SelectWindowAsync") is null;
    }

    private static async Task<bool> ProvesKillAsync(Session session, CancellationToken token)
    {
        Window doomed = await session.CreateWindowAsync(new NewWindowRequest(name: "doomed"), token);
        await doomed.KillAsync(cancellationToken: token);
        return !(await session.GetWindowsAsync(token)).Any(w => w.Id == doomed.Id)
            && typeof(Window).GetMethod("KillWindowAsync") is null;
    }

    private static async Task<bool> ProvesCreateWindowAsync(Window window, CancellationToken token)
    {
        Window created = await window.CreateWindowAsync(
            new NewWindowRequest(name: "next", direction: WindowDirection.After),
            token);
        return created.Name == "next" && created.Index == window.Index + 1;
    }

    private static async Task<bool> ProvesSplitAsync(Window window, CancellationToken token)
    {
        Pane created = await window.SplitPaneAsync(cancellationToken: token);
        IReadOnlyList<Pane> panes = await window.GetPanesAsync(token);
        return panes.Count == 2
            && panes.Any(pane => pane.Id == created.Id)
            && typeof(Window).GetMethod("SplitWindowAsync") is null;
    }

    private static async Task<bool> ProvesPaneLookupAsync(Window window, CancellationToken token)
    {
        Pane only = await TestHierarchy.RequireFirstPaneAsync(window, token);
        Pane? found = await window.GetPaneAsync(only.Id.ToString(), token);

        // A Python __getitem__ or get() becomes a named lookup that answers
        // null rather than raising on an absent pane.
        return found?.Id == only.Id
            && await window.GetPaneAsync("%9999", token) is null
            && typeof(Window).GetProperties().All(property => property.Name != "Item");
    }

    private static async Task<bool> ProvesMatchingAsync(Window window, CancellationToken token)
    {
        await window.SplitPaneAsync(cancellationToken: token);

        // where()/find_where() become one expression surface over captured rows,
        // filtering tmux's reported format fields rather than handle members.
        IReadOnlyList<Pane> panes = await window.GetPanesAsync(token);
        IReadOnlyList<PaneRow> rows = [.. panes.Select(pane => new PaneRow(pane.Id.ToString()))];
        string first = panes[0].Id.ToString();

        Assert.Equal(2, rows.Count);
        Assert.Single(rows.Matching<PaneRow>(row => row.PaneId == first));
        Assert.Equal(
            first,
            rows.Matching<PaneRow>(row => row.PaneId == first).SingleOrDefault()?.PaneId);
        Assert.Null(typeof(Window).GetMethod("Where"));
        Assert.Null(typeof(Window).GetMethod("FindWhere"));
        return true;
    }

    private static async Task<bool> ProvesSelectPaneAsync(Window window, CancellationToken token)
    {
        Pane created = await window.SplitPaneAsync(cancellationToken: token);
        Pane? selected = await window.SelectPaneAsync(created.Id.ToString(), token);
        return selected?.Id == created.Id;
    }

    private static async Task<bool> ProvesLinkAsync(
        Server server,
        Window window,
        CancellationToken token)
    {
        Session guest = await server.CreateSessionAsync(new NewSessionRequest(name: "guest"), token);
        await window.LinkAsync(new LinkWindowRequest(guest.Id.ToString(), "7"), token);
        Window linked = (await guest.GetWindowsAsync(token)).Single(w => w.Id == window.Id);
        await linked.UnlinkAsync(cancellationToken: token);
        return linked.Index == 7
            && !(await guest.GetWindowsAsync(token)).Any(w => w.Id == window.Id);
    }

    private static async Task<bool> ProvesMoveAsync(Window window, CancellationToken token)
    {
        Window moved = await window.MoveAsync(new MoveWindowRequest("6"), token);
        return moved.Index == 6 && moved.Id == window.Id;
    }

    private static async Task<bool> ProvesSwapAsync(
        Session session,
        Window window,
        CancellationToken token)
    {
        Window partner = await session.CreateWindowAsync(new NewWindowRequest(name: "swap"), token);
        int partnerIndex = partner.Index;
        await window.SwapAsync(partner.Id, cancellationToken: token);
        return (await window.RefreshAsync(token)).Index == partnerIndex;
    }

    private static async Task<bool> ProvesResizeAsync(Window window, CancellationToken token)
    {
        Window resized = await window.ResizeAsync(
            new ResizeWindowRequest(width: 88, height: 29),
            token);
        return resized.Width == 88 && resized.Height == 29;
    }

    private static async Task<bool> ProvesRotateAsync(Window window, CancellationToken token)
    {
        Pane created = await window.SplitPaneAsync(cancellationToken: token);
        IReadOnlyList<Pane> before = await window.GetPanesAsync(token);
        await window.RotateAsync(WindowRotationDirection.Down, cancellationToken: token);
        IReadOnlyList<Pane> after = await window.GetPanesAsync(token);

        // Rotation reorders the same panes rather than creating any.
        return before.Count == after.Count
            && after.Any(pane => pane.Id == created.Id)
            && !before.Select(pane => pane.Id).SequenceEqual(after.Select(pane => pane.Id));
    }

    private static async Task<bool> ProvesRespawnAsync(Window window, CancellationToken token)
    {
        await window.SplitPaneAsync(cancellationToken: token);

        // tmux refuses to respawn a window that is still running.
        await Assert.ThrowsAsync<TmuxCommandException>(
            () => window.RespawnAsync(cancellationToken: token));
        await window.RespawnAsync(new RespawnRequest(killExistingProcess: true), token);
        return (await window.GetPanesAsync(token)).Count == 1;
    }

    private static async Task<bool> ProvesLayoutAsync(Window window, CancellationToken token)
    {
        await window.SplitPaneAsync(cancellationToken: token);
        Window applied = await window.SelectLayoutAsync(
            new SelectLayoutRequest("even-horizontal"),
            token);
        Window next = await applied.SelectNextLayoutAsync(token);
        Window previous = await next.SelectPreviousLayoutAsync(token);

        // An unknown name never reaches tmux: 3.3a would take the server down.
        await Assert.ThrowsAsync<TmuxWindowException>(
            () => window.SelectLayoutAsync(new SelectLayoutRequest("no-such-layout"), token));
        return previous.Id == window.Id;
    }

    private static async Task<bool> ProvesCreatePaneAsync(
        Server server,
        Window window,
        CancellationToken token)
    {
        bool supported = TmuxCapabilities.IsSupported(
            server.Version!.Value,
            "new_pane_command");
        if (!supported)
        {
            // The command does not exist before 3.7, so there is nothing to
            // omit and nothing worth dispatching.
            await Assert.ThrowsAsync<TmuxVersionTooLowException>(
                () => window.CreatePaneAsync(cancellationToken: token));
            return true;
        }

        Pane created = await window.CreatePaneAsync(
            new NewPaneRequest(width: 20, height: 5, x: 2, y: 2),
            token);
        return (await window.GetPanesAsync(token)).Any(pane => pane.Id == created.Id);
    }

    private static async Task<bool> ProvesDisplayMessageAsync(
        Window window,
        CancellationToken token)
    {
        IReadOnlyList<string>? printed = await window.DisplayMessageAsync(
            new DisplayMessageRequest("#{window_id}", returnText: true),
            token);
        return printed?.Count == 1 && printed[0] == window.Id.ToString();
    }

    private static async Task<bool> ProvesLastWindowAsync(Session session, CancellationToken token)
    {
        Window first = await TestHierarchy.RequireFirstWindowAsync(session, token);
        Window second = await session.CreateWindowAsync(
            new NewWindowRequest(name: "second", attach: true),
            token);
        await first.SelectAsync(token);
        Window back = await session.SelectLastWindowAsync(token);
        return back.Id == second.Id;
    }
}
