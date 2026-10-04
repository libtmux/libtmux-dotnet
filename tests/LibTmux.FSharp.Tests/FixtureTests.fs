namespace LibTmux.FSharp.Tests

// fsharp-snippet: XunitClassFixture
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
// endfsharp-snippet
