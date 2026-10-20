using System;
using System.Collections.Generic;
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
string binary = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux";
string socketName = "csharp-serverqueries-" + Guid.NewGuid().ToString("N");
ServerConnectionOptions options = new()
{
    SocketName = socketName,
    ConfigurationFile = "/dev/null",
    TmuxBinaryPath = binary,
};

await using OwnedServerScope owned = await Server.CreateOwnedAsync(options, token);
// tmux reports the shell that starts a pane until the shell has become cat. The shell signals once
// echo is off, so the first line cat prints back is the proof that cat is reading.
string channel = "queries-" + Guid.NewGuid().ToString("N");
string command = "stty -echo; "
    + $"{QuoteShell(binary)} -L {QuoteShell(socketName)} wait-for -S {QuoteShell(channel)}; exec /bin/cat";
await using OwnedSessionScope demo = await owned.Value.CreateOwnedSessionAsync(
    new NewSessionRequest { Name = "demo", WindowName = "shell", Command = command }, token);
await using OwnedSessionScope worker = await owned.Value.CreateOwnedSessionAsync(
    new NewSessionRequest { Name = "worker", WindowName = "jobs", Command = "/bin/cat" }, token);
Server server = await Server.ConnectAsync(options, token);
await using TmuxWaitChannel ready = server.OpenWaitChannel(channel);
if (!await ready.WaitAsync(TimeSpan.FromSeconds(5), token))
{
    throw new InvalidOperationException("The demo pane did not signal that it was ready.");
}

Window demoWindow = (await demo.Value.GetWindowsAsync(token)).Single();
Pane demoPane = (await demoWindow.GetPanesAsync(token)).Single();
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

static string QuoteShell(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
