using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using LibTmux.Internal;
using LibTmux.UnitTests.Connection;

namespace LibTmux.UnitTests.Entities;

[UnsupportedOSPlatform("windows")]
public sealed class PaneRunValidationTests
{
    [Fact]
    public void Run_markers_use_a_full_guid_and_split_only_the_printed_marker()
    {
        PaneRunner.RunToken token = PaneRunner.RunToken.Create();

        Assert.Matches("^[0-9a-f]{32}$", token.Id);
        Assert.Equal($"lt_b_{token.Id}", token.BeginHead + token.MarkerTail);
        Assert.Equal($"lt_e_{token.Id}", token.EndHead + token.MarkerTail);
        Assert.Equal($"lt_r_{token.Id}", token.Channel);
        Assert.Equal($"@lt_s_{token.Id}", token.StatusOption);
    }

    [Fact]
    public void Nul_in_shell_command_is_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new PaneRunRequest("printf before\0after").Validate());
    }

    [Fact]
    public async Task Canceled_run_does_not_dispatch_a_command()
    {
        int dispatched = 0;
        Pane pane = CreatePane(() => Interlocked.Increment(ref dispatched));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pane.RunAsync(
                new PaneRunRequest("printf 'never executed\\n'")
                {
                    Timeout = TimeSpan.FromSeconds(1),
                }, cancellation.Token));

        Assert.Equal(0, Volatile.Read(ref dispatched));
    }

    [Fact]
    public async Task Oversized_timeout_is_rejected_before_dispatch()
    {
        int dispatched = 0;
        Pane pane = CreatePane(() => Interlocked.Increment(ref dispatched));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            pane.RunAsync(
                new PaneRunRequest("printf never") { Timeout = TimeSpan.MaxValue },
                TestContext.Current.CancellationToken));

        Assert.Equal(0, Volatile.Read(ref dispatched));
    }

    [Fact]
    public async Task Invalid_output_budget_is_rejected_before_dispatch()
    {
        int dispatched = 0;
        Pane pane = CreatePane(() => Interlocked.Increment(ref dispatched));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            pane.RunAsync(
                new PaneRunRequest("printf never") { MaxOutputBytes = 0 },
                TestContext.Current.CancellationToken));

        Assert.Equal(0, Volatile.Read(ref dispatched));
    }

    [Fact]
    public async Task Blank_pre_exec_tmux_pane_re_reads_until_posix_shell_starts()
    {
        var fixture = new PreExecRunFixture(
            [
                new PreExecPaneRow("tmux", "0", "0", "0"),
                new PreExecPaneRow("sh", "0", "0", "0"),
                new PreExecPaneRow("sh", "2", "0", "0"),
            ],
            new ServerGeneration(92, 902));

        PaneRunResult result = await fixture.Pane.RunAsync(
            "printf 'ready\\n'",
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitStatus);
        Assert.False(result.TimedOut);
        Assert.Equal(3, fixture.ListPanesCount);
        Assert.Equal(1, fixture.PasteCount);
    }

    [Theory]
    [InlineData("locale")]
    [InlineData("path_helper")]
    [InlineData("python3")]
    public async Task Blank_login_startup_helper_is_re_read_before_dispatch(string helper)
    {
        var fixture = new PreExecRunFixture(
            [
                new PreExecPaneRow(helper, "0", "0", "0"),
                new PreExecPaneRow("sh", "0", "0", "0"),
                new PreExecPaneRow("sh", "2", "0", "0"),
            ],
            new ServerGeneration(96, 906));

        PaneRunResult result = await fixture.Pane.RunAsync(
            "printf 'ready\\n'",
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitStatus);
        Assert.Equal(1, fixture.PasteCount);
    }

    [Theory]
    [InlineData("sh")]
    [InlineData("/bin/sh")]
    [InlineData("/usr/bin/bash")]
    public async Task A_bare_shell_start_command_can_settle_before_run(string start)
    {
        var fixture = new PreExecRunFixture(
            [
                new PreExecPaneRow("tmux", "0", "0", "0", start),
                new PreExecPaneRow("sh", "0", "0", "0", start),
            ],
            new ServerGeneration(102, 912));

        PaneRunResult result = await fixture.Pane.RunAsync(
            "printf 'ready\\n'", TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitStatus);
        Assert.Equal(1, fixture.PasteCount);
    }

    [Fact]
    public async Task A_startup_helper_that_never_becomes_a_shell_is_not_dispatched_to()
    {
        var fixture = new PreExecRunFixture(
            [new PreExecPaneRow("locale", "0", "0", "0")],
            new ServerGeneration(99, 909));

        TmuxPaneException refusal = await Assert.ThrowsAsync<TmuxPaneException>(() =>
            fixture.Pane.RunAsync(
                "printf 'never\\n'", TimeSpan.FromMilliseconds(50),
                TestContext.Current.CancellationToken));

        Assert.Equal(TmuxDispatchState.NotDispatched, refusal.Dispatch);
        Assert.True(fixture.ListPanesCount >= 2);
        Assert.Equal(0, fixture.PasteCount);
    }

    [Fact]
    public async Task Cancellation_during_a_startup_helper_wait_does_not_dispatch()
    {
        var fixture = new PreExecRunFixture(
            [new PreExecPaneRow("locale", "0", "0", "0")],
            new ServerGeneration(100, 910));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Pane.RunAsync(
                "printf 'never\\n'", TimeSpan.FromSeconds(1), cancellation.Token));

        Assert.Equal(0, fixture.PasteCount);
    }

    [Fact]
    public async Task The_first_pane_read_consumes_the_startup_grace()
    {
        var fixture = new PreExecRunFixture(
            [
                new PreExecPaneRow("locale", "0", "0", "0"),
                new PreExecPaneRow("sh", "0", "0", "0"),
            ],
            new ServerGeneration(103, 913))
        {
            FirstListingDelay = TimeSpan.FromMilliseconds(80),
        };

        await Assert.ThrowsAsync<TmuxPaneException>(() => fixture.Pane.RunAsync(
            "printf 'never\\n'", TimeSpan.FromMilliseconds(50),
            TestContext.Current.CancellationToken));

        Assert.Equal(0, fixture.PasteCount);
        Assert.Equal(1, fixture.ListPanesCount);
    }

    [Fact]
    public async Task Public_final_startup_wait_shares_the_initial_budget()
    {
        var fixture = new PreExecRunFixture(
            [
                new PreExecPaneRow("sh", "0", "0", "0"),
                new PreExecPaneRow("locale", "0", "0", "0"),
                new PreExecPaneRow("sh", "0", "0", "0"),
            ],
            new ServerGeneration(101, 911))
        {
            BaselineCaptureDelay = TimeSpan.FromMilliseconds(80),
        };

        await Assert.ThrowsAsync<TmuxPaneException>(() => fixture.Pane.RunAsync(
            "printf 'never\\n'", TimeSpan.FromMilliseconds(50),
            TestContext.Current.CancellationToken));

        Assert.Equal(0, fixture.PasteCount);
        Assert.Equal(2, fixture.ListPanesCount);
    }

    [Fact]
    public async Task Blank_login_helper_between_preflights_can_settle_before_dispatch()
    {
        var fixture = new PreExecRunFixture(
            [
                new PreExecPaneRow("sh", "0", "0", "0"),
                new PreExecPaneRow("locale", "0", "0", "0"),
                new PreExecPaneRow("sh", "0", "0", "0"),
            ],
            new ServerGeneration(97, 907));

        PaneRunResult result = await fixture.Pane.RunAsync(
            "printf 'ready\\n'",
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitStatus);
        Assert.Equal(1, fixture.PasteCount);
        Assert.Equal(3, fixture.ListPanesCount);
    }

    [Theory]
    [InlineData("tmux -c true")]
    [InlineData("\"tmux attach\"")]
    [InlineData("\"env FOO=1 tmux attach\"")]
    public async Task Explicit_non_shell_start_command_is_refused_before_retry(string start)
    {
        var fixture = new PreExecRunFixture(
            [
                new PreExecPaneRow("tmux", "0", "0", "0", start),
                new PreExecPaneRow("sh", "0", "0", "0"),
            ],
            new ServerGeneration(98, 908));

        TmuxPaneException refusal = await Assert.ThrowsAsync<TmuxPaneException>(() =>
            fixture.Pane.RunAsync(
                "printf 'never\\n'",
                TimeSpan.FromSeconds(1),
                TestContext.Current.CancellationToken));

        Assert.Equal(TmuxDispatchState.NotDispatched, refusal.Dispatch);
        Assert.Equal(1, fixture.ListPanesCount);
        Assert.Equal(0, fixture.PasteCount);
    }

    [Theory]
    [InlineData("1", "0", "0", "", "tmux")]
    [InlineData("0", "1", "0", "", "tmux")]
    [InlineData("0", "0", "1", "", "tmux")]
    [InlineData("0", "0", "0", "tmux attach -t nested", "tmux")]
    [InlineData("0", "0", "0", "tmux -L inner new-session", "tmux")]
    [InlineData("0", "0", "0", "vim", "vim")]
    public async Task Drawn_or_non_pre_exec_pane_is_refused_immediately_without_retrying(
        string cursorX,
        string cursorY,
        string historySize,
        string startCommand,
        string currentCommand)
    {
        var fixture = new PreExecRunFixture(
            [
                new PreExecPaneRow(currentCommand, cursorX, cursorY, historySize, startCommand),
                new PreExecPaneRow("sh", "0", "0", "0"),
            ],
            new ServerGeneration(93, 903));

        TmuxPaneException refusal = await Assert.ThrowsAsync<TmuxPaneException>(() =>
            fixture.Pane.RunAsync(
                "printf 'never\\n'",
                TimeSpan.FromSeconds(1),
                TestContext.Current.CancellationToken));

        Assert.Equal(TmuxDispatchState.NotDispatched, refusal.Dispatch);
        Assert.Equal(
            $"Pane %1 is running '{currentCommand}', not a POSIX-compatible shell.",
            refusal.Message);
        Assert.Equal(1, fixture.ListPanesCount);
        Assert.Equal(0, fixture.PasteCount);
    }

    [Theory]
    [InlineData("tmux-custom-3.8a-binary")]
    [InlineData("tmux-custom-3.8")]
    public async Task Configured_binary_basename_and_darwin_comm_prefix_re_read_on_blank_pane(
        string initialCommand)
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"lt-bin-{Guid.NewGuid():N}"[..18]);
        Directory.CreateDirectory(directory);
        string customBinary = Path.Combine(directory, "tmux-custom-3.8a-binary");
        File.CreateSymbolicLink(customBinary, "/bin/sh");
        try
        {
            var fixture = new PreExecRunFixture(
                [
                    new PreExecPaneRow(initialCommand, "0", "0", "0"),
                    new PreExecPaneRow("sh", "0", "0", "0"),
                    new PreExecPaneRow("sh", "2", "0", "0"),
                ],
                new ServerGeneration(95, 905),
                customBinary);

            PaneRunResult result = await fixture.Pane.RunAsync(
                "printf 'ready\\n'",
                TimeSpan.FromSeconds(1),
                TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitStatus);
            Assert.Equal(3, fixture.ListPanesCount);
            Assert.Equal(1, fixture.PasteCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Blank_pane_staying_tmux_past_the_bound_is_refused()
    {
        var fixture = new PreExecRunFixture(
            [new PreExecPaneRow("tmux", "0", "0", "0")],
            new ServerGeneration(94, 904));

        TmuxPaneException refusal = await Assert.ThrowsAsync<TmuxPaneException>(() =>
            fixture.Pane.RunAsync(
                "printf 'never\\n'",
                TimeSpan.FromMilliseconds(50),
                TestContext.Current.CancellationToken));

        Assert.Equal(TmuxDispatchState.NotDispatched, refusal.Dispatch);
        Assert.Equal("Pane %1 is running 'tmux', not a POSIX-compatible shell.", refusal.Message);
        Assert.True(fixture.ListPanesCount >= 2);
        Assert.Equal(0, fixture.PasteCount);
    }

    private static Pane CreatePane(Action onDispatch)
    {
        var connection = new TmuxConnection(
            new ServerConnectionOptions
            {
                TmuxBinaryPath = "/bin/sh",
                SocketPath = "/tmp/libtmux-command-run-test.sock",
            },
            FakeMultiplexer.AnsweringVersion((request, _) =>
            {
                onDispatch();
                byte[] output = Encoding.UTF8.GetBytes("91:901\n");
                return Task.FromResult(new TmuxCommandResult(
                    request.LogicalArguments,
                    0,
                    output,
                    ReadOnlyMemory<byte>.Empty,
                    Utf8BackslashDecoder.ProjectOutputLines(output),
                    []));
            }));
        var generation = new ServerGeneration(91, 901);
        var server = new Server(connection, generation, "tmux 3.7");
        return new Pane(
            server,
            connection,
            generation,
            new PaneId(1),
            new Dictionary<string, string?>
            {
                ["socket_path"] = "/tmp/libtmux-command-run-test.sock",
            });
    }

    private sealed record PreExecPaneRow(
        string CurrentCommand,
        string CursorX,
        string CursorY,
        string HistorySize,
        string StartCommand = "");

    private sealed class PreExecRunFixture
    {
        private const string TestSocketPath = "/tmp/libtmux-pre-exec-run-test.sock";
        private readonly IReadOnlyList<PreExecPaneRow> _rows;
        private readonly ServerGeneration _generation;
        private int _listPanesCount;
        private int _pasteCount;

        internal PreExecRunFixture(
            IReadOnlyList<PreExecPaneRow> rows,
            ServerGeneration generation,
            string tmuxBinaryPath = "/bin/sh")
        {
            _rows = rows;
            _generation = generation;
            var connection = new TmuxConnection(
                new ServerConnectionOptions
                {
                    TmuxBinaryPath = tmuxBinaryPath,
                    SocketPath = TestSocketPath,
                },
                FakeMultiplexer.AnsweringVersion(ExecuteAsync));
            var server = new Server(connection, generation, "tmux 3.7");
            Pane = new Pane(
                server,
                connection,
                generation,
                new PaneId(1),
                new Dictionary<string, string?>
                {
                    ["socket_path"] = TestSocketPath,
                    ["pane_width"] = "80",
                    ["pane_height"] = "24",
                });
        }

        internal Pane Pane { get; }

        internal int ListPanesCount => Volatile.Read(ref _listPanesCount);

        internal int PasteCount => Volatile.Read(ref _pasteCount);

        internal TimeSpan BaselineCaptureDelay { get; init; }

        internal TimeSpan FirstListingDelay { get; init; }

        private async Task<TmuxCommandResult> ExecuteAsync(
            TmuxCommandRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<string> arguments = request.LogicalArguments;
            if (arguments.Contains("list-panes", StringComparer.Ordinal) && ListPanesCount == 0)
            {
                await Task.Delay(FirstListingDelay, cancellationToken);
            }

            if (arguments.Contains("capture-pane", StringComparer.Ordinal) && PasteCount == 0)
            {
                await Task.Delay(BaselineCaptureDelay, cancellationToken);
            }

            if (arguments.Contains("paste-buffer", StringComparer.Ordinal))
            {
                Interlocked.Increment(ref _pasteCount);
            }

            string body = arguments.Contains("list-panes", StringComparer.Ordinal)
                ? RenderListPanes()
                : arguments.Any(static argument => argument.Contains(
                    "#{history_size}",
                    StringComparison.Ordinal))
                    ? $"4242\t0\t50000\t24\t{(PasteCount == 0 ? 0 : 1)}\t0\t0\t80\n"
                    : arguments.Contains("capture-pane", StringComparer.Ordinal)
                        ? PasteCount == 0 ? "$\n" : "ready\n"
                        : arguments.Contains("show-options", StringComparer.Ordinal)
                            ? $"{arguments[^1]} 0\n"
                            : string.Empty;
            string stdout = arguments.Count > 2
                && arguments[0] == "display-message"
                && arguments[2] == "#{pid}:#{start_time}"
                    ? $"{_generation.ProcessId.ToString(CultureInfo.InvariantCulture)}:{_generation.StartTime.ToString(CultureInfo.InvariantCulture)}\n{body}"
                    : body;
            byte[] encoded = Encoding.UTF8.GetBytes(stdout);
            return new TmuxCommandResult(
                arguments,
                0,
                encoded,
                ReadOnlyMemory<byte>.Empty,
                Utf8BackslashDecoder.ProjectOutputLines(encoded),
                []);
        }

        private string RenderListPanes()
        {
            FormatProjection projection = FormatProjection.Create(
                "list-panes",
                TmuxVersion.Parse("3.7"));
            int index = Interlocked.Increment(ref _listPanesCount) - 1;
            PreExecPaneRow row = _rows[Math.Min(index, _rows.Count - 1)];
            return string.Concat(projection.Fields.Select(field =>
                FieldValue(field.WireName, row) + FormatProjection.RowSeparator)) + "\n";
        }

        private string FieldValue(string field, PreExecPaneRow row) => field switch
        {
            "pid" => _generation.ProcessId.ToString(CultureInfo.InvariantCulture),
            "start_time" => _generation.StartTime.ToString(CultureInfo.InvariantCulture),
            "session_id" => "$1",
            "window_id" => "@1",
            "window_index" => "0",
            "pane_id" => "%1",
            "pane_pid" => "4242",
            "pane_width" => "80",
            "pane_height" => "24",
            "pane_active" => "1",
            "window_zoomed_flag" => "0",
            "pane_current_command" => row.CurrentCommand,
            "pane_start_command" => row.StartCommand,
            "pane_dead" => "0",
            "pane_input_off" => "0",
            "pane_in_mode" => "0",
            "pane_synchronized" => "0",
            "cursor_x" => row.CursorX,
            "cursor_y" => row.CursorY,
            "history_size" => row.HistorySize,
            "socket_path" => TestSocketPath,
            _ => string.Empty,
        };
    }
}
