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

    let private readServer operation token (connection: TmuxConnection) server : Task =
        let generation = ServerGeneration(17, 29)

        match operation with
        | "sessions" -> server |> Server.sessions |> Query.list token :> Task
        | "windows" -> server |> Server.windows |> Query.list token :> Task
        | "panes" -> server |> Server.panes |> Query.list token :> Task
        | "clients" -> server |> Server.clients |> Query.list token :> Task
        | "filteredSessions" ->
            server
            |> Server.sessions
            |> Query.where (SessionFields.name |> Filter.startsWith "de")
            |> Query.list token
            :> Task
        | "sessionPanes" ->
            LibTmux.Session(server, connection, generation, SessionId 7, Dictionary<string, string>())
            |> Session.panes
            |> Query.showing (ScreenSearch.Text "a,b")
            |> Query.list token
            :> Task
        | "windowPanes" ->
            LibTmux.Window(server, connection, generation, WindowId 7, Dictionary<string, string>())
            |> Window.panes
            |> Query.whereUnsafe (UnsafeTmuxFilter "#{pane_active}")
            |> Query.list token
            :> Task
        | "tryFindSession" -> Server.tryFindSession token (SessionId 7) server
        | "tryFindWindow" -> Server.tryFindWindow token (WindowId 7) server
        | "tryFindPane" -> Server.tryFindPane token (PaneId 7) server
        | "tryFindClient" -> Server.tryFindClient token "client-7" server
        | _ -> invalidArg (nameof operation) operation

    [<Theory>]
    [<InlineData("sessions", "list-sessions", "")>]
    [<InlineData("windows", "list-windows", "-a")>]
    [<InlineData("panes", "list-panes", "-a")>]
    [<InlineData("clients", "list-clients", "")>]
    [<InlineData("filteredSessions", "list-sessions", "#{m:de*,#{session_name}}")>]
    [<InlineData("sessionPanes", "list-panes", "#{C:a#,b}")>]
    [<InlineData("windowPanes", "list-panes", "#{pane_active}")>]
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
                Assert.ThrowsAsync<TmuxOperationCanceledException>(fun () ->
                    readServer operation token connection server)

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
                Assert.ThrowsAsync<TmuxTransportException>(fun () ->
                    readServer operation CancellationToken.None connection server)

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

module PaneRunTests =
    let private describe result =
        match result with
        | PaneRun.Exited status -> "exited " + string status
        | PaneRun.TimedOut -> "timed out"
        | PaneRun.NotStarted -> "not started"
        | _ -> "other"

    [<Fact>]
    let ``runs are told apart by how they ended`` () =
        let run status timedOut started =
            PaneRunResult(status, timedOut, [], TimeSpan.Zero, started, false)

        Assert.Equal("exited 3", describe (run (Nullable 3) false true))
        Assert.Equal("timed out", describe (run (Nullable()) true true))
        Assert.Equal("not started", describe (run (Nullable()) false false))

