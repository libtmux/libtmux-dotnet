namespace LibTmux.FSharp.Tests

open System
open System.Threading.Tasks
open LibTmux
open LibTmux.FSharp
open Xunit

module LifecycleExamples =
    // fsharp-snippet: AdoptPane
    open System.Threading
    open LibTmux
    open LibTmux.FSharp

    let captureThenRemovePaneAsync (token: CancellationToken) (pane: LibTmux.Pane) =
        task {
            let! owner = pane |> Pane.adopt token
            return! owner |> Owned.withResource token (Pane.capture token (CapturePaneRequest()))
        }
    // endfsharp-snippet

    // fsharp-snippet: DiscoverServers
    open System.Threading
    open LibTmux
    open LibTmux.FSharp

    let reportServersAsync (token: CancellationToken) (options: ServerDiscoveryOptions) =
        task {
            let! result = options |> Server.discover token

            for found in result.Servers do
                printfn "server: %s" found.SocketPath

            for diagnostic in result.Diagnostics do
                printfn "%s: %s (%s)" diagnostic.Path diagnostic.Message diagnostic.Kind

            printfn "search truncated: %b" result.Truncated
            return result
        }
    // endfsharp-snippet

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

            use serverProcess =
                Diagnostics.Process.GetProcessById(owned.Value.Generation.Value.ProcessId)

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

                use cleanupDeadline =
                    new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds 5.)

                do! serverProcess.WaitForExitAsync(cleanupDeadline.Token)
                Assert.True(serverProcess.HasExited)

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

    [<Fact>]
    let ``find or create distinguishes all four created owners from borrowed reuse`` () =
        withServer (fun _ root ->
            task {
                let token = TestContext.Current.CancellationToken

                let options =
                    ServerConnectionOptions(
                        SocketPath = IO.Path.Combine(root, "find.sock"),
                        ConfigurationFile = "/dev/null",
                        TmuxBinaryPath =
                            (Environment.GetEnvironmentVariable "LIBTMUX_TMUX"
                             |> Option.ofObj
                             |> Option.defaultValue "tmux")
                    )

                use! daemon = LibTmux.Server.Open(options) |> Server.findOrCreate token

                use daemonProcess =
                    Diagnostics.Process.GetProcessById(daemon.Value.Generation.Value.ProcessId)

                match daemon with
                | FindOrCreate.Created owner -> Assert.Same(daemon.Value, owner.Value)
                | FindOrCreate.Existing _ -> failwith "The new endpoint must return an owner."

                do!
                    daemon
                    |> FindOrCreate.withResource token (fun server ->
                        task {
                            use! existingDaemon = server |> Server.findOrCreate token

                            match existingDaemon with
                            | FindOrCreate.Existing found -> Assert.Equal(server.Generation, found.Generation)
                            | FindOrCreate.Created _ -> failwith "A listening daemon must remain borrowed."

                            use! session =
                                server
                                |> Server.findOrCreateSession
                                    token
                                    "selected"
                                    (Some(NewSessionRequest(Command = "/bin/sh")))

                            match session with
                            | FindOrCreate.Created owner -> Assert.Equal(session.Value.Id, owner.Value.Id)
                            | FindOrCreate.Existing _ -> failwith "The new session must return an owner."

                            use! existingSession = server |> Server.findOrCreateSession token "selected" None
                            Assert.False(existingSession.Created)
                            Assert.Equal(session.Value.Id, existingSession.Value.Id)

                            use! window =
                                session.Value
                                |> Session.findOrCreateWindow
                                    token
                                    "selected"
                                    (Some(NewWindowRequest(Command = "/bin/sh")))

                            match window with
                            | FindOrCreate.Created owner -> Assert.Equal(window.Value.Id, owner.Value.Id)
                            | FindOrCreate.Existing _ -> failwith "The new window must return an owner."

                            use! existingWindow =
                                session.Value |> Session.findOrCreateWindow token "selected" None

                            Assert.False(existingWindow.Created)
                            Assert.Equal(window.Value.Id, existingWindow.Value.Id)

                            use! pane =
                                window.Value
                                |> Window.findOrCreatePane
                                    token
                                    "worker"
                                    (Some(SplitPaneRequest(Command = "/bin/sh")))

                            match pane with
                            | FindOrCreate.Created owner -> Assert.Equal(pane.Value.Id, owner.Value.Id)
                            | FindOrCreate.Existing _ -> failwith "The new pane must return an owner."

                            use! existingPane = window.Value |> Window.findOrCreatePane token "worker" None
                            Assert.False(existingPane.Created)
                            Assert.Equal(pane.Value.Id, existingPane.Value.Id)

                            let! reusedId =
                                existingPane
                                |> FindOrCreate.withResource token (fun value -> Task.FromResult value.Id)

                            let! retained = server |> Server.tryFindPane token reusedId
                            Assert.True(retained.IsSome)

                            let! createdId =
                                pane |> FindOrCreate.withResource token (fun value -> Task.FromResult value.Id)

                            let! removed = server |> Server.tryFindPane token createdId
                            Assert.True(removed.IsNone)
                        })

                Assert.True(daemonProcess.HasExited)
                let! remaining = LibTmux.Server.Open(options).InspectAsync(token)
                Assert.Null remaining
            })

    [<Theory>]
    [<InlineData("server")>]
    [<InlineData("session")>]
    [<InlineData("window")>]
    [<InlineData("pane")>]
    let ``adoption scopes each resource and keeps its cleanup boundary`` kind =
        withServer (fun server _ ->
            task {
                let token = TestContext.Current.CancellationToken

                let! session =
                    server |> Server.newSession token (SessionSpec.running "adopted" "/bin/sh")

                let! window =
                    session
                    |> Session.newWindow token (NewWindowRequest(Name = "adopted", Command = "/bin/sh"))

                let! first = window |> Window.activePane token
                let! pane = first |> Pane.split token (SplitPaneRequest(Command = "/bin/sh"))

                match kind with
                | "server" ->
                    let! owner = server |> Server.adopt token
                    do! owner |> Owned.withResource token (fun _ -> Task.FromResult())
                    let! remaining = server.InspectAsync(token)
                    Assert.Null remaining
                | "session" ->
                    let! owner = session |> Session.adopt token

                    do!
                        owner
                        |> Owned.withResource token (fun value ->
                            task {
                                let! _ = value |> Session.rename token "renamed"
                                return ()
                            })

                    let! remaining = server |> Server.tryFindSession token session.Id
                    Assert.True(remaining.IsNone)
                    let! sessions = server.GetSessionsAsync(token)
                    Assert.Equal("keeper", Assert.Single(sessions).Name)
                | "window" ->
                    let! owner = window |> Window.adopt token
                    do! owner |> Owned.withResource token (fun _ -> Task.FromResult())
                    let! remaining = server |> Server.tryFindWindow token window.Id
                    Assert.True(remaining.IsNone)
                    let! retained = server |> Server.tryFindSession token session.Id
                    Assert.True(retained.IsSome)
                | _ ->
                    let! captured = LifecycleExamples.captureThenRemovePaneAsync token pane
                    Assert.NotEmpty captured
                    let! remaining = server |> Server.tryFindPane token pane.Id
                    Assert.True(remaining.IsNone)
                    let! retained = server |> Server.tryFindPane token first.Id
                    Assert.True(retained.IsSome)
            })

    [<Fact>]
    let ``discovery returns borrowed daemons and preserves caller bounds`` () =
        withServer (fun server root ->
            task {
                let token = TestContext.Current.CancellationToken

                let options =
                    ServerDiscoveryOptions(
                        Roots = [| root; root |],
                        IncludeConfiguredRoots = false,
                        MaximumRoots = 1,
                        Connection =
                            ServerConnectionOptions(
                                TmuxBinaryPath =
                                    (Environment.GetEnvironmentVariable "LIBTMUX_TMUX"
                                     |> Option.ofObj
                                     |> Option.defaultValue "tmux")
                            )
                    )

                let! discovered = options |> LifecycleExamples.reportServersAsync token
                Assert.Equal(server.Generation, Assert.Single(discovered.Servers).Server.Generation)
                Assert.True(discovered.Truncated)
                Assert.Contains(discovered.Diagnostics, fun diagnostic -> diagnostic.Kind = "limit")
                let! stillPresent = server.InspectAsync(token)
                Assert.NotNull stillPresent
            })

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``find or create scope preserves borrowed sessions when work fails or is canceled`` cancel =
        withServer (fun server _ ->
            task {
                let token = TestContext.Current.CancellationToken
                use canceled = new System.Threading.CancellationTokenSource()
                canceled.Cancel()
                let failure = InvalidOperationException("injected borrowed work failure")
                use! selected = server |> Server.findOrCreateSession token "keeper" None
                Assert.False(selected.Created)

                let pending =
                    selected
                    |> FindOrCreate.withResource token (fun _ ->
                        if cancel then
                            Task.FromCanceled<string>(canceled.Token)
                        else
                            Task.FromException<string>(failure))

                let! error = Record.ExceptionAsync(fun () -> pending :> Task)

                if cancel then
                    let cancellation = Assert.IsAssignableFrom<OperationCanceledException>(error)
                    Assert.Equal(canceled.Token, cancellation.CancellationToken)
                    Assert.True(pending.IsCanceled)
                else
                    Assert.Same(failure, error)

                let! remaining = server.GetSessionsAsync(token)
                Assert.Equal("keeper", Assert.Single(remaining).Name)
            })

    [<Theory>]
    [<InlineData("success")>]
    [<InlineData("failure")>]
    [<InlineData("cancel")>]
    let ``owned callback retains body and cleanup failures from an adopted resource`` outcome =
        withServer (fun server root ->
            task {
                let token = TestContext.Current.CancellationToken
                let client = failingClient root true false

                let! session =
                    client |> Server.newSession token (SessionSpec.running "adopted" "/bin/sh")

                let! owner = session |> Session.adopt token
                use canceled = new System.Threading.CancellationTokenSource()
                canceled.Cancel()
                let failure = InvalidOperationException("injected owned work failure")

                let pending =
                    owner
                    |> Owned.withResource token (fun _ ->
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
                let! remaining = server |> Server.tryFindSession token session.Id
                Assert.True(remaining.IsSome)
            })

    [<Fact>]
    let ``cancellation before owned work still cleans while borrowed work leaves the match alive`` () =
        withServer (fun server _ ->
            task {
                let token = TestContext.Current.CancellationToken

                use! created =
                    server
                    |> Server.findOrCreateSession token "cancelled" (Some(NewSessionRequest(Command = "/bin/sh")))

                use! existing = server |> Server.findOrCreateSession token "keeper" None
                use canceled = new System.Threading.CancellationTokenSource()
                canceled.Cancel()

                for result in [ created; existing ] do
                    let mutable called = false

                    let pending =
                        result
                        |> FindOrCreate.withResource canceled.Token (fun _ ->
                            called <- true
                            Task.FromResult())

                    let! error =
                        Assert.ThrowsAnyAsync<OperationCanceledException>(fun () -> pending :> Task)

                    Assert.Equal(canceled.Token, error.CancellationToken)
                    Assert.False(called)

                let! remaining = server.GetSessionsAsync(token)
                Assert.Equal("keeper", Assert.Single(remaining).Name)
            })

    [<Fact>]
    let ``every lifecycle lookup and adoption forwards cancellation`` () =
        withServer (fun server root ->
            task {
                let token = TestContext.Current.CancellationToken
                let! sessions = server.GetSessionsAsync(token)
                let session = Assert.Single sessions
                let! windows = session.GetWindowsAsync(token)
                let window = Assert.Single windows
                let! pane = window |> Window.activePane token
                use canceled = new System.Threading.CancellationTokenSource()
                canceled.Cancel()
                let ct = canceled.Token

                let calls: (unit -> Task) list =
                    [
                        (fun () -> server |> Server.findOrCreate ct :> Task)
                        (fun () -> server |> Server.findOrCreateSession ct "keeper" None :> Task)
                        (fun () -> session |> Session.findOrCreateWindow ct window.Name None :> Task)
                        (fun () -> window |> Window.findOrCreatePane ct "worker" None :> Task)
                        (fun () -> server |> Server.adopt ct :> Task)
                        (fun () -> session |> Session.adopt ct :> Task)
                        (fun () -> window |> Window.adopt ct :> Task)
                        (fun () -> pane |> Pane.adopt ct :> Task)
                        (fun () ->
                            ServerDiscoveryOptions(Roots = [| root |], IncludeConfiguredRoots = false)
                            |> Server.discover ct
                            :> Task)
                    ]

                for call in calls do
                    let! error = Assert.ThrowsAnyAsync<OperationCanceledException>(fun () -> call ())
                    Assert.Equal(ct, error.CancellationToken)

                let! remaining = server.GetPanesAsync(token)
                Assert.Equal(pane.Id, Assert.Single(remaining).Id)
            })
