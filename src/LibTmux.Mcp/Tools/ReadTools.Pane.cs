using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using ModelContextProtocol;

namespace LibTmux.Mcp;

/// <content>Reading what panes are showing.</content>
[UnsupportedOSPlatform("windows")]
internal sealed partial class ReadTools
{
    private const int MaximumSearchPatternBytes = 4_096;
    private const int MaximumSearchWorkBytes = 8 * 1_024 * 1_024;

    /// <summary>Reads a pane's content and screen state together.</summary>
    /// <param name="paneId">The pane, or null for the active one.</param>
    /// <param name="maxLines">The most lines to answer, or null for the server default.</param>
    /// <param name="socketName">The tmux socket, or null for the default.</param>
    /// <param name="cancellationToken">Cancels the tmux queries.</param>
    /// <returns>The content, the cursor, and what the pane is.</returns>
    [Description(
        "Read a pane's visible content together with its cursor position, size and "
        + "running command, in one call. Prefer this over capture_pane plus "
        + "list_panes: it is one round trip and the cursor is guaranteed to "
        + "describe the text returned with it.")]
    public async Task<PaneSnapshot> SnapshotPaneAsync(
        [Description("The pane id, such as %1. Omit for the active pane.")]
        string? paneId = null,
        [Description("The most lines to return, newest kept. Omit for the server default.")]
        int? maxLines = null,
        [Description("The tmux socket to read. Omit for the default server.")]
        string? socketName = null,
        CancellationToken cancellationToken = default)
    {
        Server server = await ServerAsync(socketName, cancellationToken).ConfigureAwait(false);
        Pane pane = await TmuxTargets.PaneAsync(server, paneId, cancellationToken)
            .ConfigureAwait(false);
        PaneRead read = await PaneReader.ReadVisibleAsync(pane, null, cancellationToken)
            .ConfigureAwait(false);

        PaneInfo paneInfo = PaneInfo.From(
            pane,
            await TmuxTargets.VerifiedCallerPaneIdAsync(server, cancellationToken)
                .ConfigureAwait(false));
        int? cursorX = await TmuxTargets.DisplayNumberAsync(
                pane,
                "#{cursor_x}",
                cancellationToken)
            .ConfigureAwait(false);
        return StructuredTextResultBudget.Fit(
            PaneText.Scrub(read.Lines, pane.Width),
            maxLines ?? _policy.MaxLines,
            _policy.MaxBytes,
            content => new PaneSnapshot(
                paneInfo,
                content,
                cursorX,
                read.State.CursorY,
                read.State.AlternateScreen),
            "pane snapshot");
    }

    /// <summary>Reads what a pane is showing.</summary>
    /// <param name="paneId">The pane, or null for the active one.</param>
    /// <param name="includeHistory">Whether to read scrollback as well as the screen.</param>
    /// <param name="maxLines">The most lines to answer, or null for the server default.</param>
    /// <param name="joinWrappedLines">Whether a line tmux wrapped is rejoined.</param>
    /// <param name="socketName">The tmux socket, or null for the default.</param>
    /// <param name="cancellationToken">Cancels the tmux queries.</param>
    /// <returns>The content.</returns>
    /// <remarks>
    /// <paramref name="joinWrappedLines" /> matters more than it looks. tmux
    /// stores a wrap as a real line break, so text a user typed into a narrow
    /// pane comes back split across rows and a search for it finds nothing.
    /// </remarks>
    [Description(
        "Read the text a pane is showing, and optionally its scrollback. The newest "
        + "lines are always kept; anything dropped to fit the budget is reported. "
        + "To watch a pane across several turns, use capture_since instead — it "
        + "returns only what is new.")]
    public async Task<CaptureResult> CapturePaneAsync(
        [Description("The pane id, such as %1. Omit for the active pane.")]
        string? paneId = null,
        [Description("Read scrollback as well as the visible screen.")]
        bool includeHistory = false,
        [Description("The most lines to return, newest kept. Omit for the server default.")]
        int? maxLines = null,
        [Description(
            "Rejoin a line tmux wrapped. Turn this on when matching text somebody "
            + "typed, which a narrow pane splits across rows.")]
        bool joinWrappedLines = false,
        [Description("The tmux socket to read. Omit for the default server.")]
        string? socketName = null,
        CancellationToken cancellationToken = default)
    {
        Server server = await ServerAsync(socketName, cancellationToken).ConfigureAwait(false);
        Pane pane = await TmuxTargets.PaneAsync(server, paneId, cancellationToken)
            .ConfigureAwait(false);

        CapturePaneRequest request = new(
            startLine: includeHistory ? CapturePanePosition.BeginningOfHistory : null,
            joinWrappedLines: joinWrappedLines);
        IReadOnlyList<string> lines = await pane.CaptureAsync(request, cancellationToken)
            .ConfigureAwait(false);

        string id = pane.Id.ToString();
        return StructuredTextResultBudget.Fit(
            PaneText.Scrub(lines, pane.Width),
            maxLines ?? _policy.MaxLines,
            _policy.MaxBytes,
            content => new CaptureResult(id, content),
            "pane capture");
    }

