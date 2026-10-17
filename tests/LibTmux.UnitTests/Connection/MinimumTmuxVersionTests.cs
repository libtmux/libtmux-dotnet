using System.Runtime.Versioning;
using System.Text;
using LibTmux.Internal;

namespace LibTmux.UnitTests.Connection;

[UnsupportedOSPlatform("windows")]
public sealed class MinimumTmuxVersionTests
{
    [Theory]
    [InlineData("3.3a")]
    [InlineData("3.3a-openbsd")]
    [InlineData("3.3b")]
    [InlineData("3.4")]
    [InlineData("3.7d")]
    [InlineData("3.8-rc")]
    [InlineData("3.8-rc2")]
    [InlineData("3.8")]
    [InlineData("next-3.9")]
    [InlineData("4.0")]
    public async Task A_daemon_at_or_above_the_minimum_is_supported(string version)
    {
        var daemon = new FakeDaemon(version);

        await daemon.Server.ExecuteCommandAsync(["list-sessions"], TestContext.Current.CancellationToken);

        Assert.Equal(["list-sessions"], daemon.Commands.Single());
    }

    [Theory]
    [InlineData("3.2")]
    [InlineData("3.2a")]
    [InlineData("3.3")]
    [InlineData("3.3.1")]
    [InlineData("3.3-rc")]
    [InlineData("3.3-rc1")]
    [InlineData("3.3-dev")]
    [InlineData("3.3-openbsd")]
    [InlineData("next-3.3")]
    [InlineData("next-3.2")]
    [InlineData("2.9a")]
    public async Task A_daemon_below_the_minimum_is_refused_before_any_command(string version)
    {
        var daemon = new FakeDaemon(version);

        TmuxVersionTooLowException refusal = await Assert.ThrowsAsync<TmuxVersionTooLowException>(
            () => daemon.Server.ExecuteCommandAsync(["new-session", "-d"], TestContext.Current.CancellationToken));

        Assert.Equal(TmuxVersion.Parse("3.3a"), refusal.RequiredVersion);
        Assert.Equal(TmuxVersion.Parse(version), refusal.ActualVersion);
        Assert.Equal(TmuxDispatchState.NotDispatched, refusal.Dispatch);
        Assert.Empty(daemon.Commands);
    }

    [Theory]
    [InlineData("master")]
    [InlineData("openbsd")]
    [InlineData("openbsd-3.8")]
    [InlineData("next-3.3a")]
    [InlineData("3.x")]
    [InlineData("next-")]
    [InlineData("#{version}")]
    [InlineData("3.8 extra")]
    public async Task A_version_nothing_can_rank_is_refused_before_any_command(string version)
    {
        var daemon = new FakeDaemon(version);

        TmuxProtocolException refusal = await Assert.ThrowsAsync<TmuxProtocolException>(
            () => daemon.Server.ExecuteCommandAsync(["new-session", "-d"], TestContext.Current.CancellationToken));

        Assert.Equal(TmuxDispatchState.NotDispatched, refusal.Dispatch);
        Assert.Empty(daemon.Commands);
    }

    [Fact]
    public async Task The_probe_runs_a_command_that_fires_no_hook()
    {
        var daemon = new FakeDaemon("3.8");

        await daemon.Server.ExecuteCommandAsync(["list-sessions"], TestContext.Current.CancellationToken);

        string[] probe = Assert.Single(daemon.Probes);
        Assert.Equal(["run-shell", "-C"], probe[..2]);
        Assert.StartsWith("libtmux-version-", probe[2], StringComparison.Ordinal);
        Assert.EndsWith("#{version},#{pid},#{start_time}", probe[2], StringComparison.Ordinal);
        Assert.DoesNotContain("display-message", daemon.Probes.SelectMany(static each => each));
    }

