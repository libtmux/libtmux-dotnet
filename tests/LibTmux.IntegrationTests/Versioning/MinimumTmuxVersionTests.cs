using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;

namespace LibTmux.IntegrationTests.Versioning;

/// <summary>Proves the tmux minimum against daemons that really are below it.</summary>
/// <remarks>
/// <para>
/// <c>LIBTMUX_TMUX</c> names the qualified tmux that serves as client and
/// daemon. <c>LIBTMUX_REJECTED_TMUX</c> names one that is below the minimum,
/// and <c>LIBTMUX_FLOOR_TMUX</c> a 3.3a build whose protocol the rejected
/// client can speak. Without the rejected build these tests skip, unless
/// <c>LIBTMUX_REJECTED_REQUIRED</c> is set, which makes its absence a failure.
/// </para>
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed class MinimumTmuxVersionTests
{
    private const string Sentinel = "kept";

    [UnixFact]
    public async Task A_qualified_daemon_serves_raw_batched_and_typed_commands()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using var daemon = await TestDaemon.StartAsync(Qualified(), token);
        Server server = Server.Open(daemon.Options(Qualified()));

        TmuxCommandResult raw = await server.ExecuteCommandAsync(
            ["new-session", "-d", "-s", "raw"],
            token);
        await server.Chain()
            .Then(TmuxChaining.Command(["new-session", "-d", "-s", "batch-one"]))
            .Then(TmuxChaining.Command(["new-session", "-d", "-s", "batch-two"]))
            .ExecuteAsync(token);
        Session typed = await server.CreateSessionAsync(new NewSessionRequest { Name = "typed" }, token);

        Assert.Equal(0, raw.ExitCode);
        Assert.Equal("typed", typed.Name);
        Assert.Equal(
            new[] { Sentinel, "raw", "batch-one", "batch-two", "typed" }.Order(),
            (await daemon.SessionsAsync(token)).Order());
        Assert.True(await server.IsAliveAsync(token));
    }

    [UnixFact]
    public async Task A_daemon_below_the_minimum_is_refused_by_every_entry_point_without_effects()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string rejected = Rejected();
        await using var daemon = await TestDaemon.StartAsync(rejected, token);
        Server server = Server.Open(daemon.Options(Qualified()));
        string version = await daemon.VersionAsync(token);

        static async Task<TmuxVersionTooLowException> RefusedAsync(Func<Task> operation)
        {
            TmuxVersionTooLowException refusal = await Assert.ThrowsAsync<TmuxVersionTooLowException>(operation);
            Assert.Equal(TmuxDispatchState.NotDispatched, refusal.Dispatch);
            return refusal;
        }

        TmuxVersionTooLowException refused = await RefusedAsync(
            () => server.ExecuteCommandAsync(["new-session", "-d", "-s", "effect-raw"], token));
        Assert.Equal(TmuxVersion.Parse("3.3a"), refused.RequiredVersion);
        Assert.Equal(TmuxVersion.Parse(version), refused.ActualVersion);

        _ = await RefusedAsync(() => server.Chain()
            .Then(TmuxChaining.Command(["rename-session", "-t", $"={Sentinel}", "effect-batch"]))
            .Then(TmuxChaining.Command(["new-session", "-d", "-s", "effect-batch-two"]))
            .ExecuteAsync(token));
        _ = await RefusedAsync(() => server.CreateSessionAsync(new NewSessionRequest { Name = "effect-typed" }, token));
        _ = await RefusedAsync(() => server.GetSessionsAsync(token));
        _ = await RefusedAsync(() => server.ConnectAsync(token));
        _ = await RefusedAsync(() => server.EnterControlModeAsync(cancellationToken: token));
        _ = await RefusedAsync(() => server.IsAliveAsync(token));
        _ = await RefusedAsync(() => server.KillSessionAsync(Sentinel, token));
        _ = await RefusedAsync(() => server.KillAsync(token));

        // Nothing the refused calls asked for happened: the daemon still has
        // only its own session, no control client attached, and it is alive.
        Assert.Equal([Sentinel], await daemon.SessionsAsync(token));
        Assert.Empty((await daemon.RunAsync(["list-clients"], token)).StandardOutputLines);
        Assert.Equal(0, (await daemon.RunAsync(["has-session", "-t", $"={Sentinel}"], token)).ExitCode);
    }

    [UnixFact]
    public async Task A_daemon_below_the_minimum_is_refused_whatever_its_client_reports()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string rejected = Rejected();
        await using var daemon = await TestDaemon.StartAsync(rejected, token);

        // Both clients speak to the same old daemon: one is as old as it is,
        // the other is the qualified build, and neither authorizes anything.
        foreach (string client in new[] { rejected, Qualified() })
        {
            Server server = Server.Open(daemon.Options(client));

            await Assert.ThrowsAsync<TmuxVersionTooLowException>(
                () => server.ExecuteCommandAsync(["new-session", "-d", "-s", "effect"], token));
        }

        Assert.Equal([Sentinel], await daemon.SessionsAsync(token));
    }

    [UnixFact]
    public async Task A_replaced_daemon_below_the_minimum_does_not_inherit_the_previous_version_check()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string rejected = Rejected();
        await using var daemon = await TestDaemon.StartAsync(Qualified(), token);
        Server server = Server.Open(daemon.Options(Qualified()));
        await server.ExecuteCommandAsync(["new-session", "-d", "-s", "before"], token);

        await daemon.ReplaceAsync(rejected, token);

        TmuxVersionTooLowException refusal = await Assert.ThrowsAsync<TmuxVersionTooLowException>(
            () => server.ExecuteCommandAsync(["new-session", "-d", "-s", "effect"], token));
        Assert.Equal(TmuxVersion.Parse(await daemon.VersionAsync(token)), refusal.ActualVersion);
        Assert.Equal(TmuxDispatchState.NotDispatched, refusal.Dispatch);
        Assert.Equal([Sentinel], await daemon.SessionsAsync(token));
    }

    [UnixFact]
    public async Task A_replaced_daemon_at_another_supported_version_is_supported_afresh()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string floor = FloorBuild();
        await using var daemon = await TestDaemon.StartAsync(Qualified(), token);
        Server server = Server.Open(daemon.Options(Qualified()));
        await server.ExecuteCommandAsync(["new-session", "-d", "-s", "before"], token);

        await daemon.ReplaceAsync(floor, token);
        await server.ExecuteCommandAsync(["new-session", "-d", "-s", "after"], token);

        Assert.Equal(["after", Sentinel], (await daemon.SessionsAsync(token)).Order());
    }

    [UnixFact]
    public async Task A_materialized_handle_stays_bound_to_its_daemon()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string rejected = Rejected();
        await using var daemon = await TestDaemon.StartAsync(Qualified(), token);
        Server server = await Server.ConnectAsync(daemon.Options(Qualified()), token);
        Session session = (await server.GetSessionsAsync(token)).Single();

        await daemon.ReplaceAsync(rejected, token);

        await Assert.ThrowsAsync<StaleServerGenerationException>(() => session.GetWindowsAsync(token));
        await Assert.ThrowsAsync<StaleServerGenerationException>(
            () => session.CreateWindowAsync(new NewWindowRequest { Name = "effect" }, token));
        _ = Assert.Single((await daemon.RunAsync(["list-windows", "-a"], token)).StandardOutputLines);
    }

    [UnixFact]
    public async Task An_absent_endpoint_is_refused_when_the_executable_that_would_start_it_is_below_the_minimum()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string rejected = Rejected();
        using var directory = new ScratchDirectory();
        string socket = Path.Join(directory.Path, "s");
        Server server = Server.Open(new ServerConnectionOptions
        {
            TmuxBinaryPath = rejected,
            SocketPath = socket,
            ConfigurationFile = "/dev/null",
        });

        await Assert.ThrowsAsync<TmuxVersionTooLowException>(
            () => server.ExecuteCommandAsync(["new-session", "-d", "-s", "effect"], token));
        await Assert.ThrowsAsync<TmuxVersionTooLowException>(() => server.StartServerAsync(token));
        await Assert.ThrowsAsync<TmuxVersionTooLowException>(
            () => server.CreateSessionAsync(new NewSessionRequest { Name = "effect" }, token));

        // No server was started for any of them.
        Assert.False(File.Exists(socket));

        // Stopping a server that is not there is still the outcome asked for,
        // and the executable's own diagnostics stay available below the floor.
        Assert.Equal(0, (await server.ExecuteCommandAsync(["kill-server"], token)).ExitCode);
        await server.KillAsync(token);
        TmuxCommandResult banner = await server.ExecuteCommandAsync(["-V"], token);
        Assert.Contains("tmux", Assert.Single(banner.StandardOutputLines), StringComparison.Ordinal);
    }

    [UnixFact]
    public async Task A_newer_daemon_is_usable_through_a_client_that_reports_a_different_version()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string rejected = Rejected();
        string floor = FloorBuild();
        await using var daemon = await TestDaemon.StartAsync(floor, token);

        Server server = await Server.ConnectAsync(daemon.Options(rejected), token);

        TmuxCommandResult listed = await server.ExecuteCommandAsync(
            ["list-sessions", "-F", "#{session_name}"],
            token);
        Assert.Equal([Sentinel], listed.StandardOutputLines);
        Assert.Equal(TmuxVersion.Parse(await daemon.VersionAsync(token)), server.Version);
        Assert.True(await server.IsAliveAsync(token));
    }

    [UnixFact]
    public async Task An_executable_that_reports_its_own_name_is_accepted_for_a_new_daemon()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string floor = FloorBuild();
        using var directory = new ScratchDirectory();
        string renamed = Path.Join(directory.Path, "tmux-3.3a");
        File.Copy(floor, renamed);
        File.SetUnixFileMode(renamed, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        // A process another test starts at the same moment can still hold the
        // copy open for writing, and running it then fails with "Text file busy".
        await WaitUntilExecutableAsync(renamed, token);
        string socket = Path.Join(directory.Path, "s");
        Server server = Server.Open(new ServerConnectionOptions
        {
            TmuxBinaryPath = renamed,
            SocketPath = socket,
            ConfigurationFile = "/dev/null",
        });

        await server.ExecuteCommandAsync(["new-session", "-d", "-s", "cold"], token);

        TmuxCommandResult banner = await server.ExecuteCommandAsync(["-V"], token);
        Assert.StartsWith("tmux-3.3a 3.3a", Assert.Single(banner.StandardOutputLines), StringComparison.Ordinal);
        Assert.Equal(
            ["cold"],
            (await server.ExecuteCommandAsync(["list-sessions", "-F", "#{session_name}"], token))
                .StandardOutputLines);
        await server.KillAsync(token);
    }

    [UnixFact]
    public async Task The_version_check_fires_neither_an_after_hook_nor_the_command_error_hook()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using var daemon = await TestDaemon.StartAsync(Qualified(), token);
        TmuxVersion version = TmuxVersion.Parse(await daemon.VersionAsync(token));

        await daemon.RequireAsync(
            ["set-hook", "-g", "after-display-message", "set-option -g @after-display fired"],
            token);
        bool hasCommandError = (await daemon.RunAsync(
            ["set-hook", "-g", "command-error", "set-option -g @command-error fired"],
            token)).ExitCode == 0;

        // Controls: the hooks are real, and the commands that would fire them
        // are the ones a probe must not be.
        await daemon.RequireAsync(["display-message", "-p", "control"], token);
        Assert.Equal("fired", await daemon.OptionAsync("@after-display", token));
        await daemon.RequireAsync(["set-option", "-gu", "@after-display"], token);
        if (hasCommandError)
        {
            Assert.True(version >= TmuxVersion.Parse("3.5"));
            _ = await daemon.RunAsync(["kill-session", "-t", "=missing"], token);
            Assert.Equal("fired", await daemon.OptionAsync("@command-error", token));
            await daemon.RequireAsync(["set-option", "-gu", "@command-error"], token);
        }
        else
        {
            Assert.True(version < TmuxVersion.Parse("3.5"));
        }

        // The shared control client attaches on its own account; this proves
        // the probe and the fence, which every transport sends.
        Server server = Server.Open(daemon.Options(Qualified()) with { ForceProcessTransport = true });
        await server.ExecuteCommandAsync(["list-sessions"], token);

        Assert.Equal(string.Empty, await daemon.OptionAsync("@after-display", token));
        Assert.Equal(string.Empty, await daemon.OptionAsync("@command-error", token));
    }

    private static async Task WaitUntilExecutableAsync(string path, CancellationToken token)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                using Process? probe = Process.Start(new ProcessStartInfo(path, "-V")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                probe?.WaitForExit();
                return;
            }
            catch (System.ComponentModel.Win32Exception busy) when (busy.NativeErrorCode == 26)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), token);
            }
        }

        throw new TimeoutException($"{path} stayed busy.");
    }

    private static string Qualified()
    {
        string path = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? string.Empty;
        return string.IsNullOrWhiteSpace(path) ? "tmux" : path;
    }

    private static string Rejected() => Fixture("LIBTMUX_REJECTED_TMUX", "a tmux below 3.3a");

    private static string FloorBuild() => Fixture("LIBTMUX_FLOOR_TMUX", "a tmux 3.3a build");

    private static string Fixture(string variable, string description)
    {
        string? path = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        Assert.False(
            Environment.GetEnvironmentVariable("LIBTMUX_REJECTED_REQUIRED") == "1",
            $"{variable} must name {description}.");
        Assert.Skip($"{variable} names no {description}.");
        throw new InvalidOperationException("unreachable");
    }

    private sealed class ScratchDirectory : IDisposable
    {
        internal ScratchDirectory() =>
            Path = Directory.CreateTempSubdirectory("ltadm-").FullName;

        internal string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: cleanup must not fail the test that passed.
            }
        }
    }

    /// <summary>One tmux daemon on a socket of its own, started by a chosen binary.</summary>
    private sealed class TestDaemon : IAsyncDisposable
    {
        private readonly ScratchDirectory _directory = new();
        private string _binary;
        private int _pid;

        private TestDaemon(string binary)
        {
            _binary = binary;
            SocketPath = System.IO.Path.Join(_directory.Path, "s");
        }

        internal string SocketPath { get; }

        internal static async Task<TestDaemon> StartAsync(string binary, CancellationToken cancellationToken)
        {
            var daemon = new TestDaemon(binary);
            try
            {
                await daemon.StartServerAsync(binary, cancellationToken);
                return daemon;
            }
            catch
            {
                await daemon.DisposeAsync();
                throw;
            }
        }

        internal ServerConnectionOptions Options(string client) => new()
        {
            TmuxBinaryPath = client,
            SocketPath = SocketPath,
            ConfigurationFile = "/dev/null",
        };

        internal async Task ReplaceAsync(string binary, CancellationToken cancellationToken)
        {
            // kill-server answers when it lands, not when the daemon is gone,
            // and a session created meanwhile would die with it.
            await RunAsync(["kill-server"], cancellationToken);
            for (int attempt = 0; attempt < 400 && IsAlive(_pid); attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
            }

            Assert.False(IsAlive(_pid), "The replaced daemon did not exit.");
            await StartServerAsync(binary, cancellationToken);
        }

        internal async Task<string> VersionAsync(CancellationToken cancellationToken) =>
            (await RequireAsync(["display-message", "-p", "#{version}"], cancellationToken))
                .StandardOutputLines.Single();

        internal async Task<IReadOnlyList<string>> SessionsAsync(CancellationToken cancellationToken) =>
            (await RequireAsync(["list-sessions", "-F", "#{session_name}"], cancellationToken))
                .StandardOutputLines;

        internal async Task<string> OptionAsync(string name, CancellationToken cancellationToken) =>
            string.Concat((await RequireAsync(["show-options", "-gqv", name], cancellationToken)).StandardOutputLines);

        internal async Task<RawProcessResult> RequireAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            RawProcessResult result = await RunAsync(arguments, cancellationToken);
            Assert.True(
                result.ExitCode == 0,
                $"tmux {string.Join(' ', arguments)} failed: {result.StandardError}");
            return result;
        }

        internal Task<RawProcessResult> RunAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken) =>
            RunWithAsync(_binary, arguments, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            try
            {
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await RunAsync(["kill-server"], budget.Token);
            }
            catch (OperationCanceledException)
            {
                // Best effort: the budget bounds teardown, and the directory
                // is removed whether or not the daemon answered.
            }
            finally
            {
                _directory.Dispose();
            }
        }

        private async Task StartServerAsync(string binary, CancellationToken cancellationToken)
        {
            _binary = binary;
            RawProcessResult started = await RunAsync(
                ["new-session", "-d", "-s", Sentinel, "-x", "80", "-y", "24"],
                cancellationToken);
            Assert.True(started.ExitCode == 0, $"{binary} did not start a server: {started.StandardError}");
            _pid = int.Parse(
                (await RequireAsync(["display-message", "-p", "#{pid}"], cancellationToken))
                    .StandardOutputLines.Single(),
                NumberStyles.None,
                CultureInfo.InvariantCulture);
        }

        private static bool IsAlive(int processId)
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private async Task<RawProcessResult> RunWithAsync(
            string binary,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo(binary)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            foreach (string argument in new[] { "-f", "/dev/null", "-S", SocketPath }.Concat(arguments))
            {
                startInfo.ArgumentList.Add(argument);
            }

            RawTmuxTestContext.ConfigureEnvironment(startInfo);
            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("tmux did not start.");
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return new RawProcessResult(process.ExitCode, await stdout, await stderr);
        }
    }

    private sealed record RawProcessResult(int ExitCode, string StandardOutput, string StandardError)
    {
        internal string[] StandardOutputLines =>
            StandardOutput.Length == 0
                ? []
                : StandardOutput.TrimEnd('\n').Split('\n');
    }
}
