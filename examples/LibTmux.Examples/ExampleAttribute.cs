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

    /// <summary>Gets or sets the first tmux release the example runs on, such as <c>3.7</c>.</summary>
    /// <remarks>
    /// The library promises some features per release, so an example that
    /// needs one is skipped, not failed, on an older tmux.
    /// </remarks>
    public string? RequiresTmux { get; set; }

    /// <summary>Gets or sets whether the ordinary tmux example suite runs this example.</summary>
    public bool RunsInDefaultSuite { get; set; } = true;
}
