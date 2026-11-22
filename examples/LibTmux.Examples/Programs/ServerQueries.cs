using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using LibTmux;
using System.Linq.Expressions;
using LibTmux.Query;

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
// tmux reports the shell that starts a pane until the shell has become cat. Echo goes off before the
// pane prints ready, so the first line cat prints back is the proof that cat is reading.
const string command = "stty -echo; printf 'ready\\n'; exec /bin/cat";
await using OwnedSessionScope demo = await owned.Value.CreateOwnedSessionAsync(
    new NewSessionRequest { Name = "demo", WindowName = "shell", Command = command }, token);
await using OwnedSessionScope worker = await owned.Value.CreateOwnedSessionAsync(
    new NewSessionRequest { Name = "worker", WindowName = "jobs", Command = "/bin/cat" }, token);
Server server = await Server.ConnectAsync(options, token);

Window demoWindow = (await demo.Value.GetWindowsAsync(token)).Single();
Pane demoPane = (await demoWindow.GetPanesAsync(token)).Single();
PaneWaitResult ready = await demoPane.WaitForTextAsync(
    PaneWaitRequest.FromTextPatterns(["ready"], simpleMatch: true)
        with
    { Timeout = TimeSpan.FromSeconds(5) },
    token);
if (ready.Outcome is not (PaneWaitOutcome.Matched or PaneWaitOutcome.PresentAtEntry))
{
    throw new InvalidOperationException($"The demo pane never signalled that it was ready: {ready.Outcome}.");
}

await demoPane.SendTextAsync("started", cancellationToken: token);
PaneWaitResult started = await demoPane.WaitForTextAsync(
    new PaneWaitRequest
    {
        Patterns = [new Regex("^started$", RegexOptions.CultureInvariant)],
        Timeout = TimeSpan.FromSeconds(5),
    },
    token);
if (started.Outcome is not (PaneWaitOutcome.Matched or PaneWaitOutcome.PresentAtEntry))
{
    throw new InvalidOperationException($"The pane never echoed through cat: {started.Outcome}.");
}

IReadOnlyList<Session> sessions = await server.GetSessionsAsync(token);

// LINQ and portable predicates operate on these rows; neither reads tmux.
Session native = sessions.Where(session => session.Name == "demo").Single();
Expression<Func<Session, bool>> predicate = session => session.Name == "demo";
QueryDocument document = QueryExtensions.Translate(predicate);
Session expressionMatch = sessions.Matching(predicate).Single();
Session documentMatch = sessions.Matching(document).Single();
Session cancellableMatch = sessions.Matching(document, token).Single();
if (new[] { native, expressionMatch, documentMatch, cancellableMatch }
    .Any(session => session.Id != demo.Value.Id))
{
    throw new InvalidOperationException("The native and portable predicates selected different sessions.");
}

// A predicate that traverses children requires an explicitly captured hierarchy.
Server captured = await server.CaptureSnapshotAsync(SnapshotDepth.Panes, token);
Session parent = captured.Sessions.Matching(session =>
    session.Windows.Any(window => window.Name == "shell" && window.Panes.Any(pane => pane.CurrentCommand == "cat"))).Single();
if (parent.Id != demo.Value.Id)
{
    throw new InvalidOperationException("The hierarchy predicate selected an unexpected parent.");
}

using CancellationTokenSource cancelled = new();
cancelled.Cancel();
try
{
    _ = sessions.Matching(document, cancelled.Token);
    throw new InvalidOperationException("Cancelled filtering must throw.");
}
catch (OperationCanceledException)
{
    Console.WriteLine("Cancelled filtering remains OperationCanceledException.");
}
Console.WriteLine($"LINQ, expression and document filters: {native.Name}; captured parent: {parent.Name}");

internal sealed class TemporaryDirectory : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("libtmux-example-");

    public string FullName => _directory.FullName;

    public void Dispose() => _directory.Delete(recursive: true);
}
