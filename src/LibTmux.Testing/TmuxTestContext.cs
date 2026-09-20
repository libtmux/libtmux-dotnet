using System.Runtime.Versioning;

namespace LibTmux.Testing;

/// <summary>A tmux server a test owns, and the environment it runs in.</summary>
/// <remarks>
/// Disposing kills the server, so a test that forgets a session still leaves
/// nothing behind. The environment is reported so a test can spawn its own
/// processes the same way tmux was spawned.
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed class TmuxTestContext : IAsyncDisposable
{
    private readonly TemporaryServerScope _scope;

    internal TmuxTestContext(TemporaryServerScope scope, TestEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(environment);
        _scope = scope;
        Environment = environment;
    }

    /// <summary>Gets the server this test owns.</summary>
    public Server Server => _scope.Server;

    /// <summary>Gets the directory and variables the server was started with.</summary>
    public TestEnvironment Environment { get; }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _scope.DisposeAsync();
}
