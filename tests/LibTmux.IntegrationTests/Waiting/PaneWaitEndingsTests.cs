using System.Runtime.Versioning;
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
}
