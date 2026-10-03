namespace LibTmux.Workspace;

/// <summary>Describes one literal pane command and an optional Enter override.</summary>
public sealed record WorkspaceCommand
{
    /// <summary>Initializes a command, inheriting the current Enter state by default.</summary>
    /// <param name="text">The literal text to send to the pane.</param>
    /// <param name="enter">Whether this and later commands press Enter, or null to inherit.</param>
    public WorkspaceCommand(string text, bool? enter = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        Text = text;
        Enter = enter;
    }

    /// <summary>Gets the literal command text.</summary>
    public string Text { get; }

    /// <summary>Gets this command's Enter override, or null to inherit.</summary>
    public bool? Enter { get; }
}
