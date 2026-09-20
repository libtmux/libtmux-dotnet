open System
open System.IO
open System.Reflection
open System.Threading
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
