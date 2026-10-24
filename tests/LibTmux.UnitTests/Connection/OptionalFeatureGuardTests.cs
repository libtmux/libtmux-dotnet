using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux.UnitTests.Connection;

[UnsupportedOSPlatform("windows")]
public sealed class OptionalFeatureGuardTests
{
    private static readonly TmuxVersion V33a = TmuxVersion.Parse("3.3a");

    [Theory]
    [InlineData("3.7", new[] { "break-pane", "-d", "-s", "%1" })]
    [InlineData("3.7", new[] { "breakp", "-P", "-F", "#{window_id}", "-s", "%1" })]
    [InlineData("3.7", new[] { "break-p", "-s", "%1" })]
    public void An_unnamed_break_pane_is_refused_on_exact_3_7(string version, string[] command)
    {
        TmuxVersionTooLowException refusal = Assert.Throws<TmuxVersionTooLowException>(
            () => TmuxFeatureGuard.Check(TmuxVersion.Parse(version), [command]));

        Assert.Equal(TmuxVersion.Parse("3.7a"), refusal.RequiredVersion);
        Assert.Equal(TmuxDispatchState.NotDispatched, refusal.Dispatch);
    }

    [Theory]
    [InlineData("3.7", new[] { "break-pane", "-n", "wanted", "-s", "%1" })]
    [InlineData("3.7", new[] { "break-pane", "-dn", "wanted", "-s", "%1" })]
    [InlineData("3.7", new[] { "break-pane", "-nwanted", "-s", "%1" })]
    [InlineData("3.7a", new[] { "break-pane", "-s", "%1" })]
    [InlineData("3.6b", new[] { "break-pane", "-s", "%1" })]
    [InlineData("3.3a", new[] { "break-pane", "-s", "%1" })]
    [InlineData("next-3.7", new[] { "break-pane", "-s", "%1" })]
    public void A_break_pane_that_cannot_crash_is_sent(string version, string[] command) =>
        TmuxFeatureGuard.Check(TmuxVersion.Parse(version), [command]);

    [Theory]
    [InlineData("3.3a", new[] { "run-shell", "echo hi" })]
    [InlineData("3.4", new[] { "run", "echo hi" })]
    [InlineData("3.4", new[] { "run-shell", "-d", "1", "echo hi" })]
    [InlineData("3.3a", new[] { "run-shell", "--", "echo hi" })]
    public void A_foreground_run_shell_naming_no_pane_is_refused_below_3_5(string version, string[] command)
    {
        TmuxVersionTooLowException refusal = Assert.Throws<TmuxVersionTooLowException>(
            () => TmuxFeatureGuard.Check(TmuxVersion.Parse(version), [command]));

        Assert.Equal(TmuxVersion.Parse("3.5"), refusal.RequiredVersion);
        Assert.Equal(TmuxDispatchState.NotDispatched, refusal.Dispatch);
    }

    [Theory]
    [InlineData("3.3a", new[] { "run-shell", "-b", "echo hi" })]
    [InlineData("3.3a", new[] { "run-shell", "-C", "display-message hi" })]
    [InlineData("3.4", new[] { "run-shell", "-t", "%1", "echo hi" })]
    [InlineData("3.4", new[] { "run-shell", "-bd", "1", "echo hi" })]
    [InlineData("3.5", new[] { "run-shell", "echo hi" })]
    [InlineData("3.7d", new[] { "run-shell", "echo hi" })]
    public void A_run_shell_that_returns_its_output_or_shows_none_is_sent(string version, string[] command) =>
        TmuxFeatureGuard.Check(TmuxVersion.Parse(version), [command]);