module FailureTests =
    let private failure dispatch =
        LibTmuxException("tmux failed", (dispatch: TmuxDispatchState)) :> exn

    let private describe error =
        match error with
        | TmuxFailure.NotSent _ -> "not sent"
        | TmuxFailure.Ran _ -> "ran"
        | TmuxFailure.MayHaveRun _ -> "may have run"
        | _ -> "other"

    [<Fact>]
    let ``failures are told apart by whether tmux saw the command`` () =
        let canceled ran =
            TmuxOperationCanceledException("canceled", CancellationToken.None, ran, 7) :> exn

        Assert.Equal<string list>(
            [ "not sent"; "ran"; "may have run"; "may have run"; "other"; "other" ],
            [
                failure TmuxDispatchState.NotDispatched
                failure TmuxDispatchState.Dispatched
                failure TmuxDispatchState.Unknown
                canceled true
                canceled false
                InvalidOperationException("not tmux") :> exn
            ]
            |> List.map describe
        )

    [<Fact>]
    let ``a chain's steps act on what the one before made and send nothing until run`` () =
        let generation = ServerGeneration(96, 906)

        let connection =
            TmuxConnection(
                ServerConnectionOptions(SocketName = "fsharp-chain-steps"),
                Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>>(fun _ _ ->
                    raise (InvalidOperationException "Building a chain reached tmux."))
            )

        let server = Server(connection, generation, "tmux 3.7")
        let session = Session(server, connection, generation, SessionId 3, Dictionary())

        let chain =
            server
            |> Chain.start
            |> Chain.newWindow session "watch"
            |> Chain.splitLeftRight
            |> Chain.splitTopBottom
            |> Chain.sendLine "tail -f log"
            |> Chain.arrange "tiled"

        Assert.Equal<string list list>(
            [
                [ "new-window"; "-t"; "$3:"; "-n"; "watch" ]
                [ "split-window"; "-h" ]
                [ "split-window"; "-v" ]
                [ "send-keys"; "-l"; "--"; "tail -f log\r" ]
                [ "select-layout"; "tiled" ]
            ],
            [ for command in chain.Commands -> List.ofSeq (command.ToArguments()) ]
        )

        // The new window's session id holds only on the server it was read from,
        // and the layout is checked before tmux, which some versions crash on.
        Assert.Equal(Nullable generation, chain.Commands[0].RequiredGeneration)
        Assert.True(chain.Commands[4].ChecksLayout)

        Assert.ThrowsAsync<ArgumentException>(fun () ->
            server
            |> Chain.start
            |> Chain.arrange "no-such-layout"
            |> Chain.run CancellationToken.None
            :> Task)
        |> fun refused -> refused.GetAwaiter().GetResult() |> ignore

    [<Fact>]
    let ``a session description is checked before tmux and names itself without printf`` () =
        task {
            let connection =
                TmuxConnection(
                    ServerConnectionOptions(SocketName = "fsharp-session-spec"),
                    Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>>(fun _ _ ->
                        raise (InvalidOperationException "A rejected description reached tmux."))
                )

            let server = Server(connection, ServerGeneration(97, 907), "tmux 3.7")

            let conflicting =
                { SessionSpec.named "dev" with
                    Directory = Some "/srv"
                    Windows =
                        [
                            { WindowSpec.named "editor" with
                                Directory = Some "/tmp"
                            }
                        ]
                }

            let! _ =
                Assert.ThrowsAsync<ArgumentException>(fun () ->
                    Server.newSession CancellationToken.None conflicting server :> Task)

            // tmux would give the first window's environment to the whole session.
            let windowEnvironment =
                { SessionSpec.named "dev" with
                    Windows =
                        [
                            { WindowSpec.named "editor" with
                                Environment = Map [ "EDITOR", "nvim" ]
                            }
                        ]
                }

            let! _ =
                Assert.ThrowsAsync<ArgumentException>(fun () ->
                    Server.newSession CancellationToken.None windowEnvironment server :> Task)

            Assert.Equal(
                ("session dev", "window editor", "split running the default shell"),
                (string conflicting, string conflicting.Windows[0], string SplitSpec.empty)
            )
        }

    [<Fact>]
    let ``retry does not repeat an operation once any command it sent reached tmux`` () =
        task {
            let sent = ResizeArray<string>()
            let mutable flaky = 0
            let slow = TaskCompletionSource<TmuxCommandResult>()

            let connection =
                TmuxConnection(
                    ServerConnectionOptions(SocketName = "fsharp-retry-ledger"),
                    Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>>(fun request _ ->
                        let name = request.LogicalArguments |> Seq.last
                        sent.Add name

                        if name = "flaky" then
                            flaky <- flaky + 1

                        if name = "slow" then
                            slow.Task
                        elif name = "refused" || (name = "flaky" && flaky = 1) then
                            Task.FromException<TmuxCommandResult>(
                                TmuxTransportException(
                                    "refused",
                                    request.LogicalArguments,
                                    TmuxDispatchState.NotDispatched
                                )
                            )
                        else
                            // The connection asks the version first; any other command echoes its name.
                            let line = if name = "-V" then "tmux 3.7" else name

                            Task.FromResult(
                                TmuxCommandResult(
                                    request.LogicalArguments,
                                    0,
                                    ReadOnlyMemory(Encoding.UTF8.GetBytes(line + "\n")),
                                    ReadOnlyMemory.Empty,
                                    [ line ],
                                    []
                                )
                            ))
                )

            let server = Server(connection, ServerGeneration(95, 905), "tmux 3.7")

            let run name token =
                server.ExecuteCommandAsync([ name ], token)

            let count name =
                sent |> Seq.filter ((=) name) |> Seq.length

            // The first step ran, so the second step's NotSent does not make the whole safe to repeat.
            let! _ =
                Assert.ThrowsAsync<TmuxTransportException>(fun () ->
                    Retry.ifNotSent CancellationToken.None 2 (fun token ->
                        task {
                            let! _ = run "first" token
                            return! run "refused" token
                        })
                    :> Task)

            // A command an inner retry ran counts for the outer retry too.
            let! _ =
                Assert.ThrowsAsync<TmuxTransportException>(fun () ->
                    Retry.ifNotSent CancellationToken.None 2 (fun token ->
                        task {
                            let! _ = Retry.ifNotSent token 2 (run "nested")
                            return! run "refused" token
                        })
                    :> Task)

            // A command still in flight when another is refused counts as sent.
            let! _ =
                Assert.ThrowsAsync<TmuxTransportException>(fun () ->
                    Retry.ifNotSent CancellationToken.None 2 (fun token ->
                        task {
                            let pending = run "slow" token
                            let! _ = run "refused" token
                            return! pending
                        })
                    :> Task)

            slow.SetResult(TmuxCommandResult([ "slow" ], 0, ReadOnlyMemory.Empty, ReadOnlyMemory.Empty, [], []))

            // A lone command refused before dispatch is still repeated.
            let! recovered = Retry.ifNotSent CancellationToken.None 2 (run "flaky")

            Assert.Equal(
                (1, 1, 1, 2, 0),
                (count "first", count "nested", count "slow", count "flaky", recovered.ExitCode)
            )
        }

    [<Fact>]
    let ``retry runs again only while tmux never saw the command`` () =
        task {
            let mutable attempts = 0

            let failingWith dispatch _ =
                attempts <- attempts + 1
                Task.FromException<int>(failure dispatch)

            let! _ =
                Assert.ThrowsAsync<LibTmuxException>(fun () ->
                    Retry.ifNotSent CancellationToken.None 2 (failingWith TmuxDispatchState.NotDispatched) :> Task)

            let notSentAttempts = attempts
            attempts <- 0

            let! _ =
                Assert.ThrowsAsync<LibTmuxException>(fun () ->
                    Retry.ifNotSent CancellationToken.None 2 (failingWith TmuxDispatchState.Unknown) :> Task)

            let mutable calls = 0

            let! recovered =
                Retry.ifNotSent CancellationToken.None 1 (fun _ ->
                    calls <- calls + 1

                    if calls = 1 then
                        Task.FromException<int>(failure TmuxDispatchState.NotDispatched)
                    else
                        Task.FromResult 42)

            Assert.Equal((3, 1, 42), (notSentAttempts, attempts, recovered))
        }
