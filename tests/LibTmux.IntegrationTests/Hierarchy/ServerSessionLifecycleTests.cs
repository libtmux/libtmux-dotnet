using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Internal;
using Microsoft.Extensions.Logging;

namespace LibTmux.IntegrationTests.Hierarchy;

[UnsupportedOSPlatform("windows")]
public sealed class ServerSessionLifecycleTests
{
    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task Refresh_returns_replacement_and_owned_scope_cleans_up()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);

        SessionId ownedId;
        await using (OwnedSessionScope scope = await server.CreateOwnedSessionAsync(
            new NewSessionRequest(name: "owned"),
            token))
        {
            ownedId = scope.Value.Id;
            Assert.Equal("owned", scope.Value.Name);

            Session renamed = await scope.Value.RenameAsync("renamed", token);

            // Handles are immutable: renaming yields a replacement and leaves
            // the original a truthful record of what was read.
            Assert.Equal("renamed", renamed.Name);
            Assert.Equal("owned", scope.Value.Name);
            Assert.Equal(scope.Value.Id, renamed.Id);
            Assert.Equal("renamed", (await scope.Value.RefreshAsync(token)).Name);
            Assert.True(await server.HasSessionAsync("renamed", true, token));

            // tmux 3.2a rewrites ':' to '_' and 3.7b stores it verbatim, so the
            // name is refused before either server sees it.
            await Assert.ThrowsAsync<ArgumentException>(
                () => scope.Value.RenameAsync("a:b", token));
            await Assert.ThrowsAsync<ArgumentException>(
                () => scope.Value.RenameAsync("a.b", token));
            Assert.Equal("renamed", (await scope.Value.RefreshAsync(token)).Name);
        }

        // Owning a session means disposing it removes it; a handle obtained
        // any other way would have left it running.
        Assert.DoesNotContain(
            await server.GetSessionsAsync(token),
            session => session.Id == ownedId);
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task New_session_flags_emit_exact_argv()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);

        Session created = await server.CreateSessionAsync(
            new NewSessionRequest(
                name: "flags",
                startDirectory: "/tmp",
                windowName: "first",
                width: "132",
                height: "40",
                environment: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["LIBTMUX_FLAG"] = "set",
                }),
            token);

        Assert.Equal("flags", created.Name);
        Assert.Equal("first", (await created.GetWindowsAsync(token))[0].Snapshot?["window_name"]);
        RawTmuxResult environment = await raw.ExecuteAsync(
            ["show-environment", "-t", created.Id.ToString(), "LIBTMUX_FLAG"],
            token);
        Assert.Equal("LIBTMUX_FLAG=set", environment.StandardOutputLines[0]);

        // The size and directory are read back off the server rather than
        // trusted. 132x40 is deliberately not tmux's 80x24 default for a
        // detached session, so these assertions fail if the flags never landed.
        RawTmuxResult shape = await RequireRawSuccessAsync(
            raw,
            [
                "display-message",
                "-p",
                "-t",
                created.Id.ToString(),
                "#{window_width} #{window_height} #{pane_current_path}",
            ],
            token);
        string[] observed = shape.StandardOutputLines[0].Split(' ');

        // tmux reports the resolved path, not the one requested -- on macOS
        // /tmp is a symlink to /private/tmp -- so both sides are resolved first.
        Assert.Equal(
            Path.GetFullPath(new DirectoryInfo("/tmp").ResolveLinkTarget(true)?.FullName ?? "/tmp"),
            Path.GetFullPath(new DirectoryInfo(observed[2]).ResolveLinkTarget(true)?.FullName ?? observed[2]));

        // tmux 3.2a ignores -x/-y for a second session, sizing it from the
        // existing session's height minus the status line; 3.3a+ honours -x/-y.
        string[] expectedSize = server.Version!.Value < TmuxVersion.Parse("3.3a")
            ? ["80", "23"]
            : ["132", "40"];
        Assert.Equal(expectedSize, observed[..2]);

        // tmux hands -c straight to chdir, so a surviving '~' would fail there
        // and silently drop the pane in the home directory instead.
        string home = System.Environment.GetFolderPath(
            System.Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Join(home, "work"), StartDirectory.Resolve("~/work"));
        Assert.Equal(home, StartDirectory.Resolve("~"));
        Assert.Equal("/tmp", StartDirectory.Resolve("/tmp"));
        Assert.Null(StartDirectory.Resolve(string.Empty));

        // tmux parses ':' and '.' as target separators, so a name carrying one
        // would silently address a different object.
        await Assert.ThrowsAsync<ArgumentException>(
            () => server.CreateSessionAsync(new NewSessionRequest(name: "a:b"), token));

        // Taking a name twice is reported as such rather than as a bare
        // command failure, so a caller can pick another name.
        TmuxSessionExistsException taken = await Assert.ThrowsAsync<TmuxSessionExistsException>(
            () => server.CreateSessionAsync(new NewSessionRequest(name: "flags"), token));
        Assert.Equal("flags", taken.SessionName);

        // Replacing removes the old session rather than attaching to it: tmux
        // offers no replace flag, and its nearest offer needs a terminal.
        Session replaced = await server.CreateSessionAsync(
            new NewSessionRequest(name: "flags", replaceExisting: true),
            token);
        Assert.Equal("flags", replaced.Name);
        Assert.NotEqual(created.Id, replaced.Id);
        Assert.Single(await server.GetSessionsAsync(token), session => session.Name == "flags");
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task Session_selection_and_attachment_flags_emit_exact_argv()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        Session session = await TestHierarchy.RequireFirstSessionAsync(server, token);
        await session.CreateWindowAsync(new NewWindowRequest(name: "second"), token);

        // ActiveWindow is served from the session's own captured fields, which
        // carry the active window's identity but not that window's fields.
        WindowId second = (await session.GetWindowsAsync(token))
            .Single(window => window.Snapshot?["window_name"] == "second")
            .Id;

        // Selection reports the window tmux settled on, so the caller never has
        // to re-read the session to find out what it selected.
        Assert.Equal(second, (await session.SelectWindowAsync("second", token)).Id);
        Assert.NotEqual(second, (await session.SelectPreviousWindowAsync(token)).Id);
        Assert.Equal(second, (await session.SelectNextWindowAsync(token)).Id);
        Assert.Equal(second, (await session.RefreshAsync(token)).ActiveWindow.Id);

        // tmux accepts ':' inside a window name, so the target stays anchored
        // to this session; tmux 3.7 alone refuses such a name (3.7a restored it).
        if (server.Version!.Value != TmuxVersion.Parse("3.7"))
        {
            await RequireRawSuccessAsync(
                raw,
                ["new-window", "-d", "-t", $"{session.Id}:", "-n", "a:b"],
                token);
            Window colon = await session.SelectWindowAsync("a:b", token);
            Assert.Equal("a:b", colon.Snapshot?["window_name"]);
        }

        // A server-level attach has no session to fall back to.
        await Assert.ThrowsAsync<ArgumentException>(
            () => server.AttachSessionAsync(new AttachSessionRequest(), token));

        // Client flags ride one comma-separated -f value: tmux reads the flag
        // once and keeps the last, so repeating it would discard all but one.
        TmuxCommandException attach = await Assert.ThrowsAsync<TmuxCommandException>(
            () => session.AttachAsync(
                new AttachSessionRequest(clientFlags: ["no-output", "read-only"]),
                token));
        Assert.Contains("-f", attach.Result.Arguments);
        Assert.Contains("no-output,read-only", attach.Result.Arguments);
        Assert.Single(attach.Result.Arguments, argument => argument == "-f");
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task Refresh_after_external_selection_captures_active_window_and_pane_relations()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        Session session = await TestHierarchy.RequireFirstSessionAsync(server, token);
        string target = session.Id.ToString();

        WindowId firstWindow = session.ActiveWindow.Id;
        PaneId firstPane = session.ActivePane.Id;

        // The change is made behind the library's back, so nothing the handle
        // did could have refreshed it as a side effect.
        await RequireRawSuccessAsync(raw, ["new-window", "-d", "-t", $"{target}:", "-n", "outside"], token);
        await RequireRawSuccessAsync(raw, ["split-window", "-d", "-t", $"{target}:outside"], token);
        await RequireRawSuccessAsync(raw, ["select-window", "-t", $"{target}:outside"], token);
        await RequireRawSuccessAsync(raw, ["select-pane", "-t", $"{target}:outside.1"], token);

        // The original handle is a record of what was read, not a live view.
        Assert.Equal(firstWindow, session.ActiveWindow.Id);
        Assert.Equal(firstPane, session.ActivePane.Id);

        Session refreshed = await session.RefreshAsync(token);

        Assert.Equal(session.Id, refreshed.Id);
        Assert.NotEqual(firstWindow, refreshed.ActiveWindow.Id);
        Assert.NotEqual(firstPane, refreshed.ActivePane.Id);

        // The refreshed relations name the objects tmux actually selected.
        RawTmuxResult active = await RequireRawSuccessAsync(
            raw,
            ["display-message", "-p", "-t", target, "#{window_id} #{pane_id}"],
            token);
        Assert.Equal(
            $"{refreshed.ActiveWindow.Id} {refreshed.ActivePane.Id}",
            active.StandardOutputLines[0]);
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task KillSessionGroupVersionPolicy()
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

        // "member" joins "grouped"'s session group; "solo" stands apart, so a
        // group kill and a single kill leave different servers behind.
        await RequireRawSuccessAsync(raw, ["new-session", "-d", "-s", "grouped"], token);
        await RequireRawSuccessAsync(
            raw,
            ["new-session", "-d", "-s", "member", "-t", "grouped"],
            token);
        await RequireRawSuccessAsync(raw, ["new-session", "-d", "-s", "solo"], token);

        TmuxVersion version = Assert.NotNull(server.Version);
        bool supported = TmuxCapabilities.IsSupported(version, "kill_session_group");
        Session grouped = (await server.GetSessionsAsync(token))
            .Single(session => session.Name == "grouped");

        await grouped.KillAsync(group: true, cancellationToken: token);

        HashSet<string> remaining =
        [
            .. (await server.GetSessionsAsync(token)).Select(session => session.Name),
        ];

        // Either way the session that was named is gone, and the session that
        // was never in the group is untouched.
        Assert.DoesNotContain("grouped", remaining);
        Assert.Contains("solo", remaining);
        if (supported)
        {
            // tmux 3.7 carries -g, so one dispatch takes the whole group.
            Assert.DoesNotContain("member", remaining);
            Assert.Empty(logger.Warnings);
        }
        else
        {
            // Older tmux rejects -g and would then kill nothing at all, so the
            // flag is omitted and the named session dies on its own.
            Assert.Contains("member", remaining);
            Assert.Single(logger.Warnings);
        }
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task Kill_detach_and_window_placement_flags_reach_tmux()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        Session session = await TestHierarchy.RequireFirstSessionAsync(server, token);

        // -E runs a command on the detached clients; it is not a client target,
        // and the session scope has to survive alongside it.
        TmuxCommandException detach = await Assert.ThrowsAsync<TmuxCommandException>(
            () => session.DetachClientAsync("true", token));
        Assert.Equal(
            ["detach-client", "-s", session.Id.ToString(), "-E", "true"],
            detach.Result.Arguments);

        // A window is placed relative to the window it names, not the session's
        // current one, so -a against "first" must land immediately after it.
        await session.CreateWindowAsync(new NewWindowRequest(name: "first"), token);
        await session.CreateWindowAsync(new NewWindowRequest(name: "last"), token);
        await session.SelectWindowAsync("last", token);
        Window inserted = await session.CreateWindowAsync(
            new NewWindowRequest(
                name: "inserted",
                targetWindow: "first",
                direction: WindowDirection.After),
            token);
        string[] order =
        [
            .. (await session.GetWindowsAsync(token))
                .OrderBy(window => int.Parse(
                    window.Snapshot?["window_index"] ?? "0",
                    System.Globalization.CultureInfo.InvariantCulture))
                .Select(window => window.Snapshot?["window_name"] ?? string.Empty),
        ];
        Assert.Equal("inserted", inserted.Snapshot?["window_name"]);
        Assert.Equal(
            Array.IndexOf(order, "first") + 1,
            Array.IndexOf(order, "inserted"));

        // An index and a target window both name a position, so asking for
        // both is rejected rather than silently resolved one way.
        Assert.Throws<ArgumentException>(
            () => new NewWindowRequest(index: "3", targetWindow: "first"));

        // -C clears alerts and leaves the session running; -a kills every other
        // session and leaves this one.
        Session spare = await server.CreateSessionAsync(new NewSessionRequest(name: "spare"), token);
        await session.KillAsync(clearAlerts: true, cancellationToken: token);
        Assert.True(await server.HasSessionAsync(session.Name, true, token));
        Assert.True(await server.HasSessionAsync("spare", true, token));

        await session.KillAsync(allExcept: true, cancellationToken: token);
        Assert.True(await server.HasSessionAsync(session.Name, true, token));
        Assert.False(await server.HasSessionAsync("spare", true, token));
        Assert.DoesNotContain(await server.GetSessionsAsync(token), other => other.Id == spare.Id);
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

    private static async Task<RawTmuxResult> RequireRawSuccessAsync(
        RawTmuxTestContext raw,
        IReadOnlyList<string> arguments,
        CancellationToken token)
    {
        RawTmuxResult result = await raw.ExecuteAsync(arguments, token);
        Assert.Equal(0, result.ExitCode);
        return result;
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
