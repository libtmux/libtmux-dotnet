using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Testing;

namespace LibTmux.IntegrationTests.Testing;

[UnsupportedOSPlatform("windows")]
public sealed class TestingHelpersTests
{
    [UnixFact]
    public async Task Scopes_clean_up_whatever_they_own()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TmuxTestFactory factory = new();
        TmuxTestOptions options = HarnessOptions();

        // A scope owning a server kills it, so nothing survives the using.
        string socket;
        await using (TmuxTestContext context = await factory.CreateContextAsync(
            options,
            token))
        {
            socket = context.Server.ConnectionOptions.SocketName
                ?? context.Server.ConnectionOptions.SocketPath!;
            // A tmux server with no sessions is not one list-sessions reports,
            // so the proof that it is there is a session made on it.
            await using TemporarySessionScope session = await factory.CreateSessionAsync(
                context.Server,
                options,
                token);
            Assert.True(await context.Server.IsAliveAsync(token));

            // The environment says what a test's own processes should inherit,
            // and what they must not: the developer's tmux.
            Assert.Null(context.Environment.Variables["TMUX"]);
            Assert.Null(context.Environment.Variables["TMUX_PANE"]);
            Assert.NotEmpty(context.Environment.WorkingDirectory);
        }

        // Connecting reads the server's generation, so a socket with nothing
        // behind it cannot be connected to at all: that refusal is the proof
        // the scope took the server with it.
        await Assert.ThrowsAnyAsync<LibTmuxException>(
            () => Server.ConnectAsync(
                new ServerConnectionOptions(
                    tmuxBinaryPath: options.ConnectionOptions.TmuxBinaryPath,
                    socketName: socket,
                    configurationFile: "/dev/null"),
                token));
    }

    [UnixFact]
    public async Task Temporary_hierarchy_is_xunit_independent_and_cleans_up()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TmuxTestFactory factory = new();

        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            HarnessOptions(),
            token);

        // The four agree with each other, which is what makes them worth
        // handing out together.
        Assert.Equal(scope.Session.Id, scope.Pane.Session.Id);
        Assert.Equal(scope.Window.Id, scope.Pane.Window.Id);
        // A handle that has read tmux is a replacement rather than the object
        // that was asked, so what ties them together is the socket they share.
        Assert.Equal(
            scope.Server.ConnectionOptions.SocketName,
            scope.Session.Server.ConnectionOptions.SocketName);

        await scope.Pane.SendTextAsync("echo hierarchy", cancellationToken: token);
        await scope.Pane.EnterAsync(token);
        string text = await TmuxWait.UntilAsync(
            async cancellation => string.Join(
                '\n',
                await scope.Pane.CaptureAsync(cancellationToken: cancellation)),
            captured => captured.Contains("hierarchy", StringComparison.Ordinal),
            TestBudget.Settle,
            TimeSpan.FromMilliseconds(20),
            token);
        Assert.Contains("hierarchy", text, StringComparison.Ordinal);

        // The helpers are ordinary types. Nothing here answers to xUnit, so a
        // project using another framework reaches them the same way.
        Assert.DoesNotContain(
            typeof(TmuxTestFactory).Assembly.GetReferencedAssemblies(),
            reference => reference.Name?.StartsWith("xunit", StringComparison.OrdinalIgnoreCase)
                == true);
    }

    [UnixFact]
    public async Task Self_contained_session_scope_stops_its_private_server()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TmuxTestFactory factory = new();
        TemporarySessionScope scope = await factory.CreateSessionAsync(HarnessOptions(), token);
        ServerConnectionOptions endpoint = scope.Session.Server.ConnectionOptions;
        try
        {
            Assert.True(await scope.Session.Server.IsAliveAsync(token));
        }
        finally
        {
            await scope.DisposeAsync();
        }

        await Assert.ThrowsAnyAsync<LibTmuxException>(
            () => Server.ConnectAsync(endpoint, token));
    }

    [UnixFact]
    public async Task Self_contained_window_scope_stops_its_private_server()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TmuxTestFactory factory = new();
        TemporaryWindowScope scope = await factory.CreateWindowAsync(HarnessOptions(), token);
        ServerConnectionOptions endpoint = scope.Window.Server.ConnectionOptions;
        try
        {
            Assert.True(await scope.Window.Server.IsAliveAsync(token));
        }
        finally
        {
            await scope.DisposeAsync();
        }

        await Assert.ThrowsAnyAsync<LibTmuxException>(
            () => Server.ConnectAsync(endpoint, token));
    }

    [UnixFact]
    public async Task Generated_names_do_not_collide()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TmuxTestFactory factory = new();
        TmuxNameGenerator names = new();

        // Two names from one generator differ, which is what keeps two tests
        // in one run out of each other's sessions.
        HashSet<string> made = [];
        for (int index = 0; index < 100; index++)
        {
            Assert.True(made.Add(names.CreateSessionName()));
            Assert.True(made.Add(names.CreateWindowName()));
        }

        // tmux reads a colon or a full stop as a target separator, so a
        // generated name may never contain one.
        Assert.All(made, name => Assert.DoesNotContain(':', name));
        Assert.All(made, name => Assert.DoesNotContain('.', name));

        await using TemporaryServerScope server = await factory.CreateServerAsync(
            HarnessOptions(),
            token);
        string available = await names.CreateAvailableSessionNameAsync(
            server.Server,
            cancellationToken: token);
        Assert.False(await server.Server.HasSessionAsync(available, true, token));

        // A prefix that could never be a session name is refused up front.
        Assert.Throws<ArgumentException>(() => new TmuxNameGenerator("has:colon"));
    }

    [UnixFact]
    public async Task Waiting_answers_as_soon_as_the_state_is_reached()
    {
        CancellationToken token = TestContext.Current.CancellationToken;

        // A probe that is already true never waits, whatever the interval.
        Assert.True(await TmuxWait.UntilAsync(
            static _ => Task.FromResult(true),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30),
            cancellationToken: token));

        // Running out is a failure by default, and an answer when asked for.
        await Assert.ThrowsAsync<TmuxWaitTimeoutException>(
            () => TmuxWait.UntilAsync(
                static _ => Task.FromResult(false),
                TimeSpan.FromMilliseconds(50),
                TimeSpan.FromMilliseconds(10),
                cancellationToken: token));
        Assert.False(await TmuxWait.UntilAsync(
            static _ => Task.FromResult(false),
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(10),
            throwOnTimeout: false,
            cancellationToken: token));

        // A wait with no time to wait in, or no pause between askings, would
        // never do what it was asked.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => TmuxWait.UntilAsync(
                static _ => Task.FromResult(true),
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(10),
                cancellationToken: token));

        // Cancelling stops the wait rather than running it out.
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => TmuxWait.UntilAsync(
                static _ => Task.FromResult(false),
                TestBudget.Settle,
                TimeSpan.FromMilliseconds(10),
                cancellationToken: cancelled.Token));
    }

    // The library reaches tmux through PATH by default, which is right for a
    // caller and wrong for a suite pinned to one build per lane.
    private static TmuxTestOptions HarnessOptions() =>
        new(new ServerConnectionOptions(
            tmuxBinaryPath: Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux",
            socketName: $"lths-{Guid.NewGuid():N}"[..20],
            configurationFile: "/dev/null"));

    [UnixFact]
    public void An_environment_says_what_to_set_and_what_to_remove()
    {
        TestEnvironment environment = new(
            "/tmp",
            new Dictionary<string, string?> { ["KEEP"] = "yes" });

        TestEnvironment richer = environment.WithVariable("ADDED", "here");
        Assert.Equal("here", richer.Variables["ADDED"]);
        Assert.Equal("yes", richer.Variables["KEEP"]);

        // Removing is not the same as setting nothing, so the entry stays with
        // no value rather than disappearing.
        TestEnvironment stripped = richer.WithoutVariable("KEEP");
        Assert.True(stripped.Variables.ContainsKey("KEEP"));
        Assert.Null(stripped.Variables["KEEP"]);

        // Each answer is a copy, so the one that was asked is unchanged.
        Assert.Equal("yes", environment.Variables["KEEP"]);
        Assert.False(environment.Variables.ContainsKey("ADDED"));
    }
}
