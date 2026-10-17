using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;

namespace LibTmux.IntegrationTests.Versioning;

/// <summary>Proves each optional-feature protection against the tmux the lane runs.</summary>
/// <remarks>
/// Every test names the releases it protects and skips on the others. On an
/// affected release it proves the refusal and that the state it guards is
/// untouched; on a release that is not, it proves the same call is sent.
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed class OptionalTmuxFeatureTests
{
    private static TmuxVersion Version => TmuxFeatureSupport.Version;

    [UnixFact]
    public async Task A_control_client_attaches_only_where_it_is_promised()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);

        if (Version >= TmuxVersion.Parse("3.7"))
        {
            await using IControlModeSession control = await server.EnterControlModeAsync(cancellationToken: token);
            Assert.True(control.IsRunning);
            return;
        }

        TmuxVersionTooLowException refusal = await Assert.ThrowsAsync<TmuxVersionTooLowException>(
            () => server.EnterControlModeAsync(cancellationToken: token));
        Assert.Equal(TmuxVersion.Parse("3.7"), refusal.RequiredVersion);
        Assert.Equal(TmuxDispatchState.NotDispatched, refusal.Dispatch);

        // Nothing attached, and no one-shot command attached one either.
        _ = await server.GetSessionsAsync(token);
        Assert.Empty((await raw.ExecuteAsync(["list-clients"], token)).StandardOutputLines);
    }

    [UnixFact]
    public async Task A_pane_wait_polls_below_the_control_floor_and_attaches_no_client()
    {
        Assert.SkipWhen(Version >= TmuxVersion.Parse("3.7"), "A control client is promised here.");
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);
        Pane pane = (await server.GetPanesAsync(token))[0];
        PaneWaitRequest request = PaneWaitRequest.FromTextPatterns(["never"], simpleMatch: true)
            with
        { Timeout = TimeSpan.FromMilliseconds(300) };

        PaneWaitResult polled = await pane.WaitForTextAsync(request, token);

        Assert.NotEqual(PaneWaitOutcome.Matched, polled.Outcome);
        Assert.True(polled.PollingFallback);
        Assert.Empty((await raw.ExecuteAsync(["list-clients"], token)).StandardOutputLines);
    }

    [UnixFact]
    public async Task A_named_buffer_is_deleted_only_where_the_target_is_found_exactly()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);
        await server.Buffers.SetAsync("kept", "kept-buffer", cancellationToken: token);

        if (Version >= TmuxVersion.Parse("3.4"))
        {
            // A missing target is tmux's own error, and the buffer survives.
            await Assert.ThrowsAsync<TmuxCommandException>(() => server.Buffers.DeleteAsync("missing", token));
            await server.Buffers.DeleteAsync("kept-buffer", token);
            Assert.DoesNotContain(await server.Buffers.GetAllAsync(token), buffer => buffer.Name == "kept-buffer");
            return;
        }

        TmuxVersionTooLowException refusal = await Assert.ThrowsAsync<TmuxVersionTooLowException>(
            () => server.Buffers.DeleteAsync("missing", token));
        Assert.Equal(TmuxVersion.Parse("3.4"), refusal.RequiredVersion);
        await Assert.ThrowsAsync<TmuxVersionTooLowException>(
            () => server.ExecuteCommandAsync(["delete-buffer", "-b", "missing"], token));
        await Assert.ThrowsAsync<TmuxVersionTooLowException>(
            () => server.ExecuteCommandAsync(["set-buffer", "-b", "missing", "-n", "renamed", "x"], token));

        // The top buffer, which an unguarded call would have deleted, is intact.
        Assert.Contains(await server.Buffers.GetAllAsync(token), buffer => buffer.Name == "kept-buffer");
    }

    [UnixFact]
    public async Task A_pane_run_stages_a_buffer_only_where_it_can_clean_up_exactly()
    {
        Assert.SkipWhen(Version >= TmuxVersion.Parse("3.4"), "Exact buffer cleanup is promised here.");
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);
        Pane pane = (await server.GetPanesAsync(token))[0];

        await Assert.ThrowsAsync<TmuxVersionTooLowException>(
            () => pane.RunAsync("true", TimeSpan.FromSeconds(5), cancellationToken: token));

        Assert.Empty((await raw.ExecuteAsync(["list-buffers"], token)).StandardOutputLines);
    }

    [UnixFact]
    public async Task A_foreground_shell_returns_its_output_only_where_tmux_returns_it()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);

        if (Version >= TmuxVersion.Parse("3.5"))
        {
            IReadOnlyList<string>? output = await server.RunShellAsync(new RunShellRequest("echo returned"), token);
            Assert.Equal(["returned"], output);
            return;
        }

        await Assert.ThrowsAsync<TmuxVersionTooLowException>(
            () => server.RunShellAsync(new RunShellRequest("echo refused"), token));
        Assert.Null(await server.RunShellAsync(new RunShellRequest("true") { Background = true }, token));
    }

    [UnixFact]
    public async Task A_layout_tmux_would_crash_on_is_refused_before_it_is_sent()
    {
        Assert.SkipWhen(Version >= TmuxVersion.Parse("3.4"), "tmux validates layouts from 3.4.");
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);
        string before = (await raw.ExecuteAsync(
            ["display-message", "-p", "-t", raw.SessionName, "#{window_layout}"], token)).StandardOutputText;

        await Assert.ThrowsAsync<ArgumentException>(
            () => server.ExecuteCommandAsync(["select-layout", "-t", raw.SessionName, "not-a-layout"], token));
        await Assert.ThrowsAsync<ArgumentException>(
            () => server.ExecuteCommandAsync(["select-layout", "-t", raw.SessionName, "0000,80x24,0,0,0"], token));

        // The daemon is alive and the window keeps its layout.
        Assert.Equal(before, (await raw.ExecuteAsync(
            ["display-message", "-p", "-t", raw.SessionName, "#{window_layout}"], token)).StandardOutputText);
        await server.ExecuteCommandAsync(["select-layout", "-t", raw.SessionName, "tiled"], token);
    }

    [UnixFact]
    public async Task A_literal_percentage_split_is_refused_on_3_4()
    {
        Assert.SkipUnless(Version.Major == 3 && Version.Minor == 4, "Only tmux 3.4 breaks split-window -p.");
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);

        await Assert.ThrowsAsync<TmuxVersionTooLowException>(
            () => server.ExecuteCommandAsync(["split-window", "-d", "-p", "30", "-t", raw.SessionName], token));

        Assert.Single((await raw.ExecuteAsync(["list-panes", "-t", raw.SessionName], token)).StandardOutputLines);
    }

    [UnixFact]
    public async Task A_break_pane_that_crashes_exact_3_7_is_refused_but_a_named_one_is_repaired()
    {
        Assert.SkipUnless(Version == TmuxVersion.Parse("3.7"), "Only exact tmux 3.7 crashes on an unnamed break-pane.");
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);
        Pane pane = (await server.GetPanesAsync(token))[0];
        Pane second = await pane.SplitAsync(cancellationToken: token);

        await Assert.ThrowsAsync<TmuxVersionTooLowException>(
            () => server.ExecuteCommandAsync(["break-pane", "-d", "-s", second.Id.ToString()], token));
        Assert.True(await server.IsAliveAsync(token));

        Window named = await second.BreakAsync("wanted", cancellationToken: token);
        Assert.Equal("wanted", named.Name);
    }

    [UnixFact]
    public async Task A_name_exact_3_7_rejects_is_refused_before_it_is_sent()
    {
        Assert.SkipUnless(Version == TmuxVersion.Parse("3.7"), "Only exact tmux 3.7 rejects these names.");
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);

        await Assert.ThrowsAsync<ArgumentException>(
            () => server.ExecuteCommandAsync(["new-window", "-d", "-n", "a:b", "-t", raw.SessionName], token));
        await Assert.ThrowsAsync<ArgumentException>(
            () => server.ExecuteCommandAsync(["rename-window", "-t", raw.SessionName, "a.b"], token));

        Assert.Single((await raw.ExecuteAsync(["list-windows", "-t", raw.SessionName], token)).StandardOutputLines);
    }

    [UnixFact]
    public async Task Breaking_out_a_windows_only_pane_still_names_the_new_window()
    {
        Assert.SkipWhen(Version >= TmuxVersion.Parse("3.6"), "tmux prints the identifier from 3.6.");
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);
        Pane only = (await server.GetPanesAsync(token)).Single();

        Window broken = await only.BreakAsync("alone", cancellationToken: token);

        Assert.Equal("alone", broken.Name);
    }

    private static Task<Server> ConnectAsync(RawTmuxTestContext raw, CancellationToken token) =>
        Server.ConnectAsync(
            new ServerConnectionOptions
            {
                TmuxBinaryPath = raw.TmuxBinaryPath,
                SocketPath = raw.SocketPath,
                ConfigurationFile = "/dev/null",
            },
            token);
}
