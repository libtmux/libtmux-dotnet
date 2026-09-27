namespace LibTmux.FSharp.Tests

open System
open System.Collections.Generic
open System.Text
open System.Threading
open System.Threading.Tasks
open LibTmux
open LibTmux.Internal
open LibTmux.FSharp
open Xunit

module ContractTests =
    let private versionReply (arguments: string array) =
        let output = Encoding.UTF8.GetBytes("tmux 3.7\n")

        Task.FromResult(
            TmuxCommandResult(
                arguments,
                0,
                ReadOnlyMemory<byte>(output),
                ReadOnlyMemory<byte>.Empty,
                [| "tmux 3.7" |],
                [||]
            )
        )

    [<Fact>]
    let ``exactlyOne distinguishes cardinality and stops after two matches`` () =
        Assert.Equal<Result<int, CardinalityError>>(Error NoMatches, Selection.exactlyOne Seq.empty)
        Assert.Equal<Result<int, CardinalityError>>(Ok 42, Selection.exactlyOne [ 42 ])
        let mutable disposed = false

        let source =
            seq {
                try
                    yield 1
                    yield 2
                    failwith "A third element must never be requested."
                finally
                    disposed <- true
            }

        Assert.Equal<Result<int, CardinalityError>>(Error MultipleMatches, Selection.exactlyOne source)
        Assert.True disposed

    [<Fact>]
    let ``unread relations remain explicit without contacting tmux`` () =
        let server =
            LibTmux.Server.Open(ServerConnectionOptions(SocketName = "fsharp-no-io"))

        match Snapshot.relation server.Panes with
        | Uncaptured(relation, depth) ->
            Assert.Equal("panes", relation)
            Assert.Equal(SnapshotDepth.Server, depth)
        | Captured _ -> failwith "An unqueried endpoint cannot contain captured panes."

    [<Fact>]
    let ``optional lookup forwards cancellation and preserves transport diagnostics`` () =
        task {
            use source = new CancellationTokenSource()
            let token = source.Token

            let failure =
                TmuxOperationCanceledException("tmux client canceled", token, true, 117)

            let mutable observedToken = CancellationToken.None
            let mutable observedCommand = Array.empty<string>

            let connection =
                TmuxConnection(
                    ServerConnectionOptions(SocketName = "fsharp-lookup-cancellation"),
                    Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>>
                        (fun request cancellationToken ->
                            let arguments = request.LogicalArguments |> Seq.toArray

                            if arguments = [| "-V" |] then
                                versionReply arguments
                            elif arguments |> Array.contains "%7" then
                                observedCommand <- arguments
                                observedToken <- cancellationToken
                                Task.FromException<TmuxCommandResult>(failure)
                            else
                                Task.FromException<TmuxCommandResult>(
                                    InvalidOperationException("Unexpected tmux command.")
                                ))
                )

            let server = LibTmux.Server(connection, ServerGeneration(17, 29), "tmux 3.7")

            let! observed =
                Assert.ThrowsAsync<TmuxOperationCanceledException>(fun () ->
                    Server.tryFindPane token (PaneId 7) server :> Task)

            let lookup = [| "display-message"; "-p"; "-t"; "%7" |]

            Assert.True(
                observedCommand |> Array.windowed lookup.Length |> Array.exists ((=) lookup),
                "The exception must come from the pane lookup."
            )

            Assert.Equal(token, observedToken)
            Assert.Same(failure, observed)
            Assert.True(observed.CommandMayHaveExecuted)
            Assert.Equal(117, observed.ClientProcessId)
        }

    [<Fact>]
    let ``send keys keeps post-dispatch cancellation token and diagnostics`` () =
        task {
            use source = new CancellationTokenSource()
            let token = source.Token

            let failure =
                TmuxOperationCanceledException("tmux client canceled", token, true, 117)

            let dispatched =
                TaskCompletionSource<string array * CancellationToken>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                )

            let reply =
                TaskCompletionSource<TmuxCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously)

            let connection =
                TmuxConnection(
                    ServerConnectionOptions(SocketName = "fsharp-send-cancellation"),
                    Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>>
                        (fun request cancellationToken ->
                            let arguments = request.LogicalArguments |> Seq.toArray

                            if arguments = [| "-V" |] then
                                versionReply arguments
                            elif arguments |> Array.contains "send-keys" then
                                dispatched.SetResult((arguments, cancellationToken))
                                reply.Task
                            else
                                Task.FromException<TmuxCommandResult>(
                                    InvalidOperationException("Unexpected tmux command.")
                                ))
                )

            let generation = ServerGeneration(17, 29)
            let server = LibTmux.Server(connection, generation, "tmux 3.7")

            let pane =
                LibTmux.Pane(server, connection, generation, PaneId 1, Dictionary<string, string>())

            let pending =
                pane
                |> Pane.sendKeys token (SendKeysRequest(Text = "payload", Literal = true, Enter = false))

            try
                let! (arguments, forwardedToken) =
                    dispatched.Task.WaitAsync(TimeSpan.FromSeconds(1.))

                Assert.Contains("send-keys", arguments)
                Assert.Contains("payload", arguments)
                Assert.Equal(token, forwardedToken)
                Assert.False(pending.IsCompleted)

                source.Cancel()
                reply.SetException(failure)

                let! observed = Assert.ThrowsAsync<TmuxOperationCanceledException>(fun () -> pending)
                Assert.Same(failure, observed)
                Assert.Equal(token, observed.CancellationToken)
                Assert.True(observed.CommandMayHaveExecuted)
                Assert.Equal(117, observed.ClientProcessId)
            finally
                reply.TrySetException(failure) |> ignore
        }

    [<Fact>]
    let ``send keys rejects cancellation before mutation dispatch`` () =
        task {
            use source = new CancellationTokenSource()
            let token = source.Token
            let mutable mutations = 0

            let connection =
                TmuxConnection(
                    ServerConnectionOptions(SocketName = "fsharp-send-precancelled"),
                    Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>>
                        (fun request cancellationToken ->
                            let arguments = request.LogicalArguments |> Seq.toArray

                            if arguments = [| "-V" |] then
                                versionReply arguments
                            elif arguments |> Array.contains "send-keys" then
                                cancellationToken.ThrowIfCancellationRequested()
                                mutations <- mutations + 1

                                Task.FromException<TmuxCommandResult>(
                                    InvalidOperationException("A canceled request was dispatched.")
                                )
                            else
                                Task.FromException<TmuxCommandResult>(
                                    InvalidOperationException("Unexpected tmux command.")
                                ))
                )

            let generation = ServerGeneration(17, 29)
            let server = LibTmux.Server(connection, generation, "tmux 3.7")

            let pane =
                LibTmux.Pane(server, connection, generation, PaneId 1, Dictionary<string, string>())

            source.Cancel()

            let! observed =
                Assert.ThrowsAnyAsync<OperationCanceledException>(fun () ->
                    pane
                    |> Pane.sendKeys token (SendKeysRequest(Text = "payload", Literal = true, Enter = false)))

            Assert.Equal(token, observed.CancellationToken)
            Assert.Equal(0, mutations)
        }
