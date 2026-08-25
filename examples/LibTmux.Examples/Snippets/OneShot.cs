using System.Runtime.Versioning;

namespace LibTmux.Examples.Snippets;

/// <summary>The default mode: one command, one client, one materialized object.</summary>
[UnsupportedOSPlatform("windows")]
public static class OneShot
{
    /// <summary>Connects, builds a hierarchy, and types into the pane it made.</summary>
    [Example("Connect, build a session and window, and type into a pane")]
    public static async Task ConnectAndBuild()
    {
        #region ConnectAndBuild
        Server server = await Server.ConnectAsync();
        #endregion
        await BuildHierarchy(server);
    }

    /// <summary>Builds the hierarchy on a supplied server.</summary>
    [Example(
        "Build a session and window on a supplied server",
        RunsInDefaultSuite = false)]
    public static async Task BuildHierarchy(Server server)
    {
        ArgumentNullException.ThrowIfNull(server);
        #region BuildHierarchy
        Session session = await server.CreateSessionAsync(new NewSessionRequest(name: "build"));
        Window window = await session.CreateWindowAsync(new NewWindowRequest(name: "tests"));
        Pane pane = (await window.GetPanesAsync())[0];

        await pane.SendTextAsync("dotnet test");
        await pane.EnterAsync();
        #endregion
    }

    /// <summary>Creates a window and prints what tmux answered about it.</summary>
    [Example("One command, one materialized window")]
    public static async Task CreateWindow(Session session, CancellationToken ct)
    {
        #region CreateWindow
        Window window = await session.CreateWindowAsync(new NewWindowRequest(name: "build"), ct);
        Console.WriteLine($"{window.Id} {window.Index}:{window.Name}");
        #endregion
    }
}
