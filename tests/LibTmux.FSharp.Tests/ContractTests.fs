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

    let private readServer operation token server : Task =
        match operation with
        | "listSessions" -> Server.listSessions token server
        | "listWindows" -> Server.listWindows token server
        | "listPanes" -> Server.listPanes token server
        | "listClients" -> Server.listClients token server
        | "tryFindSession" -> Server.tryFindSession token (SessionId 7) server
        | "tryFindWindow" -> Server.tryFindWindow token (WindowId 7) server
        | "tryFindPane" -> Server.tryFindPane token (PaneId 7) server
        | "tryFindClient" -> Server.tryFindClient token "client-7" server
        | _ -> invalidArg (nameof operation) operation

    [<Theory>]
    [<InlineData("listSessions", "list-sessions", "")>]
    [<InlineData("listWindows", "list-windows", "-a")>]
    [<InlineData("listPanes", "list-panes", "-a")>]
    [<InlineData("listClients", "list-clients", "")>]
    [<InlineData("tryFindSession", "display-message", "$7")>]
    [<InlineData("tryFindWindow", "display-message", "@7")>]
    [<InlineData("tryFindPane", "display-message", "%7")>]
    [<InlineData("tryFindClient", "list-clients", "")>]
    let ``server reads forward cancellation and preserve transport diagnostics`` operation command target =
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
                            elif arguments |> Array.contains command then
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
                Assert.ThrowsAsync<TmuxOperationCanceledException>(fun () -> readServer operation token server)

            Assert.Contains(command, observedCommand)

            if target <> "" then
                Assert.Contains(target, observedCommand)

            Assert.Equal(token, observedToken)
            Assert.Same(failure, observed)
            Assert.True(observed.CommandMayHaveExecuted)
            Assert.Equal(117, observed.ClientProcessId)
        }

    [<Theory>]
    [<InlineData("tryFindSession")>]
    [<InlineData("tryFindWindow")>]
    [<InlineData("tryFindPane")>]
    [<InlineData("tryFindClient")>]
    let ``optional server lookups distinguish absence from a failed command`` operation =
        task {
            let failure = TmuxTransportException("The tmux read failed.", [| "list" |])
            let mutable failCommand = false
            let mutable reads = 0

            let connection =
                TmuxConnection(
                    ServerConnectionOptions(SocketName = "fsharp-lookup-absence"),
                    Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>>(fun request _ ->
                        let arguments = request.LogicalArguments |> Seq.toArray

                        if arguments = [| "-V" |] then
                            versionReply arguments
                        else
                            reads <- reads + 1

                            if failCommand then
                                Task.FromException<TmuxCommandResult>(failure)
                            else
                                Task.FromResult(
                                    TmuxCommandResult(
                                        arguments,
                                        0,
                                        ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("17:29\n")),
                                        ReadOnlyMemory<byte>.Empty,
                                        [||],
                                        [||]
                                    )
                                ))
                )

            // tmux 3.2a resolves missing IDs through an empty successful listing.
            let server = LibTmux.Server(connection, ServerGeneration(17, 29), "tmux 3.2a")

            let! absent =
                task {
                    match operation with
                    | "tryFindSession" ->
                        let! found = Server.tryFindSession CancellationToken.None (SessionId 7) server
                        return Option.isNone found
                    | "tryFindWindow" ->
                        let! found = Server.tryFindWindow CancellationToken.None (WindowId 7) server
                        return Option.isNone found
                    | "tryFindPane" ->
                        let! found = Server.tryFindPane CancellationToken.None (PaneId 7) server
                        return Option.isNone found
                    | _ ->
                        let! found = Server.tryFindClient CancellationToken.None "client-7" server
                        return Option.isNone found
                }

            Assert.True(absent)
            Assert.Equal(1, reads)
            failCommand <- true

            let! observed =
                Assert.ThrowsAsync<TmuxTransportException>(fun () -> readServer operation CancellationToken.None server)

            Assert.Equal(failure.Message, observed.Message)
            Assert.Equal(failure.Dispatch, observed.Dispatch)
            Assert.Equal(2, reads)
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
