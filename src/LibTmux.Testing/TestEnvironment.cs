using System.Collections.ObjectModel;

namespace LibTmux.Testing;

/// <summary>The directory and variables a test's tmux runs with.</summary>
/// <remarks>
/// A test that inherits the developer's environment inherits their tmux too:
/// <c>TMUX</c> points a new client at the server they are sitting in. Naming
/// the environment explicitly is what keeps a test off it.
/// </remarks>
public sealed record TestEnvironment
{
    private readonly ReadOnlyDictionary<string, string?> _variables;

    /// <summary>Initializes a test environment.</summary>
    /// <param name="workingDirectory">The directory tmux starts in.</param>
    /// <param name="variables">The variables to set, or null each to remove one.</param>
    public TestEnvironment(
        string workingDirectory,
        IReadOnlyDictionary<string, string?> variables)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(variables);
        WorkingDirectory = workingDirectory;
        _variables = new ReadOnlyDictionary<string, string?>(
            new Dictionary<string, string?>(variables, StringComparer.Ordinal));
    }

    /// <summary>Gets the directory tmux starts in.</summary>
    public string WorkingDirectory { get; }

    /// <summary>Gets the variables to set, with null meaning remove.</summary>
    public IReadOnlyDictionary<string, string?> Variables => _variables;

    /// <summary>Answers a copy that also sets one variable.</summary>
    /// <param name="name">The variable name.</param>
    /// <param name="value">The value to give it.</param>
    /// <returns>The copy.</returns>
    public TestEnvironment WithVariable(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        return With(name, value);
    }

    /// <summary>Answers a copy that removes one variable.</summary>
    /// <param name="name">The variable name.</param>
    /// <returns>The copy.</returns>
    /// <remarks>
    /// Removing is not the same as setting nothing: a variable set to the
    /// empty string is still one tmux passes on.
    /// </remarks>
    public TestEnvironment WithoutVariable(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return With(name, null);
    }

    private TestEnvironment With(string name, string? value)
    {
        Dictionary<string, string?> next = new(_variables, StringComparer.Ordinal)
        {
            [name] = value,
        };
        return new TestEnvironment(WorkingDirectory, next);
    }
}