    [Theory]
    [InlineData("3.3a", new[] { "delete-buffer", "-b", "named" })]
    [InlineData("3.3a", new[] { "deleteb", "-b", "named" })]
    [InlineData("3.3a", new[] { "delete-b", "-bnamed" })]
    [InlineData("3.3a", new[] { "set-buffer", "-b", "old", "-n", "new" })]
    [InlineData("3.3a", new[] { "setb", "-b", "old", "-nnew" })]
    public void Naming_a_buffer_to_delete_or_rename_is_refused_below_3_4(string version, string[] command)
    {
        TmuxVersionTooLowException refusal = Assert.Throws<TmuxVersionTooLowException>(
            () => TmuxFeatureGuard.Check(TmuxVersion.Parse(version), [command]));

        Assert.Equal(TmuxVersion.Parse("3.4"), refusal.RequiredVersion);
        Assert.Equal(TmuxDispatchState.NotDispatched, refusal.Dispatch);
    }

    [Theory]
    [InlineData("3.4", new[] { "delete-buffer", "-b", "named" })]
    [InlineData("3.8", new[] { "set-buffer", "-b", "old", "-n", "new" })]
    [InlineData("3.3a", new[] { "delete-buffer" })]
    [InlineData("3.3a", new[] { "set-buffer", "-b", "named", "data" })]
    [InlineData("3.3a", new[] { "paste-buffer", "-d", "-b", "named", "-t", "%1" })]
    [InlineData("3.3a", new[] { "show-buffer", "-b", "named" })]
    public void Buffer_commands_that_find_their_target_exactly_are_sent(string version, string[] command) =>
        TmuxFeatureGuard.Check(TmuxVersion.Parse(version), [command]);

    [Theory]
    [InlineData("3.4")]
    public void A_literal_percentage_split_is_refused_on_3_4(string version)
    {
        Assert.Throws<TmuxVersionTooLowException>(() => TmuxFeatureGuard.Check(
            TmuxVersion.Parse(version),
            [["split-window", "-p", "30", "-t", "%1"]]));
        Assert.Throws<TmuxVersionTooLowException>(() => TmuxFeatureGuard.Check(
            TmuxVersion.Parse(version),
            [["splitw", "-hp30"]]));
    }

    [Theory]
    [InlineData("3.3a", new[] { "split-window", "-p", "30" })]
    [InlineData("3.5", new[] { "split-window", "-p", "30" })]
    [InlineData("3.4", new[] { "split-window", "-l", "30%" })]
    [InlineData("3.4", new[] { "split-window", "-c", "-p", "-t", "%1" })]
    public void Splits_that_avoid_the_defect_are_sent(string version, string[] command) =>
        TmuxFeatureGuard.Check(TmuxVersion.Parse(version), [command]);

    [Theory]
    [InlineData("bogus")]
    [InlineData("main")]
    [InlineData("0000,80x24,0,0,1")]
    [InlineData("{not-a-layout}x")]
    public void An_invalid_layout_is_refused_before_3_4(string layout)
    {
        if (layout.StartsWith('{'))
        {
            TmuxFeatureGuard.Check(V33a, [["select-layout", "-t", "@1", layout]]);
            return;
        }

        Assert.Throws<ArgumentException>(
            () => TmuxFeatureGuard.Check(V33a, [["select-layout", "-t", "@1", layout]]));
        Assert.Throws<ArgumentException>(
            () => TmuxFeatureGuard.Check(V33a, [["selectl", layout]]));
    }

    [Theory]
    [InlineData("tiled")]
    [InlineData("til")]
    [InlineData("even-h")]
    [InlineData("main-v")]
    public void A_preset_or_its_unique_prefix_is_sent_before_3_4(string layout) =>
        TmuxFeatureGuard.Check(V33a, [["select-layout", "-t", "@1", layout]]);

    [Fact]
    public void A_layout_that_cycles_or_has_none_is_sent_before_3_4()
    {
        TmuxFeatureGuard.Check(V33a, [["select-layout", "-t", "@1"]]);
        TmuxFeatureGuard.Check(V33a, [["select-layout", "-n", "-t", "@1"]]);
        TmuxFeatureGuard.Check(V33a, [["select-layout", "-p"]]);
        TmuxFeatureGuard.Check(V33a, [["select-layout", "-E", "-t", "@1"]]);
    }

