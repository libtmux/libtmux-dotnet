namespace LibTmux.FSharp.Tests

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open LibTmux
open LibTmux.FSharp
open LibTmux.Internal
open Xunit

type private EventSession(events: TmuxEvent list, ?failure: exn, ?cleanupFailure: exn, ?sessionCleanupFailure: exn) =
    let items = List.toArray events
    let mutable disposed = false
    let mutable disposeCalls = 0
    let mutable reads = 0

    member _.ReaderDisposed = disposed
    member _.DisposeCalls = disposeCalls
    member _.Reads = reads

    interface IControlModeSession with
        member _.Events =
            { new IAsyncEnumerable<TmuxEvent> with
                member _.GetAsyncEnumerator(cancellationToken) =
                    let mutable index = -1

                    { new IAsyncEnumerator<TmuxEvent> with
                        member _.Current = items[index]

                        member _.MoveNextAsync() =
                            cancellationToken.ThrowIfCancellationRequested()
                            reads <- reads + 1
                            index <- index + 1

                            if index < items.Length then
                                ValueTask<bool>(true)
                            else
                                match failure with
                                | Some error -> ValueTask<bool>(Task.FromException<bool>(error))
                                | None -> ValueTask<bool>(false)

                        member _.DisposeAsync() =
                            disposed <- true

                            match cleanupFailure with
                            | Some error -> ValueTask(Task.FromException(error))
                            | None -> ValueTask()
                    }
            }

        member _.IsRunning = true

        member _.SendAsync(_, _) =
            Task.FromResult<IReadOnlyList<string>>([])

        member _.DisposeAsync() =
            disposeCalls <- disposeCalls + 1

            match sessionCleanupFailure with
            | Some error -> ValueTask(Task.FromException(error))
            | None -> ValueTask()

