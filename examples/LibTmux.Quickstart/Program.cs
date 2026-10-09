using System;
using System.Runtime.Versioning;
using LibTmux;

[assembly: UnsupportedOSPlatform("windows")]

if (OperatingSystem.IsWindows())
{
    throw new PlatformNotSupportedException("This example requires Unix tmux.");
}

Server server = Server.Open();
OwnedSessionScope owned = await server.CreateOwnedSessionAsync(
    new NewSessionRequest { Name = $"build-{Guid.NewGuid():N}" });
try
{
    await owned.UseAsync(async (session, token) =>
    {
        Window window = await session.CreateWindowAsync(new NewWindowRequest { Name = "tests" }, token);
        Console.WriteLine($"Created {session.Id} / {window.Id}: {window.Name}");
    });
}
catch (Exception error) when (OwnedScope.CleanupFailure(error) is Exception cleanup)
{
    Console.Error.WriteLine($"Cleanup failed: {cleanup}");
    throw;
}
