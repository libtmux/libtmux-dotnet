using System;
using System.IO;
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

// Open records the endpoint without running tmux or reading its state.
Server endpoint = Server.Open(options);
if (endpoint.IsMaterialized)
{
    throw new InvalidOperationException("Open must not discover a server.");
}

await using OwnedServerScope owned = await Server.CreateOwnedAsync(options, token);
await using OwnedSessionScope session = await owned.Value.CreateOwnedSessionAsync(
    new NewSessionRequest { Name = "demo", Command = "/bin/cat" }, token);

// Connect discovers the live daemon and returns a new materialized handle.
Server connected = await endpoint.ConnectAsync(token);
Server sameEndpoint = await Server.ConnectAsync(options, token);
if (!connected.IsMaterialized || !sameEndpoint.IsMaterialized || endpoint.IsMaterialized)
{
    throw new InvalidOperationException("Connecting must leave the original handle unchanged.");
}
if (connected.Generation != sameEndpoint.Generation || connected.Sessions.IsCaptured)
{
    throw new InvalidOperationException("Connection discovery must not capture the hierarchy.");
}

Console.WriteLine("Open records an endpoint; Connect discovers the running server.");
Console.WriteLine("The original handle stays unmaterialized; relations remain uncaptured.");

internal sealed class TemporaryDirectory : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("libtmux-example-");

    public string FullName => _directory.FullName;

    public void Dispose() => _directory.Delete(recursive: true);
}
