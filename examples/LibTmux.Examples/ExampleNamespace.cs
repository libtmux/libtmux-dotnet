using System.Runtime.Versioning;
using LibTmux.Engineering;

namespace LibTmux.Examples;

/// <summary>An isolated tmux server an example connects to without naming it.</summary>
/// <remarks>
/// Disposal kills the server, so the socket must never be the one a bare tmux
/// uses: a namespace named <c>default</c> would kill the developer's own.
///
/// Both routes to a socket are moved. <c>TMUX_TMPDIR</c> places a <c>-L</c>
/// name; the temporary directory places a <c>-S</c> path, which ignores
/// <c>TMUX_TMPDIR</c>.
///
/// The environment it sets is process-wide, so namespaces cannot overlap.
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed class ExampleNamespace : IAsyncDisposable
{
    /// <summary>What every example socket is called before its own name.</summary>
    public const string SocketPrefix = "libtmux-example-";

    /// <summary>The directory every example socket lives under.</summary>
    public static readonly string SocketRoot = Path.Combine(
        WorkspaceSocketRoot.Root,
        "examples");

    /// <summary>The shortest sun_path any supported platform allows: macOS.</summary>
    /// <remarks>
    /// tmux binds <c>&lt;root&gt;/tmux-&lt;uid&gt;/&lt;name&gt;</c>; over the
    /// limit it fails at bind time complaining about the address.
    /// </remarks>
    private const int SocketPathLimit = 104;

    private static readonly string[] Variables =
    [
        "TMUX_TMPDIR",
        "TMPDIR",
        "LIBTMUX_SOCKET_NAME",
        "LIBTMUX_SOCKET_PATH",
        "LIBTMUX_MCP_COMMAND",
        "TMUX",
        "TMUX_PANE",
    ];

    private readonly string _directory;
    private readonly string _entered;
    private readonly OwnedServerScope _server;
    private readonly IReadOnlyList<(string Name, string? Value)> _restore;
    private int _disposed;

    private ExampleNamespace(
        string socketName,
        string directory,
        string entered,
        OwnedServerScope server,
        IReadOnlyList<(string Name, string? Value)> restore)
    {
        SocketName = socketName;
        _directory = directory;
        _entered = entered;
        _server = server;
        _restore = restore;
    }

    /// <summary>Gets the socket this example's server is listening on.</summary>
    public string SocketName { get; }

    /// <summary>Gets the server this example may reach.</summary>
    public Server Server => _server.Value;

    /// <summary>Gets the session waiting on that server.</summary>
    public Session Session { get; private set; } = null!;

    /// <summary>Gets that session's window.</summary>
    public Window Window { get; private set; } = null!;

    /// <summary>Gets that window's pane.</summary>
    public Pane Pane { get; private set; } = null!;

    /// <summary>Gets the scratch directory this example runs in.</summary>
    public string Directory => _directory;

    /// <summary>Opens a namespace named for the example about to run.</summary>
    /// <param name="name">The example's name, which names its socket too.</param>
    /// <param name="cancellationToken">Cancels the tmux commands.</param>
    /// <returns>The namespace, which kills its server when disposed.</returns>
    public static async Task<ExampleNamespace> EnterAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string nonce = Guid.NewGuid().ToString("N")[..8];
        string socketName = BuildSocketName(name, nonce);
        string directory = Path.Combine(SocketRoot, $"{name}-{nonce}");
        System.IO.Directory.CreateDirectory(directory);

        List<(string Name, string? Value)> restore =
        [
            .. Variables.Select(
                variable => (variable, Environment.GetEnvironmentVariable(variable))),
        ];

        // TMUX and TMUX_PANE are cleared, not moved: inherited, they point a
        // client at the server the developer is sitting in.
        Environment.SetEnvironmentVariable("TMUX_TMPDIR", SocketRoot);
        Environment.SetEnvironmentVariable("TMPDIR", directory);
        Environment.SetEnvironmentVariable("LIBTMUX_SOCKET_NAME", socketName);
        Environment.SetEnvironmentVariable("LIBTMUX_SOCKET_PATH", null);
        Environment.SetEnvironmentVariable(
            "LIBTMUX_MCP_COMMAND",
            Path.Combine(
                AppContext.BaseDirectory,
                OperatingSystem.IsWindows() ? "LibTmux.Mcp.exe" : "LibTmux.Mcp"));
        Environment.SetEnvironmentVariable("TMUX", null);
        Environment.SetEnvironmentVariable("TMUX_PANE", null);

        // Panes inherit the directory the server started in, and examples type
        // build commands.
        string entered = Environment.CurrentDirectory;
        Environment.CurrentDirectory = directory;

        try
        {
            OwnedServerScope server = await Server.CreateOwnedAsync(
                new ServerConnectionOptions(
                    tmuxBinaryPath: Environment.GetEnvironmentVariable("LIBTMUX_TMUX")
                        ?? "tmux",
                    socketName: socketName,
                    childEnvironment: new Dictionary<string, string?>
                    {
                        ["TMUX_TMPDIR"] = SocketRoot,
                    }),
                cancellationToken: cancellationToken);

            if (FindSocket(socketName) is null)
            {
                throw new InvalidOperationException(
                    $"The tmux server for example '{name}' did not start on a socket "
                    + $"named {socketName} under {SocketRoot}. Refusing to take ownership "
                    + "of a server that turned up somewhere else, and leaving it running.");
            }

            ExampleNamespace world = new(socketName, directory, entered, server, restore);
            await world.PopulateAsync(cancellationToken);
            return world;
        }
        catch
        {
            Environment.CurrentDirectory = entered;
            Restore(restore);
            TryDelete(directory);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await _server.DisposeAsync();
        }
        finally
        {
            Environment.CurrentDirectory = _entered;
            Restore(_restore);

            // The socket file outlives the server that made it.
            if (FindSocket(SocketName) is string socket)
            {
                TryDeleteFile(socket);
            }

            TryDelete(_directory);
        }
    }

    /// <summary>Returns the path of the named socket, or null if it is absent.</summary>
    public static string? FindSocket(string socketName)
    {
        if (!System.IO.Directory.Exists(SocketRoot))
        {
            return null;
        }

        return System.IO.Directory
            .EnumerateDirectories(SocketRoot, "tmux-*")
            .Select(directory => Path.Combine(directory, socketName))
            .FirstOrDefault(File.Exists);
    }

    private static string BuildSocketName(string name, string nonce)
    {
        // Budgets for the widest uid rather than asking the kernel for this one.
        string prefix = Path.Combine(SocketRoot, "tmux-4294967295") + Path.DirectorySeparatorChar;
        int room = SocketPathLimit - prefix.Length - SocketPrefix.Length - nonce.Length - 1;
        if (room < 1)
        {
            throw new InvalidOperationException(
                $"There is no room for an example socket name under {SocketRoot}: a Unix "
                + $"socket path may be {SocketPathLimit} bytes and the root alone is "
                + $"{prefix.Length}.");
        }

        // The name gives way, never the nonce: the nonce is what keeps
        // concurrent runs apart.
        string trimmed = name.Length <= room ? name : name[..room];
        return $"{SocketPrefix}{trimmed}-{nonce}";
    }

    private async Task PopulateAsync(CancellationToken cancellationToken)
    {
        // Control mode attaches, so it needs a session to attach to.
        Session = await Server.CreateSessionAsync(
            new NewSessionRequest(name: "example"),
            cancellationToken);
        Window = (await Session.GetWindowsAsync(cancellationToken))[0];
        Pane = (await Window.GetPanesAsync(cancellationToken))[0];
    }

    private static void Restore(IReadOnlyList<(string Name, string? Value)> restore)
    {
        foreach ((string name, string? value) in restore)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A descriptor the kernel has not released yet is not a failure.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
