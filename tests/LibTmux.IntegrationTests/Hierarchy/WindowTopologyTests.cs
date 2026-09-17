using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Internal;
using Microsoft.Extensions.Logging;

namespace LibTmux.IntegrationTests.Hierarchy;

[UnsupportedOSPlatform("windows")]
public sealed class WindowTopologyTests
{
    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task Linked_window_moves_preserve_session_scoped_indexes()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        Session home = await TestHierarchy.RequireFirstSessionAsync(server, token);
        Session guest = await server.CreateSessionAsync(new NewSessionRequest(name: "guest"), token);

        Window shared = await home.CreateWindowAsync(new NewWindowRequest(name: "shared"), token);
        int homeIndex = shared.Index;

        await shared.LinkAsync(new LinkWindowRequest(guest.Id.ToString(), "9"), token);

        // The same window now holds a different index in each session, so a
        // handle's Index is the index of the session it was read through.
        Window inGuest = await guest.GetWindowAsync(shared.Id, token);
        Assert.Equal(9, inGuest.Index);
        Assert.Equal("guest", inGuest.Session.Name);
        Assert.Equal(guest.Id, inGuest.ActivePane.Single().Session.Id);
        Assert.Equal(9, inGuest.ActivePane.Single().Window.Index);
        Assert.Equal(guest.Id, (await inGuest.GetPanesAsync(token))[0].Session.Id);
        Assert.Equal(home.Id, (await shared.GetPanesAsync(token))[0].Session.Id);
        Assert.Equal(homeIndex, (await shared.RefreshAsync(token)).Index);

        Window moved = await inGuest.MoveAsync(new MoveWindowRequest("3"), token);

        // Moving the guest link leaves the home link exactly where it was; a
        // bare window id would have let tmux move whichever link it chose.
        Assert.Equal(3, moved.Index);
        Assert.Equal(homeIndex, (await shared.RefreshAsync(token)).Index);
        Assert.Equal(
            homeIndex,
            (await home.GetWindowsAsync(token)).Single(window => window.Id == shared.Id).Index);

        // The pre-move handle still names the index it was read at, so it now
        // addresses a link that is no longer there. That is the immutability
        // contract, not a bug: the replacement is the live one.
        await Assert.ThrowsAsync<TmuxCommandException>(
            () => inGuest.UnlinkAsync(cancellationToken: token));

        await moved.UnlinkAsync(cancellationToken: token);

        Assert.DoesNotContain(await guest.GetWindowsAsync(token), w => w.Id == shared.Id);
        Assert.Contains(await home.GetWindowsAsync(token), w => w.Id == shared.Id);
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task Same_session_window_links_keep_each_placements_live_reads_apart()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        Session session = await TestHierarchy.RequireFirstSessionAsync(server, token);
        Window first = await TestHierarchy.RequireFirstWindowAsync(session, token);
        int originalIndex = first.Index;

        // Link the window into its own session a second time. Both
        // placements now answer to the same window id, so a target naming
        // only the session and that id cannot tell them apart.
        await first.LinkAsync(new LinkWindowRequest(session.Id.ToString(), "7"), token);

        Window[] placements =
        [
            .. (await session.GetWindowsAsync(token))
                .Where(window => window.Id == first.Id)
                .OrderBy(window => window.Index),
        ];
        Assert.Equal([originalIndex, 7], placements.Select(window => window.Index));
        Window atOriginalIndex = placements[0];
        Window atSecondIndex = placements[1];

        // Each handle's live reads answer for the index it was read at, not
        // whichever placement tmux's window-id target ranks best.
        Assert.Equal(
            originalIndex,
            Assert.Single(await atOriginalIndex.GetPanesAsync(token)).Window.Index);
        Assert.Equal(7, Assert.Single(await atSecondIndex.GetPanesAsync(token)).Window.Index);
        Assert.Equal(originalIndex, (await atOriginalIndex.RefreshAsync(token)).Index);
        Assert.Equal(7, (await atSecondIndex.RefreshAsync(token)).Index);
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task New_split_move_link_swap_resize_rotate_and_respawn_flags_emit_exact_argv()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        Session session = await TestHierarchy.RequireFirstSessionAsync(server, token);
        Window first = await session.CreateWindowAsync(new NewWindowRequest(name: "first"), token);

