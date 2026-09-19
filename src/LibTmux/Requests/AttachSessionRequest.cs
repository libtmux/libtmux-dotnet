namespace LibTmux;

/// <summary>Describes one <c>attach-session</c> invocation.</summary>
public sealed record AttachSessionRequest
{
    private readonly string[]? _clientFlags;

    /// <summary>Gets the session to attach, or null for the caller's own.</summary>
    public string? Target { get; init; }

    /// <summary>Gets whether other clients are detached.</summary>
    public bool DetachOthers { get; init; }

    /// <summary>Gets whether the client attaches read-only.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>Gets whether the client exits when the session is destroyed.</summary>
    public bool ExitOnDetach { get; init; }

    /// <summary>Gets the client flags sent with <c>-f</c>.</summary>
    public IReadOnlyList<string>? ClientFlags
    {
        get => _clientFlags;

        // The request is read again at dispatch, so a caller that kept the list
        // could otherwise change the argv after building it.
        init => _clientFlags = value is null ? null : [.. value];
    }
}
