using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LibTmux;
using System.Linq.Expressions;
using LibTmux.Query;

if (OperatingSystem.IsWindows())
{
    throw new PlatformNotSupportedException(
        "This example requires tmux on Linux or macOS.");
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

await using OwnedServerScope owned =
    await Server.CreateOwnedAsync(options, token);
NewSessionRequest demoRequest = new()
{
    Name = "demo",
    WindowName = "shell",
    Command = "/bin/cat",
};
await using OwnedSessionScope demo =
    await owned.Value.CreateOwnedSessionAsync(demoRequest, token);
NewSessionRequest workerRequest = new()
{
    Name = "worker",
    WindowName = "jobs",
    Command = "/bin/cat",
};
await using OwnedSessionScope worker =
    await owned.Value.CreateOwnedSessionAsync(workerRequest, token);
Server server = await Server.ConnectAsync(options, token);
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
    throw new InvalidOperationException(
        "The native and portable predicates selected different sessions.");
}

// A predicate that traverses children needs an explicitly captured hierarchy.
Server captured =
    await server.CaptureSnapshotAsync(SnapshotDepth.Panes, token);
Expression<Func<Session, bool>> runsCat = session => session.Windows.Any(
    window => window.Name == "shell"
        && window.Panes.Any(pane => pane.CurrentCommand == "cat"));
Session parent = captured.Sessions.Matching(runsCat).Single();
if (parent.Id != demo.Value.Id)
{
    throw new InvalidOperationException(
        "The hierarchy predicate selected an unexpected parent.");
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
    Console.WriteLine(
        "Cancelled filtering remains OperationCanceledException.");
}
Console.WriteLine(
    $"LINQ, expression and document filters: {native.Name}; "
    + $"captured parent: {parent.Name}");
