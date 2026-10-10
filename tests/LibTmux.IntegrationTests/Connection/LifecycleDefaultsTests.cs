using System.Diagnostics;
using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Internal;

namespace LibTmux.IntegrationTests.Connection;

[UnsupportedOSPlatform("windows")]
[Collection("Process environment")]
public sealed class LifecycleDefaultsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static string Binary => Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "/usr/bin/tmux";

    [UnixFact]
    public async Task An_explicit_path_does_not_create_its_missing_parent()
    {
        string root = CreateRoot();
        string missing = Path.Combine(root, "missing");
        try
        {
            Server server = Server.Open(Options(root) with
            {
                SocketName = null,
                SocketPath = Path.Combine(missing, "socket"),
            });
            await Assert.ThrowsAsync<TmuxCommandException>(() => server.StartServerAsync(Token));
            Assert.False(Directory.Exists(missing));
        }
        finally
        {
            await CleanupRootAsync(root, owned: null, bodyFailure: null);
        }
    }

    [UnixFact]
    public async Task Named_endpoint_creates_only_the_uid_directory_and_accepts_group_permissions()
    {
        string root = CreateRoot();
        string directory = Path.Combine(root, $"tmux-{UnixSocketDirectory.UserId}");
        OwnedServerScope? owned = null;
        Exception? bodyFailure = null;
        try
        {
            owned = await Server.CreateOwnedAsync(Options(root), Token);
            Session session = await owned.Value.CreateSessionAsync(new NewSessionRequest { Name = "first" }, Token);
            Assert.Equal((UnixFileMode)0x1C0, File.GetUnixFileMode(directory));
            File.SetUnixFileMode(directory, (UnixFileMode)0x1F8);
            Assert.True(await owned.Value.HasSessionAsync(session.Name, cancellationToken: Token));
        }
        catch (Exception error)
        {
            bodyFailure = error;
            throw;
        }
        finally
        {
            await CleanupRootAsync(root, owned, bodyFailure);
        }
    }

    [UnixFact]
    public async Task Missing_or_removed_root_fails_before_launch_and_never_recreates_parents()
    {
        string parent = CreateRoot();
        string launched = Path.Combine(parent, "launched");
        string executable = Path.Combine(parent, "tmux");
        await File.WriteAllTextAsync(executable, $"#!/bin/sh\n: > '{launched}'\nexit 1\n", Token);
        File.SetUnixFileMode(executable, (UnixFileMode)0x1C0);
        try
        {
            foreach (bool existed in new[] { false, true })
            {
                string root = Path.Combine(parent, "missing");
                if (existed)
                {
                    Directory.CreateDirectory(root);
                }

                Server server = Server.Open(Options(root) with { TmuxBinaryPath = executable });
                if (existed)
                {
                    Directory.Delete(root);
                }

                Exception? error = await Record.ExceptionAsync(() => server.StartServerAsync(Token));
                Assert.NotNull(error);
                Assert.Contains("socket directory", error.ToString(), StringComparison.Ordinal);
                Assert.False(File.Exists(launched));
                Assert.False(Directory.Exists(root));
            }
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [UnixFact]
    public async Task Unsafe_uid_directory_is_rejected_without_changing_its_permissions()
    {
        string root = CreateRoot();
        string directory = Path.Combine(root, $"tmux-{UnixSocketDirectory.UserId}");
        string target = Path.Combine(root, "target");
        try
        {
            Directory.CreateDirectory(directory, (UnixFileMode)0x1FF);
            File.SetUnixFileMode(directory, (UnixFileMode)0x1FF);
            Exception? modeError = await Record.ExceptionAsync(() => Server.Open(Options(root)).StartServerAsync(Token));
            Assert.NotNull(modeError);
            Assert.Contains("no other-user permissions", modeError.ToString(), StringComparison.Ordinal);
            Assert.Equal((UnixFileMode)0x1FF, File.GetUnixFileMode(directory));
            Directory.Delete(directory);
            Directory.CreateDirectory(target, (UnixFileMode)0x1C0);
            Directory.CreateSymbolicLink(directory, target);
            Exception? linkError = await Record.ExceptionAsync(() => Server.Open(Options(root)).StartServerAsync(Token));
            Assert.NotNull(linkError);
            Assert.Contains("real directory", linkError.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            await CleanupRootAsync(root, owned: null, bodyFailure: null);
        }
    }

    [UnixFact]
    public async Task Host_changes_do_not_redirect_process_control_or_cleanup_and_context_never_reaches_clients()
    {
        string root = CreateRoot();
        string executable = Path.Combine(root, "tmux");
        string log = Path.Combine(root, "clients");
        string[] variables = ["TMUX_TMPDIR", "LIBTMUX_SOCKET_PATH", "LIBTMUX_SOCKET_NAME", "TMUX", "TMUX_PANE", "PATH", "LIBTMUX_TEST_CAPTURED_ENV", "LIBTMUX_TEST_LATE_ENV"];
        Dictionary<string, string?> prior = variables.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        OwnedServerScope? owned = null;
        Exception? bodyFailure = null;
        try
        {
            await TestExecutable.WriteAsync(executable,
                $"#!/bin/sh\nprintf '%s|%s|%s|%s|%s\\n' \"${{TMUX-unset}}\" \"${{TMUX_PANE-unset}}\" \"${{LIBTMUX_TEST_CAPTURED_ENV-unset}}\" \"${{LIBTMUX_TEST_LATE_ENV-unset}}\" \"$*\" >> '{log}'\nexec '{Binary}' \"$@\"\n", Token);
            File.Delete(log);
            Environment.SetEnvironmentVariable("TMUX_TMPDIR", root);
            Environment.SetEnvironmentVariable("LIBTMUX_SOCKET_PATH", null);
            Environment.SetEnvironmentVariable("LIBTMUX_SOCKET_NAME", "captured");
            Environment.SetEnvironmentVariable("TMUX", "ignored malformed context");
            Environment.SetEnvironmentVariable("TMUX_PANE", "%9");
            Environment.SetEnvironmentVariable("PATH", root + Path.PathSeparator + prior["PATH"]);
            Environment.SetEnvironmentVariable("LIBTMUX_TEST_CAPTURED_ENV", "captured");
            Environment.SetEnvironmentVariable("LIBTMUX_TEST_LATE_ENV", null);
            Server endpoint = Server.Open(new ServerConnectionOptions { ConfigurationFile = "/dev/null" });
            Environment.SetEnvironmentVariable("TMUX_TMPDIR", "relative changed root");
            Environment.SetEnvironmentVariable("LIBTMUX_SOCKET_PATH", "relative changed path");
            Environment.SetEnvironmentVariable("LIBTMUX_SOCKET_NAME", "../invalid");
            Environment.SetEnvironmentVariable("PATH", "/no/tmux/in/this/path");
            Environment.SetEnvironmentVariable("LIBTMUX_TEST_CAPTURED_ENV", "changed");
            Environment.SetEnvironmentVariable("LIBTMUX_TEST_LATE_ENV", "added");
            Session session = await endpoint.CreateSessionAsync(new NewSessionRequest { Name = "keeper" }, Token);
            owned = await session.Server.AdoptAsync(Token);
            await using (IControlModeSession control = await endpoint.EnterControlModeAsync(session.Id.ToString(), Token))
            {
                IReadOnlyList<string> answer = await control.SendAsync(TmuxCommand.Create("display-message", "-p", "#{socket_path}"), Token);
                Assert.Equal(Path.Combine(root, $"tmux-{UnixSocketDirectory.UserId}", "captured"), Assert.Single(answer));
            }

            await owned.DisposeAsync();
            string[] launches = await File.ReadAllLinesAsync(log, Token);
            Assert.Contains(launches, line => line.Contains("-C", StringComparison.Ordinal));
            Assert.Contains(launches, line => line.Contains("kill-server", StringComparison.Ordinal));
            Assert.All(launches, line => Assert.StartsWith("unset|unset|captured|unset|", line, StringComparison.Ordinal));
            Assert.Equal("ignored malformed context", Environment.GetEnvironmentVariable("TMUX"));
            Assert.Equal("%9", Environment.GetEnvironmentVariable("TMUX_PANE"));
            Assert.Equal("changed", Environment.GetEnvironmentVariable("LIBTMUX_TEST_CAPTURED_ENV"));
            Assert.Equal("added", Environment.GetEnvironmentVariable("LIBTMUX_TEST_LATE_ENV"));
        }
        catch (Exception error)
        {
            bodyFailure = error;
            throw;
        }
        finally
        {
            foreach ((string name, string? value) in prior)
            {
                Environment.SetEnvironmentVariable(name, value);
            }

            await CleanupRootAsync(root, owned, bodyFailure);
        }
    }

    [UnixFact]
    public async Task Ensure_starts_a_usable_daemon_and_serializes_independent_handles()
    {
        string root = CreateRoot();
        OwnedServerScope? owned = null;
        Exception? bodyFailure = null;
        try
        {
            ServerConnectionOptions options = Options(root);
            Assert.Null(await Server.Open(options).InspectAsync(Token));
            Server[] servers = await Task.WhenAll(Enumerable.Range(0, 8)
                .Select(_ => Server.EnsureAsync(options, Token)));
            owned = await servers[0].AdoptAsync(Token);
            Assert.All(servers, server => Assert.Equal(owned.Value.Generation, server.Generation));
            Session bootstrap = Assert.Single(await owned.Value.GetSessionsAsync(Token));
            Assert.StartsWith("libtmux-start-", bootstrap.Name, StringComparison.Ordinal);
            TmuxCommandResult command = await owned.Value.ExecuteCommandAsync(
                ["display-message", "-p", "-t", bootstrap.Id.ToString(), "#{pane_current_command}"], Token);
            Assert.Equal("cat", Assert.Single(command.StandardOutputLines));
            TmuxCommandResult setting = await owned.Value.ExecuteCommandAsync(["show-options", "-sv", "exit-empty"], Token);
            Assert.Equal("on", Assert.Single(setting.StandardOutputLines));
            Server repeated = await servers[0].EnsureAsync(Token);
            Assert.Equal(servers[0].Generation, repeated.Generation);
            Assert.Single(await repeated.GetSessionsAsync(Token));
            Session next = await repeated.CreateSessionAsync(new NewSessionRequest { Name = "subsequent" }, Token);
            Assert.Equal("subsequent", next.Name);
        }
        catch (Exception error)
        {
            bodyFailure = error;
            throw;
        }
        finally
        {
            await CleanupRootAsync(root, owned, bodyFailure);
        }
    }

    [UnixFact]
    public async Task Ensure_preserves_a_running_sessionless_daemon_and_skips_initialization()
    {
        string root = CreateRoot();
        OwnedServerScope? owned = null;
        Exception? bodyFailure = null;
        try
        {
            ServerConnectionOptions options = Options(root);
            owned = await Server.CreateOwnedAsync(options, Token);
            TmuxCommandResult before = await owned.Value.ExecuteCommandAsync(["show-options", "-s"], Token);
            Server ensured = await Server.EnsureAsync(options with
            {
                InitializeAsync = (_, _) => throw new InvalidOperationException("Reuse must not initialize."),
            }, Token);
            Assert.Equal(owned.Value.Generation, ensured.Generation);
            Assert.Empty(await ensured.GetSessionsAsync(Token));
            TmuxCommandResult after = await ensured.ExecuteCommandAsync(["show-options", "-s"], Token);
            Assert.Equal(before.StandardOutputLines, after.StandardOutputLines);
        }
        catch (Exception error)
        {
            bodyFailure = error;
            throw;
        }
        finally
        {
            await CleanupRootAsync(root, owned, bodyFailure);
        }
    }

    [UnixFact]
    public async Task Ensure_keeps_normal_configuration_and_renamed_bootstrap_without_a_startup_marker()
    {
        string root = CreateRoot();
        OwnedServerScope? owned = null;
        Exception? bodyFailure = null;
        try
        {
            string config = Path.Combine(root, "tmux.conf");
            await File.WriteAllTextAsync(config,
                "set -g @ordinary-config loaded\nset -g default-command 'exit 0'\n"
                + "set-hook -g after-new-session 'rename-session renamed-bootstrap'\n", Token);
            int initializers = 0;
            Server server = await Server.EnsureAsync(Options(root) with
            {
                ConfigurationFile = config,
                InitializeAsync = (_, _) =>
                {
                    initializers++;
                    return ValueTask.CompletedTask;
                },
            }, Token);
            owned = await server.AdoptAsync(Token);
            Session bootstrap = Assert.Single(await server.GetSessionsAsync(Token));
            Assert.Equal("renamed-bootstrap", bootstrap.Name);
            Assert.Equal(1, initializers);
            TmuxCommandResult option = await server.ExecuteCommandAsync(["show-options", "-gqv", "@ordinary-config"], Token);
            Assert.Equal("loaded", Assert.Single(option.StandardOutputLines));
            TmuxCommandResult environment = await server.ExecuteCommandAsync(["show-environment", "-g"], Token);
            Assert.DoesNotContain(environment.StandardOutputLines, line => line.StartsWith("LIBTMUX_START_", StringComparison.Ordinal));
            Assert.NotNull(await server.EnsureAsync(Token));
            Assert.Equal(1, initializers);
            Assert.Equal(bootstrap.Id, Assert.Single(await server.GetSessionsAsync(Token)).Id);
        }
        catch (Exception error)
        {
            bodyFailure = error;
            throw;
        }
        finally
        {
            await CleanupRootAsync(root, owned, bodyFailure);
        }
    }

    [Theory(Skip = "Requires Unix sockets.", SkipType = typeof(UnixTestEnvironment), SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ensure_rolls_back_only_its_renamed_session_and_retains_failed_rollback(bool failRollback)
    {
        string root = CreateRoot();
        OwnedServerScope? owned = null;
        Exception? bodyFailure = null;
        bool rejectCleanup = failRollback;
        using var cancellation = new CancellationTokenSource();
        try
        {
            SessionId? bootstrapId = null;
            SessionId? keeperId = null;
            ServerConnectionOptions options = Options(root) with
            {
                Interceptor = (invocation, next, token) =>
                {
                    if (rejectCleanup && invocation.Arguments.Any(argument => argument.Contains("kill-session", StringComparison.Ordinal)))
                    {
                        throw new IOException("Injected bootstrap rollback failure.");
                    }
                    return next(token);
                },
                InitializeAsync = async (server, token) =>
                {
                    owned = await server.AdoptAsync(token);
                    Session bootstrap = Assert.Single(await server.GetSessionsAsync(token));
                    bootstrapId = bootstrap.Id;
                    await bootstrap.RenameAsync("renamed-bootstrap", token);
                    Session keeper = await server.CreateSessionAsync(new NewSessionRequest { Name = "keeper", Command = "cat" }, token);
                    keeperId = keeper.Id;
                    await cancellation.CancelAsync();
                },
            };
            OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => Server.EnsureAsync(options, cancellation.Token));
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.NotNull(owned);
            Assert.NotNull(bootstrapId);
            Assert.NotNull(keeperId);
            Assert.Equal(failRollback, await owned.Value.FindSessionAsync(bootstrapId.Value, Token) is not null);
            Assert.NotNull(await owned.Value.FindSessionAsync(keeperId.Value, Token));
            Assert.Equal(failRollback ? 1 : 0, OwnedScope.CleanupOwners(error).Count);
            if (failRollback)
            {
                Assert.IsType<IOException>(OwnedScope.CleanupFailure(error));
                rejectCleanup = false;
                await Assert.Single(OwnedScope.CleanupOwners(error)).DisposeAsync();
                Assert.Null(await owned.Value.FindSessionAsync(bootstrapId.Value, Token));
                Assert.NotNull(await owned.Value.FindSessionAsync(keeperId.Value, Token));
            }
        }
        catch (Exception error)
        {
            bodyFailure = error;
            throw;
        }
        finally
        {
            rejectCleanup = false;
            await CleanupRootAsync(root, owned, bodyFailure);
        }
    }

    [UnixFact]
    public async Task Ensure_rejects_a_startup_whose_initializer_removes_the_bootstrap()
    {
        string root = CreateRoot();
        OwnedServerScope? owned = null;
        Exception? bodyFailure = null;
        try
        {
            ServerConnectionOptions options = Options(root) with
            {
                InitializeAsync = async (server, token) =>
                {
                    owned = await server.AdoptAsync(token);
                    Session bootstrap = Assert.Single(await server.GetSessionsAsync(token));
                    await bootstrap.KillAsync(cancellationToken: token);
                },
            };
            await Assert.ThrowsAnyAsync<LibTmuxException>(() => Server.EnsureAsync(options, Token));
            Assert.NotNull(owned);
        }
        catch (Exception error)
        {
            bodyFailure = error;
            throw;
        }
        finally
        {
            await CleanupRootAsync(root, owned, bodyFailure);
        }
    }

    [UnixFact]
    public async Task Exact_session_cleanup_program_runs_with_only_external_endpoint_defaults()
    {
        await RunSessionCleanupAsync(failBody: false, failCleanup: false);
        await RunSessionCleanupAsync(failBody: true, failCleanup: false);
        await RunSessionCleanupAsync(failBody: false, failCleanup: true);
        await RunSessionCleanupAsync(failBody: true, failCleanup: true);
    }

    [Theory(Skip = "Requires Unix sockets.", SkipType = typeof(UnixTestEnvironment), SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Exact_ordinary_quickstart_runs_twice_with_external_defaults(bool running, bool named)
    {
        string root = CreateRoot();
        OwnedServerScope? owned = null;
        Exception? bodyFailure = null;
        try
        {
            ServerConnectionOptions options = named ? Options(root) : Options(root) with
            {
                SocketName = null,
                SocketPath = Path.Combine(root, "ordinary.sock"),
            };
            Server endpoint = Server.Open(options);
            string[]? priorState = null;
            SessionId? priorSession = null;
            if (running)
            {
                Session keeper = await endpoint.CreateSessionAsync(new NewSessionRequest { Name = "keeper", Command = "cat" }, Token);
                owned = await keeper.Server.AdoptAsync(Token);
                Window extra = await keeper.CreateWindowAsync(new NewWindowRequest { Name = "kept-window", Command = "cat" }, Token);
                await extra.SplitPaneAsync(new SplitPaneRequest { Command = "cat" }, Token);
                await endpoint.ExecuteCommandAsync(["set-option", "-g", "@user-setting", "retained"], Token);
                await endpoint.ExecuteCommandAsync(["set-environment", "-g", "USER_SETTING", "retained"], Token);
                priorSession = keeper.Id;
                priorState = await OrdinaryStateAsync(endpoint, keeper.Id);
            }
            else
            {
                Assert.Null(await endpoint.InspectAsync(Token));
            }

            string executable = Path.Combine(root, "tmux");
            await TestExecutable.WriteAsync(executable, $"#!/bin/sh\nexec '{Binary}' -f /dev/null \"$@\"\n", Token);
            string? parentPath = Environment.GetEnvironmentVariable("LIBTMUX_SOCKET_PATH");
            string? parentName = Environment.GetEnvironmentVariable("LIBTMUX_SOCKET_NAME");
            string? parentRoot = Environment.GetEnvironmentVariable("TMUX_TMPDIR");
            string[]? firstIds = null;
            for (int run = 0; run < 2; run++)
            {
                string project = Path.Combine(RepositoryRoot(), "examples", "LibTmux.Quickstart", "bin",
                    new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name,
                    new DirectoryInfo(AppContext.BaseDirectory).Name, "LibTmux.Quickstart.dll");
                string host = Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.Ordinal)
                    ? Environment.ProcessPath!
                    : Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet");
                var start = new ProcessStartInfo(host)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                start.ArgumentList.Add(project);
                start.Environment.Remove("LIBTMUX_SOCKET_PATH");
                start.Environment["LIBTMUX_SOCKET_NAME"] = named ? "owned" : "../ignored";
                if (!named)
                {
                    start.Environment["LIBTMUX_SOCKET_PATH"] = options.SocketPath;
                }
                start.Environment["TMUX_TMPDIR"] = root;
                start.Environment["TMUX"] = "ignored malformed context";
                start.Environment["TMUX_PANE"] = "%77";
                start.Environment["PATH"] = root + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
                using Process child = Process.Start(start)!;
                Task<string> stdout = child.StandardOutput.ReadToEndAsync(Token);
                Task<string> stderr = child.StandardError.ReadToEndAsync(Token);
                try
                {
                    await child.WaitForExitAsync(Token);
                }
                finally
                {
                    if (!child.HasExited)
                    {
                        child.Kill(entireProcessTree: true);
                        await child.WaitForExitAsync(CancellationToken.None);
                    }
                }
                string error = await stderr;
                Assert.True(child.ExitCode == 0, error);
                Assert.Equal("Workspace ready: libtmux-dotnet-quickstart / tests", (await stdout).Trim());
                owned ??= await endpoint.AdoptAsync(Token);
                IReadOnlyList<Session> sessions = await endpoint.GetSessionsAsync(Token);
                Assert.Equal(2, sessions.Count);
                Session workspace = Assert.Single(sessions, session => session.Name == "libtmux-dotnet-quickstart");
                IReadOnlyList<Window> windows = await workspace.GetWindowsAsync(Token);
                Assert.Collection(windows.OrderBy(window => window.Name, StringComparer.Ordinal),
                    window => Assert.Equal("tests", window.Name), window => Assert.Equal("work", window.Name));
                string[] ids = [workspace.Id.ToString(), .. windows.Select(window => window.Id.ToString()).Order(StringComparer.Ordinal)];
                if (firstIds is not null)
                {
                    Assert.Equal(firstIds, ids);
                }
                firstIds = ids;
                if (priorSession is SessionId keeperId)
                {
                    Assert.Equal(priorState, await OrdinaryStateAsync(endpoint, keeperId));
                }
                else
                {
                    Assert.Single(sessions, session => session.Name.StartsWith("libtmux-start-", StringComparison.Ordinal));
                }
            }
            Assert.Equal(parentPath, Environment.GetEnvironmentVariable("LIBTMUX_SOCKET_PATH"));
            Assert.Equal(parentName, Environment.GetEnvironmentVariable("LIBTMUX_SOCKET_NAME"));
            Assert.Equal(parentRoot, Environment.GetEnvironmentVariable("TMUX_TMPDIR"));
        }
        catch (Exception error)
        {
            bodyFailure = error;
            throw;
        }
        finally
        {
            await CleanupRootAsync(root, owned, bodyFailure);
        }
    }

    private static async Task<string[]> OrdinaryStateAsync(Server server, SessionId session)
    {
        TmuxCommandResult panes = await server.ExecuteCommandAsync(
            ["list-panes", "-s", "-t", session.ToString(), "-F", "#{session_id}:#{session_name}:#{window_id}:#{window_name}:#{pane_id}"], Token);
        TmuxCommandResult option = await server.ExecuteCommandAsync(["show-option", "-gqv", "@user-setting"], Token);
        TmuxCommandResult environment = await server.ExecuteCommandAsync(["show-environment", "-g", "USER_SETTING"], Token);
        Assert.Equal(0, panes.ExitCode);
        Assert.Equal(0, option.ExitCode);
        Assert.Equal(0, environment.ExitCode);
        return [.. panes.StandardOutputLines.Order(StringComparer.Ordinal), .. option.StandardOutputLines, .. environment.StandardOutputLines];
    }

    [UnixFact]
    public async Task Session_and_window_cleanup_share_failures_and_allow_retry_after_cancellation()
    {
        string root = CreateRoot();
        bool fail = true;
        int attempts = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        OwnedServerScope? owned = null;
        Exception? bodyFailure = null;
        try
        {
            owned = await Server.CreateOwnedAsync(Options(root), Token);
            Session kept = await owned.Value.CreateSessionAsync(new NewSessionRequest { Name = "keeper" }, Token);
            Server client = Server.Open(Options(root) with
            {
                Interceptor = async (invocation, next, token) =>
                {
                    if (invocation.Arguments.Any(arg => arg.Contains("kill-session", StringComparison.Ordinal)
                        || arg.Contains("kill-window", StringComparison.Ordinal)))
                    {
                        Assert.False(token.IsCancellationRequested);
                        attempts++;
                        if (fail)
                        {
                            entered.TrySetResult();
                            await release.Task;
                            throw new InvalidOperationException("injected cleanup failure");
                        }
                    }

                    return await next(token);
                },
            });
            OwnedSessionScope session = await client.CreateOwnedSessionAsync(new NewSessionRequest { Name = "owned" }, Token);
            Task first = session.DisposeAsync().AsTask();
            await entered.Task.WaitAsync(Token);
            Task concurrent = session.DisposeAsync().AsTask();
            Assert.Same(first, concurrent);
            release.SetResult();
            await Assert.ThrowsAsync<InvalidOperationException>(() => first);
            await Assert.ThrowsAsync<InvalidOperationException>(() => concurrent);
            Assert.True(await owned.Value.HasSessionAsync("owned", cancellationToken: Token));
            fail = false;
            await session.DisposeAsync();
            await session.DisposeAsync();
            Assert.Equal(2, attempts);
            Assert.False(await owned.Value.HasSessionAsync("owned", cancellationToken: Token));
            Session keeper = await client.GetSessionAsync(kept.Id, Token);
            OwnedWindowScope window = await keeper.CreateOwnedWindowAsync(new NewWindowRequest { Name = "owned-window" }, Token);
            fail = true;
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await window.DisposeAsync());
            fail = false;
            await window.DisposeAsync();
            await window.DisposeAsync();
            Assert.Equal(4, attempts);
            Assert.DoesNotContain(await keeper.GetWindowsAsync(Token), candidate => candidate.Id == window.Value.Id);
            using var cancelledBody = new CancellationTokenSource();
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                await using OwnedSessionScope cancelled = await client.CreateOwnedSessionAsync(
                    new NewSessionRequest { Name = "cancelled" }, Token);
                cancelledBody.Cancel();
                cancelledBody.Token.ThrowIfCancellationRequested();
            });
            Assert.False(await owned.Value.HasSessionAsync("cancelled", cancellationToken: Token));
            Assert.Equal(5, attempts);
        }
        catch (Exception error)
        {
            bodyFailure = error;
            throw;
        }
        finally
        {
            await CleanupRootAsync(root, owned, bodyFailure);
        }
    }

    private static async Task RunSessionCleanupAsync(bool failBody, bool failCleanup)
    {
        string root = CreateRoot();
        string executable = Path.Combine(root, "tmux");
        string socket = Path.Combine(root, "ordinary.sock");
        ServerConnectionOptions options = Options(root) with { SocketName = null, SocketPath = socket };
        OwnedServerScope? owned = null;
        Exception? bodyFailure = null;
        try
        {
            owned = await Server.CreateOwnedAsync(options, Token);
            await owned.Value.CreateSessionAsync(new NewSessionRequest { Name = "keeper" }, Token);
            await owned.Value.ExecuteCommandAsync(["set-hook", "-g", "after-new-session", "set-option -g @ordinary-created yes"], Token);
            string faults = (failBody ? "case \"$*\" in *new-window*) echo 'injected body failure' >&2; exit 1;; esac\n" : "")
                + (failCleanup ? "case \"$*\" in *kill-session*) echo 'injected cleanup failure' >&2; exit 1;; esac\n" : "");
            await TestExecutable.WriteAsync(executable, $"#!/bin/sh\n{faults}exec '{Binary}' \"$@\"\n", Token);
            string project = Path.Combine(RepositoryRoot(), "examples", "LibTmux.SessionCleanup", "bin",
                new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name,
                new DirectoryInfo(AppContext.BaseDirectory).Name, "LibTmux.SessionCleanup.dll");
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            // Test assemblies run as apphosts or under dotnet. The example needs
            // the same runtime host, never a new invocation of the test apphost.
            if (!Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.Ordinal))
            {
                start.FileName = Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet");
            }

            start.ArgumentList.Add(project);
            start.Environment["LIBTMUX_SOCKET_PATH"] = socket;
            start.Environment["LIBTMUX_SOCKET_NAME"] = "../ignored";
            start.Environment["TMUX"] = "ignored malformed context";
            start.Environment["TMUX_PANE"] = "%77";
            start.Environment["PATH"] = root + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            using Process child = Process.Start(start)!;
            Task<string> stdout = child.StandardOutput.ReadToEndAsync(Token);
            Task<string> stderr = child.StandardError.ReadToEndAsync(Token);
            try
            {
                await child.WaitForExitAsync(Token);
            }
            finally
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync(CancellationToken.None);
                }
            }

            string output = await stdout;
            string error = await stderr;
            Assert.Equal(!failBody && !failCleanup, child.ExitCode == 0);
            if (!failBody)
            {
                Assert.Contains(": tests", output, StringComparison.Ordinal);
            }

            if (failCleanup)
            {
                Assert.Contains("injected cleanup failure", error, StringComparison.Ordinal);
                if (failBody)
                {
                    Assert.Contains("injected body failure", error, StringComparison.Ordinal);
                }
            }
            else if (failBody)
            {
                Assert.Contains("injected body failure", error, StringComparison.Ordinal);
            }

            TmuxCommandResult effect = await owned.Value.ExecuteCommandAsync(["show-option", "-gqv", "@ordinary-created"], Token);
            Assert.Equal("yes", Assert.Single(effect.StandardOutputLines));
            Assert.Equal(failCleanup ? 2 : 1, (await owned.Value.GetSessionsAsync(Token)).Count);
            await owned.DisposeAsync();
            Assert.Null(await Server.Open(options).InspectAsync(Token));
        }
        catch (Exception error)
        {
            bodyFailure = error;
            throw;
        }
        finally
        {
            await CleanupRootAsync(root, owned, bodyFailure);
        }
    }

    [Theory(Skip = "Requires Unix sockets.", SkipType = typeof(UnixTestEnvironment), SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Fixture_root_removal_requires_successful_owned_cleanup(bool failBody, bool failCleanup)
    {
        string root = CreateRoot();
        bool rejectCleanup = false;
        var cleanupFailure = new IOException("Injected outer fixture cleanup failure.");
        Exception? bodyFailure = failBody ? new InvalidOperationException("Body failure.") : null;
        OwnedServerScope? owned = null;
        try
        {
            owned = await Server.CreateOwnedAsync(Options(root) with
            {
                Interceptor = (invocation, next, token) =>
                {
                    if (rejectCleanup && invocation.Arguments.Any(argument => argument.Contains("kill-server", StringComparison.Ordinal)))
                    {
                        return Task.FromException<TmuxCommandResult>(cleanupFailure);
                    }
                    return next(token);
                },
            }, Token);
            using Process daemon = Process.GetProcessById(owned.Value.Generation!.Value.ProcessId);
            string marker = Path.Combine(root, "retained-evidence");
            await File.WriteAllTextAsync(marker, "Keep until daemon exit.", Token);
            rejectCleanup = failCleanup;
            Exception? error = await Record.ExceptionAsync(() => CleanupRootAsync(root, owned, bodyFailure));
            if (failCleanup)
            {
                Assert.Same(cleanupFailure, failBody ? OwnedScope.CleanupFailure(bodyFailure!) : error);
                if (failBody)
                {
                    Assert.Null(error);
                }
                Assert.True(Directory.Exists(root));
                Assert.True(File.Exists(marker));
                Assert.False(daemon.HasExited);
                Assert.NotNull(await owned.Value.InspectAsync(Token));
                rejectCleanup = false;
                await CleanupRootAsync(root, owned, bodyFailure: null);
            }
            else
            {
                Assert.Null(error);
                if (bodyFailure is not null)
                {
                    Assert.Null(OwnedScope.CleanupFailure(bodyFailure));
                }
            }
            Assert.True(daemon.HasExited);
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            rejectCleanup = false;
            await CleanupRootAsync(root, owned, bodyFailure: null);
        }
    }

    [UnixFact]
    public async Task Fixture_retains_its_root_when_acquisition_never_returned_an_owner()
    {
        string root = CreateRoot();
        try
        {
            await CleanupRootAsync(root, owned: null, bodyFailure: new IOException("Unknown startup outcome."));
            Assert.True(Directory.Exists(root));
        }
        finally
        {
            // This test never dispatched a command or started a daemon.
            if (Directory.Exists(root))
            {
                Directory.Delete(root);
            }
        }
    }

    private static async Task CleanupRootAsync(string root, OwnedServerScope? owned, Exception? bodyFailure)
    {
        async Task CleanupAsync()
        {
            if (owned is null)
            {
                Console.Error.WriteLine($"Retained fixture root after incomplete acquisition: {root}");
                return;
            }
            try
            {
                await owned.DisposeAsync();
            }
            catch
            {
                Console.Error.WriteLine($"Retained fixture root after cleanup failure: {root}");
                throw;
            }
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }

        if (bodyFailure is null)
        {
            await CleanupAsync();
        }
        else
        {
            await OwnedScope.PreserveCleanupAsync(bodyFailure, CleanupAsync);
        }
    }

    private static ServerConnectionOptions Options(string root) => new()
    {
        SocketName = "owned",
        ConfigurationFile = "/dev/null",
        TmuxBinaryPath = Binary,
        ChildEnvironment = new Dictionary<string, string?> { ["TMUX_TMPDIR"] = root },
    };

    private static string CreateRoot() =>
        Directory.CreateDirectory(Path.Combine("/tmp/libtmux-dotnet-test", "mvp-" + Guid.NewGuid().ToString("N")[..12])).FullName;

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LibTmux.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