        // A window created against another lands next to it rather than at the
        // session's current window.
        Window inserted = await first.CreateWindowAsync(
            new NewWindowRequest(name: "inserted", direction: WindowDirection.After),
            token);
        Assert.Equal(first.Index + 1, inserted.Index);

        // An index and a target window both say where the window goes.
        Assert.Throws<ArgumentException>(
            () => new NewWindowRequest(index: "4", targetWindow: "@0"));

        Pane split = await first.SplitPaneAsync(
            new SplitPaneRequest(direction: PaneDirection.Right, percentage: 40),
            token);
        Assert.Equal(2, (await first.GetPanesAsync(token)).Count);
        Assert.Contains(await first.GetPanesAsync(token), pane => pane.Id == split.Id);

        // A size in cells and a percentage are two ways to say the same thing.
        Assert.Throws<ArgumentException>(() => new SplitPaneRequest(size: "10", percentage: 40));

        // Resizing is exact on every lane, unlike new-session's -x/-y.
        Window resized = await first.ResizeAsync(new ResizeWindowRequest(width: 92, height: 31), token);
        Assert.Equal(92, resized.Width);
        Assert.Equal(31, resized.Height);

        // tmux applies a mode after a size and discards the loser, so the
        // request refuses the pair rather than letting one vanish.
        Assert.Throws<ArgumentException>(
            () => new ResizeWindowRequest(width: 10, mode: WindowResizeMode.Expand));
        Assert.Throws<ArgumentException>(() => new ResizeWindowRequest(direction: ResizeDirection.Up));

        Window rotated = await first.RotateAsync(WindowRotationDirection.Down, cancellationToken: token);
        Assert.Equal(first.Id, rotated.Id);

        // Respawning a live window needs permission to kill what is running.
        await Assert.ThrowsAsync<TmuxCommandException>(
            () => first.RespawnAsync(cancellationToken: token));
        await first.RespawnAsync(new RespawnRequest(killExistingProcess: true), token);
        Assert.Single(await first.GetPanesAsync(token));

        Window swapPartner = await session.CreateWindowAsync(
            new NewWindowRequest(name: "partner"),
            token);
        int before = swapPartner.Index;
        await first.SwapAsync(swapPartner.Id, cancellationToken: token);
        Assert.Equal(before, (await first.RefreshAsync(token)).Index);
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task Killed_window_is_a_raising_tombstone()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        Session session = await TestHierarchy.RequireFirstSessionAsync(server, token);
        Window doomed = await session.CreateWindowAsync(new NewWindowRequest(name: "doomed"), token);

        await doomed.KillAsync(cancellationToken: token);

        // The handle keeps reporting what it read; it is a record, not a view.
        Assert.Equal("doomed", doomed.Name);

        // Reaching for fresh state says the window is gone rather than
        // reporting an unrelated tmux failure.
        await Assert.ThrowsAsync<TmuxObjectNotFoundException>(() => doomed.RefreshAsync(token));
        await Assert.ThrowsAsync<TmuxCommandException>(() => doomed.RenameAsync("late", token));
        Assert.DoesNotContain(await session.GetWindowsAsync(token), w => w.Id == doomed.Id);

        // Killing every other window leaves exactly one behind.
        Window keeper = await session.CreateWindowAsync(new NewWindowRequest(name: "keeper"), token);
        await session.CreateWindowAsync(new NewWindowRequest(name: "spare"), token);
        await keeper.KillAsync(allExcept: true, cancellationToken: token);
        Assert.Single(await session.GetWindowsAsync(token));
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task Unknown_layouts_are_refused_before_tmux_sees_them()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        Session session = await TestHierarchy.RequireFirstSessionAsync(server, token);
        Window window = await session.CreateWindowAsync(new NewWindowRequest(name: "layouts"), token);
        await window.SplitPaneAsync(cancellationToken: token);
        window = await window.RefreshAsync(token);

