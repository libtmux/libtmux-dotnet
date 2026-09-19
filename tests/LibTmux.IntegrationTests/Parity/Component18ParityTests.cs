using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Testing;

namespace LibTmux.IntegrationTests.Parity;

[UnsupportedOSPlatform("windows")]
public sealed class Component18ParityTests
{
    public static TheoryData<string> OwnedRows =>
    [
        "libtmux.pytest_plugin:<module>",
        "libtmux.pytest_plugin:TestServer",
        "libtmux.pytest_plugin:USING_ZSH",
        "libtmux.pytest_plugin:clear_env",
        "libtmux.pytest_plugin:config_file",
        "libtmux.pytest_plugin:control_mode",
        "libtmux.pytest_plugin:home_path",
        "libtmux.pytest_plugin:home_user_name",
        "libtmux.pytest_plugin:server",
        "libtmux.pytest_plugin:session",
        "libtmux.pytest_plugin:session_params",
        "libtmux.pytest_plugin:user_path",
        "libtmux.pytest_plugin:zshrc",
        "libtmux.test.constants:<module>",
        "libtmux.test.constants:RETRY_INTERVAL_SECONDS",
        "libtmux.test.constants:RETRY_TIMEOUT_SECONDS",
        "libtmux.test.constants:TEST_SESSION_PREFIX",
        "libtmux.test.environment:<module>",
        "libtmux.test.environment:EnvironmentVarGuard",
        "libtmux.test.environment:EnvironmentVarGuard.set",
        "libtmux.test.environment:EnvironmentVarGuard.unset",
        "libtmux.test.random:<module>",
        "libtmux.test.random:RandomStrSequence",
        "libtmux.test.random:get_test_session_name",
        "libtmux.test.random:get_test_window_name",
        "libtmux.test.random:namer",
        "libtmux.test.retry:<module>",
        "libtmux.test.retry:retry_until",
        "libtmux.test.temporary:<module>",
        "libtmux.test.temporary:temp_session",
        "libtmux.test.temporary:temp_window",
        "libtmux.test:<module>",
    ];