module ControlTests =
    [<Fact>]
    let ``fold stops before the next event and leaves a borrowed client open`` () =
        task {
            let session =
                EventSession([ TmuxNotificationEvent("first", []); TmuxNotificationEvent("second", []) ])

            let! count =
                Control.foldEventsWhile
                    CancellationToken.None
                    (fun state _ -> Task.FromResult(StreamStep.Stop(state + 1)))
                    0
                    session

            Assert.Equal(1, count)
            Assert.Equal(1, session.Reads)
            Assert.True(session.ReaderDisposed)
            Assert.Equal(0, session.DisposeCalls)
        }

    [<Fact>]
    let ``iter awaits each handler and disposes the borrowed reader`` () =
        task {
            let session =
                EventSession([ TmuxNotificationEvent("first", []); TmuxNotificationEvent("second", []) ])

            let observed = ResizeArray<string>()
            let mutable inFlight = 0
            let mutable maximumInFlight = 0

            do!
                Control.iterEvents
                    CancellationToken.None
                    (fun event ->
                        task {
                            inFlight <- inFlight + 1
                            maximumInFlight <- max maximumInFlight inFlight
                            observed.Add((event :?> TmuxNotificationEvent).Name)
                            do! Task.Yield()
                            inFlight <- inFlight - 1
                        })
                    session

            Assert.Equal([ "first"; "second" ], observed)
            Assert.Equal(1, maximumInFlight)
            Assert.True(session.ReaderDisposed)
            Assert.Equal(0, session.DisposeCalls)
        }

    [<Fact>]
    let ``handler failure preserves the error and disposes the borrowed reader`` () =
        task {
            let failure = InvalidOperationException("handler failed")
            let session = EventSession([ TmuxNotificationEvent("first", []) ])

            let! thrown =
                Assert.ThrowsAsync<InvalidOperationException>(fun () ->
                    Control.iterEvents CancellationToken.None (fun _ -> Task.FromException(failure)) session)

            Assert.Same(failure, thrown)
            Assert.True(session.ReaderDisposed)
            Assert.Equal(0, session.DisposeCalls)
        }

    [<Fact>]
    let ``handler failure remains primary when reader cleanup also fails`` () =
        task {
            let primary = InvalidOperationException("handler failed")
            let cleanup = IOException("reader cleanup failed")

            let session =
                EventSession([ TmuxNotificationEvent("first", []) ], cleanupFailure = cleanup)

            let! thrown =
                Assert.ThrowsAsync<InvalidOperationException>(fun () ->
                    Control.iterEvents CancellationToken.None (fun _ -> Task.FromException(primary)) session)

            Assert.Same(primary, thrown)
            Assert.Same(cleanup, thrown.Data["LibTmux.ControlModeEventCleanupFailure"])
            Assert.True(session.ReaderDisposed)
            Assert.Equal(0, session.DisposeCalls)
        }

    [<Fact>]
    let ``reader cleanup failure propagates after a successful stream`` () =
        task {
            let cleanup = IOException("reader cleanup failed")
            let session = EventSession([], cleanupFailure = cleanup)

            let! thrown =
                Assert.ThrowsAsync<IOException>(fun () ->
                    Control.iterEvents CancellationToken.None (fun _ -> Task.CompletedTask) session)

            Assert.Same(cleanup, thrown)
            Assert.True(session.ReaderDisposed)
            Assert.Equal(0, session.DisposeCalls)
        }

    [<Fact>]
    let ``owned session scope disposes the client after work succeeds`` () =
        task {
            let session = EventSession([])

            let! result = Control.useSession (fun _ -> Task.FromResult("complete")) session

            Assert.Equal("complete", result)
            Assert.True(session.ReaderDisposed |> not)
            Assert.Equal(1, session.DisposeCalls)
        }

    [<Fact>]
    let ``owned session scope preserves work failure when client cleanup also fails`` () =
        task {
            let primary = InvalidOperationException("work failed")
            let cleanup = IOException("client cleanup failed")
            let session = EventSession([], sessionCleanupFailure = cleanup)

            let! thrown =
                Assert.ThrowsAsync<InvalidOperationException>(fun () ->
                    Control.useSession (fun _ -> Task.FromException<string>(primary)) session)

            Assert.Same(primary, thrown)
            Assert.Same(cleanup, thrown.Data["LibTmux.ControlModeClientCleanupFailure"])
            Assert.True(session.ReaderDisposed |> not)
            Assert.Equal(1, session.DisposeCalls)
        }

    [<Fact>]
    let ``owned session cancellation awaits cleanup and preserves its token`` () =
        task {
            use canceled = new CancellationTokenSource()
            canceled.Cancel()
            let cleanup = IOException("client cleanup failed")
            let session = EventSession([], sessionCleanupFailure = cleanup)

            let operation =
                Control.useSession (fun _ -> Task.FromCanceled<string>(canceled.Token)) session

            let! thrown =
                Assert.ThrowsAnyAsync<OperationCanceledException>(fun () -> operation :> Task)

            Assert.True(operation.IsCanceled)
            Assert.Equal(canceled.Token, thrown.CancellationToken)
            Assert.Same(cleanup, thrown.Data["LibTmux.ControlModeClientCleanupFailure"])
            Assert.Equal(1, session.DisposeCalls)
        }

    [<Fact>]
    let ``canceling control acquisition preserves the token and skips work`` () =
        task {
            use source = new CancellationTokenSource()
            let token = source.Token

            let acquiring =
                TaskCompletionSource<string array * CancellationToken>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                )

            let held =
                TaskCompletionSource<TmuxCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously)

            let connection =
                TmuxConnection(
                    ServerConnectionOptions(SocketName = "fsharp-control-acquisition"),
                    Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>>
                        (fun request cancellationToken ->
                            let arguments = request.LogicalArguments |> Seq.toArray

                            if arguments = [| "-V" |] then
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
                            else
                                acquiring.SetResult((arguments, cancellationToken))
                                held.Task.WaitAsync(cancellationToken))
                )

            let server = LibTmux.Server(connection, ServerGeneration(17, 29), "tmux 3.7")
            let mutable worked = false

            let pending =
                Control.withSession
                    token
                    (fun _ ->
                        worked <- true
                        Task.FromResult("unexpected"))
                    server

            try
                let! (arguments, forwardedToken) = acquiring.Task.WaitAsync(TimeSpan.FromSeconds(1.))

                Assert.Contains("display-message", arguments)
                Assert.Equal(token, forwardedToken)
                Assert.False(pending.IsCompleted)

                source.Cancel()

                let! thrown =
                    Assert.ThrowsAnyAsync<OperationCanceledException>(fun () -> pending :> Task)

                Assert.Equal(token, thrown.CancellationToken)
                Assert.True(pending.IsCanceled)
                Assert.False(worked)
            finally
                source.Cancel()
                held.TrySetCanceled(token) |> ignore
        }

    [<Fact>]
    let ``buffered events precede a terminal stream failure`` () =
        task {
            let failure = IOException("stream failed")
            let session = EventSession([ TmuxNotificationEvent("retained", []) ], failure)
            let observed = ResizeArray<string>()

            let! thrown =
                Assert.ThrowsAsync<IOException>(fun () ->
                    Control.iterEvents
                        CancellationToken.None
                        (fun event ->
                            observed.Add((event :?> TmuxNotificationEvent).Name)
                            Task.CompletedTask)
                        session)

            Assert.Same(failure, thrown)
            Assert.Equal([ "retained" ], observed)
            Assert.True(session.ReaderDisposed)
            Assert.Equal(0, session.DisposeCalls)
        }

    [<Fact>]
    let ``cancellation disposes the borrowed reader without closing the client`` () =
        task {
            use canceled = new CancellationTokenSource()
            canceled.Cancel()
            let session = EventSession([])

            let! _ =
                Assert.ThrowsAsync<OperationCanceledException>(fun () ->
                    Control.iterEvents canceled.Token (fun _ -> Task.CompletedTask) session)

            Assert.True(session.ReaderDisposed)
            Assert.Equal(0, session.DisposeCalls)
        }
