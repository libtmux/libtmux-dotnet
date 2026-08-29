using System.Text.RegularExpressions;

namespace LibTmux.Query;

internal static class QueryRegexSemantics
{
    internal const string Dialect = "dotnet";
    internal const int MaximumPatternLength = 1024;

    // One match may consume this much CPU before hostile backtracking is refused.
    internal static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    internal const RegexOptions AllowedOptions =
        RegexOptions.IgnoreCase
        | RegexOptions.Multiline
        | RegexOptions.ExplicitCapture
        | RegexOptions.Singleline
        | RegexOptions.IgnorePatternWhitespace
        | RegexOptions.CultureInvariant;

    internal static bool IsSupported(RegexOptions options) =>
        (options & ~AllowedOptions) == 0
        && (options & RegexOptions.CultureInvariant) != 0;

    internal static bool IsValidPattern(string pattern, RegexOptions options)
    {
        try
        {
            _ = new Regex(pattern, options, MatchTimeout);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
