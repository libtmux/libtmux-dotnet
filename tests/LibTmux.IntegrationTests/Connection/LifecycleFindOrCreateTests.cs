using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Transport;
using static LibTmux.IntegrationTests.Connection.LifecycleOwnershipTests;

namespace LibTmux.IntegrationTests.Connection;

// Creating executable wrappers must not overlap other fixtures' process forks.
[Collection("Process environment")]
[UnsupportedOSPlatform("windows")]
public sealed class LifecycleFindOrCreateTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [UnixFact]
    public async Task Concurrent_server_calls_share_creation_and_preserve_normal_config()
    {
        await using var fixture = new Fixture();
        string configuration = Path.Combine(fixture.Root, "tmux.conf");
        await File.WriteAllTextAsync(configuration, "set-option -g @lifecycle-config retained\n", Token);
        ServerConnectionOptions options = fixture.Options with { ConfigurationFile = configuration };
        FoundOrCreated<Server>[] answers = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Server.Open(options).FindOrCreateAsync(Token)));
        FoundOrCreated<Server> created = Assert.Single(answers, item => item.Created);
        try
        {
            Assert.Single(answers.Select(item => item.Value.Generation).Distinct());
            foreach (FoundOrCreated<Server> reused in answers.Where(item => !item.Created))
            {
                Assert.Null(reused.Owner);
                await reused.DisposeAsync();
            }
            Assert.NotNull(await Server.Open(options).InspectAsync(Token));
            TmuxCommandResult retained = await created.Value.ExecuteCommandAsync(["show-options", "-gv", "@lifecycle-config"], Token);
            Assert.Equal("retained", Assert.Single(retained.StandardOutputLines));
        }
        finally
        {
            await created.DisposeAsync();
        }
        Assert.Null(await Server.Open(options).InspectAsync(Token));
    }

    [UnixFact]
    public async Task Concurrent_session_window_and_pane_calls_create_once_then_reuse_borrowed()
    {
        await using var fixture = await Fixture.StartAsync();
        FoundOrCreated<Session>[] sessions = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Server.Open(fixture.Options).FindOrCreateSessionAsync("literal#name", cancellationToken: Token)));
        await using FoundOrCreated<Session> session = Assert.Single(sessions, item => item.Created);
        Assert.Single(sessions.Select(item => item.Value.Id).Distinct());
        Assert.Equal("literal#name", session.Value.Name);
        foreach (FoundOrCreated<Session> reused in sessions.Where(item => !item.Created))
        {
            await reused.DisposeAsync();
        }
        Assert.NotNull(await fixture.Server.FindSessionAsync(session.Value.Id, Token));
        FoundOrCreated<Window>[] windows = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => session.Value.FindOrCreateWindowAsync("literal#window", cancellationToken: Token)));
        await using FoundOrCreated<Window> window = Assert.Single(windows, item => item.Created);
        Assert.Single(windows.Select(item => item.Value.Id).Distinct());
        foreach (FoundOrCreated<Window> reused in windows.Where(item => !item.Created))
        {
            await reused.DisposeAsync();
        }
        Assert.NotNull(await fixture.Server.FindWindowAsync(window.Value.Id, Token));
        FoundOrCreated<Pane>[] panes = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => window.Value.FindOrCreatePaneAsync("application/build", cancellationToken: Token)));
        await using FoundOrCreated<Pane> pane = Assert.Single(panes, item => item.Created);
        Assert.Single(panes.Select(item => item.Value.Id).Distinct());
        foreach (FoundOrCreated<Pane> reused in panes.Where(item => !item.Created))
        {
            Assert.Null(reused.Owner);
            await reused.DisposeAsync();
        }
        Assert.NotNull(await fixture.Server.FindPaneAsync(pane.Value.Id, Token));
    }

    [UnixFact]
    public async Task Ambiguous_window_and_pane_identities_fail_without_creating_another_match()
    {
        await using var fixture = await Fixture.StartAsync();
        Session session = await fixture.Server.CreateSessionAsync(new() { Name = "unique-session" }, Token);
        Window one = await session.CreateWindowAsync(new() { Name = "duplicate" }, Token);
        await session.CreateWindowAsync(new() { Name = "duplicate" }, Token);
        TmuxAmbiguousMatchException windowFailure = await Assert.ThrowsAsync<TmuxAmbiguousMatchException>(() =>
            session.FindOrCreateWindowAsync("duplicate", cancellationToken: Token));
        Assert.Equal(2, windowFailure.Count);
        await one.SplitPaneAsync(cancellationToken: Token);
        foreach (Pane pane in await one.GetPanesAsync(Token))
        {
            await RunAsync(fixture.Server, ["set-option", "-p", "-t", pane.Id.ToString(), "@libtmux-identity", "duplicate"]);
        }
        TmuxAmbiguousMatchException paneFailure = await Assert.ThrowsAsync<TmuxAmbiguousMatchException>(() =>
            one.FindOrCreatePaneAsync("duplicate", cancellationToken: Token));
        Assert.Equal(2, paneFailure.Count);
        Assert.Equal(2, (await one.GetPanesAsync(Token)).Count);
        await Assert.ThrowsAsync<TmuxSessionExistsException>(() => fixture.Server.CreateSessionAsync(new() { Name = "unique-session" }, Token));
        await using FoundOrCreated<Session> reused = await fixture.Server.FindOrCreateSessionAsync("unique-session", cancellationToken: Token);
        Assert.False(reused.Created);
    }

    [UnixFact]
    public async Task Creation_and_identity_installation_failures_leave_no_owned_objects()
    {
        bool failCreate = false;
        bool failIdentity = false;
        await using var fixture = await Fixture.StartAsync(async (invocation, next, token) =>
        {
            if ((failCreate && invocation.Arguments.Any(argument => argument is "new-session" or "new-window" or "split-window"))
                || (failIdentity && invocation.Arguments.Contains("set-option", StringComparer.Ordinal)
                    && invocation.Arguments.Contains("@libtmux-identity", StringComparer.Ordinal)))
            {
                throw new IOException("Injected creation failure.");
            }
            return await next(token);
        });
        Session session = await fixture.Server.CreateSessionAsync(new() { Name = "keeper" }, Token);
        Window window = Assert.Single(await session.GetWindowsAsync(Token));
        failCreate = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Server.FindOrCreateSessionAsync("failed", cancellationToken: Token));
        await Assert.ThrowsAsync<IOException>(() => session.FindOrCreateWindowAsync("failed", cancellationToken: Token));
        await Assert.ThrowsAsync<IOException>(() => window.FindOrCreatePaneAsync("failed", cancellationToken: Token));
        failCreate = false;
        failIdentity = true;
        await Assert.ThrowsAsync<IOException>(() => window.FindOrCreatePaneAsync("failed", cancellationToken: Token));
        Assert.Single(await fixture.Server.GetSessionsAsync(Token));
        Assert.Single(await session.GetWindowsAsync(Token));
        Assert.Single(await window.GetPanesAsync(Token));
        await using var empty = new Fixture();
        Server endpoint = Server.Open(empty.Options with
        {
            Interceptor = (invocation, next, token) => invocation.Arguments.Contains("new-session", StringComparer.Ordinal)
                ? throw new IOException("Injected server creation failure.") : next(token),
        });
        await Assert.ThrowsAsync<IOException>(() => endpoint.FindOrCreateAsync(Token));
        Assert.Null(await Server.Open(empty.Options).InspectAsync(Token));
    }

    [Fact(Skip = "Requires the Linux Python wrapper used to force equal numeric identities.", SkipType = typeof(LifecycleOwnershipTests), SkipUnless = nameof(IsLinux))]
    public async Task Pane_identity_installation_rejects_a_replacement_with_reused_numeric_ids()
    {
        await using var fixture = await Fixture.StartAsync();
        Session originalSession = await fixture.Server.CreateSessionAsync(new() { Name = "original" }, Token);
        Window originalWindow = Assert.Single(await originalSession.GetWindowsAsync(Token));
        PaneId originalPaneId = Assert.Single(await originalWindow.GetPanesAsync(Token)).Id;
        ServerGeneration numeric = fixture.Server.Generation!.Value;
        string forced = Path.Combine(fixture.Root, "forced-generation");
        string wrapper = Path.Combine(fixture.Root, "tmux-wrapper");
        string executable = System.Text.Json.JsonSerializer.Serialize(fixture.Options.TmuxBinaryPath);
        await File.WriteAllTextAsync(wrapper,
            "#!/usr/bin/python3\nimport os, pathlib, sys\n"
            + "forced = pathlib.Path(" + System.Text.Json.JsonSerializer.Serialize(forced) + ")\n"
            + "args = [a.replace('#{pid}:#{start_time}', forced.read_text()) for a in sys.argv[1:]] if forced.exists() else sys.argv[1:]\n"
            + "os.execv(" + executable + ", [" + executable + ", *args])\n", Token);
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        OwnedServerScope? replacement = null;
        Pane? replacementPane = null;
        bool replace = true;
        try
        {
            Server borrowed = await Server.ConnectAsync(fixture.Options with
            {
                TmuxBinaryPath = wrapper,
                Interceptor = async (invocation, next, token) =>
                {
                    if (replace && invocation.Arguments.Contains("set-option", StringComparer.Ordinal)
                        && invocation.Arguments.Contains("@libtmux-identity", StringComparer.Ordinal))
                    {
                        replace = false;
                        Pane created = (await originalWindow.GetPanesAsync(token)).Single(pane => pane.Id != originalPaneId);
                        await fixture.Owner.DisposeAsync();
                        Session bootstrap = await Server.Open(fixture.Options).CreateSessionAsync(new() { Name = "bootstrap" }, token);
                        replacement = await bootstrap.Server.AdoptAsync(token);
                        await RunAsync(replacement.Value, ["set-option", "-s", "exit-empty", "off"]);
                        await RunAsync(replacement.Value, ["kill-session", "-t", bootstrap.Id.ToString()]);
                        Session replacementSession = await replacement.Value.CreateSessionAsync(new() { Name = "replacement" }, token);
                        Window replacementWindow = Assert.Single(await replacementSession.GetWindowsAsync(token));
                        replacementPane = await replacementWindow.SplitPaneAsync(cancellationToken: token);
                        Assert.NotEqual(numeric, replacement.Value.Generation);
                        Assert.Equal(originalSession.Id, replacementSession.Id);
                        Assert.Equal(originalWindow.Id, replacementWindow.Id);
                        Assert.Equal(created.Id, replacementPane.Id);
                        await File.WriteAllTextAsync(forced, FormattableString.Invariant($"{numeric.ProcessId}:{numeric.StartTime}"), token);
                    }
                    return await next(token);
                },
            }, Token);
            Window window = await borrowed.GetWindowAsync(originalWindow.Id, Token);
            StaleServerGenerationException failure = await Assert.ThrowsAsync<StaleServerGenerationException>(() =>
                window.FindOrCreatePaneAsync("must-not-reach-replacement", cancellationToken: Token));
            Assert.Equal(failure.Expected, failure.Actual);
            Assert.IsType<StaleServerGenerationException>(OwnedScope.CleanupFailure(failure));
            Assert.NotNull(replacementPane);
            TmuxCommandResult identity = await replacement!.Value.ExecuteCommandAsync(
                ["show-options", "-p", "-qv", "-t", replacementPane.Id.ToString(), "@libtmux-identity"], Token);
            Assert.Equal(0, identity.ExitCode);
            Assert.Empty(identity.StandardOutputLines);
            Assert.NotNull(await replacement.Value.GetPaneAsync(replacementPane.Id, Token));
        }
        finally
        {
            if (replacement is not null)
            {
                await replacement.DisposeAsync();
            }
        }
    }
}
