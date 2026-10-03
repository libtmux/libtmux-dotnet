using System;
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
string socketName = "csharp-snapshotaccess-" + Guid.NewGuid().ToString("N");
ServerConnectionOptions options = new()
{
    SocketName = socketName,
    ConfigurationFile = "/dev/null",
    TmuxBinaryPath = binary,
};

await using OwnedServerScope owned = await Server.CreateOwnedAsync(options, token);
Server server;
await using (OwnedSessionScope session = await owned.Value.CreateOwnedSessionAsync(
    new NewSessionRequest { Name = "demo", Command = "/bin/cat" }, token))
{
    server = await Server.ConnectAsync(options, token);
    if (server.Sessions.IsCaptured)
    {
        throw new InvalidOperationException("Connecting must not capture the hierarchy.");
    }
    Console.WriteLine("Sessions have not been captured.");

    Server captured = await server.CaptureSnapshotAsync(SnapshotDepth.Panes, token);
    if (!captured.Sessions.IsCaptured)
    {
        throw new InvalidOperationException("The requested sessions were not captured.");
    }
    Session capturedSession = captured.Sessions.Single();
    if (!capturedSession.ActivePane.IsCaptured)
    {
        throw new InvalidOperationException("The active pane was not captured.");
    }
    Pane pane = capturedSession.ActivePane.Value;
    if (pane.CurrentPath is null || pane.CurrentCommand is null)
    {
        throw new InvalidOperationException("The running pane should report its path and command.");
    }
    Console.WriteLine("The captured active pane has a path and command.");
    if (server.Sessions.IsCaptured)
    {
        throw new InvalidOperationException("Capturing must return a new immutable handle.");
    }
    Console.WriteLine("The original handle is still uncaptured.");

    // Keep this private daemon alive after its only owned session is disposed.
    TmuxCommandResult result = await server.ExecuteCommandAsync(
        ["set-option", "-s", "exit-empty", "off"], token);
    if (result.ExitCode != 0)
    {
        throw new InvalidOperationException("Could not keep the empty server alive.");
    }
}
Server empty = await server.CaptureSnapshotAsync(SnapshotDepth.Sessions, token);
if (!empty.Sessions.IsCaptured || empty.Sessions.Count != 0)
{
    throw new InvalidOperationException("An empty captured relation differs from an uncaptured relation.");
}
Console.WriteLine("Captured sessions can be empty.");
