using System.Runtime.Versioning;

namespace LibTmux.Examples.Snippets;

/// <summary>The default mode: one command, one client, one materialized object.</summary>
[UnsupportedOSPlatform("windows")]
public static class OneShot
{
    /// <summary>Creates a session and window, then removes the session at scope exit.</summary>
    [Example("Create a session and window with scoped cleanup")]
    public static async Task ConnectAndBuild()
    {
        #region ConnectAndBuild
        Server server = Server.Open();
        OwnedSessionScope owned = await server.CreateOwnedSessionAsync(
            new NewSessionRequest { Name = $"build-{Guid.NewGuid():N}" });
        await owned.UseAsync(async (session, token) =>
        {
            Window window = await session.CreateWindowAsync(new NewWindowRequest { Name = "tests" }, token);
            Console.WriteLine($"Created {session.Id} / {window.Id}: {window.Name}");
        });
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
