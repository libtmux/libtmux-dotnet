namespace LibTmux;

/// <summary>Describes one <c>paste-buffer</c> invocation.</summary>
public sealed record PasteBufferRequest
{
    /// <summary>Gets the buffer to paste, or null for the most recent.</summary>
    public string? Name { get; init; }

    /// <summary>Gets whether the buffer is deleted once pasted.</summary>
    public bool DeleteAfter { get; init; }

    /// <summary>Gets whether line feeds separate the lines.</summary>
    public bool UseLineFeedSeparator { get; init; }

    /// <summary>Gets whether the paste is bracketed.</summary>
    public bool Bracketed { get; init; }

    /// <summary>Gets the separator used between lines.</summary>
    public string? Separator { get; init; }

    /// <summary>Gets whether the bytes are pasted without translation.</summary>
    /// <remarks>
    /// tmux gained the flag in 3.7. Older servers already paste raw bytes, so
    /// omitting it there asks for what they already do.
    /// </remarks>
    public bool RawBytes { get; init; }
}
