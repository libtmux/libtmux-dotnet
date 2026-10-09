namespace LibTmux.FSharp.Tests

open System
open System.Threading.Tasks
open LibTmux
open LibTmux.FSharp
open Xunit

module LifecycleTests =
    let private withServer (work: LibTmux.Server -> string -> Task) =
        task {
            let token = TestContext.Current.CancellationToken

            let root =
                IO.Directory
                    .CreateDirectory(
                        IO.Path.Combine("/tmp/libtmux-dotnet-test", "fsharp-" + Guid.NewGuid().ToString("N"))
                    )
                    .FullName

            let binary =
                match Environment.GetEnvironmentVariable "LIBTMUX_TMUX" with
                | null
                | "" -> "tmux"
                | value -> value

            let options =
                ServerConnectionOptions(
                    SocketPath = IO.Path.Combine(root, "owned.sock"),
                    ConfigurationFile = "/dev/null",
                    TmuxBinaryPath = binary
                )

            let! owned = LibTmux.Server.CreateOwnedAsync(options, token)
            let mutable failure = None

            try
                let! _ =
                    owned.Value.CreateSessionAsync(NewSessionRequest(Name = "keeper", Command = "/bin/sh"), token)

                do! work owned.Value root
            with error ->
                failure <- Some error

            let mutable cleanupFailure = None

            try
                do! owned.DisposeAsync().AsTask()

                let! remaining =
                    LibTmux.Server.Open(options).InspectAsync(System.Threading.CancellationToken.None)

                Assert.Null remaining
                IO.Directory.Delete(root, true)
            with error ->
                cleanupFailure <- Some error

            match failure, cleanupFailure with
            | Some error, Some cleanup -> return raise (AggregateException(error, cleanup))
            | Some error, None
            | None, Some error -> return System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw()
            | None, None -> return ()
        }

    let private failingClient root failCleanup failWindow =
        let wrapper = IO.Path.Combine(root, "tmux")

        let binary =
            match Environment.GetEnvironmentVariable "LIBTMUX_TMUX" with
            | null
            | "" -> "/usr/local/bin/tmux"
            | value -> value

        IO.File.WriteAllText(
            wrapper,
            "#!/bin/sh\n"
            + (if failCleanup then
                   "case \"$*\" in *kill-session*) echo 'injected session cleanup failure' >&2; exit 1;; esac\n"
               else
                   "")
            + (if failWindow then
                   "case \"$*\" in *new-window*) echo 'injected window failure' >&2; exit 1;; esac\n"
               else
                   "")
            + "exec '"
            + binary.Replace("'", "'\\''")
            + "' \"$@\"\n"
        )

        IO.File.SetUnixFileMode(
            wrapper,
            IO.UnixFileMode.UserRead
            ||| IO.UnixFileMode.UserWrite
            ||| IO.UnixFileMode.UserExecute
        )

        LibTmux.Server.Open(
            ServerConnectionOptions(SocketPath = IO.Path.Combine(root, "owned.sock"), TmuxBinaryPath = wrapper)
        )

    [<Fact>]
    let ``a failed later window rolls back the created session`` () =
        withServer (fun server root ->
            task {
                let token = TestContext.Current.CancellationToken
                let client = failingClient root false true

                let spec =
                    { SessionSpec.named "partial" with
                        Windows = [ WindowSpec.empty; WindowSpec.empty ]
                    }

                let! error =
                    Record.ExceptionAsync(fun () -> Server.newSession token spec client :> Task)

                let error = Assert.IsAssignableFrom<exn>(error)
                Assert.Contains("injected window failure", error.ToString())
                let! remaining = server.GetSessionsAsync(token)
                Assert.Equal("keeper", Assert.Single(remaining).Name)
            })

    [<Fact>]
    let ``session work returns its result and cleans the renamed session by identity`` () =
        withServer (fun server _ ->
            task {
                let token = TestContext.Current.CancellationToken
                let mutable created = None

                let! result =
                    server
                    |> Server.withNewSession token (SessionSpec.running "scoped" "/bin/sh") (fun session ->
                        task {
                            created <- Some session.Id
                            let! renamed = session |> Session.rename token "renamed"
                            Assert.Equal("renamed", renamed.Name)
                            return "complete"
                        })

                Assert.Equal("complete", result)
                let! remaining = server.GetSessionsAsync(token)
                Assert.DoesNotContain(remaining, fun session -> Some session.Id = created)
                Assert.Equal("keeper", Assert.Single(remaining).Name)
            })

    [<Fact>]
    let ``cancellation during a later layout step rolls back with a fresh token`` () =
        withServer (fun server root ->
            task {
                use source = new System.Threading.CancellationTokenSource()

                let client =
                    LibTmux.Server.Open(
                        ServerConnectionOptions(
                            SocketPath = IO.Path.Combine(root, "owned.sock"),
                            TmuxBinaryPath =
                                (Environment.GetEnvironmentVariable "LIBTMUX_TMUX"
                                 |> Option.ofObj
                                 |> Option.defaultValue "tmux"),
                            Interceptor =
                                TmuxInterceptor(fun invocation next token ->
                                    if invocation.Arguments |> Seq.contains "new-window" then
                                        source.Cancel()
                                        Task.FromCanceled<TmuxCommandResult>(source.Token)
                                    else
                                        next.Invoke(token))
                        )
                    )

                let spec =
                    { SessionSpec.named "partial" with
                        Windows = [ WindowSpec.empty; WindowSpec.empty ]
                    }

                let pending = Server.newSession source.Token spec client

                let! error =
                    Assert.ThrowsAnyAsync<OperationCanceledException>(fun () -> pending :> Task)

                Assert.Equal(source.Token, error.CancellationToken)
                Assert.True(pending.IsCanceled)
                Assert.Equal(None, Control.cleanupFailure error)
                let! remaining = server.GetSessionsAsync(TestContext.Current.CancellationToken)
                Assert.Equal("keeper", Assert.Single(remaining).Name)
            })

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``body failure keeps its exception and cleans the session`` cancel =
        withServer (fun server _ ->
            task {
                let token = TestContext.Current.CancellationToken
                use canceled = new System.Threading.CancellationTokenSource()
                canceled.Cancel()
                let failure = InvalidOperationException("injected work failure")

                let pending =
                    server
                    |> Server.withNewSession token (SessionSpec.running "scoped" "/bin/sh") (fun _ ->
                        if cancel then
                            Task.FromCanceled<string>(canceled.Token)
                        else
                            Task.FromException<string>(failure))

                let! error = Record.ExceptionAsync(fun () -> pending :> Task)
                let error = Assert.IsAssignableFrom<exn>(error)

                if cancel then
                    let cancellation = Assert.IsAssignableFrom<OperationCanceledException>(error)
                    Assert.Equal(canceled.Token, cancellation.CancellationToken)
                    Assert.True(pending.IsCanceled)
                else
                    Assert.Same(failure, error)

                Assert.Equal(None, Control.cleanupFailure error)
                let! remaining = server.GetSessionsAsync(token)
                Assert.Equal("keeper", Assert.Single(remaining).Name)
            })

    [<Theory>]
    [<InlineData("success")>]
    [<InlineData("failure")>]
    [<InlineData("cancel")>]
    let ``session cleanup failures remain observable with each body outcome`` outcome =
        withServer (fun server root ->
            task {
                let token = TestContext.Current.CancellationToken
                let client = failingClient root true false
                use canceled = new System.Threading.CancellationTokenSource()
                canceled.Cancel()
                let failure = InvalidOperationException("injected work failure")

                let pending =
                    client
                    |> Server.withNewSession token (SessionSpec.running "scoped" "/bin/sh") (fun _ ->
                        match outcome with
                        | "cancel" -> Task.FromCanceled<string>(canceled.Token)
                        | "failure" -> Task.FromException<string>(failure)
                        | _ -> Task.FromResult "complete")

                let! error = Record.ExceptionAsync(fun () -> pending :> Task)
                let error = Assert.IsAssignableFrom<exn>(error)

                let cleanup =
                    match outcome with
                    | "cancel" ->
                        let cancellation = Assert.IsAssignableFrom<OperationCanceledException>(error)
                        Assert.Equal(canceled.Token, cancellation.CancellationToken)
                        Assert.True(pending.IsCanceled)
                        Control.cleanupFailure error |> Option.get
                    | "failure" ->
                        Assert.Same(failure, error)
                        Control.cleanupFailure error |> Option.get
                    | _ -> error

                Assert.Contains("injected session cleanup failure", cleanup.ToString())
                let! remaining = server.GetSessionsAsync(token)
                Assert.Contains(remaining, fun session -> session.Name = "scoped")
                Assert.Equal(2, remaining.Count)
            })

    [<Fact>]
    let ``layout failure retains rollback failure without hiding the layout error`` () =
        withServer (fun server root ->
            task {
                let token = TestContext.Current.CancellationToken
                let client = failingClient root true true

                let spec =
                    { SessionSpec.running "partial" "/bin/sh" with
                        Windows =
                            [
                                WindowSpec.empty
                                { WindowSpec.empty with
                                    Command = Some "/bin/sh"
                                }
                            ]
                    }

                let! error =
                    Record.ExceptionAsync(fun () -> Server.newSession token spec client :> Task)

                let error = Assert.IsAssignableFrom<exn>(error)
                let cleanup = Control.cleanupFailure error |> Option.get
                Assert.Contains("injected session cleanup failure", cleanup.ToString())
                Assert.DoesNotContain("injected session cleanup failure", error.Message)
                let! remaining = server.GetSessionsAsync(token)
                Assert.Contains(remaining, fun session -> session.Name = "partial")
            })

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``initial core readback rolls back and exposes cleanup through the FSharp accessor`` failCleanup =
        withServer (fun server root ->
            task {
                let token = TestContext.Current.CancellationToken
                let mutable created = false
                let readback = IO.IOException("Injected initial readback failure.")
                let rollback = IO.IOException("Injected initial rollback failure.")

                let client =
                    LibTmux.Server.Open(
                        ServerConnectionOptions(
                            SocketPath = IO.Path.Combine(root, "owned.sock"),
                            TmuxBinaryPath =
                                (Environment.GetEnvironmentVariable "LIBTMUX_TMUX"
                                 |> Option.ofObj
                                 |> Option.defaultValue "tmux"),
                            Interceptor =
                                TmuxInterceptor(fun invocation next commandToken ->
                                    task {
                                        if
                                            created
                                            && failCleanup
                                            && (invocation.Arguments |> Seq.contains "kill-session")
                                        then
                                            return raise rollback
                                        elif
                                            created
                                            && (invocation.Arguments
                                                |> Seq.exists (fun value ->
                                                    value.Contains("#{session_id}", StringComparison.Ordinal)))
                                        then
                                            return raise readback
                                        else
                                            let! result = next.Invoke(commandToken)

                                            if invocation.Arguments |> Seq.contains "new-session" then
                                                created <- true

                                            return result
                                    })
                        )
                    )

                let! error =
                    Record.ExceptionAsync(fun () ->
                        Server.newSession token (SessionSpec.named "initial") client :> Task)

                let failure = Assert.IsType<LibTmuxException>(error)
                Assert.Same(readback, failure.InnerException)
                Assert.True(created)

                if failCleanup then
                    Assert.Same(rollback, Control.cleanupFailure failure |> Option.get)
                else
                    Assert.Equal(None, Control.cleanupFailure failure)

                let! remaining = server.GetSessionsAsync(token)
                Assert.Equal((if failCleanup then 2 else 1), remaining.Count)
            })
