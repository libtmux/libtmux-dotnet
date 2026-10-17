using System;
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
string socketName = "csharp-entitylookups-" + Guid.NewGuid().ToString("N");
ServerConnectionOptions options = new()
{
    SocketName = socketName,
    ConfigurationFile = "/dev/null",
    TmuxBinaryPath = binary,
};

Server stoppedServer;
await using (OwnedServerScope owned =
    await Server.CreateOwnedAsync(options, token))
{
    NewSessionRequest request = new() { Name = "demo", Command = "/bin/cat" };
    await using OwnedSessionScope session =
        await owned.Value.CreateOwnedSessionAsync(request, token);
    Server server = await Server.ConnectAsync(options, token);
    Session found = await server.GetSessionAsync(session.Value.Id, token);
    Window listedWindow = (await found.GetWindowsAsync(token)).Single();
    Window window = await server.GetWindowAsync(listedWindow.Id, token);
    Pane listedPane = (await window.GetPanesAsync(token)).Single();
    Pane pane = await server.GetPaneAsync(listedPane.Id, token);
    if (found.Id != session.Value.Id || pane.Id != listedPane.Id)
    {
        throw new InvalidOperationException(
            "Typed lookup returned an unexpected entity.");
    }

    SessionId missingId = new(int.MaxValue);
    Session? missing = await server.FindSessionAsync(missingId, token);
    if (missing is not null)
    {
        throw new InvalidOperationException(
            "The absent session unexpectedly exists.");
    }
    Console.WriteLine("A successful lookup can return null.");
    try
    {
        _ = await server.GetSessionAsync(missingId, token);
        throw new InvalidOperationException(
            "GetSessionAsync must throw for an absent session.");
    }
    catch (TmuxObjectNotFoundException)
    {
        Console.WriteLine(
            "Required lookup reports TmuxObjectNotFoundException.");
    }

    using CancellationTokenSource cancelled = new();
    cancelled.Cancel();
    try
    {
        _ = await server.FindSessionAsync(session.Value.Id, cancelled.Token);
        throw new InvalidOperationException("Cancelled lookup must throw.");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("Cancellation remains OperationCanceledException.");
    }
    try
    {
        _ = await window.FindPaneAsync(" ", token);
        throw new InvalidOperationException(
            "A blank pane target must be rejected.");
    }
    catch (ArgumentException)
    {
        Console.WriteLine("A blank pane target remains ArgumentException.");
    }
    stoppedServer = server;
}

try
{
    SessionId absent = new(int.MaxValue);
    _ = await stoppedServer.FindSessionAsync(absent, token);
    throw new InvalidOperationException("A failed read must not become null.");
}
catch (LibTmuxException)
{
    Console.WriteLine("A stopped server remains a LibTmuxException.");
}
