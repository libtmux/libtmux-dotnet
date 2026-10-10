using System;
using LibTmux;

if (OperatingSystem.IsWindows())
{
    throw new PlatformNotSupportedException("This example requires Unix tmux.");
}

Server server = await Server.EnsureAsync();
FoundOrCreated<Session> session = await server.FindOrCreateSessionAsync(
    "libtmux-dotnet-quickstart", new NewSessionRequest { WindowName = "work" });
FoundOrCreated<Window> window = await session.Value.FindOrCreateWindowAsync("tests");
Console.WriteLine($"Workspace ready: {session.Value.Name} / {window.Value.Name}");
