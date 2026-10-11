using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LibTmux;

if (OperatingSystem.IsWindows())
{
    throw new PlatformNotSupportedException(
        "This example requires tmux on Linux or macOS.");
}

using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
CancellationToken token = deadline.Token;
string binary = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux";
string socketName = "csharp-serverlistings-" + Guid.NewGuid().ToString("N");
ServerConnectionOptions options = new()
{
    SocketName = socketName,
    ConfigurationFile = "/dev/null",
    TmuxBinaryPath = binary,
};

await using OwnedServerScope owned =
    await Server.CreateOwnedAsync(options, token);
string cat = "/bin/cat";
NewSessionRequest demoRequest = new() { Name = "demo", Command = cat };
await using OwnedSessionScope demo =
    await owned.Value.CreateOwnedSessionAsync(demoRequest, token);
NewSessionRequest workerRequest = new() { Name = "worker", Command = cat };
await using OwnedSessionScope worker =
    await owned.Value.CreateOwnedSessionAsync(workerRequest, token);
Server server = await Server.ConnectAsync(options, token);

IReadOnlyList<Session> sessions = await server.GetSessionsAsync(token);
IReadOnlyList<Window> windows = await server.GetWindowsAsync(token);
IReadOnlyList<Pane> panes = await server.GetPanesAsync(token);
IReadOnlyList<Client> clients = await server.GetClientsAsync(token);
if (sessions.Count != 2
    || windows.Count != 2
    || panes.Count != 2
    || clients.Count != 0)
{
    throw new InvalidOperationException(
        "Expected two detached sessions, each with one window and pane.");
}
if (sessions.Any(session => session.Windows.IsCaptured))
{
    throw new InvalidOperationException(
        "Scalar listings must not imply captured child relations.");
}

IEnumerable<Session> ordered =
    sessions.OrderBy(session => session.Name, StringComparer.Ordinal);
foreach (Session session in ordered)
{
    Console.WriteLine($"Session: {session.Name} ({session.Id})");
}
Console.WriteLine(
    $"Windows: {windows.Count}; panes: {panes.Count}; "
    + $"clients: {clients.Count}");
