using System.Runtime.Versioning;
using LibTmux.Mcp;
using Microsoft.Extensions.DependencyInjection;

namespace LibTmux.Examples.Snippets;

/// <summary>Driving tmux the way the MCP server does, from your own code.</summary>
/// <remarks>
/// The server ships as a .NET tool, but its tools are ordinary classes. An
/// application that already has an assistant in it can host them beside its
/// own rather than launching a second process.
/// </remarks>
[UnsupportedOSPlatform("windows")]
public static class Mcp
{
    /// <summary>Runs a command and reads the status the shell actually returned.</summary>
    [Example("Run a command and get its real exit status")]
    public static async Task RunAndReadExitStatus(Server server, CancellationToken ct)
    {
        Session session = await server.CreateSessionAsync(
            new NewSessionRequest(name: "mcp-example"),
            ct);
        Pane pane = session.ActivePane!;

        #region RunAndReadExitStatus
        await using WriteTools tools = McpTools.Writing(server);

        RunResult result = await tools.RunAsync(
            "test -f /etc/hostname && echo present",
            pane.Id.ToString(),
            timeoutSeconds: 20,
            cancellationToken: ct);

        // The status comes from the shell, not from reading the screen, so a
        // command that prints nothing still says what it did.
        Console.WriteLine($"exit {result.ExitStatus}, timed out: {result.TimedOut}");
        #endregion

        Console.WriteLine(string.Join('\n', result.Output.Lines));
    }

    /// <summary>Reads only what a pane printed since the last look.</summary>
    [Example("Read only what is new since last time")]
    public static async Task ReadOnlyWhatIsNew(Server server, CancellationToken ct)
    {
        Session session = await server.CreateSessionAsync(
            new NewSessionRequest(name: "mcp-tail"),
            ct);
        Pane pane = session.ActivePane!;

        #region ReadOnlyWhatIsNew
        ReadTools reading = McpTools.Reading(server);
        string paneId = pane.Id.ToString();

        // A first call establishes a position and returns nothing, so watching
        // a pane never starts by paying for a screenful nobody asked for.
        TailResult first = await reading.TailPaneAsync(paneId, cancellationToken: ct);

        await reading.WaitForTextAsync(
            paneId,
            patterns: null,
            timeoutSeconds: 5,
            cancellationToken: ct);

        TailResult next = await reading.TailPaneAsync(paneId, first.Cursor, cancellationToken: ct);
        Console.WriteLine($"{next.Content.Lines.Count} new lines");
        #endregion

        Console.WriteLine(next.Cursor.Length > 0 ? "cursor issued" : "no cursor");
    }

    /// <summary>Keeps a long answer inside a budget without hiding the loss.</summary>
    [Example("Keep the newest lines and report what was dropped")]
    public static async Task KeepTheNewestLines(Server server, CancellationToken ct)
    {
        Session session = await server.CreateSessionAsync(
            new NewSessionRequest(name: "mcp-budget"),
            ct);
        Pane pane = session.ActivePane!;

        await using WriteTools writing = McpTools.Writing(server);
        await writing.RunAsync(
            "seq 1 200",
            pane.Id.ToString(),
            timeoutSeconds: 20,
            cancellationToken: ct);

        #region KeepTheNewestLines
        ReadTools reading = McpTools.Reading(server);

        CaptureResult captured = await reading.CapturePaneAsync(
            pane.Id.ToString(),
            includeHistory: true,
            maxLines: 5,
            cancellationToken: ct);

        // The newest line says what happened, so the budget keeps the end and
        // reports what was dropped — silence would look like nothing printed.
        Console.WriteLine(captured.Content.ToDisplayString());
        Console.WriteLine($"dropped {captured.Content.DroppedLines} earlier lines");
        #endregion
    }

    /// <summary>Offers the tmux tools from an assistant you already host.</summary>
    [Example("Host the tmux tools inside your own MCP server")]
    public static Task HostTheToolsYourself(Server server, CancellationToken ct)
    {
        _ = ct;

        #region HostTheToolsYourself
        ServiceCollection services = new();
        services.AddLogging();

        // Registers the tools, resources and prompts, and gates them on the
        // tier. Choose the transport yourself — this returns the builder.
        McpServerComposition.Add(
            services,
            new ServerPolicy { Tier = SafetyTier.ReadOnly },
            server.ConnectionOptions,
            callerPaneId: null);
        #endregion

        Console.WriteLine("composed");
        return Task.CompletedTask;
    }
}
