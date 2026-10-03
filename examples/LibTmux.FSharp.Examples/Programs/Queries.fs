// fsharp-snippet: Queries
open System
open System.Threading
open LibTmux
open LibTmux.FSharp

let runAsync () =
    task {
        use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 10.)
        let token = deadline.Token

        let options =
            ServerConnectionOptions(
                SocketName = "fsharp-queries-" + Guid.NewGuid().ToString("N"),
                ConfigurationFile = "/dev/null",
                TmuxBinaryPath =
                    (Environment.GetEnvironmentVariable "LIBTMUX_TMUX"
                     |> Option.ofObj
                     |> Option.defaultValue "tmux")
            )

        use! owned = options |> Server.createOwned token

        let! build =
            owned.Value.CreateSessionAsync(
                NewSessionRequest(Name = "build", WindowName = "make", Command = "/bin/sh"),
                token
            )

        let! _ =
            owned.Value.CreateSessionAsync(
                NewSessionRequest(
                    Name = "logs",
                    WindowName = "tail",
                    Command = "printf 'ERROR: disk full\\n'; exec sleep 60"
                ),
                token
            )

        let! server = options |> Server.connect token

        let! logs =
            server
            |> Server.sessions
            |> Query.where (SessionFields.name |> Filter.eq "logs")
            |> Query.list token

        let! logPane = logs[0] |> Session.panes |> Query.list token

        let! logged = logPane[0] |> Pane.waitForText token (TimeSpan.FromSeconds 5.) "ERROR:"


        // tmux narrows each listing itself; every row is then rechecked.
        let! named =
            server
            |> Server.sessions
            |> Query.where (SessionFields.name |> Filter.startsWith "bu")
            |> Query.exactlyOne token

        // A relation filter reads only the sessions whose windows can match.
        let! tailing =
            server
            |> Server.sessions
            |> Query.where (WindowFields.name |> Filter.eq "tail" |> Filter.any SessionFields.windows)
            |> Query.list token

        // Session and window scopes use the same functions.
        let! make =
            build
            |> Session.windows
            |> Query.where (WindowFields.name |> Filter.eq "make")
            |> Query.tryExactlyOne token

        // tryExactlyOne is None when no window, or several, matched.
        let! makePanes =
            task {
                match make with
                | Some window ->
                    let! panes = window |> Window.panes |> Query.list token
                    return Some panes.Count
                | None -> return None
            }

        // tmux searches each pane's visible rows, as find-window does.
        let! showingErrors =
            server
            |> Server.panes
            |> Query.showing (ScreenSearch.Text "ERROR:")
            |> Query.list token

        let! errorRow =
            showingErrors[0] |> Pane.findOnScreen token (ScreenSearch.Text "disk full")

        // A raw tmux filter is the escape hatch; nothing rechecks it.
        let! active =
            server
            |> Server.panes
            |> Query.whereUnsafe (UnsafeTmuxFilter "#{pane_active}")
            |> Query.list token

        // Find a session or create it: atMostOne is None only when nothing
        // matched, and raises when several do.
        let deploy =
            server
            |> Server.sessions
            |> Query.where (SessionFields.name |> Filter.eq "deploy")

        let! existing = deploy |> Query.atMostOne token

        if existing.IsNone then
            let! _ =
                owned.Value.CreateSessionAsync(NewSessionRequest(Name = "deploy", Command = "exec sleep 60"), token)

            ()

        let! found = deploy |> Query.atMostOne token

        let! several =
            task {
                try
                    let! _ = server |> Server.sessions |> Query.atMostOne token
                    return "one or none"
                with :? InvalidOperationException ->
                    return "refused"
            }

        printfn "logged: %b" logged.Found
        printfn "named: %A" (named |> Result.map (fun session -> session.Name))
        printfn "tailing: %A" [ for session in tailing -> session.Name ]
        printfn "make panes: %A" makePanes
        printfn "error row: %A" errorRow
        printfn "active panes: %d" active.Count
        printfn "deploy: absent %b, then found %b" existing.IsNone found.IsSome
        printfn "at most one of every session: %s" several

    }

runAsync().GetAwaiter().GetResult()
// endfsharp-snippet
