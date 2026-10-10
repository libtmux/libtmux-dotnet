// fsharp-snippet: Quickstart
open System
open System.Threading
open LibTmux
open LibTmux.FSharp

let runAsync () =
    task {
        use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 30.)
        let token = deadline.Token
        let server = LibTmux.Server.Open()

        do!
            server
            |> Server.withNewSession token (SessionSpec.running "build" "/bin/sh") (fun session ->
                task {
                    let! window =
                        session
                        |> Session.newWindow token (NewWindowRequest(Name = "tests", Command = "/bin/sh"))

                    printfn "window: %s" window.Name
                    let! windows = session |> Session.windows |> Query.list token
                    printfn "windows: %d" windows.Count
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
