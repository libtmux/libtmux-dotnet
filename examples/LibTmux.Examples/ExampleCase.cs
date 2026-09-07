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

    /// <summary>Runs the example against a tmux server of its own.</summary>
    /// <param name="cancellationToken">Cancels the example and its tmux commands.</param>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await using ExampleNamespace world = await ExampleNamespace.EnterAsync(
            Id,
            cancellationToken);

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
