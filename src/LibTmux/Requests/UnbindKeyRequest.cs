namespace LibTmux;

/// <summary>Describes one <c>unbind-key</c> invocation.</summary>
public sealed record UnbindKeyRequest
{
    /// <summary>Gets the key to unbind, or null when removing them all.</summary>
    public string? Key { get; init; }

    /// <summary>Gets the key table, or null for the prefix table.</summary>
    public string? KeyTable { get; init; }

    /// <summary>Gets whether every binding in the table goes.</summary>
    public bool All { get; init; }

    /// <summary>Gets whether an absent binding is passed over in silence.</summary>
    public bool Quiet { get; init; }

    /// <summary>Resolves the key argument, refusing a request that names no binding.</summary>
    /// <returns>The key tmux is given.</returns>
    /// <exception cref="ArgumentException">
    /// Neither a key nor every binding in the table is asked for.
    /// </exception>
    /// <remarks>
    /// Initializers cannot check one property against another, so the pairing
    /// is settled where the key is derived. tmux still wants a key after the
    /// all flag, and takes any one.
    /// </remarks>
    internal string ResolveKey() =>
        !All && string.IsNullOrWhiteSpace(Key)
            ? throw new ArgumentException(
                "Removing one binding needs the key it is bound to.",
                nameof(Key))
            : Key ?? "-a";
}
