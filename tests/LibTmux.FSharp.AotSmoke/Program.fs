open System
open System.Globalization
open System.IO
open System.Threading
open LibTmux
open LibTmux.FSharp
open LibTmux.Testing

let private socketRoot = "/tmp/libtmux-dotnet-test"

let private createOptions () =
    Directory.CreateDirectory(socketRoot) |> ignore
    Environment.SetEnvironmentVariable("TMUX_TMPDIR", socketRoot)
    Environment.SetEnvironmentVariable("TMPDIR", socketRoot)

    let connection =
        ServerConnectionOptions(
            TmuxBinaryPath =
                (Environment.GetEnvironmentVariable("LIBTMUX_TMUX")
                 |> Option.ofObj
                 |> Option.defaultValue "tmux"),
            SocketName = ("libtmux-fsharp-aot-" + Guid.NewGuid().ToString("N"))[..23],
            ConfigurationFile = "/dev/null"
        )

    TmuxTestOptions(connectionOptions = connection)

[<EntryPoint>]
let main _ =
    let run =
        task {
            if OperatingSystem.IsWindows() then
                Console.Error.WriteLine("tmux does not run on Windows.")
                return 1
            else
                use! scope =
                    TmuxTestFactory().CreateHierarchyAsync(createOptions (), CancellationToken.None)

                let! captured =
                    scope.Server |> Server.capture CancellationToken.None SnapshotDepth.Panes

                let commands = captured.Panes |> Seq.choose Pane.currentCommand |> Seq.toList

                // Portable filters bind catalog accessors, so trimming keeps them.
                let withPane =
                    captured.Sessions
                    |> Query.matching (
                        Filter.allOf
                            [
                                SessionFields.name |> Filter.containsIgnoreCase ""
                                PaneFields.currentCommand
                                |> Filter.matches "."
                                |> Filter.any WindowFields.panes
                                |> Filter.any SessionFields.windows
                            ]
                    )

                // FSharp.Core's Result prints itself with printf, which NativeAOT
                // rejects, so trimmed code reads cardinality as an option.
                let! sole =
                    scope.Server |> Server.sessions |> Query.tryExactlyOne CancellationToken.None

                let agree =
                    match sole, captured.Sessions |> Seq.tryExactlyOne with
                    | Some live, Some local -> live.Id.Equals(local.Id)
                    | _ -> false

                Console.WriteLine(
                    "panes "
                    + commands.Length.ToString(CultureInfo.InvariantCulture)
                    + " matched "
                    + withPane.Count.ToString(CultureInfo.InvariantCulture)
                )

                return
                    if commands.Length.Equals(1) && withPane.Count.Equals(1) && agree then
                        0
                    else
                        1
        }

    run.GetAwaiter().GetResult()
