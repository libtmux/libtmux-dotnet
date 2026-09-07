using System.Collections.Immutable;
using System.Runtime.Versioning;
using ModelContextProtocol;

namespace LibTmux.Mcp;

[UnsupportedOSPlatform("windows")]
internal sealed record CapabilitySelection(
    ImmutableHashSet<Toolset> Toolsets,
    ImmutableHashSet<string> IncludedNames,
    ImmutableHashSet<string> ExcludedNames)
{
    internal const string ToolsetsVariable = "LIBTMUX_TOOLSETS";
    internal const string ToolsVariable = "LIBTMUX_TOOLS";
    internal const string ExcludeToolsVariable = "LIBTMUX_EXCLUDE_TOOLS";

    internal static CapabilitySelection All { get; } = new(
        Enum.GetValues<Toolset>().ToImmutableHashSet(),
        ImmutableHashSet.Create<string>(StringComparer.Ordinal),
        ImmutableHashSet.Create<string>(StringComparer.Ordinal));

    internal static CapabilitySelection WithoutTeardown { get; } = new(
        ImmutableHashSet.Create(Toolset.Inspect, Toolset.Manage, Toolset.Execute),
        ImmutableHashSet.Create<string>(StringComparer.Ordinal),
        ImmutableHashSet.Create<string>(StringComparer.Ordinal));

    internal bool Includes(ToolDefinition definition) =>
        (Toolsets.Contains(definition.Toolset) || IncludedNames.Contains(definition.Name))
        && !ExcludedNames.Contains(definition.Name);

    internal static CapabilitySelection FromEnvironment(
        Func<string, string?> read,
        IEnumerable<Toolset> defaultToolsets)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(defaultToolsets);
        if (read(ServerPolicy.SafetyVariable) is not null)
        {
            throw new McpException(
                $"{ServerPolicy.SafetyVariable} has been retired. Use {ToolsetsVariable}, "
                + $"{ToolsVariable}, and {ExcludeToolsVariable} instead.");
        }

        string? rawToolsets = read(ToolsetsVariable);
        ImmutableHashSet<Toolset> toolsets = rawToolsets is null
            ? defaultToolsets.ToImmutableHashSet()
            : ParseToolsets(rawToolsets);
        ImmutableHashSet<string> included = ParseNames(
            read(ToolsVariable), ToolsVariable, CapabilityRegistry.Manifest.Select(tool => tool.Name));
        ImmutableHashSet<string> excluded = ParseNames(
            read(ExcludeToolsVariable), ExcludeToolsVariable, CapabilityRegistry.Manifest.Select(tool => tool.Name));
        return new CapabilitySelection(toolsets, included, excluded);
    }

    private static ImmutableHashSet<Toolset> ParseToolsets(string value)
    {
        if (value.Length == 0)
        {
            return ImmutableHashSet<Toolset>.Empty;
        }

        ImmutableHashSet<Toolset>.Builder parsed = ImmutableHashSet.CreateBuilder<Toolset>();
        foreach (string token in Tokens(value, ToolsetsVariable))
        {
            Toolset toolset = token switch
            {
                "inspect" => Toolset.Inspect,
                "manage" => Toolset.Manage,
                "execute" => Toolset.Execute,
                "teardown" => Toolset.Teardown,
                _ => throw new McpException(
                    $"{ToolsetsVariable} contains unknown toolset '{token}'."),
            };
            parsed.Add(toolset);
        }

        return parsed.ToImmutable();
    }

    private static ImmutableHashSet<string> ParseNames(
        string? value,
        string variable,
        IEnumerable<string> knownNames)
    {
        if (value is null)
        {
            return ImmutableHashSet.Create<string>(StringComparer.Ordinal);
        }

        HashSet<string> known = knownNames.ToHashSet(StringComparer.Ordinal);
        ImmutableHashSet<string>.Builder parsed = ImmutableHashSet.CreateBuilder<string>(
            StringComparer.Ordinal);
        foreach (string token in Tokens(value, variable))
        {
            if (!known.Contains(token))
            {
                throw new McpException($"{variable} contains unknown tool '{token}'.");
            }

            parsed.Add(token);
        }

        return parsed.ToImmutable();
    }

    private static IEnumerable<string> Tokens(string value, string variable)
    {
        string[] tokens = value.Split(',', StringSplitOptions.None);
        if (tokens.Any(token => token.Length == 0 || string.IsNullOrWhiteSpace(token)))
        {
            throw new McpException(
                $"{variable} contains an empty comma-separated token.");
        }

        foreach (string token in tokens)
        {
            yield return token.Trim();
        }
    }
}
