open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Reflection
open System.Threading
open System.Threading.Tasks
open LibTmux
open LibTmux.FSharp
open LibTmux.Testing

let check label condition =
    if not condition then
        failwith label

    printfn "PASS %s" label

let verifyPackage () =
    let assembly = typeof<CardinalityError>.Assembly

    let expected =
        Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
        |> Seq.find (fun attribute -> attribute.Key = "ExpectedPackageVersion")
        |> fun attribute -> attribute.Value
        |> Option.ofObj
        |> Option.defaultWith (fun () -> failwith "The consumer has no expected package version.")

    let version =
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        |> Option.ofObj
        |> Option.defaultWith (fun () -> failwith "The F# assembly has no informational version.")
        |> fun attribute -> attribute.InformationalVersion

    let exactVersion =
        version = expected
        || version.StartsWith(expected + "+", StringComparison.Ordinal)

    check "packed F# assembly and suite version" (assembly.GetName().Name = "LibTmux.FSharp" && exactVersion)

    let cache =
        Environment.GetEnvironmentVariable "NUGET_PACKAGES"
        |> Option.ofObj
        |> Option.filter (String.IsNullOrEmpty >> not)
        |> Option.defaultWith (fun () -> failwith "Set NUGET_PACKAGES to an isolated cache.")

    let loaded = File.ReadAllBytes assembly.Location

    let restored =
        Directory.EnumerateFiles(
            Path.Combine(cache, "libtmux.fsharp", expected, "lib"),
            "LibTmux.FSharp.dll",
            SearchOption.AllDirectories
        )

    check
        "loaded assembly matches restored package bytes"
        (restored |> Seq.exists (fun path -> File.ReadAllBytes path = loaded))

