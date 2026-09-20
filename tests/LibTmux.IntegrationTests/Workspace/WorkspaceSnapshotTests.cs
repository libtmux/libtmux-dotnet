using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Internal;
using LibTmux.Workspace;

namespace LibTmux.IntegrationTests;

[UnsupportedOSPlatform("windows")]
public sealed class WorkspaceSnapshotTests
{
    [UnixFact]
    public void Captured_null_paths_remain_unspecified_without_reading_commands_or_ambient_context()
    {
        WorkspaceFile workspace = WorkspaceFile.FromSnapshot(Capture());

        Assert.Equal("captured", workspace.SessionName);
        Assert.Null(workspace.StartDirectory);
        Assert.Null(workspace.DocumentDirectory);
        Assert.Null(workspace.BeforeScript);
        Assert.Empty(workspace.Options);
        Assert.Empty(workspace.Environment);
        Assert.Empty(workspace.ShellCommandsBefore);
        WorkspaceWindow window = Assert.Single(workspace.Windows);
        Assert.Equal("editor", window.WindowName);
        Assert.Equal("even-horizontal", window.Layout);
        Assert.True(window.Focus);
        Assert.Null(window.StartDirectory);
        WorkspacePane pane = Assert.Single(window.Panes);
        Assert.True(pane.Focus);
        Assert.Null(pane.StartDirectory);
        Assert.Empty(pane.ShellCommands);
        Assert.Empty(pane.Options);
        Assert.Empty(pane.Environment);
        Assert.Empty(pane.ShellCommandsBefore);
    }

    [UnixFact]
    public void Missing_capture_is_rejected_instead_of_becoming_empty_or_inactive()
    {
        Assert.Throws<ArgumentNullException>(() => WorkspaceFile.FromSnapshot(null!));
        foreach (string omitted in new[] { "session_name", "windows", "window_name", "window_layout", "window_active", "panes", "active pane", "pane_current_path" })
        {
            Assert.Throws<IncompleteSnapshotException>(() => WorkspaceFile.FromSnapshot(Capture(omitted)));
        }
    }