    [Fact]
    public void Any_layout_is_sent_from_3_4() =>
        TmuxFeatureGuard.Check(TmuxVersion.Parse("3.4"), [["select-layout", "-t", "@1", "bogus"]]);

    [Theory]
    [InlineData(new object[] { new[] { "new-window", "-n", "a:b" } })]
    [InlineData(new object[] { new[] { "neww", "-t", "$1:", "-na.b" } })]
    [InlineData(new object[] { new[] { "new-session", "-d", "-s", "x.y" } })]
    [InlineData(new object[] { new[] { "new-session", "-d", "-n", "x:y", "-s", "ok" } })]
    [InlineData(new object[] { new[] { "rename-window", "-t", "@1", "a.b" } })]
    [InlineData(new object[] { new[] { "rename-session", "-t", "$1", "a:b" } })]
    [InlineData(new object[] { new[] { "break-pane", "-n", "a:b", "-s", "%1" } })]
    public void A_name_with_a_colon_or_dot_is_refused_on_exact_3_7(string[] command) =>
        Assert.Throws<ArgumentException>(
            () => TmuxFeatureGuard.Check(TmuxVersion.Parse("3.7"), [command]));

    [Theory]
    [InlineData("3.7a")]
    [InlineData("3.6b")]
    [InlineData("3.3a")]
    [InlineData("3.8")]
    public void The_same_name_is_sent_on_other_releases(string version)
    {
        TmuxFeatureGuard.Check(TmuxVersion.Parse(version), [["new-window", "-n", "a:b"]]);
        TmuxFeatureGuard.Check(TmuxVersion.Parse(version), [["rename-window", "-t", "@1", "a.b"]]);
    }

    [Fact]
    public void A_name_that_is_a_target_is_not_a_window_name_on_3_7()
    {
        TmuxFeatureGuard.Check(TmuxVersion.Parse("3.7"), [["new-window", "-t", "$1:2", "-n", "plain"]]);
        TmuxFeatureGuard.Check(TmuxVersion.Parse("3.7"), [["rename-window", "-t", "@1.0", "plain"]]);
    }

