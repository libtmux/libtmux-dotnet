using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using ModelContextProtocol.Client;

namespace LibTmux.Examples;

/// <summary>One example: what it shows, where it lives, and how to run it.</summary>
/// <remarks>
/// A parameter typed <see cref="Server"/>, <see cref="Session"/>,
/// <see cref="Window"/>, <see cref="Pane"/>, <see cref="McpClient"/> or
/// <see cref="CancellationToken"/> is supplied from the namespace; any other
/// type throws.
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed class ExampleCase
{
    private ExampleCase(MethodInfo method, string title)
    {
        Method = method;
        Title = title;
    }

    /// <summary>Gets the example's name, which is its method's name.</summary>
    public string Id => Method.Name;

    /// <summary>Gets the line saying what the example shows.</summary>
    public string Title { get; }

    /// <summary>Gets the group it belongs to, which is its file's name.</summary>
    public string Topic => Method.DeclaringType!.Name;

    private MethodInfo Method { get; }

    /// <summary>Maps each example to the cross-port artifact id the arena runs it as.</summary>
    /// <remarks>
    /// One table rather than a literal at each caller. Every example
    /// <see cref="Discover"/> returns carries exactly one entry here, and the
    /// reverse holds too; <c>ArenaArtifactTests</c> checks both directions.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> ArenaArtifactsByCase { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Chaining.ManyCommandsOneProcess"] = "csharp-many-commands-one-process",
            ["Chaining.ReadBackFromAChain"] = "csharp-read-back-from-a-chain",
            ["ControlMode.NoticeDroppedEvents"] = "csharp-notice-dropped-events",
            ["ControlMode.WatchForWindowAdd"] = "csharp-watch-for-window-add",
            ["Mcp.ConnectToSelectedSurface"] = "csharp-connect-to-selected-surface",
            ["Mcp.HostTheToolsYourself"] = "csharp-host-the-tools-yourself",
            ["Mcp.KeepTheNewestLines"] = "csharp-keep-the-newest-lines",
            ["Mcp.ReadCapabilities"] = "csharp-read-capabilities",
            ["Mcp.ReadOnlyWhatIsNew"] = "csharp-read-only-what-is-new",
            ["Mcp.ReadSeveralFacts"] = "csharp-read-several-facts",
            ["Mcp.RunAndReadExitStatus"] = "csharp-run-and-read-exit-status",
            // The pre-existing cross-port id for the connect-with-no-ceremony
            // idiom; every other id below is the example's own name in kebab
            // case.
            ["OneShot.BuildHierarchy"] = "csharp-build-hierarchy",
            ["OneShot.ConnectAndBuild"] = "csharp-one-shot",
            ["OneShot.CreateWindow"] = "csharp-create-window",
            ["Tour.FilterWhatIsThere"] = "csharp-filter-what-is-there",
            ["Tour.ReactToAnEvent"] = "csharp-react-to-an-event",
            ["Tour.ReadAndWriteOptions"] = "csharp-read-and-write-options",
            ["Tour.RunACommand"] = "csharp-run-a-command",
            ["Tour.ShowHierarchy"] = "csharp-show-hierarchy",
        };

    /// <summary>Gets the cross-port artifact id the arena runs this example as.</summary>
    public string? ArenaArtifact => ArenaArtifactsByCase.GetValueOrDefault($"{Topic}.{Id}");

    /// <summary>Finds the ordinary tmux examples, in a stable order.</summary>
    /// <returns>The default-suite examples, ordered by topic and then by name.</returns>
    public static IReadOnlyList<ExampleCase> Discover() =>
    [
        .. typeof(ExampleCase).Assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Select(method => (Method: method, Example: method.GetCustomAttribute<ExampleAttribute>()))
            .Where(found => found.Example?.RunsInDefaultSuite is true)
            .Select(found => Create(found.Method, found.Example!))
            .OrderBy(example => example.Topic, StringComparer.Ordinal)
            .ThenBy(example => example.Id, StringComparer.Ordinal),
    ];

    /// <summary>Finds the example the arena should run for an artifact id.</summary>
    /// <param name="artifact">The artifact id the arena supervisor named.</param>
    /// <returns>The matching example, or null when no example claims that id.</returns>
    public static ExampleCase? FindByArenaArtifact(string artifact) =>
        Discover().FirstOrDefault(
            example => string.Equals(example.ArenaArtifact, artifact, StringComparison.Ordinal));

    /// <summary>Runs the example against a tmux server of its own.</summary>
    /// <param name="cancellationToken">Cancels the example and its tmux commands.</param>
    public async Task RunAsync(CancellationToken cancellationToken = default) =>
        await RunAsync(await ExampleNamespace.EnterAsync(Id, cancellationToken), cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Runs the example against a server the arena lent rather than one of its own.</summary>
    /// <param name="tmuxBinaryPath">The tmux executable the lending server was started with.</param>
    /// <param name="socketPath">The socket the lending server is listening on.</param>
    /// <param name="cancellationToken">Cancels the example and its tmux commands.</param>
    /// <remarks>Disposal never stops the lent server; only an owned one is stopped.</remarks>
    public async Task RunUnderArenaAsync(
        string tmuxBinaryPath,
        string socketPath,
        CancellationToken cancellationToken = default) =>
        await RunAsync(
                await ExampleNamespace.EnterArenaAsync(
                    Id,
                    tmuxBinaryPath,
                    socketPath,
                    cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);

    private async Task RunAsync(ExampleNamespace world, CancellationToken cancellationToken)
    {
        await using ExampleNamespace disposesWorld = world;

        ParameterInfo[] parameters = Method.GetParameters();
        await using ExampleMcpConnection? mcp = parameters.Any(
            parameter => parameter.ParameterType == typeof(McpClient))
                ? await ExampleMcpConnection.OpenAsync(world.Server, cancellationToken)
                : null;
        object?[] arguments = new object?[parameters.Length];
        for (int index = 0; index < parameters.Length; index++)
        {
            Type wanted = parameters[index].ParameterType;
            arguments[index] = wanted switch
            {
                _ when wanted == typeof(Server) => world.Server,
                _ when wanted == typeof(Session) => world.Session,
                _ when wanted == typeof(Window) => world.Window,
                _ when wanted == typeof(Pane) => world.Pane,
                _ when wanted == typeof(McpClient) => mcp!.Client,
                _ when wanted == typeof(CancellationToken) => cancellationToken,
                _ => throw new InvalidOperationException(
                    $"Example {Topic}.{Id} asks for a {wanted.Name}, and an example "
                    + "may ask for a Server, Session, Window, Pane, McpClient "
                    + "or CancellationToken."),
            };
        }

        try
        {
            await (Task)Method.Invoke(null, arguments)!;
        }
        catch (TargetInvocationException invocation) when (invocation.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(invocation.InnerException).Throw();
            throw;
        }
    }

    private static ExampleCase Create(MethodInfo method, ExampleAttribute example)
    {
        if (method.ReturnType != typeof(Task))
        {
            throw new InvalidOperationException(
                $"Example {method.DeclaringType!.Name}.{method.Name} must return Task.");
        }

        return new ExampleCase(method, example.Title);
    }
}
