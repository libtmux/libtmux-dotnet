namespace LibTmux.Examples;

/// <summary>Marks a method as a published example.</summary>
/// <remarks>
/// The method name identifies the example, and names the <c>#region</c> a
/// document publishes from it.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ExampleAttribute : Attribute
{
    /// <summary>Initializes the attribute.</summary>
    /// <param name="title">One line saying what the example shows.</param>
    public ExampleAttribute(string title) => Title = title;

    /// <summary>Gets the line saying what the example shows.</summary>
    public string Title { get; }

    /// <summary>
    /// Gets or sets whether the ordinary tmux example suite runs this example.
    /// </summary>
    public bool RunsInDefaultSuite { get; set; } = true;
}