    /// <summary>Reads what a pane has printed since the last read.</summary>
    /// <param name="paneId">The pane, or null for the active one.</param>
    /// <param name="cursor">Where the last read finished, or null to start now.</param>
    /// <param name="maxLines">The most lines to answer, or null for the server default.</param>
    /// <param name="socketName">The tmux socket, or null for the default.</param>
    /// <param name="cancellationToken">Cancels the tmux queries.</param>
    /// <returns>The new text and a cursor for next time.</returns>
    [Description(
        "Read only what a pane has printed since the last call. Pass back the cursor "
        + "each time. Use this to watch a long-running process across turns: the "
        + "tenth read costs what the first did, where re-capturing the pane would "
        + "return everything again. Call with no cursor to start watching from now.")]
    public async Task<TailResult> TailPaneAsync(
        [Description("The pane id, such as %1. Omit for the active pane.")]
        string? paneId = null,
        [Description("The cursor from the previous call. Omit to start from what is on screen now.")]
        string? cursor = null,
        [Description("The most lines to return, newest kept. Omit for the server default.")]
        int? maxLines = null,
        [Description("The tmux socket to read. Omit for the default server.")]
        string? socketName = null,
        CancellationToken cancellationToken = default)
    {
        Server server = await ServerAsync(socketName, cancellationToken).ConfigureAwait(false);
        Pane pane = await TmuxTargets.PaneAsync(server, paneId, cancellationToken)
            .ConfigureAwait(false);
        string id = pane.Id.ToString();

        TailCursor? previous = TailCursor.Decode(cursor, pane);
        PaneRead read = previous is null
            ? await PaneReader.ReadVisibleAsync(pane, null, cancellationToken).ConfigureAwait(false)
            : await PaneReader.ReadSinceAsync(pane, previous, cancellationToken).ConfigureAwait(false);

        // A first read establishes a position without spending the caller's
        // budget on a screenful they did not ask for.
        IReadOnlyList<string> lines = previous is null ? [] : read.Lines;

        string nextCursor = TailCursor.Build(pane, read.State, read.CursorRows).Encode();
        return StructuredTextResultBudget.Fit(
            PaneText.Scrub(lines, pane.Width),
            maxLines ?? _policy.MaxLines,
            _policy.MaxBytes,
            content => new TailResult(
                id,
                content,
                nextCursor,
                read.LinesMissed,
                previous is not null && read.AnchorLost),
            "pane tail");
    }

