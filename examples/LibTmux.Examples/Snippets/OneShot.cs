using System.Runtime.Versioning;

namespace LibTmux.Examples.Snippets;

/// <summary>
/// The default mode: one command, one client, one materialized object.
/// </summary>
[UnsupportedOSPlatform("windows")]
public static class OneShot
{
    /// <summary>
    /// Connects, builds a hierarchy, and types into the pane it made.
    /// </summary>
    [Example("Connect, build a session and window, and type into a pane")]
    public static async Task ConnectAndBuild()
    {
        #region ConnectAndBuild
        // Requires a tmux server already listening on this socket:
        // ConnectAsync() discovers one, it never starts one. With nothing
        // running yet, call Server.CreateOwnedAsync() instead.
        Server server = await Server.ConnectAsync();
        NewSessionRequest sessionRequest = new() { Name = "build" };
        Session session = await server.CreateSessionAsync(sessionRequest);
        NewWindowRequest windowRequest = new() { Name = "tests" };
        Window window = await session.CreateWindowAsync(windowRequest);
        Pane pane = (await window.GetPanesAsync())[0];

        await pane.SendTextAsync("dotnet test");
        #endregion
    }

    /// <summary>
    /// Creates a window and prints what tmux answered about it.
    /// </summary>
    [Example("One command, one materialized window")]
    public static async Task CreateWindow(Session session, CancellationToken ct)
    {
        #region CreateWindow
        NewWindowRequest request = new() { Name = "build" };
        Window window = await session.CreateWindowAsync(request, ct);
        Console.WriteLine($"{window.Id} {window.Index}:{window.Name}");
        #endregion
    }
}
