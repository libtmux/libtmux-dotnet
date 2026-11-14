using System.Runtime.Versioning;

namespace LibTmux.Examples.Snippets;

/// <summary>The default mode: one command, one client, one materialized object.</summary>
[UnsupportedOSPlatform("windows")]
public static class OneShot
{
    /// <summary>Starts a server, builds a hierarchy, and types into the pane it made.</summary>
    [Example("Start a server, build a session and window, and type into a pane")]
    public static async Task ConnectAndBuild()
    {
        #region ConnectAndBuild
        // A tmux server of its own on the socket "quickstart". Disposing the
        // scope stops it, so nothing is left running.
        await using OwnedServerScope owned = await Server.CreateOwnedAsync(
            new ServerConnectionOptions { SocketName = "quickstart" });
        Server server = owned.Value;
        Session session = await server.CreateSessionAsync(new NewSessionRequest { Name = "build" });
        Window window = await session.CreateWindowAsync(new NewWindowRequest { Name = "tests" });
        Pane pane = (await window.GetPanesAsync())[0];

        await pane.SendTextAsync("dotnet test");
        Console.WriteLine($"{session.Name}:{window.Name}");
        #endregion
    }

    /// <summary>Creates a window and prints what tmux answered about it.</summary>
    [Example("One command, one materialized window")]
    public static async Task CreateWindow(Session session, CancellationToken ct)
    {
        #region CreateWindow
        Window window = await session.CreateWindowAsync(new NewWindowRequest { Name = "build" }, ct);
        Console.WriteLine($"{window.Id} {window.Index}:{window.Name}");
        #endregion
    }
}