let runScenario mode =
    task {
        use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 10.)
        let token = deadline.Token

        let binary =
            match Environment.GetEnvironmentVariable "LIBTMUX_TMUX" with
            | null
            | "" -> "tmux"
            | value -> value

        let childEnvironment = Dictionary<string, string | null>()
        childEnvironment["TMUX"] <- null
        childEnvironment["TMUX_PANE"] <- null
        let mutable calls = 0

        let options =
            ServerConnectionOptions(
                SocketName = "libtmux-fsharp-" + Guid.NewGuid().ToString("N"),
                ConfigurationFile = "/dev/null",
                TmuxBinaryPath = binary,
                ChildEnvironment = childEnvironment,
                Interceptor =
                    TmuxInterceptor(fun _ next ct ->
                        Interlocked.Increment(&calls) |> ignore
                        next.Invoke(ct))
            )

        let mutable serverProcessHandle: Process option = None
        let mutable initialPlacement: WindowPlacementKey option = None
        let mutable observed = "success"

        let work () =
            task {
                use! context = TmuxTestFactory().CreateContextAsync(TmuxTestOptions(options), token)
                let server = context.Server

                let! first =
                    server.CreateSessionAsync(NewSessionRequest(Name = "fsharp", Command = "/bin/sh"), token)

                let! scopedId =
                    server
                    |> Server.withNewSession token (SessionSpec.running "scoped" "/bin/sh") (fun session ->
                        Task.FromResult session.Id)

                let! scopedSession = server |> Server.tryFindSession token scopedId
                check "session task scope removes its created session" scopedSession.IsNone

                use! reused = server |> Server.findOrCreateSession token "fsharp" None

                match reused with
                | FindOrCreate.Existing value -> check "packed find-or-create preserves borrowing" (value.Id = first.Id)
                | FindOrCreate.Created _ -> failwith "An existing session was classified as created."

                let! selected =
                    server
                    |> Server.findOrCreateSession token "owned" (Some(NewSessionRequest(Command = "/bin/sh")))

                let! ownedId =
                    selected
                    |> FindOrCreate.withResource token (fun session ->
                        task {
                            let! pane = session |> Session.activePane token
                            let! owner = pane |> Pane.adopt token
                            do! owner |> Owned.withResource token (fun _ -> Task.FromResult())
                            return session.Id
                        })

                let! removed = server |> Server.tryFindSession token ownedId
                check "packed created scope and adopted pane clean up" removed.IsNone

                let! sessions = server.GetSessionsAsync(token)

                check
                    "core session discovery works from F#"
                    (sessions |> Seq.exists (fun listed -> listed.Id = first.Id))

                let! clients = server.GetClientsAsync(token)
                check "detached core client discovery works from F#" (clients.Count = 0)

                let! snapshot = server |> Server.capture token SnapshotDepth.Panes
                serverProcessHandle <- Some(Process.GetProcessById(snapshot.Generation.Value.ProcessId))

                let mutable clientProcess: Process option = None
                use scopedCancellation = new CancellationTokenSource()

                try
                    let! controlState =
                        task {
                            try
                                return!
                                    server
                                    |> Control.withSession token (fun session ->
                                        task {
                                            let! liveClients = server.GetClientsAsync(token)

                                            let controlClient =
                                                liveClients
                                                |> Seq.filter (fun client -> client.IsControlClient)
                                                |> Selection.exactlyOne
                                                |> Result.defaultWith (fun error ->
                                                    failwithf "Expected one control client: %A" error)

                                            let clientProcessId =
                                                controlClient.RawFormatFields["client_pid"]
                                                |> Option.ofObj
                                                |> Option.map Int32.Parse
                                                |> Option.defaultWith (fun () ->
                                                    failwith "The control client has no process ID.")

                                            clientProcess <- Some(Process.GetProcessById(clientProcessId))

                                            if mode = "failure" then
                                                raise (InvalidOperationException "expected control scope failure")

                                            if mode = "cancellation" then
                                                scopedCancellation.Cancel()
                                                do! Task.FromCanceled(scopedCancellation.Token)

                                            let! lines =
                                                session.SendAsync(
                                                    TmuxCommand.Create("display-message", "-p", "scoped"),
                                                    token
                                                )

                                            return StreamStep.Stop lines.Count
                                        })
                            with
                            | :? InvalidOperationException as error when
                                mode = "failure" && error.Message = "expected control scope failure"
                                ->
                                return StreamStep.Stop -1
                            | :? OperationCanceledException as error when
                                mode = "cancellation" && error.CancellationToken = scopedCancellation.Token
                                ->
                                return StreamStep.Stop -2
                        }

                    let observedClient =
                        clientProcess
                        |> Option.defaultWith (fun () -> failwith "No control client process was observed.")

                    do! observedClient.WaitForExitAsync(token)

                    if mode = "failure" then
                        check "failed control scope closed client" observedClient.HasExited

                    if mode = "cancellation" then
                        check "canceled control scope closed client" observedClient.HasExited

                    check "owned control client exited" observedClient.HasExited

                    match mode, controlState with
                    | "failure", StreamStep.Stop -1 -> ()
                    | "cancellation", StreamStep.Stop -2 -> ()
                    | "success", StreamStep.Stop 1 -> check "owned control scope returns a typed state" true
                    | _ -> failwith "The owned control scope did not return its command result."
                finally
                    clientProcess |> Option.iter (fun client -> client.Dispose())

                let! ordinalNear =
                    server.CreateSessionAsync(NewSessionRequest(Name = "FSharp-ordinal", Command = "/bin/sh"), token)

                let! ordinalSnapshot = server |> Server.capture token SnapshotDepth.Sessions

                let nativeOrdinal =
                    ordinalSnapshot.Sessions
                    |> Seq.filter (fun session -> session.Name.StartsWith("fsharp", StringComparison.Ordinal))
                    |> Seq.toArray

                let portableOrdinal =
                    ordinalSnapshot.Sessions
                    |> Query.matching (Filter.startsWith "fsharp" SessionFields.name)

                check
                    "portable string filters preserve ordinal case semantics"
                    (nativeOrdinal.Length = 1
                     && nativeOrdinal[0].Id = first.Id
                     && portableOrdinal.Count = nativeOrdinal.Length
                     && portableOrdinal[0].Id = first.Id
                     && portableOrdinal |> Seq.forall (fun session -> session.Id <> ordinalNear.Id))

                let pane =
                    snapshot.Panes
                    |> Selection.exactlyOne
                    |> Result.defaultWith (fun error -> failwithf "%A" error)

                initialPlacement <- Some(Window.placementKey pane.Window)
                let before = calls
                let paths = snapshot.Panes |> Seq.choose Pane.currentPath |> Seq.toList
                let commands = snapshot.Panes |> Seq.choose Pane.currentCommand |> Seq.toList

                let placements =
                    snapshot.Windows
                    |> Seq.map (fun window -> Window.placementKey window, window)
                    |> Map.ofSeq

                check
                    "native queries and comparable keys perform no I/O"
                    (calls = before
                     && paths.Length = 1
                     && commands.Length = 1
                     && placements.Count = 1)

                match Snapshot.relation snapshot.Panes with
                | Captured panes -> check "captured relation is usable from installed signature" (panes.Count = 1)
                | Uncaptured _ -> failwith "The pane relation was not captured."

                let! livePanes = server |> Server.panes |> Query.list token
                check "task pipeline lists the live pane" (livePanes |> Seq.exists (fun live -> live.Id = pane.Id))

                let! found = server |> Server.tryFindPane token pane.Id
                check "successful lookup uses Some" (found |> Option.exists (fun live -> live.Id = pane.Id))

                let! absent = server |> Server.tryFindPane token (PaneId 999999)
                check "successful absence uses option" absent.IsNone
                let! lines = pane |> Pane.capture token (CapturePaneRequest())
                check "task pipeline captures a live pane" (lines.Count > 0)

                if mode = "success" then
                    let! split =
                        pane |> Pane.split token (SplitPaneRequest(Command = "/bin/sh", Attach = false))

                    check "curried split returns a new pane" (split.Id <> pane.Id)
                    use channel = server.OpenWaitChannel("fsharp-capture")
                    let completion = channel.WaitAsync(TimeSpan.FromMilliseconds 900., token)
                    let quotedBinary = "'" + binary.Replace("'", "'\\''") + "'"
                    let socketName = options.SocketName |> Option.ofObj |> Option.get

                    let text =
                        "printf 'FSHARP_%s\\n' 'PACKED'; "
                        + quotedBinary
                        + " -L "
                        + socketName
                        + " wait-for -S fsharp-capture"

                    do!
                        pane
                        |> Pane.sendKeys token (SendKeysRequest(Text = text, Literal = true, Enter = true))

                    let! completed = completion
                    check "sent command reached its completion marker" completed
                    let! output = pane |> Pane.capture token (CapturePaneRequest())

                    check
                        "literal send and capture agree"
                        (output |> Seq.exists (fun line -> line.TrimEnd() = "FSHARP_PACKED"))

                    let! second =
                        server.CreateSessionAsync(NewSessionRequest(Name = "linked", Command = "/bin/sh"), token)

                    for target in [ "fsharp:7"; "linked:9" ] do
                        let! linked =
                            server.ExecuteCommandAsync(
                                [| "link-window"; "-s"; pane.Window.Id.ToString(); "-t"; target |],
                                token
                            )

                        check "core link escape hatch succeeds" (linked.ExitCode = 0)

                    let! linkedSnapshot = server |> Server.capture token SnapshotDepth.Panes

                    let occurrences =
                        linkedSnapshot.Windows
                        |> Seq.filter (fun window -> window.Id = pane.Window.Id)
                        |> Seq.toArray

                    let keys = occurrences |> Array.map Window.placementKey

                    let expected =
                        set [ (first.Id.Value, 0); (first.Id.Value, 7); (second.Id.Value, 9) ]

                    let actual =
                        occurrences
                        |> Seq.map (fun window -> window.Edge.SessionId.Value, window.Edge.WindowIndex)
                        |> Set.ofSeq

                    check
                        "comparable keys retain three placements"
                        (keys |> Set.ofArray |> Set.count = 3 && actual = expected)

                    check
                        "key comparison agrees with equality"
                        (keys
                         |> Array.forall (fun left ->
                             keys |> Array.forall (fun right -> (compare left right = 0) = (left = right))))

                    check
                        "each pane retains its exact parent placement"
                        (occurrences
                         |> Array.forall (fun window ->
                             window.Panes
                             |> Seq.forall (fun child -> Window.placementKey child.Window = Window.placementKey window)))

                    match Snapshot.value occurrences[0].ActivePane with
                    | Captured active ->
                        check "captured value survives the package boundary" (active.Window.Id = pane.Window.Id)
                    | Uncaptured _ -> failwith "The active pane was not captured."

                    let samePane = Filter.eq pane.Id PaneFields.id
                    let mutable enumerations = 0

                    let source =
                        seq {
                            enumerations <- enumerations + 1

                            for window in occurrences do
                                yield! window.Panes
                        }

                    let matched = source |> Query.matching samePane

                    let matchedKeys =
                        matched
                        |> Seq.map (fun child -> Window.placementKey child.Window)
                        |> Seq.toArray

                    check "portable matching retains ordered placements" (matchedKeys = keys)
                    check "portable matching materializes once" (matched |> Seq.length = 3 && enumerations = 1)

                    check
                        "exactly-one counts contextual matches"
                        (matched |> Selection.exactlyOne = Error MultipleMatches)

                    let accepts = Filter.toPredicate samePane

                    check
                        "compiled predicate works in lazy native filtering"
                        (source |> Seq.filter accepts |> Seq.length = 3 && enumerations = 2)

                    let partialFilter =
                        Filter.eq "nvim" PaneFields.currentCommand
                        |> Filter.any WindowFields.panes
                        |> Filter.any SessionFields.windows

                    let! windowsOnly = server |> Server.capture token SnapshotDepth.Windows
                    let beforeQueries = calls

                    let incompleteRelationRaised =
                        try
                            windowsOnly.Sessions |> Query.matching partialFilter |> ignore
                            false
                        with :? IncompleteSnapshotException ->
                            true

                    check
                        "portable matching preserves uncaptured relations instead of empty matches"
                        incompleteRelationRaised

                    let otherWindow =
                        linkedSnapshot.Sessions
                        |> Seq.find (fun session -> session.Id = second.Id)
                        |> fun session -> session.Windows
                        |> Seq.find (fun window -> window.Id <> pane.Window.Id)

                    let conditionA = Filter.eq pane.Window.Id WindowFields.id
                    let conditionB = Filter.eq otherWindow.Id WindowFields.id

                    let oneWitness =
                        Filter.allOf [ conditionA; conditionB ] |> Filter.any SessionFields.windows

                    let separateWitnesses =
                        Filter.allOf
                            [
                                conditionA |> Filter.any SessionFields.windows
                                conditionB |> Filter.any SessionFields.windows
                            ]

                    check
                        "one child must satisfy a grouped predicate"
                        ((linkedSnapshot.Sessions |> Query.matching oneWitness).Count = 0)

                    let separate = linkedSnapshot.Sessions |> Query.matching separateWitnesses

                    check
                        "separate existential predicates may use separate children"
                        (separate.Count = 1 && separate[0].Id = second.Id)

                    check "snapshot and portable queries perform no I/O" (beforeQueries = calls)

                match mode with
                | "failure" -> raise (InvalidOperationException "expected consumer failure")
                | "cancellation" ->
                    use canceled = new CancellationTokenSource()
                    canceled.Cancel()
                    let! _ = server |> Server.panes |> Query.list canceled.Token
                    failwith "Cancellation was not forwarded."
                | _ -> ()
            }

        try
            do! work ()
        with
        | :? InvalidOperationException as error when mode = "failure" && error.Message = "expected consumer failure" ->
            observed <- "failure"
        | :? OperationCanceledException when mode = "cancellation" -> observed <- "cancellation"

        check (mode + " result") (observed = mode)

        match serverProcessHandle with
        | Some serverProcess ->
            use ownedProcess = serverProcess
            do! ownedProcess.WaitForExitAsync(token)
            check (mode + " owned server exited") ownedProcess.HasExited
        | None -> failwith "No server process was observed."

        return initialPlacement |> Option.get
    }

[<EntryPoint>]
let main _ =
    let run =
        task {
            verifyPackage ()
            let keys = ResizeArray<WindowPlacementKey>()

            for mode in [ "success"; "failure"; "cancellation" ] do
                let! key = runScenario mode
                keys.Add key

            check "placement keys distinguish server generations" (keys |> Set.ofSeq |> Set.count = keys.Count)
        }

    run.GetAwaiter().GetResult()
    0
