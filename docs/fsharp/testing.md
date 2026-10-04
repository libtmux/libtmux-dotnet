# Testing code that drives tmux

`LibTmux.Testing` gives each test its own tmux server on a private socket, and
removes it when the test ends, whether the test passed or threw. Tests run in
parallel without seeing each other's sessions, and nothing touches the tmux
you are working in.

```console
$ dotnet package add LibTmux.Testing --prerelease
```

## A scope per test

`TmuxTestFactory.CreateHierarchyAsync` starts a server and creates a session,
window and pane in it. `use!` disposes the scope when the task ends, which
kills the server. Set up what the test depends on explicitly, such as the
shell a pane runs, rather than relying on the default shell:

<!-- fsharp-snippet: TestWithScope run -->
```fsharp run
open System
open System.Threading
open LibTmux
open LibTmux.FSharp
open LibTmux.Testing

let greetingAsync (cancellationToken: CancellationToken) =
    task {
        // A private tmux server, session, window and pane, removed even if
        // the test fails.
        use! scope =
            TmuxTestFactory()
                .CreateHierarchyAsync(cancellationToken = cancellationToken)

        let! shell =
            scope.Pane
            |> Pane.split
                cancellationToken
                (SplitPaneRequest(Command = "/bin/sh"))

        let! result =
            shell
            |> Pane.run
                cancellationToken
                (TimeSpan.FromSeconds 10.)
                "printf 'hello\\n'"

        return List.ofSeq result.Output
    }
```
<!-- endfsharp-snippet -->

A test asserts on what this returns, in any test framework.

## One server for a test class

Starting a server for every test costs a few tens of milliseconds each. With
xUnit, a class fixture starts one for a test class and stops it after the
class's last test; each test splits a shell of its own, so tests that run in
any order do not read each other's output. This one runs in the library's own
F# tests:

<!-- fsharp-snippet: XunitClassFixture tested -->
```fsharp
open System
open System.Threading.Tasks
open LibTmux
open LibTmux.FSharp
open LibTmux.Testing
open Xunit

/// Starts one private tmux server for a test class, and stops it after the class's last test.
type TmuxFixture() =
    let mutable scope: TemporaryHierarchyScope option = None

    member _.Pane =
        match scope with
        | Some started -> started.Pane
        | None -> invalidOp "The tmux fixture has not started."

    interface IAsyncLifetime with
        member _.InitializeAsync() =
            ValueTask(
                task {
                    let! started =
                        TmuxTestFactory()
                            .CreateHierarchyAsync(cancellationToken = TestContext.Current.CancellationToken)

                    scope <- Some started
                }
            )

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            match scope with
            | Some started -> started.DisposeAsync()
            | None -> ValueTask.CompletedTask

// Each test splits a shell of its own from the fixture's pane, so tests
// that run in any order do not read each other's output.
type ShellTests(fixture: TmuxFixture) =
    interface IClassFixture<TmuxFixture>

    [<Fact>]
    member _.``a command run in its own shell returns what it printed``() =
        task {
            let cancellationToken = TestContext.Current.CancellationToken

            let! shell =
                fixture.Pane
                |> Pane.split cancellationToken (SplitPaneRequest(Command = "/bin/sh"))

            let! result =
                shell
                |> Pane.run cancellationToken (TimeSpan.FromSeconds 10.) "printf 'hello\\n'"

            Assert.Equal<string list>([ "hello" ], List.ofSeq result.Output)
        }
```
<!-- endfsharp-snippet -->

`CreateServerAsync`, `CreateSessionAsync` and `CreateWindowAsync` create less
when a test needs less, and `TmuxNameGenerator` hands out session and window
names that no live session is using.

## Wait for state, never sleep

A pane runs its program asynchronously, so a test that reads right after
sending keys races the program. Wait for the result instead:

- `Pane.run` returns once the command has exited, with its status and output.
- `Pane.sendAndWait` types a line and returns once the pane prints the
  expected text after it; the line's own echo does not count.
- `Pane.waitForText` and `Pane.waitUntil` return once the pane shows what the
  test expects, or report `TimedOut`.
- `Mirror.waitUntil` waits for sessions, windows and panes to reach a state,
  and raises `TmuxWaitTimeoutException` when they do not in time;
  `Mirror.tryWaitUntil` returns `None` instead.

Each returns when tmux reports the state rather than after a fixed sleep, so a
passing test finishes as soon as tmux does, and a failing one at its timeout.

## Run tests in CI

- Install tmux 3.2a or later. Scopes run the first `tmux` on `PATH`, as
  Python libtmux's pytest plugin does, so a CI job picks its tmux by putting
  that binary's directory first. To name a binary instead, pass it to each
  scope through `TmuxTestOptions`, as below.
- Keep `TMUX_TMPDIR` short, such as `/tmp/tmux-tests`: a socket path longer
  than about 100 bytes cannot be bound.
- Clear `TMUX` and `TMUX_PANE` when tests run inside a tmux pane. The scopes
  use their own sockets either way, but code under test that finds its server
  from the environment, such as `Server.FromEnvironment()`, would otherwise
  reach the outer server.

<!-- fsharp-snippet: CiTestOptions -->
```fsharp
open System
open LibTmux
open LibTmux.Testing

// Options that name a connection replace the private socket a test gets
// by default, so name a socket of the test's own as well as the binary.
let testOptionsWith (tmuxBinary: string) =
    TmuxTestOptions(
        ServerConnectionOptions(
            SocketName = "libtmux-test-" + Guid.NewGuid().ToString("N"),
            TmuxBinaryPath = tmuxBinary
        )
    )
```
<!-- endfsharp-snippet -->

Pass the options to each scope, such as
`TmuxTestFactory().CreateHierarchyAsync(testOptionsWith "/opt/tmux/bin/tmux", cancellationToken)`.