    [Fact]
    public async Task Every_command_list_opens_with_a_fence_on_the_supported_version()
    {
        var daemon = new FakeDaemon("3.8");

        await daemon.Server.ExecuteCommandAsync(["list-sessions"], TestContext.Current.CancellationToken);
        await daemon.Server.ExecuteCommandAsync(["new-session", "-d"], TestContext.Current.CancellationToken);

        Assert.Equal(2, daemon.Requests.Count);
        foreach (TmuxCommandRequest request in daemon.Requests)
        {
            IReadOnlyList<string> fence = request.Commands[0];
            Assert.Equal(["if-shell", "-F", "#{==:#{version},3.8}", ""], fence.Take(4));
            Assert.StartsWith("libtmux-version-", fence[4], StringComparison.Ordinal);
            Assert.True(request.PreventServerStart);
        }

        // The probe runs once; the fence is what keeps the answer honest.
        Assert.Single(daemon.Probes);
    }

    [Fact]
    public async Task A_fence_is_numbered_afresh_for_every_command_list()
    {
        var daemon = new FakeDaemon("3.8");

        await daemon.Server.ExecuteCommandAsync(["list-sessions"], TestContext.Current.CancellationToken);
        await daemon.Server.ExecuteCommandAsync(["list-sessions"], TestContext.Current.CancellationToken);

        Assert.NotEqual(daemon.Requests[0].Commands[0][4], daemon.Requests[1].Commands[0][4]);
    }

    [Fact]
    public async Task Results_describe_the_commands_asked_for_not_the_fence()
    {
        var daemon = new FakeDaemon("3.8") { Output = "alpha\n" };

        TmuxCommandResult result = await daemon.Server.ExecuteCommandAsync(
            ["list-sessions", "-F", "#{session_name}"],
            TestContext.Current.CancellationToken);

        Assert.Equal(["list-sessions", "-F", "#{session_name}"], result.Arguments);
        Assert.Equal(["alpha"], result.StandardOutputLines);
    }

    [Fact]
    public async Task A_replaced_daemon_below_the_minimum_fails_the_fence_and_is_refused()
    {
        var daemon = new FakeDaemon("3.8");
        await daemon.Server.ExecuteCommandAsync(["list-sessions"], TestContext.Current.CancellationToken);
        daemon.Version = "3.2a";

        TmuxVersionTooLowException refusal = await Assert.ThrowsAsync<TmuxVersionTooLowException>(
            () => daemon.Server.ExecuteCommandAsync(["new-session", "-d"], TestContext.Current.CancellationToken));

        Assert.Equal(TmuxVersion.Parse("3.2a"), refusal.ActualVersion);
        Assert.Equal(TmuxDispatchState.NotDispatched, refusal.Dispatch);
        Assert.Equal(1, daemon.Effects);
    }

    [Fact]
    public async Task A_replacement_below_the_minimum_stays_refused_on_the_next_command()
    {
        var daemon = new FakeDaemon("3.8");
        await daemon.Server.ExecuteCommandAsync(["list-sessions"], TestContext.Current.CancellationToken);
        daemon.Version = "3.3";

        for (int attempt = 0; attempt < 2; attempt++)
        {
            await Assert.ThrowsAsync<TmuxVersionTooLowException>(
                () => daemon.Server.ExecuteCommandAsync(["new-session", "-d"], TestContext.Current.CancellationToken));
        }

        Assert.Equal(1, daemon.Effects);
    }

    [Fact]
    public async Task A_replacement_at_another_supported_version_is_supported_afresh()
    {
        var daemon = new FakeDaemon("3.8");
        await daemon.Server.ExecuteCommandAsync(["list-sessions"], TestContext.Current.CancellationToken);
        daemon.Version = "3.4";

        await daemon.Server.ExecuteCommandAsync(["new-session", "-d"], TestContext.Current.CancellationToken);

        Assert.Equal(2, daemon.Effects);
        Assert.Equal(2, daemon.Probes.Count);
        Assert.Equal("#{==:#{version},3.4}", daemon.Requests[^1].Commands[0][2]);
    }

