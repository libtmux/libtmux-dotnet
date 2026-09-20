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
