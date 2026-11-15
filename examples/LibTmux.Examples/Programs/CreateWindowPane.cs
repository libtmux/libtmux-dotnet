using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using LibTmux;

if (OperatingSystem.IsWindows())
{
    throw new PlatformNotSupportedException("This example requires tmux on Linux or macOS.");
}

using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
CancellationToken token = deadline.Token;
string binary = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux";
using TemporaryDirectory socketRoot = new();
ServerConnectionOptions options = new()
{
    SocketPath = Path.Combine(socketRoot.FullName, "tmux.sock"),
    ConfigurationFile = "/dev/null",
    TmuxBinaryPath = binary,
};

// The outer server scope owns everything created directly inside this daemon.
await using OwnedServerScope owned = await Server.CreateOwnedAsync(options, token);
Session session = await owned.Value.CreateSessionAsync(
    new NewSessionRequest { Name = "demo", WindowName = "shell", Command = "/bin/cat" }, token);
Window window = await session.CreateWindowAsync(
    new NewWindowRequest { Name = "editor", Command = "/bin/cat" }, token);
Pane original = (await window.GetPanesAsync(token)).Single();
Pane added = await original.SplitAsync(
    new SplitPaneRequest { Direction = PaneDirection.Right, Command = "/bin/cat" }, token);

IReadOnlyList<Window> windows = await session.GetWindowsAsync(token);
IReadOnlyList<Pane> panes = await session.GetPanesAsync(token);
if (windows.Count != 2 || panes.Count != 3 || added.Id == original.Id)
{
    throw new InvalidOperationException("Expected two windows and three distinct panes.");
}

Console.WriteLine($"Created session {session.Name} and window {window.Name}.");
Console.WriteLine($"Windows: {windows.Count}; panes: {panes.Count}");

internal sealed class TemporaryDirectory : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("libtmux-example-");

    public string FullName => _directory.FullName;

    public void Dispose() => _directory.Delete(recursive: true);
}