    /// <summary>Searches what panes are showing.</summary>
    /// <param name="pattern">The regular expression to look for.</param>
    /// <param name="session">A session id or name to narrow to, or null for all of them.</param>
    /// <param name="includeHistory">Whether to search scrollback as well as the screen.</param>
    /// <param name="ignoreCase">Whether case is ignored.</param>
    /// <param name="maxMatchesPerPane">The most matching lines to answer per pane.</param>
    /// <param name="socketName">The tmux socket, or null for the default.</param>
    /// <param name="cancellationToken">Cancels the tmux queries.</param>
    /// <returns>The panes that matched.</returns>
    [Description(
        "Find which panes are showing text matching a regular expression. This is the "
        + "tool for 'which pane has the error', 'where is the build running', or any "
        + "question about what a pane CONTAINS — list tools only see names "
        + "and sizes.")]
    public async Task<SearchResult> SearchPanesAsync(
        [Description("A .NET regular expression to look for, at most 4096 UTF-8 bytes.")]
        string pattern,
        [Description("A session id such as $0, or its name. Omit to search every session.")]
        string? session = null,
        [Description("Search scrollback as well as the visible screen. Slower.")]
        bool includeHistory = false,
        [Description("Ignore case when matching.")] bool ignoreCase = true,
        [Description("The most matching lines to return from any one pane.")]
        int maxMatchesPerPane = 20,
        [Description("The tmux socket to read. Omit for the default server.")]
        string? socketName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ValidateSearchMatchLimit(maxMatchesPerPane, _policy.MaxLines);
        ValidateSearchPatternBudget(pattern, _policy.MaxBytes);

        Regex regex = CompilePattern(pattern, ignoreCase);

        Server server = await ServerAsync(socketName, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<Pane> panes = string.IsNullOrWhiteSpace(session)
            ? await TmuxAvailability
                .OrEmptyAsync(server, () => server.GetPanesAsync(cancellationToken))
                .ConfigureAwait(false)
            : await (await TmuxTargets.SessionAsync(server, session, cancellationToken)
                    .ConfigureAwait(false))
                .GetPanesAsync(cancellationToken)
                .ConfigureAwait(false);

        CapturePaneRequest request = new(
            startLine: includeHistory ? CapturePanePosition.BeginningOfHistory : null,
            joinWrappedLines: true);

        var budget = new SearchResultBudget(
            pattern,
            panes.Count,
            _policy.MaxLines,
            _policy.MaxBytes);
        var workBudget = new SearchWorkBudget(MaximumSearchWorkBytes);
        int panesSearched = 0;
        bool truncated = false;
        foreach (Pane pane in panes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<string> lines;
            try
            {
                lines = await pane.CaptureAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (LibTmuxException)
            {
                // A pane can close between the listing and the capture. One
                // that has gone cannot match, and cannot fail the search either.
                continue;
            }

            panesSearched++;
            int visibleTop = lines.Count - pane.Height;
            SearchPaneBudgetOutcome outcome = AddSearchMatches(
                budget,
                pane.Id.ToString(),
                pane.Window.Id.ToString(),
                pane.Session.Id.ToString(),
                lines,
                Math.Max(visibleTop, 0),
                regex,
                maxMatchesPerPane,
                workBudget,
                cancellationToken);
            truncated |= outcome != SearchPaneBudgetOutcome.Complete;
            if (outcome == SearchPaneBudgetOutcome.GlobalLimit)
            {
                break;
            }
        }

        return budget.Build(panesSearched, truncated);
    }

    /// <summary>Compiles a caller's pattern, refusing one that cannot be run safely.</summary>
    /// <remarks>
    /// The timeout is the point. A pattern a model wrote can backtrack for
    /// minutes on a screenful of text, and there is no way to tell from the
    /// pattern alone which one will.
    /// </remarks>
    internal static Regex CompilePattern(string pattern, bool ignoreCase)
    {
        RegexOptions options = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking
            | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
        try
        {
            return new Regex(pattern, options, TimeSpan.FromSeconds(1));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            throw new McpException(
                $"'{pattern}' is not a supported bounded regular expression: {error.Message}");
        }
    }

    /// <summary>Validates a per-pane match budget against the server-wide ceiling.</summary>
    internal static void ValidateSearchMatchLimit(int requested, int maximum)
    {
        if (requested < 1 || requested > maximum)
        {
            throw new McpException(
                $"maxMatchesPerPane must be between 1 and {maximum}, inclusive.");
        }
    }

    /// <summary>Rejects a pattern too large to compile or report within policy.</summary>
    internal static void ValidateSearchPatternBudget(string pattern, int resultMaxBytes)
    {
        int patternMaxBytes = Math.Min(MaximumSearchPatternBytes, resultMaxBytes);
        int patternBytes = System.Text.Encoding.UTF8.GetByteCount(pattern);
        if (patternBytes > patternMaxBytes)
        {
            throw new McpException(
                $"pattern is {patternBytes} UTF-8 bytes; the limit is {patternMaxBytes}. "
                + "Use a shorter regular expression.");
        }

        _ = new SearchResultBudget(pattern, int.MaxValue, 1, resultMaxBytes);
    }

    /// <summary>Adds one pane's matches and distinguishes its local cap from exhaustion.</summary>
    internal static SearchPaneBudgetOutcome AddSearchMatches(
        SearchResultBudget budget,
        string paneId,
        string windowId,
        string sessionId,
        IReadOnlyList<string> lines,
        int visibleTop,
        Regex regex,
        int maxMatchesPerPane,
        CancellationToken cancellationToken = default)
    {
        return AddSearchMatches(
            budget,
            paneId,
            windowId,
            sessionId,
            lines,
            visibleTop,
            regex,
            maxMatchesPerPane,
            new SearchWorkBudget(MaximumSearchWorkBytes),
            cancellationToken);
    }

    private static SearchPaneBudgetOutcome AddSearchMatches(
        SearchResultBudget budget,
        string paneId,
        string windowId,
        string sessionId,
        IReadOnlyList<string> lines,
        int visibleTop,
        Regex regex,
        int maxMatchesPerPane,
        SearchWorkBudget workBudget,
        CancellationToken cancellationToken)
    {
        List<MatchedLine> matched = [];
        SearchPaneBudgetOutcome outcome = SearchPaneBudgetOutcome.Complete;
        for (int index = 0; index < lines.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string line = lines[index];
            workBudget.Consume(System.Text.Encoding.UTF8.GetByteCount(line));
            bool matches;
            try
            {
                matches = regex.IsMatch(line);
            }
            catch (RegexMatchTimeoutException)
            {
                throw new McpException(
                    $"The pattern '{regex}' took too long to match. Simplify it — "
                    + "nested quantifiers such as (a+)+ backtrack badly on terminal text.");
            }

            if (!matches)
            {
                continue;
            }

            if (matched.Count >= maxMatchesPerPane)
            {
                outcome = SearchPaneBudgetOutcome.PerPaneLimit;
                break;
            }

            SearchMatchBudgetOutcome added = budget.TryAdd(
                paneId,
                windowId,
                sessionId,
                matched,
                new MatchedLine(index - visibleTop, line));
            if (added == SearchMatchBudgetOutcome.GlobalLimit)
            {
                outcome = SearchPaneBudgetOutcome.GlobalLimit;
                break;
            }

            if (added == SearchMatchBudgetOutcome.PaneCannotFit)
            {
                outcome = SearchPaneBudgetOutcome.OversizedMatchSkipped;
                break;
            }

            if (added == SearchMatchBudgetOutcome.ItemTooLarge)
            {
                outcome = SearchPaneBudgetOutcome.OversizedMatchSkipped;
            }
        }

        budget.Commit(paneId, windowId, sessionId, matched);
        return outcome;
    }
}

/// <summary>A deterministic ceiling for candidate text examined by pane search.</summary>
internal sealed class SearchWorkBudget
{
    private int _remainingBytes;
    private readonly string _failure;

    internal SearchWorkBudget(
        int maximumBytes,
        string failure = "Pane search matching work limit exceeded; narrow the session or history.")
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(failure);
        _remainingBytes = maximumBytes;
        _failure = failure;
    }

    internal void Consume(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        if (bytes > _remainingBytes)
        {
            throw new McpException(_failure);
        }

        _remainingBytes -= bytes;
    }
}

/// <summary>Why adding one pane's search matches stopped.</summary>
internal enum SearchPaneBudgetOutcome
{
    /// <summary>Every matching line fit.</summary>
    Complete = 0,

    /// <summary>This pane reached the caller's local cap.</summary>
    PerPaneLimit = 1,

    /// <summary>The server-wide line budget was exhausted.</summary>
    GlobalLimit = 2,

    /// <summary>At least one matching line was too large for the remaining budget.</summary>
    OversizedMatchSkipped = 3,
}
