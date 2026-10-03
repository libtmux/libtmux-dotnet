open System
open System.IO
open System.Reflection
open System.Threading
open System.Threading.Tasks
open LibTmux
open LibTmux.FSharp
open LibTmux.Testing
open LibTmux.FSharp.Examples

let private exactlyOne label source =
    source
    |> Selection.exactlyOne
    |> Result.defaultWith (fun error -> failwithf "%s: %A" label error)

let private verifyPackedAssembly () =
    if Environment.GetEnvironmentVariable("LIBTMUX_FSHARP_EXPECT_PACKED") = "1" then
        let assembly = typeof<Filter<Pane>>.Assembly

        let expected =
            Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            |> Seq.find (fun attribute -> attribute.Key = "ExpectedPackageVersion")
            |> fun attribute -> attribute.Value
            |> Option.ofObj
            |> Option.defaultWith (fun () -> failwith "The example has no expected package version.")

        let version =
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            |> Option.ofObj
            |> Option.defaultWith (fun () -> failwith "The F# package has no informational version.")
            |> fun attribute -> attribute.InformationalVersion

        let cache =
            Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            |> Option.ofObj
            |> Option.filter (String.IsNullOrWhiteSpace >> not)
            |> Option.defaultWith (fun () -> failwith "Set NUGET_PACKAGES to an isolated cache.")

        let restored =
            Directory.EnumerateFiles(
                Path.Combine(cache, "libtmux.fsharp", expected, "lib"),
                "LibTmux.FSharp.dll",
                SearchOption.AllDirectories
            )

        let loaded = File.ReadAllBytes(assembly.Location)

        let exactVersion =
            version = expected
            || version.StartsWith(expected + "+", StringComparison.Ordinal)

        if
            assembly.GetName().Name <> "LibTmux.FSharp"
            || not exactVersion
            || restored |> Seq.exists (fun path -> File.ReadAllBytes(path) = loaded) |> not
        then
            failwith "The F# example did not load the expected packed LibTmux.FSharp assembly."

let private verifyBoundedCleanupAsync (cancellationToken: CancellationToken) =
    task {
        let mutable started = 0
        let mutable active = 0
        let failure = InvalidOperationException("expected bounded worker failure")

        let failingWork (_: CancellationToken) value =
            task {
                Interlocked.Increment(&started) |> ignore
                Interlocked.Increment(&active) |> ignore

                try
                    do! Task.Yield()

                    if value <= 2 then
                        raise failure

                    return value
                finally
                    Interlocked.Decrement(&active) |> ignore
            }

        let failing =
            GuideSnippets.boundedMapAsync 2 cancellationToken failingWork [ 1; 2; 3; 4 ]

        let mutable observedFailure = false

        try
            let! _ = failing.WaitAsync(TimeSpan.FromMilliseconds 750., cancellationToken)
            ()
        with :? InvalidOperationException as error when obj.ReferenceEquals(error, failure) ->
            observedFailure <- true

        if not observedFailure || started <> 4 || active <> 0 then
            failwith "The bounded helper did not drain all workers after failure."

        use canceled = new CancellationTokenSource()
        let token = canceled.Token

        let entered =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let held =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let mutable cancelStarted = 0
        let mutable cancelActive = 0
        let mutable cancelMaximum = 0
        let maximumLock = obj ()

        let cancelableWork (workToken: CancellationToken) value =
            task {
                let active = Interlocked.Increment(&cancelActive)
                lock maximumLock (fun () -> cancelMaximum <- max cancelMaximum active)

                if Interlocked.Increment(&cancelStarted) = 2 then
                    entered.TrySetResult() |> ignore

                try
                    do! held.Task.WaitAsync(workToken)
                    return value
                finally
                    Interlocked.Decrement(&cancelActive) |> ignore
            }

        let pending = GuideSnippets.boundedMapAsync 2 token cancelableWork [ 1; 2; 3; 4 ]

        try
            do! entered.Task.WaitAsync(TimeSpan.FromMilliseconds 750., cancellationToken)

            if cancelStarted <> 2 || cancelMaximum <> 2 then
                failwith "The bounded helper did not hold two workers before cancellation."

            canceled.Cancel()

            let mutable observedCancellation = false

            try
                let! _ = pending.WaitAsync(TimeSpan.FromMilliseconds 750., cancellationToken)
                ()
            with :? OperationCanceledException as error when error.CancellationToken = token ->
                observedCancellation <- true

            if
                not observedCancellation
                || not pending.IsCanceled
                || cancelMaximum > 2
                || cancelActive <> 0
            then
                failwithf
                    "Bounded cancellation: observed=%b taskCanceled=%b started=%d maximum=%d active=%d status=%A"
                    observedCancellation
                    pending.IsCanceled
                    cancelStarted
                    cancelMaximum
                    cancelActive
                    pending.Status
        finally
            canceled.Cancel()
            held.TrySetCanceled(token) |> ignore
    }

