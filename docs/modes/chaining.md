# Chaining: many commands, one process

A chain hands tmux a whole sequence at once. The process cost is paid once no
matter how many commands are in it.

<!-- snippet: ManyCommandsOneProcess -->
```csharp
await server.Chain()
    .Then("new-window", "-d", "-n", "build")
    .Then("new-window", "-d", "-n", "test")
    .Then("new-window", "-d", "-n", "lint")
    .ExecuteAsync(ct);
```
<!-- endsnippet -->

Example output — the chain returns what that one invocation produced, so ask
tmux for anything you want back:

<!-- snippet: ReadBackFromAChain -->
```csharp
TmuxCommandResult result = await server.Chain()
    .Then("new-window", "-d", "-n", "build")
    .Then("display-message", "-p", "#{window_id}")
    .ExecuteAsync(ct);
```
<!-- endsnippet -->

```
@1
```

## Chaining the typed requests

The request records the one-shot methods take are the same ones a chain takes.
Every one of them answers a command, so a sequence keeps the typed arguments
rather than dropping to strings:

```csharp
await server.Chain()
    .Then(new NewWindowRequest(name: "build").ToCommand(session))
    .Then(new SendKeysRequest("make").ToCommand(pane))
    .ExecuteAsync(ct);
```

Each also runs on its own, which is the same request sent as one invocation
instead of joining a sequence:

```csharp
await new SendKeysRequest("make").ExecuteAsync(pane, ct);
```

Both take the thing the request acts on. A request says *what* to do and not
*to whom*, and the arguments tmux receives depend on what the running server
knows: which tmux version is answering, which target the object resolves to,
whether a flag exists on that build at all. Handing over the pane or session is
what lets the same record produce the right command line on 3.2a and on 3.7b.

One request answers several commands rather than one. Setting a hook's entries
is a clear then one command per entry, so it answers a list:

```csharp
IReadOnlyList<TmuxCommand> commands = new SetHooksRequest(
    "after-new-window",
    new Dictionary<int, string> { [0] = "display-message first" },
    clearExisting: true).ToCommands(server.Hooks);
```

## Building reaches nothing

A chain is a description. Holding one changes nothing on the server; only
`ExecuteAsync` does. Each `Then` returns a new chain, so a partly built one can
be shared without another caller's additions showing up in it.

## A semicolon you pass stays data

tmux separates grouped commands with `;`. A semicolon in one of *your*
arguments is a value, and it survives as one:

```csharp
await server.Chain().Then("new-window", "-d", "-n", "a;b").ExecuteAsync(ct);
```

creates one window named `a;b` rather than two commands. That distinction is
part of the transport contract in
[ADR 0001](../decisions/0001-transport-framing-bakeoff.md).

## Failure

tmux runs the commands in order and stops at the first one that fails, which is
its own behavior for a grouped run rather than anything imposed here. The chain
surfaces that as `TmuxCommandException`.

## When this is not the right mode

When you want the typed object back for each step, use
[one-shot](one-shot.md) — a chain returns the invocation's output, not one
materialized object per command. When you need to observe what tmux does on its
own, use [control mode](control-mode.md).

What each mode costs, measured for one command and for fifty, is in
[choosing a mode](matrix.md).
