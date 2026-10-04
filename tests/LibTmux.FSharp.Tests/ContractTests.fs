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
    let ``pressKey sends one key name and no Enter`` () =
        task {
            let sent = System.Collections.Concurrent.ConcurrentQueue<string array>()

            let connection =
                TmuxConnection(
                    ServerConnectionOptions(SocketName = "fsharp-press-key"),
                    Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>>(fun request _ ->
                        let arguments = request.LogicalArguments |> Seq.toArray

                        if arguments = [| "-V" |] then
                            versionReply arguments
                        else
                            sent.Enqueue arguments

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

            let generation = ServerGeneration(17, 29)
            let server = LibTmux.Server(connection, generation, "tmux 3.7")

            let pane =
                LibTmux.Pane(server, connection, generation, PaneId 1, Dictionary<string, string>())

            do! pane |> Pane.pressKey CancellationToken.None "C-c"

            // Each command also carries the server generation check, so look
            // for the key; a following Enter would be a second command.
            let arguments = Assert.Single(sent)
            Assert.Contains("C-c", arguments)
            Assert.DoesNotContain("-l", arguments)

            Assert.Throws<ArgumentException>(fun () -> pane |> Pane.pressKey CancellationToken.None " " |> ignore)
            |> ignore
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

    [<Fact>]
    let ``kill sends one command for the handle it is given`` () =
        task {
            let sent = Collections.Concurrent.ConcurrentQueue<string list>()

            let connection =
                TmuxConnection(
                    ServerConnectionOptions(SocketName = "fsharp-kill"),
                    Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>>(fun request _ ->
                        let arguments = request.LogicalArguments |> Seq.toArray

                        if arguments = [| "-V" |] then
                            versionReply arguments
                        else
                            // Entity commands follow the server generation guard.
                            sent.Enqueue(arguments |> Array.skip (arguments.Length - 3) |> List.ofArray)
                            let generation = Encoding.UTF8.GetBytes("17:31\n")

                            Task.FromResult(
                                TmuxCommandResult(
                                    arguments,
                                    0,
                                    ReadOnlyMemory<byte>(generation),
                                    ReadOnlyMemory<byte>.Empty,
                                    [| "17:31" |],
                                    [||]
                                )
                            ))
                )

            let generation = ServerGeneration(17, 31)
            let server = LibTmux.Server(connection, generation, "tmux 3.7")
            let fields () = Dictionary<string, string>()
            let token = TestContext.Current.CancellationToken

            do!
                LibTmux.Pane(server, connection, generation, PaneId 4, fields ())
                |> Pane.kill token

            do!
                LibTmux.Window(server, connection, generation, WindowId 3, fields ())
                |> Window.kill token

            do!
                LibTmux.Session(server, connection, generation, SessionId 2, fields ())
                |> Session.kill token

            Assert.Equal<string list list>(
                [
                    [ "kill-pane"; "-t"; "%4" ]
                    [ "kill-window"; "-t"; "@3" ]
                    [ "kill-session"; "-t"; "$2" ]
                ],
                sent |> List.ofSeq
            )
        }

module PaneRunTests =
    let private describe result =
        match result with
        | PaneRun.Exited status -> "exited " + string status
        | PaneRun.Ended -> "ended"
        | PaneRun.NotStarted -> "not started"
        | PaneRun.TimedOut -> "timed out"

    [<Fact>]
    let ``runs are told apart by how they ended`` () =
        let run status timedOut started =
            PaneRunResult(status, timedOut, [], TimeSpan.Zero, started, false)

        Assert.Equal("exited 3", describe (run (Nullable 3) false true))

        Assert.Equal(
            "ended",
            describe (PaneRunResult(Nullable(), false, [], TimeSpan.Zero, true, false, PaneExited = true))
        )
        // The shell's exit decides, whether or not the command was seen to begin.
        Assert.Equal(
            "ended",
            describe (PaneRunResult(Nullable(), false, [], TimeSpan.Zero, false, false, PaneExited = true))
        )

        Assert.Equal("timed out", describe (run (Nullable()) true true))
        Assert.Equal("not started", describe (run (Nullable()) false false))
        // A run typed into something other than a shell also waits out its time.
        Assert.Equal("not started", describe (run (Nullable()) true false))

        Assert.Throws<ArgumentOutOfRangeException>(fun () -> describe (run (Nullable()) false true) |> ignore)
        |> ignore

module PaneWaitTests =
    let private describe result =
        match result with
        | PaneWait.Found -> "found"
        | PaneWait.Printed -> "printed"
        | PaneWait.Stopped pattern -> "stopped by " + pattern
        | PaneWait.TimedOut -> "timed out"
        | PaneWait.Ended -> "ended"

    [<Fact>]
    let ``every wait outcome has one pattern`` () =
        let wait outcome (pattern: string | null) =
            PaneWaitResult(outcome, pattern, TimeSpan.Zero) |> describe

        Assert.Equal<string list>(
            [
                "found"
                "found"
                "printed"
                "stopped by FAIL"
                "timed out"
                "ended"
                "ended"
            ],
            [
                wait PaneWaitOutcome.Matched "ok"
                wait PaneWaitOutcome.PresentAtEntry "ok"
                wait PaneWaitOutcome.AnyOutput null
                wait PaneWaitOutcome.Stopped "FAIL"
                wait PaneWaitOutcome.TimedOut null
                wait PaneWaitOutcome.PaneExited null
                wait PaneWaitOutcome.AlternateScreen null
            ]
        )

        // An outcome the core adds later must have a case here before it ships.
        for outcome in Enum.GetValues<PaneWaitOutcome>() do
            wait outcome "pattern" |> ignore

module PaneWatchTests =
    let private describe event =
        match event with
        | PaneWatch.Output output -> "output " + output.Data
        | PaneWatch.Paused pane -> "paused " + pane.ToString()
        | PaneWatch.Continued pane -> "continued " + pane.ToString()
        | PaneWatch.Dropped loss -> "dropped " + loss.Count.ToString()
        | PaneWatch.Gone pane -> "gone " + pane.ToString()
        | PaneWatch.Exited reason -> "exited " + defaultArg reason "silently"

    [<Fact>]
    let ``every event a pane watch yields has one pattern`` () =
        let pane = PaneId 3

        Assert.Equal<string list>(
            [
                "output hi"
                "paused %3"
                "continued %3"
                "dropped 2"
                "gone %3"
                "exited silently"
                "exited detached"
            ],
            [
                describe (TmuxOutputEvent(pane, "hi"))
                describe (TmuxPanePausedEvent pane)
                describe (TmuxPaneContinuedEvent pane)
                describe (TmuxEventsDroppedEvent(2L, 5L))
                describe (TmuxPaneGoneEvent pane)
                describe (TmuxExitEvent null)
                describe (TmuxExitEvent "detached")
            ]
        )

        // A watch never yields a notification, so matching one is a mistake.
        Assert.Throws<ArgumentOutOfRangeException>(fun () ->
            describe (TmuxNotificationEvent("window-add", [| "@1" |])) |> ignore)
        |> ignore

        // An event type the core adds later fails here until it has a case, or
        // is shown never to reach a pane watch.
        Assert.Equal<string array>(
            [|
                "TmuxEventsDroppedEvent"
                "TmuxExitEvent"
                "TmuxNotificationEvent"
                "TmuxOutputEvent"
                "TmuxPaneContinuedEvent"
                "TmuxPaneGoneEvent"
                "TmuxPanePausedEvent"
            |],
            typeof<TmuxEvent>.Assembly.GetTypes()
            |> Array.filter (fun kind -> kind.IsSubclassOf typeof<TmuxEvent> && not kind.IsAbstract)
            |> Array.map (fun kind -> kind.Name)
            |> Array.sort
        )

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

    // Async.AwaitTask loses a tmux client's cancellation after it started;
    // TmuxAsync keeps it, and still cancels the workflow for any other.
    [<Fact>]
    let ``an async workflow keeps the cancellation that says tmux may have acted`` () =
        use canceled = new CancellationTokenSource()
        canceled.Cancel()

        let kept () : Task<int> =
            task { return raise (TmuxOperationCanceledException("may have run", canceled.Token, true, 7)) }

        let plain () : Task<int> =
            task { return raise (OperationCanceledException(canceled.Token)) }

        let failed () : Task =
            task { return raise (failure TmuxDispatchState.Unknown) } :> Task

        let describe (work: Async<unit>) =
            let attempt =
                async {
                    try
                        do! work
                        return "ran"
                    with TmuxFailure.MayHaveRun error ->
                        return "may have run: " + error.GetType().Name
                }

            try
                Async.RunSynchronously attempt
            with :? OperationCanceledException ->
                "cancelled"

        Assert.Equal(
            "may have run: TmuxOperationCanceledException",
            describe (TmuxAsync.awaitTask (kept ()) |> Async.Ignore)
        )

        Assert.Equal("cancelled", describe (TmuxAsync.awaitTask (plain ()) |> Async.Ignore))
        Assert.Equal("may have run: LibTmuxException", describe (TmuxAsync.awaitUnitTask (failed ())))
        Assert.Equal("cancelled", describe (Async.AwaitTask(kept ()) |> Async.Ignore))

    // Async.AwaitTask and Task.Wait wrap a failed task's exception; a match
    // that missed it would read a command that may have run as some other failure.
    [<Fact>]
    let ``a failure wrapped by Async.AwaitTask is told apart the same way`` () =
        let failed dispatch =
            Task.FromException<unit>(failure dispatch)
            |> Async.AwaitTask
            |> Async.Catch
            |> Async.RunSynchronously

        let wrapped =
            [
                TmuxDispatchState.NotDispatched
                TmuxDispatchState.Dispatched
                TmuxDispatchState.Unknown
            ]
            |> List.map (fun dispatch ->
                match failed dispatch with
                | Choice2Of2 error -> error
                | Choice1Of2() -> failwith "the task failed")

        Assert.All(wrapped, fun error -> Assert.IsType<AggregateException>(error) |> ignore)
        Assert.Equal<string list>([ "not sent"; "ran"; "may have run" ], wrapped |> List.map describe)

        let both =
            AggregateException(failure TmuxDispatchState.NotDispatched, failure TmuxDispatchState.Dispatched)

        Assert.Equal("other", describe both)

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

            // A size outside the documented range is refused before the session exists;
            // tmux itself refuses a share over 100 and clamps a split of no cells.
            for size in [ SplitSize.Cells 0; SplitSize.Percent 101 ] do
                let sized =
                    { SessionSpec.named "dev" with
                        Windows =
                            [
                                { WindowSpec.named "editor" with
                                    Splits =
                                        [
                                            { SplitSpec.empty with
                                                Size = Some size
                                            }
                                        ]
                                }
                            ]
                    }

                let! _ =
                    Assert.ThrowsAsync<ArgumentOutOfRangeException>(fun () ->
                        Server.newSession CancellationToken.None sized server :> Task)

                ()

            Assert.Equal(
                ("session dev", "window editor", "split running the default shell"),
                (string conflicting, string conflicting.Windows[0], string SplitSpec.empty)
            )

            Assert.Equal(("20 cells", "50%"), (string (SplitSize.Cells 20), string (SplitSize.Percent 50)))
        }

    [<Fact>]
    let ``retry does not repeat an operation once any command it sent reached tmux`` () =
        task {
            let sent = ResizeArray<string>()
            let mutable flaky = 0
            let mutable absent = 0
            let slow = TaskCompletionSource<TmuxCommandResult>()

            let connection =
                TmuxConnection(
                    ServerConnectionOptions(SocketName = "fsharp-retry-ledger"),
                    Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>>(fun request _ ->
                        let name = request.LogicalArguments |> Seq.last
                        sent.Add name

                        if name = "flaky" then
                            flaky <- flaky + 1

                        if name = "absent" then
                            absent <- absent + 1

                        if name = "slow" then
                            slow.Task
                        elif name = "absent" && absent = 1 then
                            // The client ran, but no server listened.
                            Task.FromResult(
                                TmuxCommandResult(
                                    request.LogicalArguments,
                                    1,
                                    ReadOnlyMemory.Empty,
                                    ReadOnlyMemory.Empty,
                                    [],
                                    [ "no server running on /tmp/fsharp-retry-ledger" ]
                                )
                            )
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

            // A command no server heard is repeated, as while a server starts.
            let! started =
                Retry.ifNotSent CancellationToken.None 2 (fun token ->
                    task {
                        let! result = run "absent" token

                        return
                            if result.ExitCode = 0 then
                                result
                            else
                                raise (TmuxCommandException("absent failed", result))
                    })

            Assert.Equal(
                (1, 1, 1, 2, 0, 2, 0),
                (count "first",
                 count "nested",
                 count "slow",
                 count "flaky",
                 recovered.ExitCode,
                 count "absent",
                 started.ExitCode)
            )
        }

    [<Fact>]
    let ``retry after delays waits before each attempt and stops when they run out`` () =
        task {
            let mutable attempts = 0
            let started = Diagnostics.Stopwatch.StartNew()

            let! _ =
                Assert.ThrowsAsync<LibTmuxException>(fun () ->
                    Retry.ifNotSentAfter
                        CancellationToken.None
                        [ TimeSpan.FromMilliseconds 20.; TimeSpan.FromMilliseconds 30. ]
                        (fun _ ->
                            attempts <- attempts + 1
                            Task.FromException<int>(failure TmuxDispatchState.NotDispatched))
                    :> Task)

            Assert.Equal(3, attempts)
            Assert.True(started.Elapsed >= TimeSpan.FromMilliseconds 50.)
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