    [Fact]
    public async Task A_batch_is_fenced_once_and_ignores_nothing_behind_it()
    {
        var daemon = new FakeDaemon("3.8");

        TmuxCommandResult result = await daemon.Server.Chain()
            .Then(TmuxChaining.Command(["rename-session", "-t", "$1", "one"]))
            .Then(TmuxChaining.Command(["rename-session", "-t", "$1", "two"]))
            .ExecuteAsync(TestContext.Current.CancellationToken);

        TmuxCommandRequest request = Assert.Single(daemon.Requests);
        Assert.Equal(3, request.Commands.Count);
        Assert.Equal("if-shell", request.Commands[0][0]);
        Assert.Equal(["rename-session", "-t", "$1", "one"], request.Commands[1]);
        Assert.Equal(["rename-session", "-t", "$1", "two"], request.Commands[2]);
        Assert.Equal(["rename-session", "-t", "$1", "one", ";", "rename-session", "-t", "$1", "two"], result.Arguments);
    }

    [Fact]
    public async Task A_batch_on_a_daemon_below_the_minimum_runs_nothing()
    {
        var daemon = new FakeDaemon("3.2a");

        await Assert.ThrowsAsync<TmuxVersionTooLowException>(
            () => daemon.Server.Chain()
                .Then(TmuxChaining.Command(["rename-session", "-t", "$1", "one"]))
                .Then(TmuxChaining.Command(["rename-session", "-t", "$1", "two"]))
                .ExecuteAsync(TestContext.Current.CancellationToken));

        Assert.Empty(daemon.Commands);
    }

    [Fact]
    public async Task An_absent_endpoint_is_supported_by_the_executable_that_would_start_a_daemon()
    {
        var daemon = new FakeDaemon("3.8") { Absent = true, Executable = "tmux 3.8\n" };

        await daemon.Server.ExecuteCommandAsync(["new-session", "-d"], TestContext.Current.CancellationToken);

        TmuxCommandRequest request = Assert.Single(daemon.Requests);
        Assert.Equal("#{==:#{version},3.8}", request.Commands[0][2]);
        Assert.False(request.PreventServerStart);
    }

    [Theory]
    [InlineData("tmux 3.2a\n", "3.2a")]
    [InlineData("tmux 3.3\n", "3.3")]
    public async Task An_absent_endpoint_is_refused_when_the_executable_is_below_the_minimum(string banner, string version)
    {
        var daemon = new FakeDaemon("3.8") { Absent = true, Executable = banner };

        TmuxVersionTooLowException refusal = await Assert.ThrowsAsync<TmuxVersionTooLowException>(
            () => daemon.Server.ExecuteCommandAsync(["new-session", "-d"], TestContext.Current.CancellationToken));

        Assert.Equal(TmuxVersion.Parse(version), refusal.ActualVersion);
        Assert.Empty(daemon.Commands);
    }

    [Fact]
    public async Task A_daemon_newer_than_its_client_is_supported_on_the_daemons_version()
    {
        var daemon = new FakeDaemon("3.8") { Executable = "tmux 3.2a\n" };

        await daemon.Server.ExecuteCommandAsync(["list-sessions"], TestContext.Current.CancellationToken);

        Assert.Equal("#{==:#{version},3.8}", daemon.Requests.Single().Commands[0][2]);
    }

    [Fact]
    public async Task A_daemon_below_the_minimum_is_refused_whatever_its_client_reports()
    {
        var daemon = new FakeDaemon("3.2a") { Executable = "tmux 3.8\n" };

        await Assert.ThrowsAsync<TmuxVersionTooLowException>(
            () => daemon.Server.ExecuteCommandAsync(["list-sessions"], TestContext.Current.CancellationToken));

        Assert.Empty(daemon.Commands);
    }

    [Fact]
    public async Task A_daemon_that_goes_away_after_the_version_check_is_checked_again_without_a_server_start()
    {
        var daemon = new FakeDaemon("3.8");
        await daemon.Server.ExecuteCommandAsync(["list-sessions"], TestContext.Current.CancellationToken);
        daemon.Absent = true;
        daemon.Executable = "tmux 3.8\n";

        await daemon.Server.ExecuteCommandAsync(["new-session", "-d"], TestContext.Current.CancellationToken);

        Assert.True(daemon.Requests[1].PreventServerStart);
        Assert.False(daemon.Requests[2].PreventServerStart);
        Assert.Equal(1, daemon.Effects - 1);
    }

