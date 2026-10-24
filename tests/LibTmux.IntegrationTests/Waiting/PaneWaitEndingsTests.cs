using System.Diagnostics;
using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Mcp;
using LibTmux.Testing;

namespace LibTmux.IntegrationTests.Waiting;

/// <summary>Proves how a pane wait ends when the text wraps and when the server goes.</summary>
/// <remarks>
/// Each test runs on whichever tmux the lane uses: below 3.7 the wait polls,
/// and from 3.7 it listens to a control client and reads the grid when it is
/// told something changed.
/// </remarks>
[Collection("tmux control clients")]
[UnsupportedOSPlatform("windows")]
public sealed class PaneWaitEndingsTests
{
    private const string Wrapped = "abcdefghijklmnopqNEEDLE_SPLIT_here_zzzz";
    private const string Needle = "NEEDLE_SPLIT";

    // The program prints after the wait has started, so the wait sees new
    // output rather than text already on screen.
    private static readonly string PrintsWrappedLater =
        $"sleep 1; printf '%s\\n' {Wrapped}; sleep 60";

    private static readonly string PrintsThenWaits =
        "printf 'SO_FAR_LINE\\n'; sleep 60";

    [UnixFact]
    public async Task A_library_wait_finds_text_that_wrapped_onto_two_rows()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        await using TemporaryServerScope scope = await new TmuxTestFactory().CreateServerAsync(mcp.Options, token);
        Pane pane = await StartPaneAsync(scope, "wrap-new", PrintsWrappedLater, 20, token);

        PaneWaitResult result = await pane.WaitForTextAsync(
            PaneWaitRequest.FromTextPatterns([Needle], simpleMatch: true) with { Timeout = TimeSpan.FromSeconds(10) },
            token);

