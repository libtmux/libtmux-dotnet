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

                Console.WriteLine("panes " + commands.Length.ToString(CultureInfo.InvariantCulture))

                return if commands.Length.Equals(1) then 0 else 1
        }

    run.GetAwaiter().GetResult()
