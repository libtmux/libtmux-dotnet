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
            TmuxTestFactory().CreateHierarchyAsync(cancellationToken = cancellationToken)

        let! shell =
            scope.Pane
            |> Pane.split cancellationToken (SplitPaneRequest(Command = "/bin/sh"))

        let! result =
            shell
            |> Pane.run cancellationToken (TimeSpan.FromSeconds 10.) "printf 'hello\\n'"

        return List.ofSeq result.Output
    }
```
<!-- endfsharp-snippet -->

A test asserts on what this returns, in any test framework. With xUnit, bind
`let! greeting = greetingAsync cancellationToken` and check
`Assert.Equal<string list>([ "hello" ], greeting)`.

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

- Install tmux 3.2a or later. Set `LIBTMUX_TMUX` to choose a binary other than
  the first `tmux` on `PATH`, and pass it to each scope through
  `TmuxTestOptions`, as below.
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
let ciTestOptions () =
    let binary =
        Environment.GetEnvironmentVariable "LIBTMUX_TMUX"
        |> Option.ofObj
        |> Option.defaultValue "tmux"

    TmuxTestOptions(
        ServerConnectionOptions(
            SocketName = "libtmux-test-" + Guid.NewGuid().ToString("N"),
            TmuxBinaryPath = binary
        )
    )
```
<!-- endfsharp-snippet -->

Pass the options to each scope, such as
`TmuxTestFactory().CreateHierarchyAsync(ciTestOptions (), cancellationToken)`.