    [Theory(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    [MemberData(nameof(OwnedRows))]
    public async Task Owned_parity_row_has_testing_behavior(string pythonSymbolId)
    {
        bool proved = pythonSymbolId switch
        {
            "libtmux.test:<module>" or "libtmux.pytest_plugin:<module>" =>
                await ProvesOneEntryPointAsync(),
            "libtmux.pytest_plugin:TestServer" or "libtmux.pytest_plugin:server"
                or "libtmux.pytest_plugin:config_file" =>
                await ProvesServerFixtureAsync(),
            "libtmux.pytest_plugin:session" or "libtmux.pytest_plugin:session_params" =>
                await ProvesSessionFixtureAsync(),
            "libtmux.pytest_plugin:clear_env" or "libtmux.pytest_plugin:home_path"
                or "libtmux.pytest_plugin:home_user_name" or "libtmux.pytest_plugin:user_path"
                or "libtmux.pytest_plugin:zshrc" or "libtmux.pytest_plugin:USING_ZSH"
                or "libtmux.test.environment:<module>"
                or "libtmux.test.environment:EnvironmentVarGuard"
                or "libtmux.test.environment:EnvironmentVarGuard.set"
                or "libtmux.test.environment:EnvironmentVarGuard.unset" =>
                ProvesEnvironment(),
            "libtmux.pytest_plugin:control_mode" => await ProvesControlModeAsync(),
            "libtmux.test.constants:<module>" or "libtmux.test.constants:RETRY_INTERVAL_SECONDS"
                or "libtmux.test.constants:RETRY_TIMEOUT_SECONDS"
                or "libtmux.test.constants:TEST_SESSION_PREFIX" =>
                ProvesDefaults(),
            "libtmux.test.random:<module>" or "libtmux.test.random:RandomStrSequence"
                or "libtmux.test.random:namer" =>
                ProvesNaming(),
            "libtmux.test.random:get_test_session_name" =>
                await ProvesAvailableNameAsync(session: true),
            "libtmux.test.random:get_test_window_name" =>
                await ProvesAvailableNameAsync(session: false),
            "libtmux.test.retry:<module>" or "libtmux.test.retry:retry_until" =>
                await ProvesWaitingAsync(),
            "libtmux.test.temporary:<module>" => await ProvesHierarchyAsync(),
            "libtmux.test.temporary:temp_session" => await ProvesTemporaryAsync(window: false),
            "libtmux.test.temporary:temp_window" => await ProvesTemporaryAsync(window: true),
            _ => false,
        };

        Assert.True(proved, $"Parity behavior was not proved for {pythonSymbolId}.");
    }

    private static TmuxTestOptions Options() =>
        new(new ServerConnectionOptions { TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux", SocketName = $"ltp-{Guid.NewGuid():N}"[..20], ConfigurationFile = "/dev/null" });

    private static async Task<bool> ProvesOneEntryPointAsync()
    {
        // Python ships a pytest plugin, which only helps a pytest user. The
        // helpers here are ordinary types, so any test framework reaches them.
        TmuxTestFactory factory = new();
        await using TemporaryServerScope scope = await factory.CreateServerAsync(
            Options(),
            TestContext.Current.CancellationToken);
        return scope.Server is not null;
    }

    private static async Task<bool> ProvesServerFixtureAsync()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TmuxTestFactory factory = new();
        TmuxTestOptions options = Options();

        await using (TmuxTestContext context = await factory.CreateContextAsync(options, token))
        {
            // The fixture names its own configuration file, so nothing a
            // developer has in theirs can change what a test sees.
            Assert.Equal("/dev/null", context.Server.ConnectionOptions.ConfigurationFile);
            await using TemporarySessionScope session = await factory.CreateSessionAsync(
                context.Server,
                options,
                token);
            Assert.True(await context.Server.IsAliveAsync(token));
        }

        return true;
    }

    private static async Task<bool> ProvesSessionFixtureAsync()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TmuxTestFactory factory = new();
        await using TemporarySessionScope scope = await factory.CreateSessionAsync(
            Options(),
            token);

        Assert.True(await scope.Session.Server.HasSessionAsync(scope.Session.Name, true, token));
        return scope.Session.Name.StartsWith("lt", StringComparison.Ordinal);
    }

    private static bool ProvesEnvironment()
    {
        // The environment is described rather than mutated, so two tests
        // running at once cannot see each other's changes.
        TestEnvironment environment = new(
            "/tmp",
            new Dictionary<string, string?> { ["TMUX"] = null });
        Assert.Null(environment.Variables["TMUX"]);

        TestEnvironment richer = environment.WithVariable("SHELL", "/bin/zsh");
        Assert.Equal("/bin/zsh", richer.Variables["SHELL"]);
        Assert.False(environment.Variables.ContainsKey("SHELL"));

        TestEnvironment stripped = richer.WithoutVariable("SHELL");
        return stripped.Variables.ContainsKey("SHELL") && stripped.Variables["SHELL"] is null;
    }

    private static async Task<bool> ProvesControlModeAsync()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            Options(),
            token);

        // Control mode is reached through the ordinary connection rather than
        // through a fixture of its own, so a scope is all a test needs.
        Assert.NotNull(scope.Server.Connection);
        return scope.Pane.Id.ToString().StartsWith('%');
    }

    private static bool ProvesDefaults()
    {
        // These are defaults on an options record rather than fixed constants,
        // so a slow machine can wait longer without editing the library.
        TmuxTestOptions defaults = TmuxTestOptions.Default;
        Assert.True(defaults.Timeout > TimeSpan.Zero);
        Assert.True(defaults.PollInterval > TimeSpan.Zero);
        Assert.True(defaults.PollInterval < defaults.Timeout);
        Assert.Equal("lt", defaults.SessionNamePrefix);

        TmuxTestOptions patient = new(timeout: TimeSpan.FromMinutes(1));
        return patient.Timeout == TimeSpan.FromMinutes(1)
            && patient.SessionNamePrefix == defaults.SessionNamePrefix;
    }

