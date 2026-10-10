// fsharp-snippet: FindOrCreateSession
open System
open System.Threading
open LibTmux
open LibTmux.FSharp

let runAsync () =
    task {
        use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 30.)
        let token = deadline.Token
        let server = LibTmux.Server.Open()

        let! selected =
            server
            |> Server.findOrCreateSession token "build" (Some(NewSessionRequest(Command = "/bin/sh")))

        match selected with
        | FindOrCreate.Existing _ -> printfn "session: existing"
        | FindOrCreate.Created _ -> printfn "session: created"

        do!
            selected
            |> FindOrCreate.withResource token (fun session ->
                task {
                    let! window =
                        session
                        |> Session.findOrCreateWindow token "tests" (Some(NewWindowRequest(Command = "/bin/sh")))

                    do!
                        window
                        |> FindOrCreate.withResource token (fun value ->
                            task {
                                printfn "window: %s" value.Name
                                let! windows = session |> Session.windows |> Query.list token
                                printfn "windows: %d" windows.Count
                            })
                })
    }

try
    runAsync().GetAwaiter().GetResult()
with error ->
    error
    |> Control.cleanupFailure
    |> Option.iter (fun cleanup -> eprintfn "Cleanup failed: %O" cleanup)

    reraise ()
// endfsharp-snippet
