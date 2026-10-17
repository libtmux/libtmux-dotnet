// fsharp-snippet: WatchPanes
open System
open System.Threading
open LibTmux
open LibTmux.FSharp

let runAsync () =
    task {
        use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 20.)
        let token = deadline.Token

        let options =
            ServerConnectionOptions(
                SocketName =
                    "fsharp-watch-panes-" + Guid.NewGuid().ToString("N"),
                ConfigurationFile = "/dev/null"
            )

        use! owned = options |> Server.createOwned token

        let! session =
            owned.Value
            |> Server.newSession
                token
                (SessionSpec.running "work" "exec sleep 60")

        // The client buffers everything from the moment it attaches, so the
        // panes it should see can start afterwards.
        use! client = session |> Control.enterSession token
        let! first = session |> Session.panes |> Query.list token

        let start command =
            first[0] |> Pane.split token (SplitPaneRequest(Command = command))

        let! build = start "printf 'build: ok\\n'; exec sleep 60"
        let! test = start "printf 'test: ok\\n'; exec sleep 60"

        // One client, both panes: each output event names its pane.
        let! printed =
            client
            |> Control.watchPanes [ build; test ]
            |> Control.foldWhile
                token
                (fun (printed: Map<string, string>) event ->
                    task {
                        match event with
                        | PaneWatch.Output output ->
                            let pane = output.PaneId.ToString()

                            let sofar =
                                printed
                                |> Map.tryFind pane
                                |> Option.defaultValue ""

                            let printed =
                                printed |> Map.add pane (sofar + output.Data)

                            if
                                printed.Count = 2
                                && printed
                                   |> Map.forall (fun _ text ->
                                       text.Contains '\n')
                            then
                                return StreamStep.Stop printed
                            else
                                return StreamStep.Continue printed
                        | _ -> return StreamStep.Continue printed
                    })
                Map.empty

        // Each pane's end arrives as PaneWatch.Gone, and the stream ends
        // once both are gone.
        do! build |> Pane.kill token
        do! test |> Pane.kill token

        let! ended =
            client
            |> Control.watchPanes [ build; test ]
            |> Control.foldWhile
                token
                (fun ended event ->
                    task {
                        match event with
                        | PaneWatch.Gone pane ->
                            return StreamStep.Continue(ended @ [ pane ])
                        | _ -> return StreamStep.Continue ended
                    })
                []

        for pane in [ build; test ] do
            printfn "%s" (printed[pane.Id.ToString()].Trim())

        printfn "ended in order given: %b" (ended = [ build.Id; test.Id ])
    }

runAsync().GetAwaiter().GetResult()
// endfsharp-snippet