        Assert.Equal(PaneWaitOutcome.Matched, result.Outcome);
        Assert.Equal(Needle, result.Pattern);
    }

    [UnixFact]
    public async Task A_library_wait_finds_wrapped_text_already_on_screen()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        await using TemporaryServerScope scope = await new TmuxTestFactory().CreateServerAsync(mcp.Options, token);
        Pane pane = await StartPaneAsync(scope, "wrap-old", $"printf '%s\\n' {Wrapped}; sleep 60", 20, token);
        await Task.Delay(500, token);

        PaneWaitResult result = await pane.WaitForTextAsync(
            PaneWaitRequest.FromTextPatterns([Needle], simpleMatch: true) with { Timeout = TimeSpan.FromSeconds(10) },
            token);

        Assert.Equal(PaneWaitOutcome.PresentAtEntry, result.Outcome);
    }

    [UnixFact]
    public async Task A_wait_does_not_join_rows_that_a_newline_separates()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        await using TemporaryServerScope scope = await new TmuxTestFactory().CreateServerAsync(mcp.Options, token);
        // Twenty columns, filled exactly, then a real line break.
        Pane pane = await StartPaneAsync(
            scope, "no-join", "printf 'abcdefghijklmnopqNEE\\nDLE_SPLIT\\n'; sleep 60", 20, token);
        await Task.Delay(500, token);

        PaneWaitResult result = await pane.WaitForTextAsync(
            PaneWaitRequest.FromTextPatterns([Needle], simpleMatch: true) with { Timeout = TimeSpan.FromSeconds(1) },
            token);

        Assert.Equal(PaneWaitOutcome.TimedOut, result.Outcome);
    }

    [UnixFact]
    public async Task An_mcp_wait_finds_text_that_wrapped_onto_two_rows()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        await using TemporaryServerScope scope = await new TmuxTestFactory().CreateServerAsync(mcp.Options, token);
        Pane pane = await StartPaneAsync(scope, "wrap-mcp", PrintsWrappedLater, 20, token);

        WaitResult result = await mcp.Read.WaitForTextAsync(
            pane.Id.ToString(), [Needle], timeoutSeconds: 10, ignoreCase: false, cancellationToken: token);

        Assert.Equal(WaitOutcome.Matched, result.Outcome);
    }

    // A row of spaces inside a wrapped line comes back from capture-pane -p as
    // an empty row. Each screen below holds one, next to an ordinary wrapped
    // line where the screen has two, and the wait must find what is on both.
    private const string SpacesInTheMiddle = "printf '%s%20s%s\\n' abcdefghijklmnopqrst '' END_OF_LINE; ";

    private const string SpacesAtTheEnd = "printf 'abcdefghijklmnopqrst        \\n'; ";

    private static readonly string AcrossTheSpaces = "rst" + new string(' ', 20) + "END";

    [UnixFact]
    public Task A_wait_finds_text_across_a_wrapped_row_made_only_of_spaces() =>
        AssertFoundAsync("gap-alone", SpacesInTheMiddle + "sleep 60", AcrossTheSpaces);

    [UnixFact]
    public Task A_wait_finds_an_ordinary_wrapped_line_beside_a_row_made_only_of_spaces() =>
        AssertFoundAsync("gap-beside", SpacesInTheMiddle + $"printf '%s\\n' {Wrapped}; sleep 60", Needle);

    [UnixFact]
    public Task A_wait_finds_an_ordinary_wrapped_line_printed_before_a_row_made_only_of_spaces() =>
        AssertFoundAsync("gap-after", $"printf '%s\\n' {Wrapped}; " + SpacesInTheMiddle + "sleep 60", Needle);

    [UnixFact]
    public Task A_wait_finds_a_wrapped_line_after_a_spill_row_made_only_of_spaces() =>
        AssertFoundAsync("spill", SpacesAtTheEnd + $"printf '%s\\n' {Wrapped}; sleep 60", Needle);

    // A line indented by a whole pane width starts with a row of only spaces.
    // The wait must read that line as one, and must still read what follows.
    private const string IndentedByAPaneWidth =
        "printf 'x\\n'; printf '%20s%s\\n' '' abcdefghijklmnopqNEEDLE_ONE_here; ";

    private const string TwiceIndentedByAPaneWidth =
        "printf 'x\\n'; printf '%20s%s\\n' '' lead_one; printf '%20s%s\\n' '' lead_two; ";

    private const string IndentedThenBlank =
        "printf 'x\\n'; printf '%20s%s\\n' '' lead_one; printf '   \\n'; ";

    [UnixFact]
    public Task A_wait_finds_text_across_the_wrap_of_a_line_indented_by_a_pane_width() =>
        AssertFoundAsync("lead-alone", IndentedByAPaneWidth + "sleep 60", "NEEDLE_ONE");

    [UnixFact]
    public Task A_wait_finds_an_ordinary_wrapped_line_after_two_lines_indented_by_a_pane_width() =>
        AssertFoundAsync(
            "lead-twice", TwiceIndentedByAPaneWidth + $"printf '%s\\n' {Wrapped}; sleep 60", Needle);

    [UnixFact]
    public Task A_wait_finds_an_ordinary_wrapped_line_after_an_indented_line_and_a_blank_one() =>
        AssertFoundAsync(
            "lead-blank", IndentedThenBlank + $"printf '%s\\n' {Wrapped}; sleep 60", Needle);

    private static async Task AssertFoundAsync(string name, string command, string wanted)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        await using TemporaryServerScope scope = await new TmuxTestFactory().CreateServerAsync(mcp.Options, token);

        // Already on screen when the wait starts.
        Pane old = await StartPaneAsync(scope, name + "-old", command, 20, token);
        await Task.Delay(700, token);
        PaneWaitResult present = await old.WaitForTextAsync(
            PaneWaitRequest.FromTextPatterns([wanted], simpleMatch: true) with { Timeout = TimeSpan.FromSeconds(10) },
            token);
        Assert.Equal(PaneWaitOutcome.PresentAtEntry, present.Outcome);

        WaitResult listed = await mcp.Read.WaitForTextAsync(
            old.Id.ToString(), [wanted], timeoutSeconds: 10, ignoreCase: false, cancellationToken: token);
        Assert.Equal(WaitOutcome.PresentAtEntry, listed.Outcome);

        // Printed after the wait has started.
        Pane later = await StartPaneAsync(scope, name + "-new", "sleep 3; " + command, 20, token);
        PaneWaitResult arrived = await later.WaitForTextAsync(
            PaneWaitRequest.FromTextPatterns([wanted], simpleMatch: true) with { Timeout = TimeSpan.FromSeconds(10) },
            token);
        Assert.Equal(PaneWaitOutcome.Matched, arrived.Outcome);

        WaitResult listedLater = await mcp.Read.WaitForTextAsync(
            (await StartPaneAsync(scope, name + "-mcp", "sleep 3; " + command, 20, token)).Id.ToString(),
            [wanted],
            timeoutSeconds: 10,
            ignoreCase: false,
            cancellationToken: token);
        Assert.Equal(WaitOutcome.Matched, listedLater.Outcome);
    }

    [UnixFact]
    public async Task A_library_wait_ends_as_exited_when_the_server_is_killed()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        await using TemporaryServerScope scope = await new TmuxTestFactory().CreateServerAsync(mcp.Options, token);
        Pane pane = await StartPaneAsync(scope, "kill-lib", PrintsThenWaits, 80, token);
        await WaitUntilPrintedAsync(pane, token);

        Task<PaneWaitResult> waiting = pane.WaitForTextAsync(
            PaneWaitRequest.FromTextPatterns(["NEVER_PRINTED"], simpleMatch: true) with { Timeout = TimeSpan.FromSeconds(30) },
            token);
        await Task.Delay(700, token);
        await TmuxAsync(scope, ["kill-server"]);
        PaneWaitResult result = await waiting.WaitAsync(TimeSpan.FromSeconds(20), token);

        Assert.Equal(PaneWaitOutcome.PaneExited, result.Outcome);
        Assert.Contains("SO_FAR_LINE", result.Tail);
    }

    [UnixFact]
    public async Task A_library_wait_ends_as_exited_when_a_stopped_client_holds_the_server_up()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        await using TemporaryServerScope scope = await new TmuxTestFactory().CreateServerAsync(mcp.Options, token);
        Pane pane = await StartPaneAsync(scope, "stop-lib", PrintsThenWaits, 80, token);
        await WaitUntilPrintedAsync(pane, token);
        using StoppedClient stopped = await StoppedClient.StartAsync(scope, "stop-lib");

        Task<PaneWaitResult> waiting = pane.WaitForTextAsync(
            PaneWaitRequest.FromTextPatterns(["NEVER_PRINTED"], simpleMatch: true) with { Timeout = TimeSpan.FromSeconds(30) },
            token);
        await Task.Delay(700, token);
        await TmuxAsync(scope, ["kill-server"]);
        PaneWaitResult result = await waiting.WaitAsync(TimeSpan.FromSeconds(20), token);

        Assert.Equal(PaneWaitOutcome.PaneExited, result.Outcome);
        Assert.Contains("SO_FAR_LINE", result.Tail);
    }

    [UnixFact]
    public async Task An_mcp_wait_reports_the_pane_died_when_the_server_is_killed()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        await using TemporaryServerScope scope = await new TmuxTestFactory().CreateServerAsync(mcp.Options, token);
        Pane pane = await StartPaneAsync(scope, "kill-mcp", PrintsThenWaits, 80, token);
        await WaitUntilPrintedAsync(pane, token);

        Task<WaitResult> waiting = mcp.Read.WaitForTextAsync(
            pane.Id.ToString(), ["NEVER_PRINTED"], timeoutSeconds: 30, cancellationToken: token);
        await Task.Delay(700, token);
        await TmuxAsync(scope, ["kill-server"]);
        WaitResult result = await waiting.WaitAsync(TimeSpan.FromSeconds(20), token);

        Assert.Equal(WaitOutcome.PaneDied, result.Outcome);
        Assert.Contains("SO_FAR_LINE", result.Tail.Lines);
    }

    [UnixFact]
    public async Task An_mcp_wait_reports_the_pane_died_when_a_stopped_client_holds_the_server_up()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        await using TemporaryServerScope scope = await new TmuxTestFactory().CreateServerAsync(mcp.Options, token);
        Pane pane = await StartPaneAsync(scope, "stop-mcp", PrintsThenWaits, 80, token);
        await WaitUntilPrintedAsync(pane, token);
        using StoppedClient stopped = await StoppedClient.StartAsync(scope, "stop-mcp");

        Task<WaitResult> waiting = mcp.Read.WaitForTextAsync(
            pane.Id.ToString(), ["NEVER_PRINTED"], timeoutSeconds: 30, cancellationToken: token);
        await Task.Delay(700, token);
        await TmuxAsync(scope, ["kill-server"]);
        WaitResult result = await waiting.WaitAsync(TimeSpan.FromSeconds(20), token);

        Assert.Equal(WaitOutcome.PaneDied, result.Outcome);
        Assert.Contains("SO_FAR_LINE", result.Tail.Lines);
    }

    [UnixFact]
    public async Task A_wait_on_a_live_pane_whose_stream_drops_reports_the_lost_stream()
    {
        TmuxFeatureSupport.RequireControl();
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        await using TemporaryServerScope scope = await new TmuxTestFactory().CreateServerAsync(mcp.Options, token);
        Pane pane = await StartPaneAsync(scope, "drop-lib", PrintsThenWaits, 80, token);
        await WaitUntilPrintedAsync(pane, token);

        Task<PaneWaitResult> waiting = pane.WaitForTextAsync(
            PaneWaitRequest.FromTextPatterns(["NEVER_PRINTED"], simpleMatch: true) with { Timeout = TimeSpan.FromSeconds(30) },
            token);
        await Task.Delay(1000, token);
        await TmuxAsync(scope, ["detach-client", "-a"]);
        string clients = await TmuxAsync(scope, ["list-clients", "-F", "#{client_name}"]);
        foreach (string client in clients.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            await TmuxAsync(scope, ["detach-client", "-t", client]);
        }

        TmuxTransportException lost = await Assert.ThrowsAsync<TmuxTransportException>(
            () => waiting.WaitAsync(TimeSpan.FromSeconds(20), token));
        Assert.Contains("live control client", lost.Message, StringComparison.Ordinal);
        Assert.True(await scope.Server.IsAliveAsync(token));
    }

    private static async Task<Pane> StartPaneAsync(
        TemporaryServerScope scope,
        string name,
        string shellCommand,
        int width,
        CancellationToken token)
    {
        Session session = await scope.Server.CreateSessionAsync(
            new NewSessionRequest
            {
                Name = name,
                Command = shellCommand,
                Width = width.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Height = "10",
            },
            token);
        return Assert.Single(await session.GetPanesAsync(token));
    }

    private static async Task WaitUntilPrintedAsync(Pane pane, CancellationToken token)
    {
        PaneWaitResult seen = await pane.WaitForTextAsync(
            PaneWaitRequest.FromTextPatterns(["SO_FAR_LINE"], simpleMatch: true) with { Timeout = TimeSpan.FromSeconds(10) },
            token);
        Assert.True(seen.Outcome is PaneWaitOutcome.PresentAtEntry or PaneWaitOutcome.Matched);
    }

    private static async Task<string> TmuxAsync(TemporaryServerScope scope, string[] arguments)
    {
        ProcessStartInfo start = new(RawTmuxTestContext.ConfiguredBinary)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-L");
        start.ArgumentList.Add(scope.Server.ConnectionOptions.SocketName!);
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;
        string output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return output;
    }

    // A second control client that is stopped keeps the server alive while it
    // shuts down, as a real client that is slow to leave does.
    private sealed class StoppedClient : IDisposable
    {
        private readonly Process _process;

        private StoppedClient(Process process) => _process = process;

        internal static async Task<StoppedClient> StartAsync(TemporaryServerScope scope, string session)
        {
            ProcessStartInfo start = new(RawTmuxTestContext.ConfiguredBinary)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string argument in new[] { "-L", scope.Server.ConnectionOptions.SocketName!, "-C", "attach", "-t", session })
            {
                start.ArgumentList.Add(argument);
            }

            Process process = Process.Start(start)!;
            _ = process.StandardOutput.ReadLineAsync();
            await Task.Delay(500);
            using Process stop = Process.Start("kill", ["-STOP", process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            await stop.WaitForExitAsync();
            return new StoppedClient(process);
        }

        public void Dispose()
        {
            using Process resume = Process.Start("kill", ["-CONT", _process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            resume.WaitForExit();
            if (!_process.HasExited)
            {
                _process.Kill();
            }

            _process.Dispose();
        }
    }
}
