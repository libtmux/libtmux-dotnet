namespace LibTmux.UnitTests;

/// <summary>Temporary directories for tests that bind a Unix domain socket.</summary>
/// <remarks>
/// A Unix domain socket path is capped at 104 bytes, and macOS spends about
/// half of that on <see cref="Path.GetTempPath" /> alone — its temporary
/// directory is a per-boot path under <c>/var/folders</c>. A directory name
/// carrying a whole GUID leaves nothing for the socket file, so these tests
/// failed there and only there. Eight hex digits still separate two runs.
/// </remarks>
internal static class SocketRoots
{
    /// <summary>Names a directory short enough to hold a socket.</summary>
    /// <param name="label">A few characters naming the test.</param>
    /// <returns>The path, which the caller creates.</returns>
    internal static string Reserve(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        string root = Path.Combine(Path.GetTempPath(), $"lt-{label}-{Short()}");

        // Fail here, where the budget is visible, rather than inside a bind
        // that reports only a length and not what spent it.
        if (root.Length > MaximumSocketPath - SocketFileAllowance)
        {
            throw new InvalidOperationException(
                $"'{root}' leaves under {SocketFileAllowance} bytes of the "
                + $"{MaximumSocketPath}-byte socket path budget for a socket.");
        }

        return root;
    }

    /// <summary>Names a socket short enough to sit under a reserved root.</summary>
    /// <param name="label">A few characters naming the test.</param>
    /// <returns>The socket name.</returns>
    internal static string Name(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        return $"{label}-{Short()}";
    }

    private static string Short() => Guid.NewGuid().ToString("N")[..8];

    private const int MaximumSocketPath = 104;

    private const int SocketFileAllowance = 24;
}