let private runAsync () =
    task {
        verifyPackedAssembly ()
        use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 10.)
        let cancellationToken = deadline.Token

        let tmuxBinary =
            Environment.GetEnvironmentVariable("LIBTMUX_TMUX")
            |> Option.ofObj
            |> Option.defaultValue "tmux"

        let connection =
            ServerConnectionOptions(
                SocketName = "libtmux-fsharp-example-" + Guid.NewGuid().ToString("N"),
                TmuxBinaryPath = tmuxBinary
            )

        use! scope =
            TmuxTestFactory().CreateHierarchyAsync(TmuxTestOptions(connection), cancellationToken)

        let! ownedCommands = GuideSnippets.readOwnedPaneCommandsAsync cancellationToken

        match ownedCommands with
        | [ command ] when not (String.IsNullOrWhiteSpace command) -> ()
        | _ -> failwithf "The package README did not read one owned pane command: %A" ownedCommands

        let! ownedPaneIds, foundPaneId =
            GuideSnippets.inspectOwnedSessionAsync cancellationToken

        if
            ownedPaneIds.Length <> 2
            || not (foundPaneId |> Option.exists (fun id -> List.contains id ownedPaneIds))
        then
            failwith "The getting-started guide did not find its new pane."

        let! captured = scope.Server |> Server.capture cancellationToken SnapshotDepth.Panes

        let command =
            captured.Panes
            |> Seq.choose Pane.currentCommand
            |> exactlyOne "captured pane command"

        let! guideCommands =
            GuideSnippets.readPaneCommandsAsync cancellationToken scope.Server

        if guideCommands |> List.contains command |> not then
            failwith "The getting-started guide did not read the captured pane command."

        let! guideMatches =
            GuideSnippets.readMatchingSessionNamesAsync [ command ] cancellationToken scope.Server

        if guideMatches |> List.isEmpty then
            failwith "The capture-and-filter guide did not match the owned session."

        let! _ = GuideSnippets.readEditorSessionNamesAsync cancellationToken scope.Server

        let chainOutput = GuideSnippets.readChainOutputAsync cancellationToken scope.Server

        let! chained = chainOutput

        if chained <> [ "fsharp-chain-first"; "fsharp-chain-second" ] then
            failwith "The chaining guide did not preserve command order."

        let! indexedOption, inheritedOption, hookIndex, renderedFormat =
            GuideSnippets.inspectCoreSettingsAsync cancellationToken scope.Server scope.Session

        printfn
            "Core interop: command-alias[%d], inherited status-keys=%s, hook[%d], %s"
            indexedOption
            inheritedOption
            hookIndex
            renderedFormat

        do!
            GuideSnippets.exerciseCoreOperationsAsync
                cancellationToken
                scope.Server
                scope.Session
                scope.Window
                scope.Pane

        do! GuideSnippets.exerciseWindowInputAsync cancellationToken scope.Session

        let encodedFilter = GuideSnippets.encodeEditorPaneFilter ()
        let decodedFilter = GuideSnippets.decodeFilter encodedFilter

        if
            decodedFilter
            <> Filter.toDocument (Filter.oneOf [ "nvim"; "vim" ] PaneFields.currentCommand)
        then
            failwith "The JSON guide did not round trip the portable filter."

        let firstTwoEntered =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let mutable entered = 0
        let mutable inFlight = 0
        let mutable maximumInFlight = 0
        let maximumLock = obj ()

        let recordMaximum value =
            lock maximumLock (fun () -> maximumInFlight <- max maximumInFlight value)

        let work (token: CancellationToken) (value: int) =
            task {
                let active = Interlocked.Increment(&inFlight)
                recordMaximum active

                if Interlocked.Increment(&entered) = 2 then
                    firstTwoEntered.TrySetResult() |> ignore

                do! firstTwoEntered.Task.WaitAsync(token)
                Interlocked.Decrement(&inFlight) |> ignore
                return value * value
            }

        let! squared = GuideSnippets.boundedMapAsync 2 cancellationToken work [ 1; 2; 3; 4 ]

        if squared <> [ 1; 4; 9; 16 ] || maximumInFlight <> 2 then
            failwith "The bounded-concurrency guide did not preserve its bound and input order."

        do! verifyBoundedCleanupAsync cancellationToken

        let! captures =
            GuideSnippets.capturePanesBoundedAsync 2 cancellationToken captured.Panes

        if captures.Length <> captured.Panes.Count then
            failwith "The bounded capture guide did not complete every pane read."

        let native =
            captured.Panes
            |> Seq.filter (fun pane -> Pane.currentCommand pane = Some command)
            |> Seq.toArray

        let portable =
            captured.Panes |> Query.matching (Filter.eq command PaneFields.currentCommand)

        if
            portable.Count <> native.Length
            || portable
               |> Seq.exists2 (fun (left: Pane) (right: Pane) -> left.Id <> right.Id) native
        then
            failwith "The portable filter did not match the native captured-pane query."

        let! watchedPane =
            scope.Pane
            |> Pane.split cancellationToken (SplitPaneRequest(Command = "/bin/sh"))

        let! watcher = scope.Session |> Control.enterSession cancellationToken

        let! watched =
            watcher
            |> Control.useSession (fun control ->
                task {
                    let reading =
                        GuideSnippets.readPaneUntilAsync cancellationToken "watched" watchedPane control

                    do!
                        watchedPane
                        |> Pane.sendKeys
                            cancellationToken
                            (SendKeysRequest(Text = "printf 'watch''ed\\n'", Literal = true))

                    return! reading
                })

        if not (watched.Contains "watched") then
            failwithf "The pane watch guide did not read the pane's output: %A" watched

        let! observed =
            scope.Server
            |> Control.withSession cancellationToken (fun control ->
                task {
                    let events = GuideSnippets.readUntilTerminalAsync cancellationToken control
                    do! scope.Server.KillAsync(cancellationToken)
                    return! events
                })

        match List.tryLast observed with
        | Some(:? TmuxExitEvent) -> ()
        | _ -> failwith "The control-mode guide did not observe tmux exiting."

        printfn "PASS F# snapshot and portable query example"
    }

[<EntryPoint>]
let main _ =
    if OperatingSystem.IsWindows() then
        Console.Error.WriteLine("The F# tmux example requires tmux on Linux or macOS.")
        1
    else
        (runAsync ()).GetAwaiter().GetResult()
        0
