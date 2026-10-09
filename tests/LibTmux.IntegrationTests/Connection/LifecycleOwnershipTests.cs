using System.Diagnostics;
using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Transport;

namespace LibTmux.IntegrationTests.Connection;

// Creating executable wrappers must not overlap other fixtures' process forks.
[Collection("Process environment")]
[UnsupportedOSPlatform("windows")]
public sealed class LifecycleOwnershipTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public static bool IsLinux => OperatingSystem.IsLinux();

    [UnixFact]
    public async Task Adopted_resources_retain_ids_after_rename_move_and_client_disposal()
    {
        await using var fixture = await Fixture.StartAsync();
        Server server = fixture.Server;
        Session source = await server.CreateSessionAsync(new() { Name = "source" }, Token);
        Session destination = await server.CreateSessionAsync(new() { Name = "destination" }, Token);
        await using (IControlModeSession client = await server.EnterControlModeAsync(source.Id.ToString(), Token))
        {
            Assert.NotNull(client);
        }
        Assert.NotNull(await server.FindSessionAsync(source.Id, Token));
        Window window = await source.CreateWindowAsync(new() { Name = "owned-window" }, Token);
        OwnedWindowScope windowOwner = await window.AdoptAsync(Token);
        await window.RenameAsync("renamed-window", Token);
        await RunAsync(server, ["move-window", "-s", window.Id.ToString(), "-t", destination.Id + ":"]);
        await RunAsync(server, ["link-window", "-s", window.Id.ToString(), "-t", source.Id + ":"]);
        Pane pane = await (await source.GetWindowsAsync(Token))[0].SplitPaneAsync(cancellationToken: Token);
        OwnedPaneScope paneOwner = await pane.AdoptAsync(Token);
        await RunAsync(server, ["join-pane", "-s", pane.Id.ToString(), "-t", window.Id.ToString()]);
        await paneOwner.DisposeAsync();
        await paneOwner.DisposeAsync();
        Assert.Null(await server.FindPaneAsync(pane.Id, Token));
        await windowOwner.DisposeAsync();
        await windowOwner.DisposeAsync();
        Assert.DoesNotContain(await server.GetWindowsAsync(Token), item => item.Id == window.Id);
        OwnedSessionScope sessionOwner = await source.AdoptAsync(Token);
        await source.RenameAsync("renamed-session", Token);
        await server.CreateSessionAsync(new() { Name = "source" }, Token);
        await sessionOwner.DisposeAsync();
        await sessionOwner.DisposeAsync();
        Assert.Null(await server.FindSessionAsync(source.Id, Token));
        Assert.True(await server.HasSessionAsync("source", cancellationToken: Token));
        OwnedServerScope adopted = await Server.Open(fixture.Options).AdoptAsync(Token);
        await adopted.DisposeAsync();
        await adopted.DisposeAsync();
        Assert.Null(await Server.Open(fixture.Options).InspectAsync(Token));
    }

    [UnixFact]
    public async Task Stale_server_owner_cannot_kill_a_replacement_and_success_does_not_run_twice()
    {
        await using var fixture = await Fixture.StartAsync();
        OwnedServerScope stale = await Server.Open(fixture.Options).AdoptAsync(Token);
        await fixture.Owner.DisposeAsync();
        await using OwnedServerScope replacement = await Server.CreateOwnedAsync(fixture.Options, Token);
        Assert.NotEqual(stale.Value.Generation, replacement.Value.Generation);
        await Assert.ThrowsAsync<StaleServerGenerationException>(() => stale.DisposeAsync().AsTask());
        Assert.Equal(replacement.Value.Generation, (await Server.Open(fixture.Options).InspectAsync(Token))!.Generation);
        await fixture.Owner.DisposeAsync();
        Assert.NotNull(await replacement.Value.InspectAsync(Token));
    }

    [Theory(Skip = "Requires the Linux Python wrapper used to force equal numeric identities.", SkipUnless = nameof(IsLinux))]
    [InlineData("server")]
    [InlineData("session")]
    [InlineData("window")]
    [InlineData("pane")]
    public async Task Equal_numeric_generation_with_a_different_token_rejects_every_owner(string kind)
    {
        await using var fixture = await Fixture.StartAsync();
        Session originalSession = await fixture.Server.CreateSessionAsync(new() { Name = "accepted" }, Token);
        Window originalWindow = await originalSession.CreateWindowAsync(new() { Name = "accepted-window" }, Token);
        Pane originalPane = await originalWindow.SplitPaneAsync(cancellationToken: Token);
        string forced = Path.Combine(fixture.Root, "forced-generation");
        string wrapper = Path.Combine(fixture.Root, "tmux-wrapper");
        string executable = System.Text.Json.JsonSerializer.Serialize(fixture.Options.TmuxBinaryPath);
        await File.WriteAllTextAsync(wrapper,
            "#!/usr/bin/python3\nimport os, pathlib, sys\n"
            + "forced = pathlib.Path(" + System.Text.Json.JsonSerializer.Serialize(forced) + ")\n"
            + "args = [a.replace('#{pid}:#{start_time}', forced.read_text()) for a in sys.argv[1:]] if forced.exists() else sys.argv[1:]\n"
            + "os.execv(" + executable + ", [" + executable + ", *args])\n", Token);
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Server accepted = await Server.ConnectAsync(fixture.Options with { TmuxBinaryPath = wrapper }, Token);
        IAsyncDisposable owner = kind switch
        {
            "server" => await accepted.AdoptAsync(Token),
            "session" => await (await accepted.GetSessionAsync(originalSession.Id, Token)).AdoptAsync(Token),
            "window" => await (await accepted.GetWindowAsync(originalWindow.Id, Token)).AdoptAsync(Token),
            _ => await (await accepted.GetPaneAsync(originalPane.Id, Token)).AdoptAsync(Token),
        };
        ServerGeneration numeric = accepted.Generation!.Value;
        await fixture.Owner.DisposeAsync();
        await using OwnedServerScope replacement = await Server.CreateOwnedAsync(fixture.Options, Token);
        Session replacementSession = await replacement.Value.CreateSessionAsync(new() { Name = "replacement" }, Token);
        Window replacementWindow = await replacementSession.CreateWindowAsync(new() { Name = "replacement-window" }, Token);
        Pane replacementPane = await replacementWindow.SplitPaneAsync(cancellationToken: Token);
        Assert.NotEqual(numeric, replacement.Value.Generation);
        Assert.Equal(originalSession.Id, replacementSession.Id);
        Assert.Equal(originalWindow.Id, replacementWindow.Id);
        Assert.Equal(originalPane.Id, replacementPane.Id);
        // The real replacement keeps its own token; only the numeric format is forced to collide.
        await File.WriteAllTextAsync(forced, FormattableString.Invariant($"{numeric.ProcessId}:{numeric.StartTime}"), Token);
        StaleServerGenerationException failure = await Assert.ThrowsAsync<StaleServerGenerationException>(() => owner.DisposeAsync().AsTask());
        Assert.Equal(failure.Expected, failure.Actual);
        Assert.NotNull(await replacement.Value.GetPaneAsync(replacementPane.Id, Token));
    }

    [UnixFact]
    public async Task Adoption_of_an_absent_daemon_does_not_start_one()
    {
        await using var fixture = new Fixture();
        await Assert.ThrowsAsync<TmuxObjectNotFoundException>(() => Server.Open(fixture.Options).AdoptAsync(Token));
        Assert.Null(await Server.Open(fixture.Options).InspectAsync(Token));
    }

    [Theory(Skip = "Requires a Unix process environment.", SkipType = typeof(UnixTestEnvironment), SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    [InlineData("server")]
    [InlineData("session")]
    [InlineData("window")]
    [InlineData("pane")]
    public async Task Missing_socket_with_live_daemon_is_unknown_and_cleanup_can_retry(string kind)
    {
        await using var fixture = await Fixture.StartAsync();
        Session session = await fixture.Server.CreateSessionAsync(new() { Name = "accepted" }, Token);
        Window window = Assert.Single(await session.GetWindowsAsync(Token));
        Pane pane = await window.SplitPaneAsync(cancellationToken: Token);
        IAsyncDisposable owner = kind switch
        {
            "server" => await fixture.Server.AdoptAsync(Token),
            "session" => await session.AdoptAsync(Token),
            "window" => await window.AdoptAsync(Token),
            _ => await pane.AdoptAsync(Token),
        };
        string socket = fixture.Options.SocketPath!;
        using Process daemon = Process.GetProcessById(fixture.Server.Generation!.Value.ProcessId);
        File.Delete(socket);
        try
        {
            TmuxTransportException failure = await Assert.ThrowsAsync<TmuxTransportException>(() => owner.DisposeAsync().AsTask());
            Assert.Equal(TmuxDispatchState.Unknown, failure.Dispatch);
            Assert.False(daemon.HasExited);
        }
        finally
        {
            // SIGUSR1 restores this fixture's socket; the library never repairs an endpoint implicitly.
            using Process restore = Process.Start(new ProcessStartInfo("/bin/kill")
            {
                UseShellExecute = false,
                ArgumentList = { "-USR1", daemon.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            })!;
            await restore.WaitForExitAsync(CancellationToken.None);
            Assert.Equal(0, restore.ExitCode);
            Server? restored = null;
            for (int attempt = 0; attempt < 200; attempt++)
            {
                restored = await Server.Open(fixture.Options).InspectAsync(CancellationToken.None);
                if (restored?.Generation == fixture.Server.Generation)
                {
                    break;
                }
                await Task.Delay(10, CancellationToken.None);
            }
            Assert.Equal(fixture.Server.Generation, restored?.Generation);
        }
        await owner.DisposeAsync();
        await owner.DisposeAsync();
        if (kind == "server")
        {
            Assert.Null(await Server.Open(fixture.Options).InspectAsync(Token));
        }
        else
        {
            Assert.Null(await fixture.Server.FindPaneAsync(pane.Id, Token));
        }
    }

    [Theory(Skip = "Requires a Unix process environment.", SkipType = typeof(UnixTestEnvironment), SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    [InlineData("")]
    [InlineData("malformed")]
    public async Task Reserved_token_reuses_valid_metadata_and_rejects_malformed_metadata_before_creation(string invalid)
    {
        await using var fixture = await Fixture.StartAsync();
        Server server = fixture.Server;
        string original = Assert.Single((await server.ExecuteCommandAsync(["show-options", "-sv", "@libtmux_owner_generation"], Token)).StandardOutputLines);
        const string valid = "ABCDEF0123456789ABCDEF0123456789";
        try
        {
            await RunAsync(server, ["set-option", "-s", "@libtmux_owner_generation", valid]);
            _ = await Server.Open(fixture.Options).AdoptAsync(Token);
            Assert.Equal(valid, Assert.Single((await server.ExecuteCommandAsync(["show-options", "-sv", "@libtmux_owner_generation"], Token)).StandardOutputLines));
            await RunAsync(server, ["set-option", "-s", "@libtmux_owner_generation", invalid]);
            await Assert.ThrowsAsync<TmuxCommandException>(() => Server.Open(fixture.Options).AdoptAsync(Token));
            await Assert.ThrowsAsync<TmuxCommandException>(() => Server.Open(fixture.Options).CreateSessionAsync(new() { Name = "never-created" }, Token));
            TmuxCommandResult retained = await server.ExecuteCommandAsync(["show-options", "-sv", "@libtmux_owner_generation"], Token);
            Assert.Equal(0, retained.ExitCode);
            Assert.Equal(invalid, System.Text.Encoding.UTF8.GetString(retained.StandardOutput.Span).TrimEnd('\r', '\n'));
            Assert.False(await server.HasSessionAsync("never-created", cancellationToken: Token));
        }
        finally
        {
            await RunAsync(server, ["set-option", "-s", "@libtmux_owner_generation", original]);
        }
    }

    [UnixFact]
    public async Task Creation_receipt_token_is_not_replaced_by_later_readback_metadata()
    {
        bool armed = false;
        Fixture? current = null;
        await using var fixture = await Fixture.StartAsync(async (invocation, next, token) =>
        {
            TmuxCommandResult result = await next(token);
            if (armed && invocation.Arguments.Contains("new-session", StringComparer.Ordinal))
            {
                armed = false;
                await RunAsync(Server.Open(current!.Options with { Interceptor = null }), ["set-option", "-s", "@libtmux_owner_generation", Guid.NewGuid().ToString("N")]);
            }
            return result;
        });
        current = fixture;
        string original = Assert.Single((await fixture.Server.ExecuteCommandAsync(["show-options", "-sv", "@libtmux_owner_generation"], Token)).StandardOutputLines);
        try
        {
            armed = true;
            LibTmuxException failure = await Assert.ThrowsAsync<LibTmuxException>(() => fixture.Server.CreateOwnedSessionAsync(new() { Name = "created-before-change" }, Token));
            StaleServerGenerationException stale = Assert.IsType<StaleServerGenerationException>(failure.InnerException);
            Assert.Equal(stale.Expected, stale.Actual);
            Assert.IsType<StaleServerGenerationException>(OwnedScope.CleanupFailure(failure));
            Assert.True(await Server.Open(fixture.Options).HasSessionAsync("created-before-change", cancellationToken: Token));
        }
        finally
        {
            await RunAsync(Server.Open(fixture.Options), ["set-option", "-s", "@libtmux_owner_generation", original]);
        }
    }

    [UnixFact]
    public async Task Startup_race_returns_the_unrelated_daemon_borrowed_without_changing_exit_empty()
    {
        await using var fixture = new Fixture();
        bool startCompetitor = true;
        OwnedServerScope? competitorOwner = null;
        try
        {
            Server endpoint = Server.Open(fixture.Options with
            {
                Interceptor = async (invocation, next, token) =>
                {
                    if (startCompetitor && invocation.Arguments.Contains("new-session", StringComparer.Ordinal))
                    {
                        startCompetitor = false;
                        Session session = await Server.Open(fixture.Options).CreateSessionAsync(new() { Name = "competitor" }, token);
                        competitorOwner = await session.Server.AdoptAsync(token);
                    }
                    return await next(token);
                },
            });
            await using FoundOrCreated<Server> found = await endpoint.FindOrCreateAsync(Token);
            Assert.False(found.Created);
            Assert.Null(found.Owner);
            Assert.Equal("competitor", Assert.Single(await found.Value.GetSessionsAsync(Token)).Name);
            TmuxCommandResult value = await found.Value.ExecuteCommandAsync(["show-options", "-sv", "exit-empty"], Token);
            Assert.Equal("on", Assert.Single(value.StandardOutputLines));
            await found.DisposeAsync();
            Assert.NotNull(await endpoint.InspectAsync(Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Server.CreateOwnedAsync(fixture.Options, Token));
        }
        finally
        {
            if (competitorOwner is not null)
            {
                await competitorOwner.DisposeAsync();
            }
        }
    }

    [Theory(Skip = "Requires a Unix process environment.", SkipType = typeof(UnixTestEnvironment), SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    [InlineData("normal")]
    [InlineData("body")]
    [InlineData("cancel")]
    [InlineData("cleanup")]
    [InlineData("both")]
    [InlineData("cancel-cleanup")]
    public async Task Callback_scope_preserves_failure_identity_and_cleanup_retries(string scenario)
    {
        bool failCleanup = false;
        await using var fixture = await Fixture.StartAsync(async (invocation, next, token) =>
        {
            if (failCleanup && invocation.Arguments.Contains("kill-session", StringComparer.Ordinal))
            {
                throw new IOException("Injected cleanup failure.");
            }
            return await next(token);
        });
        OwnedSessionScope owned = await fixture.Server.CreateOwnedSessionAsync(new() { Name = "scoped" }, Token);
        failCleanup = scenario.Contains("cleanup", StringComparison.Ordinal) || scenario == "both";
        var bodyFailure = new InvalidOperationException("Body failed.");
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        Task body = owned.UseAsync((_, _) => scenario switch
        {
            "body" or "both" => Task.FromException(bodyFailure),
            "cancel" or "cancel-cleanup" => Task.FromCanceled(canceled.Token),
            _ => Task.CompletedTask,
        }, Token);
        Exception? failure = await Record.ExceptionAsync(() => body);
        if (scenario is "body" or "both")
        {
            Assert.Same(bodyFailure, failure);
        }
        else if (scenario.StartsWith("cancel", StringComparison.Ordinal))
        {
            Assert.IsAssignableFrom<OperationCanceledException>(failure);
            Assert.Equal(canceled.Token, ((OperationCanceledException)failure!).CancellationToken);
            Assert.True(body.IsCanceled);
        }
        else if (scenario == "cleanup")
        {
            Assert.IsType<IOException>(failure);
        }
        else
        {
            Assert.Null(failure);
        }
        if (scenario is "both" or "cancel-cleanup")
        {
            Assert.IsType<IOException>(OwnedScope.CleanupFailure(failure!));
        }
        Assert.Equal(failCleanup, await fixture.Server.HasSessionAsync("scoped", cancellationToken: Token));
        failCleanup = false;
        await owned.DisposeAsync();
        await owned.DisposeAsync();
        Assert.False(await fixture.Server.HasSessionAsync("scoped", cancellationToken: Token));
    }

    [Theory(Skip = "Requires a Unix process environment.", SkipType = typeof(UnixTestEnvironment), SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    [InlineData("session", false, false)]
    [InlineData("window", false, false)]
    [InlineData("pane", false, false)]
    [InlineData("session", true, false)]
    [InlineData("window", true, false)]
    [InlineData("pane", true, false)]
    [InlineData("session", false, true)]
    [InlineData("window", false, true)]
    [InlineData("pane", false, true)]
    public async Task Known_creation_rolls_back_readback_failure_and_cancellation(string kind, bool cancel, bool cleanupFails)
    {
        bool armed = false;
        bool created = false;
        var readbackFailure = new IOException("Injected readback failure.");
        using CancellationTokenSource cancellation = new();
        string create = kind switch { "session" => "new-session", "window" => "new-window", _ => "split-window" };
        await using var fixture = await Fixture.StartAsync(async (invocation, next, token) =>
        {
            if (armed && created && invocation.Arguments.Contains("kill-" + kind, StringComparer.Ordinal) && cleanupFails)
            {
                throw new IOException("Injected rollback failure.");
            }
            if (armed && created && (invocation.Arguments.Contains("list-" + kind + "s", StringComparer.Ordinal)
                || invocation.Arguments.Any(argument => argument.Contains("#{" + kind + "_id}", StringComparison.Ordinal))))
            {
                throw readbackFailure;
            }
            TmuxCommandResult result = await next(token);
            if (armed && invocation.Arguments.Contains(create, StringComparer.Ordinal))
            {
                created = true;
                if (cancel)
                {
                    cancellation.Cancel();
                }
            }
            return result;
        });
        Session session = await fixture.Server.CreateSessionAsync(new() { Name = "keeper" }, Token);
        Window window = Assert.Single(await session.GetWindowsAsync(Token));
        Pane pane = Assert.Single(await window.GetPanesAsync(Token));
        armed = true;
        Task acquisition = kind switch
        {
            "session" => fixture.Server.CreateOwnedSessionAsync(new() { Name = "rollback" }, cancellation.Token),
            "window" => session.CreateOwnedWindowAsync(new() { Name = "rollback" }, cancellation.Token),
            _ => pane.SplitOwnedAsync(cancellationToken: cancellation.Token),
        };
        Exception? failure = await Record.ExceptionAsync(() => acquisition);
        Assert.NotNull(failure);
        Assert.True(created);
        if (cancel)
        {
            Assert.IsAssignableFrom<OperationCanceledException>(failure);
            Assert.True(acquisition.IsCanceled);
        }
        else
        {
            Assert.Equal(TmuxDispatchState.Unknown, Assert.IsType<LibTmuxException>(failure).Dispatch);
            Assert.Same(readbackFailure, failure.InnerException);
        }
        if (cleanupFails)
        {
            Assert.IsType<IOException>(OwnedScope.CleanupFailure(failure));
        }
        armed = false;
        int count = kind switch
        {
            "session" => (await fixture.Server.GetSessionsAsync(Token)).Count,
            "window" => (await session.GetWindowsAsync(Token)).Count,
            _ => (await window.GetPanesAsync(Token)).Count,
        };
        Assert.Equal(cleanupFails ? 2 : 1, count);
    }

    [UnixFact]
    public async Task An_unknown_initial_creation_result_is_exposed_without_guessing_ownership()
    {
        bool armed = false;
        await using var fixture = await Fixture.StartAsync(async (invocation, next, token) =>
        {
            TmuxCommandResult result = await next(token);
            if (armed && invocation.Arguments.Contains("new-session", StringComparer.Ordinal))
            {
                throw new TmuxTransportException("Creation reply was lost.", invocation.Arguments, TmuxDispatchState.Unknown);
            }
            return result;
        });
        armed = true;
        TmuxTransportException failure = await Assert.ThrowsAsync<TmuxTransportException>(() =>
            fixture.Server.CreateOwnedSessionAsync(new() { Name = "uncertain" }, Token));
        Assert.Equal(TmuxDispatchState.Unknown, failure.Dispatch);
        Assert.Null(OwnedScope.CleanupFailure(failure));
        Assert.True(await fixture.Server.HasSessionAsync("uncertain", cancellationToken: Token));
    }

    [Theory(Skip = "Requires a Unix process environment.", SkipType = typeof(UnixTestEnvironment), SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    [InlineData("server")]
    [InlineData("session")]
    [InlineData("window")]
    [InlineData("pane")]
    public async Task All_owners_share_concurrent_failure_and_allow_retry(string kind)
    {
        bool fail = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupFailure = new IOException("Injected destroy failure.");
        await using var fixture = await Fixture.StartAsync(async (invocation, next, token) =>
        {
            if (fail && invocation.Arguments.Contains("kill-" + kind, StringComparer.Ordinal))
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                throw cleanupFailure;
            }
            return await next(token);
        });
        Session session = await fixture.Server.CreateSessionAsync(new() { Name = "owned" }, Token);
        Window window = await session.CreateWindowAsync(cancellationToken: Token);
        Pane pane = await (await window.GetPanesAsync(Token))[0].SplitAsync(cancellationToken: Token);
        IAsyncDisposable owner = kind switch
        {
            "server" => await fixture.Server.AdoptAsync(Token),
            "session" => await session.AdoptAsync(Token),
            "window" => await window.AdoptAsync(Token),
            _ => await pane.AdoptAsync(Token),
        };
        fail = true;
        Task first = owner.DisposeAsync().AsTask();
        await entered.Task.WaitAsync(Token);
        Task second = owner.DisposeAsync().AsTask();
        Assert.Same(first, second);
        release.SetResult();
        Assert.Same(cleanupFailure, await Record.ExceptionAsync(() => first));
        Assert.Same(cleanupFailure, await Record.ExceptionAsync(() => second));
        Assert.NotNull(await fixture.Server.InspectAsync(Token));
        fail = false;
        await owner.DisposeAsync();
        await owner.DisposeAsync();
        if (kind == "server")
        {
            Assert.Null(await Server.Open(fixture.Options).InspectAsync(Token));
        }
        else
        {
            TmuxCommandResult remaining = await fixture.Server.ExecuteCommandAsync(
                ["list-" + kind + "s", "-F", "#{" + kind + "_id}"], Token);
            Assert.DoesNotContain(kind switch { "session" => session.Id.ToString(), "window" => window.Id.ToString(), _ => pane.Id.ToString() }, remaining.StandardOutputLines);
        }
    }

    [UnixFact]
    public async Task Server_acquisition_cancellation_after_creation_still_destroys_the_proven_daemon()
    {
        await using var fixture = new Fixture();
        using CancellationTokenSource cancellation = new();
        bool created = false;
        Server endpoint = Server.Open(fixture.Options with
        {
            Interceptor = async (invocation, next, token) =>
            {
                TmuxCommandResult result = await next(token);
                if (invocation.Arguments.Contains("new-session", StringComparer.Ordinal))
                {
                    created = true;
                    cancellation.Cancel();
                }
                return result;
            },
        });
        Task acquisition = endpoint.FindOrCreateAsync(cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquisition);
        Assert.True(created);
        Assert.True(acquisition.IsCanceled);
        Assert.Null(await Server.Open(fixture.Options).InspectAsync(Token));
    }

    internal static async Task RunAsync(Server server, IReadOnlyList<string> args)
    {
        TmuxCommandResult result = await server.ExecuteCommandAsync(args, Token);
        Assert.Equal(0, result.ExitCode);
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        internal Fixture()
        {
            Root = Path.Combine("/tmp/libtmux-dotnet-test", "lifecycle-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Options = new ServerConnectionOptions
            {
                SocketPath = Path.Combine(Root, "socket"),
                TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "/usr/bin/tmux",
                ConfigurationFile = "/dev/null",
                CommandTimeout = TimeSpan.FromSeconds(3),
            };
        }

        internal string Root { get; }
        internal ServerConnectionOptions Options { get; }
        internal OwnedServerScope Owner { get; private set; } = null!;
        internal Server Server => Owner.Value;

        internal static async Task<Fixture> StartAsync(TmuxInterceptor? interceptor = null)
        {
            var fixture = new Fixture();
            try
            {
                fixture.Owner = await Server.CreateOwnedAsync(fixture.Options with { Interceptor = interceptor }, Token);
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Owner is not null)
            {
                await Owner.DisposeAsync();
            }
            Directory.Delete(Root, recursive: true);
        }
    }
}
