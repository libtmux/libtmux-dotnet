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
string socketName = "csharp-inputcapture-" + Guid.NewGuid().ToString("N");
ServerConnectionOptions options = new()
{
    SocketName = socketName,
    ConfigurationFile = "/dev/null",
    TmuxBinaryPath = binary,
};

await using OwnedServerScope owned =
    await Server.CreateOwnedAsync(options, token);
NewSessionRequest request = new() { Name = "demo", Command = "/bin/sh" };
await using OwnedSessionScope session =
    await owned.Value.CreateOwnedSessionAsync(request, token);
Server server = await Server.ConnectAsync(options, token);
Pane pane = (await server.GetPanesAsync(token)).Single();
string channel = "capture-" + Guid.NewGuid().ToString("N");
await using TmuxWaitChannel wait = server.OpenWaitChannel(channel);
const string marker = "api capture ready";
string signal = $"{QuoteShell(binary)} -L {QuoteShell(socketName)} "
    + $"wait-for -S {QuoteShell(channel)}";
string command = $"printf '%s\\n' {QuoteShell(marker)}; {signal}";

SendKeysRequest typed = new() { Text = command, Literal = true, Enter = false };
await pane.SendKeysAsync(typed, token);
SendKeysRequest submit = new() { Text = "Enter", Enter = false };
await pane.SendKeysAsync(submit, token);
if (!await wait.WaitAsync(TimeSpan.FromSeconds(5), token))
{
    throw new InvalidOperationException(
        "The pane did not signal that its output was ready.");
}
IReadOnlyList<string> lines = await pane.CaptureAsync(
    new CapturePaneRequest { JoinWrappedLines = true }, token);
// Joining wrapped lines preserves terminal padding after the output.
if (!lines.Any(line => line.TrimEnd() == marker))
{
    throw new InvalidOperationException(
        "Capture did not contain the complete output line.");
}
Console.WriteLine(marker);

static string QuoteShell(string value) =>
    "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
