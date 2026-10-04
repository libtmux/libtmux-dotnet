using System.Diagnostics;
using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;

namespace LibTmux.IntegrationTests.Connection;

/// <summary>What owning a server does when one is already listening, and when stopping it fails.</summary>
/// <remarks>
/// Each socket root here is the test's own, so "the default socket" is one
/// nobody else uses. On a developer's machine it is the server they are
/// sitting in, which is what the refusal protects.
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed class OwnedServerAdoptionTests
{
    [UnixFact]
    public async Task Refuses_a_default_socket_that_is_already_serving()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string root = CreateSocketRoot();
        Server occupant = Server.Open(Options(root, socketName: null));
        try
        {
            await occupant.CreateSessionAsync(new NewSessionRequest { Name = "occupant" }, token);

            InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
                () => Server.CreateOwnedAsync(Options(root, socketName: null), token));

            Assert.Contains("default socket", refused.Message, StringComparison.Ordinal);
            Assert.True(await occupant.IsAliveAsync(token), "the occupying server was adopted and stopped");
        }
        finally
        {
            await CleanUpAsync(occupant, root, token);
        }
    }

    [UnixFact]
    public async Task Fails_rather_than_starting_when_tmux_cannot_say_whether_a_server_listens()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string root = CreateSocketRoot();
        string started = Path.Join(root, "started");
        string tmux = Path.Join(root, "tmux");
        Server occupant = Server.Open(Options(root, socketName: null));
        try
        {
            await occupant.CreateSessionAsync(new NewSessionRequest { Name = "occupant" }, token);

            // Answers as tmux does for a socket it may not open.
            await TestExecutable.WriteAsync(
                tmux,
                "#!/bin/sh\n"
                + "case \" $* \" in *\" list-sessions \"*) echo 'error connecting to /socket (Permission denied)' >&2; exit 1 ;; "
                + $"*\" start-server \"*) : > '{started}' ;; esac\n"
                + $"exec '{Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux"}' \"$@\"\n",
                token);

            await Assert.ThrowsAsync<TmuxCommandException>(
                () => Server.CreateOwnedAsync(Options(root, socketName: null) with { TmuxBinaryPath = tmux }, token));

            Assert.False(File.Exists(started), "a server was started without knowing whether one listened");
            Assert.True(await occupant.IsAliveAsync(token));
        }
        finally
        {
            await CleanUpAsync(occupant, root, token);
        }
    }

    [UnixFact]
    public async Task Owns_a_named_socket_while_the_default_one_is_serving()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string root = CreateSocketRoot();
        Server occupant = Server.Open(Options(root, socketName: null));
        try
        {
            await occupant.CreateSessionAsync(new NewSessionRequest { Name = "occupant" }, token);

            await using (OwnedServerScope owned = await Server.CreateOwnedAsync(Options(root, "owned"), token))
            {
                Session created = await owned.Value.CreateSessionAsync(new NewSessionRequest { Name = "owned" }, token);
                Assert.Equal("owned", created.Name);
            }

            Assert.True(await occupant.IsAliveAsync(token));
        }
        finally
        {
            await CleanUpAsync(occupant, root, token);
        }
    }

    [UnixFact]
    public async Task Disposing_again_after_a_failed_stop_stops_the_server()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string root = CreateSocketRoot();
        Server observer = Server.Open(Options(root, "owned"));
        try
        {
            (OwnedServerScope owned, string refuse) = await CreateRefusableAsync(root, token);

            await File.WriteAllTextAsync(refuse, string.Empty, token);
            await Assert.ThrowsAnyAsync<LibTmuxException>(() => owned.DisposeAsync().AsTask());
            Assert.True(await observer.IsAliveAsync(token));

            File.Delete(refuse);
            await owned.DisposeAsync();
            Assert.False(await observer.IsAliveAsync(token), "a second dispose returned without stopping the server");
        }
        finally
        {
            await CleanUpAsync(observer, root, token);
        }
    }

    [UnixFact]
    public async Task Concurrent_disposals_share_one_stop_and_its_failure()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string root = CreateSocketRoot();
        Server observer = Server.Open(Options(root, "owned"));
        try
        {
            (OwnedServerScope owned, string refuse) = await CreateRefusableAsync(root, token);

            await File.WriteAllTextAsync(refuse, string.Empty, token);
            Task first = owned.DisposeAsync().AsTask();
            Task second = owned.DisposeAsync().AsTask();
            await Assert.ThrowsAnyAsync<LibTmuxException>(() => first);
            await Assert.ThrowsAnyAsync<LibTmuxException>(() => second);
            Assert.True(await observer.IsAliveAsync(token));

            File.Delete(refuse);
            await owned.DisposeAsync();
            Assert.False(await observer.IsAliveAsync(token));
        }
        finally
        {
            await CleanUpAsync(observer, root, token);
        }
    }

    [UnixFact]
    public async Task A_retried_stop_still_waits_for_the_process_the_first_attempt_found()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string root = CreateSocketRoot();
        string pid = Path.Join(root, "pid");
        string hidePid = Path.Join(root, "hide-pid");
        string refuseKill = Path.Join(root, "refuse-kill");
        string tmux = Path.Join(root, "tmux");
        using System.Diagnostics.Process stand = System.Diagnostics.Process.Start("sleep", "30");
        Server observer = Server.Open(Options(root, "owned"));
        try
        {
            // Reports the stand-in's process as the server's, and can refuse
            // kill-server or hide the process ID, as a socket already gone does.
            await TestExecutable.WriteAsync(
                tmux,
                "#!/bin/sh\n"
                + $"case \" $* \" in *\" display-message \"*) [ -e '{hidePid}' ] && exit 1; [ -e '{pid}' ] && {{ cat '{pid}'; exit 0; }} ;; esac\n"
                + $"case \" $* \" in *\" kill-server \"*) [ -e '{refuseKill}' ] && exit 1 ;; esac\n"
                + $"exec '{Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux"}' \"$@\"\n",
                token);
            OwnedServerScope owned = await Server.CreateOwnedAsync(Options(root, "owned") with { TmuxBinaryPath = tmux }, token);
            await owned.Value.CreateSessionAsync(new NewSessionRequest { Name = "kept" }, token);
            await File.WriteAllTextAsync(pid, stand.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), token);
            await File.WriteAllTextAsync(refuseKill, string.Empty, token);
            await Assert.ThrowsAnyAsync<LibTmuxException>(() => owned.DisposeAsync().AsTask());

            File.Delete(refuseKill);
            await File.WriteAllTextAsync(hidePid, string.Empty, token);
            Task retry = owned.DisposeAsync().AsTask();
            Assert.NotSame(retry, await Task.WhenAny(retry, Task.Delay(TimeSpan.FromMilliseconds(500), token)));

            stand.Kill();
            await retry;
            Assert.False(await observer.IsAliveAsync(token));
        }
        finally
        {
            if (!stand.HasExited)
            {
                stand.Kill();
            }

            await CleanUpAsync(observer, root, token);
        }
    }

    [UnixFact]
    public async Task Waiting_for_a_stopped_server_ignores_a_process_that_reused_its_id()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        using System.Diagnostics.Process stranger = System.Diagnostics.Process.Start("sleep", "30");
        try
        {
            // Same ID, earlier start: the process recorded has gone and this one took its ID.
            Task waiting = OwnedServerScope.WaitForExitAsync(
                (stranger.Id, stranger.StartTime.AddSeconds(-1)),
                token);

            await waiting.WaitAsync(TimeSpan.FromSeconds(2), token);
            Assert.False(stranger.HasExited);
        }
        finally
        {
            stranger.Kill();
        }
    }

    [UnixFact]
    public async Task A_start_cancelled_after_tmux_started_stops_that_server()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string root = CreateSocketRoot();
        string started = Path.Join(root, "started");
        string listed = Path.Join(root, "listed");
        string configuration = Path.Join(root, "tmux.conf");
        string tmux = Path.Join(root, "tmux");

        // The server outlives having no sessions, and once started it never
        // answers list-sessions, so the start waits until it is cancelled.
        await File.WriteAllTextAsync(configuration, "set-option -s exit-empty off\n", token);
        await TestExecutable.WriteAsync(
            tmux,
            "#!/bin/sh\n"
            + $"case \" $* \" in *\" start-server \"*) : > '{started}' ;; *\" list-sessions \"*) [ -e '{started}' ] && printf x 1<>'{listed}' && exit 1 ;; esac\n"
            + $"exec '{Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux"}' \"$@\"\n",
            token);
        using (Process mkfifo = Process.Start(new ProcessStartInfo("mkfifo") { ArgumentList = { listed } })!)
        {
            await mkfifo.WaitForExitAsync(token);
            Assert.Equal(0, mkfifo.ExitCode);
        }

        ServerConnectionOptions options = Options(root, "owned") with { ConfigurationFile = configuration };
        Server observer = Server.Open(options);
        // The wrapper writes to the FIFO once the start has run and a later
        // check is under way; that read, not a guessed delay, triggers the cancel.
        Task listing = Task.Run(
            () =>
            {
                using FileStream fifo = new(listed, FileMode.Open, FileAccess.Read);
                _ = fifo.ReadByte();
            },
            token);
        try
        {
            using CancellationTokenSource cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task<OwnedServerScope> creating = Server.CreateOwnedAsync(options with { TmuxBinaryPath = tmux }, cancelled.Token);
            await listing.WaitAsync(token);

            await cancelled.CancelAsync();
            // The start had run, so the cancellation arrives as a failure that says so.
            LibTmuxException failure = await Assert.ThrowsAnyAsync<LibTmuxException>(() => creating);
            Assert.IsAssignableFrom<OperationCanceledException>(failure.InnerException);

            Assert.True(File.Exists(started));
            Assert.False(await observer.IsAliveAsync(token), "the started server was left running");
        }
        finally
        {
            // A read-write open never blocks on a FIFO and releases a reader
            // still waiting after a failure.
            using (new FileStream(listed, FileMode.Open, FileAccess.ReadWrite))
            {
            }

            await CleanUpAsync(observer, root, token);
        }
    }

    // An owned server whose tmux exits 1 while the returned file exists, so a
    // test can make stopping it fail and then succeed.
    private static async Task<(OwnedServerScope Owned, string Refuse)> CreateRefusableAsync(
        string root,
        CancellationToken cancellationToken)
    {
        string refuse = Path.Join(root, "refuse");
        string tmux = Path.Join(root, "tmux");
        await TestExecutable.WriteAsync(
            tmux,
            $"#!/bin/sh\n[ -e '{refuse}' ] && exit 1\nexec '{Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux"}' \"$@\"\n",
            cancellationToken);
        OwnedServerScope owned = await Server.CreateOwnedAsync(
            Options(root, "owned") with { TmuxBinaryPath = tmux },
            cancellationToken);
        await owned.Value.CreateSessionAsync(new NewSessionRequest { Name = "kept" }, cancellationToken);
        return (owned, refuse);
    }

    private static string CreateSocketRoot()
    {
        // A socket path is limited to about 104 bytes and this root is part of
        // one, `<root>/tmux-<uid>/<name>`, so it is short rather than descriptive.
        string root = Path.Join(Path.GetTempPath(), $"lt-{Guid.NewGuid():N}"[..11]);
        Directory.CreateDirectory(root);
        return root;
    }

    private static ServerConnectionOptions Options(string root, string? socketName) =>
        new()
        {
            TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux",
            SocketName = socketName,
            ConfigurationFile = "/dev/null",
            ChildEnvironment = new Dictionary<string, string?> { ["TMUX_TMPDIR"] = root },
        };

    private static async Task CleanUpAsync(Server server, string root, CancellationToken cancellationToken)
    {
        if (await server.IsAliveAsync(cancellationToken))
        {
            await server.KillAsync(cancellationToken);
        }

        Directory.Delete(root, recursive: true);
    }
}