        // Pane.Left and Pane.Top read directly, not only through the raw
        // wire names in RawFormatFields.
        foreach (Pane pane in await window.GetPanesAsync(token))
        {
            Assert.Equal(
                pane.RawFormatFields["pane_left"],
                pane.Left.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(
                pane.RawFormatFields["pane_top"],
                pane.Top.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // tmux 3.3a crashes its whole server on a layout it cannot parse,
        // taking every session on the socket with it, so the name never
        // reaches tmux unless this version is known to accept it.
        await Assert.ThrowsAsync<TmuxWindowException>(
            () => window.SelectLayoutAsync(new SelectLayoutRequest("not-a-layout"), token));
        Assert.NotEmpty(await server.GetSessionsAsync(token));

        foreach (string layout in new[] { "even-horizontal", "tiled", "main-vertical" })
        {
            Window applied = await window.SelectLayoutAsync(new SelectLayoutRequest(layout), token);
            Assert.Equal(window.Id, applied.Id);
        }

        // The mirrored layouts arrived in 3.5; below that they are refused
        // here rather than by a server that would die reporting it.
        bool mirroredKnown = server.Version!.Value >= TmuxVersion.Parse("3.5");
        Task<Window> mirrored = window.SelectLayoutAsync(
            new SelectLayoutRequest("main-vertical-mirrored"),
            token);
        if (mirroredKnown)
        {
            Assert.Equal(window.Id, (await mirrored).Id);
        }
        else
        {
            await Assert.ThrowsAsync<TmuxWindowException>(() => mirrored);
        }

        // tmux 3.8 made #{window_layout} JSON for a non-control client, and
        // select-layout accepts that dump back -- read this window's own
        // current layout through the typed property and feed it
        // straight back in, rather than fabricating one, since the string is
        // opaque either way.
        bool jsonLayoutsKnown = server.Version!.Value >= TmuxVersion.Parse("3.8");
        Window read = await window.RefreshAsync(token);
        string dumpedLayout = read.Layout;
        Assert.Equal(read.RawFormatFields["window_layout"], dumpedLayout);
        Assert.Equal(jsonLayoutsKnown, dumpedLayout.StartsWith('{'));
        Window restored = await window.SelectLayoutAsync(
            new SelectLayoutRequest(dumpedLayout),
            token);
        Assert.Equal(window.Id, restored.Id);

        // Only the JSON form (3.8+) is documented to restore byte-identical;
        // the classic form can restore the same shape under a different
        // string on 3.7 and earlier.
        if (jsonLayoutsKnown)
        {
            Assert.Equal(dumpedLayout, restored.Layout);
        }

        Assert.NotEmpty(await server.GetSessionsAsync(token));
        Window cycled = await window.SelectNextLayoutAsync(token);
        Assert.Equal(window.Id, (await cycled.SelectPreviousLayoutAsync(token)).Id);
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task Unique_layout_prefixes_resolve_the_same_way_tmux_resolves_them()
    {
        // tmux's own layout_set_lookup accepts a prefix naming exactly one
        // preset ("tile" -> tiled) and refuses one naming more than one
        // ("even-" -> even-horizontal or even-vertical); ValidateLayout
        // must resolve a unique prefix the same way.
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        Session session = await TestHierarchy.RequireFirstSessionAsync(server, token);
        Window window = await session.CreateWindowAsync(
            new NewWindowRequest(name: "layout-prefixes"),
            token);
        await window.SplitPaneAsync(cancellationToken: token);
        window = await window.RefreshAsync(token);

        Window tiled = await window.SelectLayoutAsync(new SelectLayoutRequest("tile"), token);
        Assert.Equal(window.Id, tiled.Id);
        Window evenHorizontal = await tiled.SelectLayoutAsync(
            new SelectLayoutRequest("even-h"),
            token);
        Assert.Equal(window.Id, evenHorizontal.Id);

        // "even-" names both even-horizontal and even-vertical, so tmux
        // itself refuses it; the message names the guard's reason, not the
        // connected tmux version.
        TmuxWindowException ambiguous = await Assert.ThrowsAsync<TmuxWindowException>(
            () => evenHorizontal.SelectLayoutAsync(new SelectLayoutRequest("even-"), token));
        Assert.Contains("even-horizontal", ambiguous.Message, StringComparison.Ordinal);
        Assert.Contains("even-vertical", ambiguous.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("does not know", ambiguous.Message, StringComparison.Ordinal);
        Assert.NotEmpty(await server.GetSessionsAsync(token));

        // main-vertical-mirrored (3.5+) shares the "main-v" prefix with
        // main-vertical, so whether that prefix is unique depends on the
        // connected tmux the same way the exact name's availability does.
        bool mirroredKnown = server.Version!.Value >= TmuxVersion.Parse("3.5");
        Task<Window> byMainVPrefix = window.SelectLayoutAsync(
            new SelectLayoutRequest("main-v"),
            token);
        if (mirroredKnown)
        {
            TmuxWindowException mainVAmbiguous = await Assert.ThrowsAsync<TmuxWindowException>(
                () => byMainVPrefix);
            Assert.Contains("main-vertical", mainVAmbiguous.Message, StringComparison.Ordinal);
            Assert.Contains("main-vertical-mirrored", mainVAmbiguous.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(window.Id, (await byMainVPrefix).Id);
        }

        // A prefix naming no preset at all still refuses before dispatch,
        // with the version-blaming message the exact-unknown case always had.
        TmuxWindowException unknown = await Assert.ThrowsAsync<TmuxWindowException>(
            () => window.SelectLayoutAsync(new SelectLayoutRequest("zz"), token));
        Assert.Contains("does not know the layout 'zz'", unknown.Message, StringComparison.Ordinal);
        Assert.NotEmpty(await server.GetSessionsAsync(token));
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task DisplayMessageLiteralVersionPolicy()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        RecordingLogger logger = new();
        Server server = await Server.ConnectAsync(
            new ServerConnectionOptions(
                tmuxBinaryPath: raw.TmuxBinaryPath,
                socketPath: raw.SocketPath,
                configurationFile: "/dev/null",
                logger: logger),
            token);
        Session session = await TestHierarchy.RequireFirstSessionAsync(server, token);
        Window window = await TestHierarchy.RequireFirstWindowAsync(session, token);

        bool supported = TmuxCapabilities.IsSupported(
            server.Version!.Value,
            "display_message_literal");

        IReadOnlyList<string>? literal = await window.DisplayMessageAsync(
            new DisplayMessageRequest("#{window_id}", returnText: true, noExpand: true),
            token);

        Assert.NotNull(literal);
        if (supported)
        {
            // tmux 3.4 carries -l, so the message survives unexpanded.
            Assert.Equal("#{window_id}", literal[0]);
            Assert.Empty(logger.Warnings);
        }
        else
        {
            // Older tmux has no way to suppress expansion, so the flag is
            // dropped and the caller is told the message will be expanded.
            Assert.Equal(window.Id.ToString(), literal[0]);
            Assert.Single(logger.Warnings);
        }

        // Expansion is the default either way, and it costs one command.
        IReadOnlyList<string>? expanded = await window.DisplayMessageAsync(
            new DisplayMessageRequest("#{window_id}", returnText: true),
            token);
        Assert.Equal(window.Id.ToString(), Assert.Single(expanded!));

        // Redrawing a pane while a message shows is pane-scoped.
        await Assert.ThrowsAsync<ArgumentException>(
            () => window.DisplayMessageAsync(
                new DisplayMessageRequest("x", updatePane: true),
                token));
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