    private static bool ProvesNaming()
    {
        TmuxNameGenerator names = new();
        HashSet<string> made = [];
        for (int index = 0; index < 50; index++)
        {
            Assert.True(made.Add(names.CreateSessionName()));
            Assert.True(made.Add(names.CreateWindowName()));
        }

        // tmux reads a colon or a full stop as a target separator, so a name
        // holding one could never be addressed again.
        Assert.All(made, name => Assert.DoesNotContain(':', name));
        return made.All(name => !name.Contains('.', StringComparison.Ordinal));
    }

    private static async Task<bool> ProvesAvailableNameAsync(bool session)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TmuxTestFactory factory = new();
        TmuxNameGenerator names = new();
        await using TemporarySessionScope scope = await factory.CreateSessionAsync(
            Options(),
            token);

        if (session)
        {
            string name = await names.CreateAvailableSessionNameAsync(
                scope.Session.Server,
                cancellationToken: token);
            return !await scope.Session.Server.HasSessionAsync(name, true, token);
        }

        string window = await names.CreateAvailableWindowNameAsync(
            scope.Session,
            cancellationToken: token);
        IReadOnlyList<Window> windows = await scope.Session.GetWindowsAsync(token);
        return !windows.Any(existing =>
            string.Equals(existing.Name, window, StringComparison.Ordinal));
    }

    private static async Task<bool> ProvesWaitingAsync()
    {
        CancellationToken token = TestContext.Current.CancellationToken;

        // A probe that is already true answers at once, whatever the interval,
        // so waiting never costs a test time it did not need to spend.
        Assert.True(await TmuxWait.UntilAsync(
            static _ => Task.FromResult(true),
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1),
            cancellationToken: token));

        // Running out is a failure by default, and an answer when asked for,
        // which is what separates a wait from a poll.
        await Assert.ThrowsAsync<TmuxWaitTimeoutException>(
            () => TmuxWait.UntilAsync(
                static _ => Task.FromResult(false),
                TimeSpan.FromMilliseconds(50),
                TimeSpan.FromMilliseconds(10),
                cancellationToken: token));
        return !await TmuxWait.UntilAsync(
            static _ => Task.FromResult(false),
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(10),
            throwOnTimeout: false,
            cancellationToken: token);
    }

    private static async Task<bool> ProvesHierarchyAsync()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            Options(),
            token);

        // The four agree with each other because they came from one reading.
        Assert.Equal(scope.Session.Id, scope.Pane.Session.Id);
        return scope.Window.Id == scope.Pane.Window.Id;
    }

    private static async Task<bool> ProvesTemporaryAsync(bool window)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TmuxTestFactory factory = new();
        TmuxTestOptions options = Options();
        await using TemporaryServerScope server = await factory.CreateServerAsync(options, token);
        await using TemporarySessionScope host = await factory.CreateSessionAsync(
            server.Server,
            options,
            token);

        if (!window)
        {
            string name;
            await using (TemporarySessionScope scope = await factory.CreateSessionAsync(
                server.Server,
                options,
                token))
            {
                name = scope.Session.Name;
                Assert.True(await server.Server.HasSessionAsync(name, true, token));
            }

            // Leaving the scope removes what it made and nothing else.
            Assert.False(await server.Server.HasSessionAsync(name, true, token));
            return await server.Server.HasSessionAsync(host.Session.Name, true, token);
        }

        WindowId id;
        await using (TemporaryWindowScope scope = await factory.CreateWindowAsync(
            host.Session,
            options,
            token))
        {
            id = scope.Window.Id;
            Assert.Contains(await host.Session.GetWindowsAsync(token), found => found.Id == id);
        }

        return !(await host.Session.GetWindowsAsync(token)).Any(found => found.Id == id);
    }
}