    [Fact]
    public async Task A_raw_command_is_refused_before_the_daemon_runs_anything()
    {
        var daemon = new MinimumTmuxVersionTests.FakeDaemon("3.3a");

        await Assert.ThrowsAsync<TmuxVersionTooLowException>(
            () => daemon.Server.ExecuteCommandAsync(
                ["delete-buffer", "-b", "named"],
                TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(
            () => daemon.Server.ExecuteCommandAsync(
                ["select-layout", "bogus"],
                TestContext.Current.CancellationToken));

        Assert.Empty(daemon.Commands);
        await daemon.Server.ExecuteCommandAsync(["delete-buffer"], TestContext.Current.CancellationToken);
        Assert.Single(daemon.Commands);
    }

    [Fact]
    public void A_nested_command_is_not_read_so_the_documented_limit_holds()
    {
        TmuxVersion exact37 = TmuxVersion.Parse("3.7");

        Assert.Throws<TmuxVersionTooLowException>(
            () => TmuxFeatureGuard.Check(exact37, [["break-pane", "-d"]]));
        TmuxFeatureGuard.Check(exact37, [["if-shell", "-F", "1", "break-pane -d"]]);
    }

    [Fact]
    public async Task A_batch_is_refused_whole_when_one_command_is_unsafe()
    {
        var daemon = new MinimumTmuxVersionTests.FakeDaemon("3.7");

        await Assert.ThrowsAsync<TmuxVersionTooLowException>(
            () => daemon.Server.Chain()
                .Then(TmuxChaining.Command(["rename-session", "-t", "$1", "fine"]))
                .Then(TmuxChaining.Command(["break-pane", "-s", "%1"]))
                .ExecuteAsync(TestContext.Current.CancellationToken));

        Assert.Empty(daemon.Commands);
    }

    [Fact]
    public async Task A_control_client_is_refused_below_3_7_before_it_attaches()
    {
        var daemon = new MinimumTmuxVersionTests.FakeDaemon("3.6b");
        Server connected = await daemon.Server.ConnectAsync(TestContext.Current.CancellationToken);
        int before = daemon.Requests.Count;

        TmuxVersionTooLowException refusal = await Assert.ThrowsAsync<TmuxVersionTooLowException>(
            () => connected.EnterControlModeAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(TmuxVersion.Parse("3.7"), refusal.RequiredVersion);
        Assert.Equal(TmuxVersion.Parse("3.6b"), refusal.ActualVersion);
        Assert.Equal(TmuxDispatchState.NotDispatched, refusal.Dispatch);
        Assert.Equal(before, daemon.Requests.Count);
    }

    [Theory]
    [InlineData("3.3a")]
    [InlineData("3.4")]
    public async Task Captured_shell_output_is_refused_where_tmux_shows_it_in_a_pane(string version)
    {
        var daemon = new MinimumTmuxVersionTests.FakeDaemon(version);
        Server connected = await daemon.Server.ConnectAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<TmuxVersionTooLowException>(
            () => connected.RunShellAsync(
                new RunShellRequest("echo hi"),
                TestContext.Current.CancellationToken));

        Assert.DoesNotContain(daemon.Commands, command => command[0] == "run-shell");
        _ = await connected.RunShellAsync(
            new RunShellRequest("echo hi") { Background = true },
            TestContext.Current.CancellationToken);
        _ = await connected.RunShellAsync(
            new RunShellRequest("echo hi") { TargetPane = "%1" },
            TestContext.Current.CancellationToken);
        Assert.Equal(2, daemon.Commands.Count(command => command[0] == "run-shell"));
    }

    [Fact]
    public async Task Captured_shell_output_is_sent_from_3_5()
    {
        var daemon = new MinimumTmuxVersionTests.FakeDaemon("3.5") { Output = "hi\n" };
        Server connected = await daemon.Server.ConnectAsync(TestContext.Current.CancellationToken);

        IReadOnlyList<string>? output = await connected.RunShellAsync(
            new RunShellRequest("echo hi"),
            TestContext.Current.CancellationToken);

        Assert.Equal(["hi"], output);
    }

    [Fact]
    public async Task Staging_a_named_buffer_is_refused_before_3_4()
    {
        var daemon = new MinimumTmuxVersionTests.FakeDaemon("3.3a");
        Server connected = await daemon.Server.ConnectAsync(TestContext.Current.CancellationToken);

        TmuxVersionTooLowException refusal = await Assert.ThrowsAsync<TmuxVersionTooLowException>(
            () => connected.RequireBufferStagingAsync("paste_text", TestContext.Current.CancellationToken));

        Assert.Equal(TmuxVersion.Parse("3.4"), refusal.RequiredVersion);
        await new MinimumTmuxVersionTests.FakeDaemon("3.4").Server
            .ConnectAsync(TestContext.Current.CancellationToken)
            .ContinueWith(
                static server => server.Result.RequireBufferStagingAsync(
                    "paste_text",
                    CancellationToken.None),
                TaskScheduler.Default)
            .Unwrap();
    }

    // One refused form per guarded command, on a release that mishandles it;
    // the arguments are joined with a bar.
    public static TheoryData<string, string, string> RefusedForms => new()
    {
        { "break-pane", "3.7", "-s|%1" },
        { "delete-buffer", "3.3a", "-b|victim" },
        { "set-buffer", "3.3a", "-b|victim|-n|renamed|x" },
        { "run-shell", "3.3a", "echo hi" },
        { "split-window", "3.4", "-p|30" },
        { "select-layout", "3.3a", "garbage" },
        { "new-window", "3.7", "-n|a:b" },
        { "new-session", "3.7", "-s|a:b" },
        { "rename-window", "3.7", "a:b" },
        { "rename-session", "3.7", "a:b" },
        { "refresh-client", "3.3a", "-l" },
    };

    [Theory]
    [MemberData(nameof(RefusedForms))]
    public void Every_word_tmux_resolves_to_a_guarded_command_is_refused(
        string command,
        string release,
        string joined)
    {
        string[] arguments = joined.Split('|');
        TmuxVersion version = TmuxVersion.Parse(release);
        string[] words = [.. Enumerable.Range(1, command.Length)
            .Select(length => command[..length])
            .Append(TmuxCommandNames.Listed(version).Single(entry => entry.Name == command).Alias ?? command)
            .Where(word => TmuxCommandNames.Resolve(version, word) == command)];

        Assert.Contains(command, words);
        Assert.True(words.Length > 1, "an alias or a shorter prefix resolves too");
        Assert.All(
            words,
            word => Assert.NotNull(Record.Exception(
                () => TmuxFeatureGuard.Check(version, [[word, .. arguments]]))));
    }

    [Theory]
    [MemberData(nameof(RefusedForms))]
    public void A_refused_command_is_refused_inside_a_semicolon_list(
        string command,
        string release,
        string joined)
    {
        string[] arguments = joined.Split('|');
        TmuxVersion version = TmuxVersion.Parse(release);

        Assert.NotNull(Record.Exception(
            () => TmuxFeatureGuard.Check(version, [["list-sessions", ";", command, .. arguments]])));
        Assert.NotNull(Record.Exception(
            () => TmuxFeatureGuard.Check(version, [["list-sessions;", command, .. arguments]])));
        Assert.NotNull(Record.Exception(
            () => TmuxFeatureGuard.Check(version, [["list-sessions"], [command, .. arguments]])));
    }

    [Fact]
    public void A_word_that_is_ambiguous_or_unknown_runs_nothing_and_is_not_refused()
    {
        TmuxFeatureGuard.Check(V33a, [["r", "-b", "echo hi"]]);
        TmuxFeatureGuard.Check(V33a, [["s", "-b", "x"]]);
        TmuxFeatureGuard.Check(V33a, [["not-a-command", "-b", "x"]]);
    }

    [Theory]
    [InlineData("3.3a", new[] { "refresh-client", "-l" })]
    [InlineData("3.3a", new[] { "refresh-client", "-t", "/dev/pts/1", "-l" })]
    [InlineData("3.3a", new[] { "refresh-c", "-Sl" })]
    [InlineData("3.4", new[] { "refresh-client", "-l" })]
    [InlineData("3.5", new[] { "refresh-client", "-t", "/dev/pts/1", "-l" })]
    [InlineData("3.5a", new[] { "refresh-client", "-l" })]
    [InlineData("3.6", new[] { "refresh-client", "-l" })]
    public void A_clipboard_query_is_refused_before_3_6a(string version, string[] command)
    {
        TmuxVersionTooLowException refusal = Assert.Throws<TmuxVersionTooLowException>(
            () => TmuxFeatureGuard.Check(TmuxVersion.Parse(version), [command]));

        Assert.Equal(TmuxVersion.Parse("3.6a"), refusal.RequiredVersion);
        Assert.Equal(TmuxDispatchState.NotDispatched, refusal.Dispatch);
    }

    [Theory]
    [InlineData("3.6a", new[] { "refresh-client", "-l" })]
    [InlineData("3.8", new[] { "refresh-client", "-t", "c", "-l" })]
    [InlineData("3.4", new[] { "refresh-client", "-S" })]
    [InlineData("3.3a", new[] { "refresh-client", "-S" })]
    [InlineData("3.3a", new[] { "refresh-client", "-t", "-l" })]
    [InlineData("3.3a", new[] { "refresh-client", "-C", "80x24" })]
    public void A_refresh_without_a_clipboard_query_is_sent(string version, string[] command) =>
        TmuxFeatureGuard.Check(TmuxVersion.Parse(version), [command]);
}
