namespace LibTmux;

/// <summary>Describes one <c>find-window</c> invocation.</summary>
public sealed record FindWindowRequest
{
    /// <summary>Initializes a window-search request.</summary>
    /// <param name="pattern">The text to look for.</param>
    /// <exception cref="ArgumentException"><paramref name="pattern" /> is blank.</exception>
    public FindWindowRequest(string pattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        Pattern = pattern;
    }

    /// <summary>Gets the text to look for.</summary>
    public string Pattern { get; }

    /// <summary>Gets whether pane content is searched.</summary>
    public bool MatchContent { get; init; }

    /// <summary>Gets whether the search ignores case.</summary>
    public bool IgnoreCase { get; init; }

    /// <summary>Gets whether window names are searched.</summary>
    public bool MatchName { get; init; }

    /// <summary>Gets whether the pattern is a regular expression.</summary>
    public bool Regex { get; init; }

    /// <summary>Gets whether pane titles are searched.</summary>
    public bool MatchTitle { get; init; }
}
