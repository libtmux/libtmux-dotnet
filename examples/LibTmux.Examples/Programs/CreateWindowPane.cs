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
string socketName = "csharp-createwindowpane-" + Guid.NewGuid().ToString("N");
ServerConnectionOptions options = new()
{
    SocketName = socketName,
    ConfigurationFile = "/dev/null",
    TmuxBinaryPath = binary,
};

// The outer server scope owns everything created directly inside this daemon.
await using OwnedServerScope owned =
    await Server.CreateOwnedAsync(options, token);
NewSessionRequest request = new()
{
    Name = "demo",
    WindowName = "shell",
    Command = "/bin/cat",
};
Session session = await owned.Value.CreateSessionAsync(request, token);
NewWindowRequest editor = new() { Name = "editor", Command = "/bin/cat" };
Window window = await session.CreateWindowAsync(editor, token);
Pane original = (await window.GetPanesAsync(token)).Single();
SplitPaneRequest split = new()
{
    Direction = PaneDirection.Right,
    Command = "/bin/cat",
};
Pane added = await original.SplitAsync(split, token);

IReadOnlyList<Window> windows = await session.GetWindowsAsync(token);
IReadOnlyList<Pane> panes = await session.GetPanesAsync(token);
if (windows.Count != 2 || panes.Count != 3 || added.Id == original.Id)
{
    throw new InvalidOperationException(
        "Expected two windows and three distinct panes.");
}

Console.WriteLine($"Created session {session.Name} and window {window.Name}.");
Console.WriteLine($"Windows: {windows.Count}; panes: {panes.Count}");
