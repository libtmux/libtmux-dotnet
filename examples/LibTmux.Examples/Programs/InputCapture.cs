using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using LibTmux;

if (OperatingSystem.IsWindows())
{
    throw new PlatformNotSupportedException("This example requires tmux on Linux or macOS.");
}

using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
CancellationToken token = deadline.Token;
string? testTmux = Environment.GetEnvironmentVariable("LIBTMUX_TEST_TMUX");
string? configured = !string.IsNullOrWhiteSpace(testTmux)
    ? testTmux
    : Environment.GetEnvironmentVariable("LIBTMUX_TMUX");
string binary = string.IsNullOrWhiteSpace(configured) ? "tmux" : configured;
using TemporaryDirectory socketRoot = new();
ServerConnectionOptions options = new()
{
    SocketPath = Path.Combine(socketRoot.FullName, "tmux.sock"),
    ConfigurationFile = "/dev/null",
    TmuxBinaryPath = binary,
};

await using OwnedServerScope owned = await Server.CreateOwnedAsync(options, token);
await using OwnedSessionScope session = await owned.Value.CreateOwnedSessionAsync(
    new NewSessionRequest
    {
        Name = "demo",
        Command = "env PS1='ready> ' /bin/sh",
    },
    token);
Server server = await Server.ConnectAsync(options, token);
Pane pane = (await server.GetPanesAsync(token)).Single();
// The shell has to be reading before anything is typed, or its prompt lands in front of the output.
// PS1 rides on the command because a non-interactive bash default-shell drops it from the environment.
PaneWaitResult prompt = await pane.WaitForTextAsync(
    PaneWaitRequest.FromTextPatterns(["ready>"], simpleMatch: true)
        with
    { Timeout = TimeSpan.FromSeconds(5) },
    token);
if (prompt.Outcome is not (PaneWaitOutcome.Matched or PaneWaitOutcome.PresentAtEntry))
{
    throw new InvalidOperationException($"The shell never showed its prompt: {prompt.Outcome}.");
}

const string marker = "api capture ready";
await pane.SendKeysAsync(
    new SendKeysRequest { Text = $"printf '%s\\n' '{marker}'", Literal = true, Enter = false }, token);
await pane.SendKeysAsync(new SendKeysRequest { Text = "Enter", Enter = false }, token);
// The echoed command contains the marker, so only a whole line counts as the output.
PaneWaitResult printed = await pane.WaitForTextAsync(
    new PaneWaitRequest
    {
        Patterns = [new Regex($"^{Regex.Escape(marker)}$", RegexOptions.CultureInvariant)],
        Timeout = TimeSpan.FromSeconds(5),
    },
    token);
if (printed.Outcome is not (PaneWaitOutcome.Matched or PaneWaitOutcome.PresentAtEntry))
{
    throw new InvalidOperationException($"The pane never printed the marker: {printed.Outcome}.");
}

IReadOnlyList<string> lines = await pane.CaptureAsync(
    new CapturePaneRequest { JoinWrappedLines = true }, token);
// Joining wrapped lines preserves terminal padding after the output.
if (!lines.Any(line => string.Equals(line.TrimEnd(), marker, StringComparison.Ordinal)))
{
    throw new InvalidOperationException("Capture did not contain the complete output line.");
}
Console.WriteLine(marker);

internal sealed class TemporaryDirectory : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("libtmux-example-");

    public string FullName => _directory.FullName;

    public void Dispose() => _directory.Delete(recursive: true);
}