    [UnixFact]
    public async Task Freeze_after_daemon_exit_preserves_contextual_order_focus_and_literal_paths()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string directory = Path.Combine(Path.GetTempPath(), $"libtmux-freeze-{Guid.NewGuid():N}");
        string firstPath = Path.Combine(directory, "$cash ${literal} #{session_name} first");
        string secondPath = Path.Combine(directory, "second space");
        Directory.CreateDirectory(firstPath);
        Directory.CreateDirectory(secondPath);
        try
        {
            await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
            Assert.Equal(0, (await raw.ExecuteAsync(["respawn-pane", "-k", "-t", "%0", "-c", firstPath.Replace("#", "#{a:35}", StringComparison.Ordinal), "exec /bin/cat"], token)).ExitCode);
            Assert.Equal(0, (await raw.ExecuteAsync(["rename-window", "-t", "@0", "editor"], token)).ExitCode);
            Assert.Equal(0, (await raw.ExecuteAsync(["split-window", "-d", "-h", "-t", "%0", "-c", secondPath, "exec /bin/cat"], token)).ExitCode);
            Assert.Equal(0, (await raw.ExecuteAsync(["new-window", "-d", "-t", "$0:3", "-n", "middle", "exec /bin/cat"], token)).ExitCode);
            Assert.Equal(0, (await raw.ExecuteAsync(["link-window", "-s", "@0", "-t", "$0:7"], token)).ExitCode);
            Assert.Equal(0, (await raw.ExecuteAsync(["select-pane", "-t", "%1"], token)).ExitCode);
            Assert.Equal(0, (await raw.ExecuteAsync(["select-window", "-t", "$0:7"], token)).ExitCode);
            bool forbidCommands = false;
            Server endpoint = Server.Open(new ServerConnectionOptions
            {
                TmuxBinaryPath = raw.TmuxBinaryPath,
                SocketPath = raw.SocketPath,
                ConfigurationFile = "/dev/null",
                Interceptor = (invocation, next, cancellation) => forbidCommands
                    ? throw new InvalidOperationException("Snapshot conversion reached tmux.")
                    : next(cancellation),
            });
            Server snapshot = await endpoint.CaptureSnapshotAsync(SnapshotDepth.Panes, token);
            Session session = Assert.Single(snapshot.Sessions);
            Assert.Equal([0, 3, 7], session.Windows.Select(window => window.Index));
            Assert.Equal(firstPath, session.Windows[0].Panes[0].CurrentPath);
            using Process daemon = Process.GetProcessById(Assert.IsType<ServerGeneration>(snapshot.Generation).ProcessId);
            Task exited = daemon.WaitForExitAsync(token);
            Assert.Equal(0, (await raw.ExecuteAsync(["kill-server"], token)).ExitCode);
            await exited;
            forbidCommands = true;

            WorkspaceFile frozen = WorkspaceFile.FromSnapshot(session);

            Assert.Equal(raw.SessionName, frozen.SessionName);
            Assert.Null(frozen.DocumentDirectory);
            Assert.Equal(["editor", "middle", "editor"], frozen.Windows.Select(window => window.WindowName));
            Assert.Equal([false, false, true], frozen.Windows.Select(window => window.Focus));
            Assert.Equal(session.Windows.Select(window => window.Layout), frozen.Windows.Select(window => window.Layout));
            Assert.All(new[] { frozen.Windows[0], frozen.Windows[2] }, window =>
            {
                Assert.Equal([false, true], window.Panes.Select(pane => pane.Focus));
                Assert.Equal([firstPath.Replace("$", "$$", StringComparison.Ordinal), secondPath], window.Panes.Select(pane => pane.StartDirectory));
                Assert.All(window.Panes, pane => Assert.Empty(pane.ShellCommands));
            });
            string declaration = JsonSerializer.Serialize(new
            {
                windows = frozen.Windows.Select(window => new
                {
                    panes = window.Panes.Select(pane => new { start_directory = pane.StartDirectory }),
                }),
            });
            WorkspaceFile restored = WorkspaceFile.Parse(declaration).Resolve(directory);
            Assert.Equal(firstPath, restored.Windows[0].Panes[0].StartDirectory);
            Assert.Equal(secondPath, restored.Windows[2].Panes[1].StartDirectory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Session Capture(string? omitted = null)
    {
        var generation = new ServerGeneration(93, 903);
        var connection = new TmuxConnection(new ServerConnectionOptions { SocketName = "snapshot-freeze" },
            (_, _) => throw new InvalidOperationException("Snapshot conversion reached tmux."));
        var server = new Server(connection, generation, "tmux 3.7");
        var session = new Session(server, connection, generation, new SessionId(0), Fields(("session_name", "captured")));
        var window = new Window(server, connection, generation, new WindowId(0),
            Fields(("window_name", "editor"), ("window_layout", "even-horizontal"), ("window_active", "1")));
        var pane = new Pane(server, connection, generation, new PaneId(0), Fields(("pane_current_path", null)));
        window.WithCaptured(Relation([pane], "panes"), CapturedRelation.Capture([session], "sessions", SnapshotDepth.Panes),
            new SessionWindowEdge { SessionId = session.Id, WindowId = window.Id, WindowIndex = 0 }, session,
            omitted == "active pane" ? null : pane);
        return session.WithCaptured(Relation([window], "windows"), CapturedRelation.Capture([pane], "panes", SnapshotDepth.Panes));

        Dictionary<string, string?> Fields(params (string Name, string? Value)[] fields) =>
            fields.Where(field => field.Name != omitted).ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal);

        CapturedRelation<T> Relation<T>(T[] values, string name) => omitted == name
            ? CapturedRelation.Uncaptured<T>(name, SnapshotDepth.Server)
            : CapturedRelation.Capture(values, name, SnapshotDepth.Panes);
    }
}