    [Fact]
    public async Task Stopping_an_absent_server_is_a_no_op_that_asks_nothing_of_the_executable()
    {
        var daemon = new FakeDaemon("3.8") { Absent = true, Executable = "tmux 3.2a\n" };

        TmuxCommandResult result = await daemon.Server.ExecuteCommandAsync(
            ["kill-server"],
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(daemon.Commands);
    }

    [Fact]
    public async Task Stopping_a_server_below_the_minimum_is_refused()
    {
        var daemon = new FakeDaemon("3.2a");

        await Assert.ThrowsAsync<TmuxVersionTooLowException>(
            () => daemon.Server.ExecuteCommandAsync(["kill-server"], TestContext.Current.CancellationToken));

        Assert.Empty(daemon.Commands);
    }

    [Theory]
    [InlineData("-V")]
    [InlineData("-h")]
    public async Task Executable_diagnostics_need_no_daemon_and_accept_nothing(string flag)
    {
        var daemon = new FakeDaemon("3.2a");

        TmuxCommandResult result = await daemon.Server.ExecuteCommandAsync(
            [flag],
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(daemon.Probes);
        await Assert.ThrowsAsync<TmuxVersionTooLowException>(
            () => daemon.Server.ExecuteCommandAsync(["list-sessions"], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_liveness_check_does_not_hide_a_refused_daemon()
    {
        var daemon = new FakeDaemon("3.2a");

        await Assert.ThrowsAsync<TmuxVersionTooLowException>(
            () => daemon.Server.IsAliveAsync(TestContext.Current.CancellationToken));
    }

    // tmux joins the -L name onto its socket directory as it is given, so a
    // rooted name stays beneath that directory instead of replacing it.
    [Fact]
    public void A_named_socket_that_is_rooted_stays_beneath_the_socket_directory()
    {
        string? path = UnixFileIdentity.NamedSocketPath("/rooted/name");

        Assert.NotNull(path);
        Assert.NotEqual("/rooted/name", path);
        Assert.Contains("/tmux-", path, StringComparison.Ordinal);
        Assert.EndsWith("/rooted/name", path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_liveness_check_still_reports_an_absent_daemon_as_not_alive()
    {
        var daemon = new FakeDaemon("3.8") { Absent = true, Executable = "tmux 3.8\n" };

        Assert.False(await daemon.Server.IsAliveAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Connecting_reads_the_generation_with_the_probe_not_display_message()
    {
        var daemon = new FakeDaemon("3.8");

        Server connected = await daemon.Server.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new ServerGeneration(91, 901), connected.Generation);
        Assert.Equal(TmuxVersion.Parse("3.8"), connected.Version);
        Assert.Empty(daemon.Requests);
        Assert.DoesNotContain("display-message", daemon.Probes.SelectMany(static each => each));
    }

    [Fact]
    public async Task A_probe_report_over_more_than_one_line_is_refused()
    {
        var daemon = new FakeDaemon("3.8") { ExtraProbeLine = "extra" };

        await Assert.ThrowsAsync<TmuxCommandException>(
            () => daemon.Server.ExecuteCommandAsync(["new-session", "-d"], TestContext.Current.CancellationToken));

        Assert.Empty(daemon.Commands);
    }

    [Fact]
    public async Task An_unreadable_probe_is_an_error_not_an_absent_endpoint()
    {
        var daemon = new FakeDaemon("3.8") { ProbeError = "protocol version mismatch\n" };

        TmuxCommandException failure = await Assert.ThrowsAsync<TmuxCommandException>(
            () => daemon.Server.ExecuteCommandAsync(["new-session", "-d"], TestContext.Current.CancellationToken));

        Assert.Contains("protocol version mismatch", failure.Message, StringComparison.Ordinal);
        Assert.Empty(daemon.Commands);
    }

    [Theory]
    [InlineData("tmux 3.3a\n", "tmux", "3.3a")]
    [InlineData("tmux-3.3a 3.3a\n", "tmux-3.3a", "3.3a")]
    [InlineData("tmux-3.3a 3.3a\n", "/opt/tmux-3.3a", "3.3a")]
    [InlineData("my-tmux next-3.9\n", "/usr/bin/my-tmux", "next-3.9")]
    public async Task The_executable_may_report_its_own_name_in_place_of_tmux(
        string banner,
        string binary,
        string version)
    {
        var daemon = new FakeDaemon(version, binary) { Absent = true, Executable = banner };

        await daemon.Server.ExecuteCommandAsync(["new-session", "-d"], TestContext.Current.CancellationToken);

        Assert.Equal($"#{{==:#{{version}},{version}}}", daemon.Requests.Single().Commands[0][2]);
    }

    [Fact]
    public async Task An_executable_that_reports_another_name_is_not_a_tmux()
    {
        var daemon = new FakeDaemon("3.8") { Absent = true, Executable = "other 3.8\n" };

        await Assert.ThrowsAsync<TmuxCommandException>(
            () => daemon.Server.ExecuteCommandAsync(["new-session", "-d"], TestContext.Current.CancellationToken));

        Assert.Empty(daemon.Commands);
    }

    private sealed class FakeDaemon
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(false);

        internal FakeDaemon(string version, string binary = "tmux")
        {
            Version = version;
            var connection = new TmuxConnection(
                new ServerConnectionOptions
                {
                    SocketName = "version-unit",
                    TmuxBinaryPath = binary,
                },
                (request, _) => Task.FromResult(Answer(request)),
                markerFactory: null,
                daemonVersionCheck: true);
            Server = new Server(connection, generation: null, rawVersion: null);
        }

        internal Server Server { get; }

        internal string Version { get; set; }

        internal bool Absent { get; set; }

        internal string Executable { get; set; } = "tmux 3.8\n";

        internal string Output { get; set; } = string.Empty;

        internal string? ProbeError { get; set; }

        internal string? ExtraProbeLine { get; set; }

        internal List<TmuxCommandRequest> Requests { get; } = [];

        internal List<string[]> Probes { get; } = [];

        internal int Effects { get; private set; }

        /// <summary>Gets the commands the daemon would have run, fence excluded.</summary>
        internal List<string[]> Commands { get; } = [];

        private TmuxCommandResult Answer(TmuxCommandRequest request)
        {
            string[] arguments = [.. request.LogicalArguments];
            if (arguments is ["-V"] or ["-h"])
            {
                return Result(request, 0, Executable, string.Empty);
            }

            if (arguments is ["run-shell", "-C", { } expression])
            {
                Probes.Add(arguments);
                if (ProbeError is not null)
                {
                    return Result(request, 1, string.Empty, ProbeError);
                }

                if (Absent)
                {
                    return Result(request, 1, string.Empty, "no server running on /tmp/version-unit\n");
                }

                string marker = expression[..^"#{version},#{pid},#{start_time}".Length];
                return Result(
                    request,
                    1,
                    string.Empty,
                    $"unknown command: {marker}{Version},91,901\n" + (ExtraProbeLine is null ? string.Empty : ExtraProbeLine + "\n"));
            }

            Requests.Add(request);
            if (Absent && (request.PreventServerStart
                || !request.Commands.Skip(1).Any(static command => command[0] is "new-session" or "start-server")))
            {
                return Result(request, 1, string.Empty, "no server running on /tmp/version-unit\n");
            }

            IReadOnlyList<string> fence = request.Commands[0];
            string expected = $"#{{==:#{{version}},{Version}}}";
            if (fence[0] == "if-shell" && fence[2] != expected)
            {
                return Result(request, 1, string.Empty, $"unknown command: {fence[4]}\n");
            }

            foreach (IReadOnlyList<string> command in request.Commands.Skip(1))
            {
                Commands.Add([.. command]);
            }

            Effects++;
            return Result(request, 0, Output, string.Empty);
        }

        private static TmuxCommandResult Result(
            TmuxCommandRequest request,
            int exitCode,
            string output,
            string error)
        {
            byte[] stdout = Utf8.GetBytes(output);
            byte[] stderr = Utf8.GetBytes(error);
            return new TmuxCommandResult(
                request.LogicalArguments,
                exitCode,
                stdout,
                stderr,
                Utf8BackslashDecoder.ProjectOutputLines(stdout),
                Utf8BackslashDecoder.ProjectErrorLines(stderr));
        }
    }
}
