namespace LibTmux.McpSwap;

internal sealed class ServerSpec : IEquatable<ServerSpec>
{
    internal ServerSpec(
        string command,
        IEnumerable<string>? arguments = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        Command = command;
        Arguments = (arguments ?? []).ToArray();
        SortedDictionary<string, string> values = new(StringComparer.Ordinal);
        foreach ((string name, string value) in environment ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            values[name] = value;
        }

        Environment = values;
    }

    internal string Command { get; }

    internal IReadOnlyList<string> Arguments { get; }

    internal IReadOnlyDictionary<string, string> Environment { get; }

    internal ServerSpec WithCommand(string command) => new(command, Arguments, Environment);

    internal ServerSpec WithEnvironment(IReadOnlyDictionary<string, string> environment) =>
        new(Command, Arguments, environment);

    internal ServerSpec MergeEnvironment(IReadOnlyDictionary<string, string> earlier)
    {
        SortedDictionary<string, string> merged = new(StringComparer.Ordinal);
        foreach ((string name, string value) in earlier)
        {
            merged[name] = value;
        }
        foreach ((string name, string value) in Environment)
        {
            merged[name] = value;
        }

        return WithEnvironment(merged);
    }

    public bool Equals(ServerSpec? other) =>
        other is not null
        && string.Equals(Command, other.Command, StringComparison.Ordinal)
        && Arguments.SequenceEqual(other.Arguments, StringComparer.Ordinal)
        && Environment.Count == other.Environment.Count
        && Environment.All(
            pair => other.Environment.TryGetValue(pair.Key, out string? value)
                && string.Equals(pair.Value, value, StringComparison.Ordinal));

    public override bool Equals(object? obj) => Equals(obj as ServerSpec);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Command, StringComparer.Ordinal);
        foreach (string argument in Arguments)
        {
            hash.Add(argument, StringComparer.Ordinal);
        }

        foreach ((string name, string value) in Environment)
        {
            hash.Add(name, StringComparer.Ordinal);
            hash.Add(value, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }
}

internal enum ConfigScope
{
    User,
    Project,
}

internal enum ConfigAction
{
    Added,
    Replaced,
    Removed,
    Unchanged,
}

internal sealed record ConfigEdit(byte[] Bytes, ConfigAction Action);
