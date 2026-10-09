using System.Runtime.Versioning;

namespace LibTmux.Examples.Snippets;

/// <summary>Demonstrates explicit remote ownership, bounded discovery and created versus reused handles.</summary>
[UnsupportedOSPlatform("windows")]
public static class Lifecycle
{
    /// <summary>Adopts a session already created through a borrowed server handle.</summary>
    [Example("Adopt a session by immutable identity")]
    public static async Task AdoptExisting()
    {
        #region AdoptExisting
        Server server = Server.Open();
        Session existing = await server.CreateSessionAsync(new NewSessionRequest { Name = $"adopt-{Guid.NewGuid():N}" });
        OwnedSessionScope owner = await existing.AdoptAsync();
        try
        {
            await owner.UseAsync(async (session, token) =>
            {
                await session.RenameAsync("renamed-" + Guid.NewGuid().ToString("N"), token);
                Console.WriteLine($"Cleanup retains session ID {session.Id}.");
            });
        }
        catch (Exception error)
        {
            if (OwnedScope.CleanupFailure(error) is Exception cleanup)
            {
                Console.Error.WriteLine($"Cleanup failed: {cleanup.Message}");
            }
            throw;
        }
        #endregion
    }

    /// <summary>Creates a temporary hierarchy and reports the result of a second exact match.</summary>
    [Example("Find or create a named hierarchy")]
    public static async Task FindOrCreateHierarchy()
    {
        #region FindOrCreateHierarchy
        Server server = Server.Open();
        string name = "build-" + Guid.NewGuid().ToString("N");
        await using FoundOrCreated<Session> session = await server.FindOrCreateSessionAsync(name);
        await using FoundOrCreated<Window> window = await session.Value.FindOrCreateWindowAsync("tests");
        await using FoundOrCreated<Pane> pane = await window.Value.FindOrCreatePaneAsync("application/test-runner");
        await using FoundOrCreated<Pane> reused = await window.Value.FindOrCreatePaneAsync("application/test-runner");
        Console.WriteLine($"Created: {pane.Created}; reused: {!reused.Created}; borrowed: {reused.Owner is null}.");
        #endregion
    }

    /// <summary>Inspects configured socket directories within entry, probe and time bounds.</summary>
    [Example("Discover configured socket directories")]
    public static async Task DiscoverServers()
    {
        #region DiscoverServers
        ServerDiscoveryResult discovery = await Server.DiscoverAsync(new ServerDiscoveryOptions
        {
            MaximumEntries = 64,
            MaximumProbes = 16,
            Timeout = TimeSpan.FromSeconds(2),
        });
        foreach (DiscoveredServer found in discovery.Servers)
        {
            Console.WriteLine($"{found.SocketPath}: {found.Server.Generation}");
        }
        foreach (ServerDiscoveryDiagnostic diagnostic in discovery.Diagnostics)
        {
            Console.WriteLine($"{diagnostic.Kind}: {diagnostic.Path}: {diagnostic.Message}");
        }
        Console.WriteLine($"Truncated: {discovery.Truncated}.");
        #endregion
    }

    /// <summary>Owns a daemon at an explicitly disposable endpoint.</summary>
    [Example("Own a disposable daemon")]
    public static async Task OwnDisposableServer()
    {
        #region OwnDisposableServer
        var options = new ServerConnectionOptions { SocketName = "disposable-" + Guid.NewGuid().ToString("N") };
        OwnedServerScope owner = await Server.CreateOwnedAsync(options);
        await owner.UseAsync(async (server, token) =>
        {
            await server.CreateSessionAsync(new NewSessionRequest { Name = "work" }, token);
            Console.WriteLine($"Owned daemon: {server.Generation}.");
        });
        #endregion
    }
}
