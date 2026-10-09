open System
open System.Threading
open LibTmux
open LibTmux.FSharp

let check label condition =
    if not condition then
        failwith label

    printfn "PASS %s" label

let run () =
    task {
        use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 10.)
        let token = deadline.Token

        let binary =
            match Environment.GetEnvironmentVariable "LIBTMUX_TMUX" with
            | null
            | "" -> "tmux"
            | value -> value

        let options =
            ServerConnectionOptions(
                SocketName = "libtmux-fsharp-sdk8-" + Guid.NewGuid().ToString("N"),
                ConfigurationFile = "/dev/null",
                TmuxBinaryPath = binary
            )

        use! scope = LibTmux.Server.CreateOwnedAsync(options, token)
        let server = scope.Value

        let! created =
            server.CreateSessionAsync(NewSessionRequest(Name = "sdk8", Command = "/bin/sh"), token)

        let! scopedId =
            server
            |> Server.withNewSession token (SessionSpec.running "scoped" "/bin/sh") (fun session ->
                task { return session.Id })

        let! scopedSession = server |> Server.tryFindSession token scopedId
        check "session scope works with the SDK's FSharp.Core" scopedSession.IsNone

        let! sessions = server |> Server.sessions |> Query.list token

        let named =
            sessions
            |> Query.matching (SessionFields.name |> Filter.startsWith "sdk")
            |> Selection.exactlyOne

        check
            "filters and selects with the SDK's FSharp.Core"
            (match named with
             | Ok session -> session.Id = created.Id
             | Error _ -> false)

        let! found = server |> Server.tryFindSession token created.Id
        check "runs task helpers with the SDK's FSharp.Core" (found |> Option.exists (fun s -> s.Id = created.Id))
    }

[<EntryPoint>]
let main _ =
    let fsharpCore = typeof<option<int>>.Assembly.GetName().Version
    check $"runs on .NET 8 with FSharp.Core {fsharpCore}" (Environment.Version.Major = 8 && fsharpCore.Major = 8)
    run().GetAwaiter().GetResult()
    0
