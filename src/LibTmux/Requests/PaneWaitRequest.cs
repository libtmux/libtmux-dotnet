using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;

namespace LibTmux;

/// <summary>Describes rendered output to wait for in a pane.</summary>
/// <remarks>
/// Patterns are tested line by line, first against the current screen and then
/// against rows the pane writes or rewrites. Text typed into the pane is output
/// too; command completion requires <see cref="Pane.RunAsync(PaneRunRequest, CancellationToken)" />.
/// </remarks>
public sealed record PaneWaitRequest
{
    internal const int MaximumPatterns = 32;
    internal const int MaximumPatternBytes = 999;
    internal const int MaximumPatternBytesTotal = 16_384;
    internal const int MaximumMatchWorkBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);
    private IReadOnlyList<Regex> _patterns = Array.AsReadOnly(Array.Empty<Regex>());
    private IReadOnlyList<Regex> _stopPatterns = Array.AsReadOnly(Array.Empty<Regex>());
    private PatternSource[]? _wantedSources;
    private PatternSource[]? _stopSources;

    /// <summary>Gets patterns that end the wait; none means any new output does.</summary>
    public IReadOnlyList<Regex> Patterns
    {
        get => _patterns;
        init => _patterns = Own(value, nameof(Patterns));
    }

    /// <summary>Gets stop patterns, checked before wanted patterns even at entry.</summary>
    public IReadOnlyList<Regex> StopPatterns
    {
        get => _stopPatterns;
        init => _stopPatterns = Own(value, nameof(StopPatterns));
    }

    /// <summary>Gets the total time allowed, including attach and captures.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Permits observable timed reads when the control stream fails.</summary>
    public bool AllowPollingFallback { get; init; }

    /// <summary>Gets the maximum recent rendered lines returned, from 1 through 1000.</summary>
    public int TailLines { get; init; } = 20;

    /// <summary>Gets the maximum UTF-8 bytes returned in the tail, from 1 through 1048576.</summary>
    public int MaxOutputBytes { get; init; } = 65_536;

    /// <summary>Builds a request from text patterns while retaining their original spelling.</summary>
    /// <param name="patterns">Wanted patterns, or null for any new output.</param>
    /// <param name="stopPatterns">Patterns that stop the wait.</param>
    /// <param name="ignoreCase">Whether text matching ignores case.</param>
    /// <param name="simpleMatch">Whether pattern text is matched literally.</param>
    /// <returns>A request with owned, bounded regular expressions.</returns>
    /// <exception cref="ArgumentException">A pattern is empty, oversized, or invalid.</exception>
    public static PaneWaitRequest FromTextPatterns(
        IReadOnlyList<string>? patterns = null,
        IReadOnlyList<string>? stopPatterns = null,
        bool ignoreCase = false,
        bool simpleMatch = false)
    {
        int wantedCount = patterns?.Count ?? 0;
        int stopCount = stopPatterns?.Count ?? 0;
        if (wantedCount > MaximumPatterns || stopCount > MaximumPatterns - wantedCount)
        {
            throw new ArgumentException($"A pane wait accepts at most {MaximumPatterns} patterns across both lists.");
        }

        int totalBytes = 0;
        PatternSource[] wanted = CompileText(patterns, ignoreCase, simpleMatch, ref totalBytes);
        PatternSource[] stops = CompileText(stopPatterns, ignoreCase, simpleMatch, ref totalBytes);
        return new PaneWaitRequest
        {
            Patterns = [.. wanted.Select(source => source.Regex)],
            StopPatterns = [.. stops.Select(source => source.Regex)],
            _wantedSources = wanted,
            _stopSources = stops,
        };
    }

    /// <summary>Validates and copies bounded patterns before pane I/O.</summary>
    public void Validate() => _ = Snapshot();

    internal ValidatedPaneWaitRequest Snapshot()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(Timeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Timeout, Internal.PaneTextWaiter.LongestTimeout);
        ArgumentOutOfRangeException.ThrowIfLessThan(TailLines, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(TailLines, 1000);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxOutputBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxOutputBytes, 1_048_576);
        int wantedCount = Patterns.Count;
        int stopCount = StopPatterns.Count;
        if (wantedCount > MaximumPatterns || stopCount > MaximumPatterns - wantedCount)
        {
            throw new ArgumentException($"A pane wait accepts at most {MaximumPatterns} patterns across both lists.");
        }

        int totalBytes = 0;
        PaneWaitPattern[] wanted = Recompile(Patterns, _wantedSources, ref totalBytes);
        PaneWaitPattern[] stops = Recompile(StopPatterns, _stopSources, ref totalBytes);
        return new ValidatedPaneWaitRequest(
            wanted, stops, Timeout, AllowPollingFallback, TailLines, MaxOutputBytes);
    }

    private static ReadOnlyCollection<Regex> Own(IReadOnlyList<Regex>? values, string property)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count > MaximumPatterns)
        {
            throw new ArgumentException($"A pane wait accepts at most {MaximumPatterns} patterns in one list.", property);
        }

        Regex[] owned = new Regex[values.Count];
        for (int index = 0; index < owned.Length; index++)
        {
            owned[index] = values[index] ?? throw new ArgumentException(
                "A pane wait pattern cannot be null.", property);
        }

        return Array.AsReadOnly(owned);
    }

    private static PatternSource[] CompileText(
        IReadOnlyList<string>? values,
        bool ignoreCase,
        bool simpleMatch,
        ref int totalBytes)
    {
        if (values is null)
        {
            return [];
        }

        PatternSource[] result = new PatternSource[values.Count];
        RegexOptions options = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking
            | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
        for (int index = 0; index < result.Length; index++)
        {
            string? value = values[index];
            ValidatePatternText(value, ref totalBytes);
            try
            {
                Regex regex = new(simpleMatch ? Regex.Escape(value!) : value!, options, MatchTimeout);
                result[index] = new PatternSource(regex, value!);
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException)
            {
                throw new ArgumentException("The pane wait pattern is invalid or unsupported.", nameof(values), error);
            }
        }

        return result;
    }

    private static PaneWaitPattern[] Recompile(
        IReadOnlyList<Regex> values,
        PatternSource[]? sources,
        ref int totalBytes)
    {
        PaneWaitPattern[] result = new PaneWaitPattern[values.Count];
        for (int index = 0; index < result.Length; index++)
        {
            Regex original = values[index] ?? throw new ArgumentException("A pane wait pattern cannot be null.");
            string text = original.ToString();
            string label = sources is not null && index < sources.Length
                && ReferenceEquals(sources[index].Regex, original)
                    ? sources[index].Text
                    : text;
            ValidatePatternText(label, ref totalBytes);
            TimeSpan timeout = original.MatchTimeout == Regex.InfiniteMatchTimeout
                ? MatchTimeout
                : original.MatchTimeout < MatchTimeout ? original.MatchTimeout : MatchTimeout;
            Regex bounded = new(text, original.Options & ~RegexOptions.Compiled, timeout);
            result[index] = new PaneWaitPattern(bounded, label);
        }

        return result;
    }

    private static void ValidatePatternText(string? value, ref int totalBytes)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new ArgumentException("A pane wait pattern cannot be empty.");
        }

        int bytes = Encoding.UTF8.GetByteCount(value);
        if (bytes > MaximumPatternBytes || bytes > MaximumPatternBytesTotal - totalBytes)
        {
            throw new ArgumentException(
                $"Pane wait patterns may use at most {MaximumPatternBytes} UTF-8 bytes each "
                + $"and {MaximumPatternBytesTotal} bytes in total.");
        }

        totalBytes += bytes;
    }

    private sealed record PatternSource(Regex Regex, string Text);
}

internal sealed record PaneWaitPattern(Regex Regex, string Text);

internal sealed record ValidatedPaneWaitRequest(
    PaneWaitPattern[] Wanted,
    PaneWaitPattern[] Stops,
    TimeSpan Timeout,
    bool AllowPollingFallback,
    int TailLines,
    int MaxOutputBytes);
