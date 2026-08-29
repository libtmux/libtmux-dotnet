using System.Text.RegularExpressions;

namespace LibTmux.Query;

internal static class QueryRegexSemantics
{
    internal const string Dialect = "dotnet";
    internal const int MaximumPatternLength = 1024;

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
}
