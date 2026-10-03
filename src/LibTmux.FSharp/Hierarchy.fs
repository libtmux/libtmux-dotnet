namespace LibTmux.FSharp

open System
open System.Collections.Generic
open System.Threading
open LibTmux
open LibTmux.Query

[<RequireQualifiedAccess>]
module Server =
    let createOwned (cancellationToken: CancellationToken) (options: ServerConnectionOptions) =
        LibTmux.Server.CreateOwnedAsync(options, cancellationToken)

    let connect (cancellationToken: CancellationToken) (options: ServerConnectionOptions) =
        LibTmux.Server.ConnectAsync(options, cancellationToken)

    let sessions (server: LibTmux.Server) =
        Query<LibTmux.Session>.Create(server, QueryTarget.Session, None, None)

    let windows (server: LibTmux.Server) =
        Query<LibTmux.Window>.Create(server, QueryTarget.Window, None, None)

    let panes (server: LibTmux.Server) =
        Query<LibTmux.Pane>.Create(server, QueryTarget.Pane, None, None)

    let clients (server: LibTmux.Server) =
        Query<LibTmux.Client>.Create(server, QueryTarget.Client, None, None)

    let within (timeout: TimeSpan) (server: LibTmux.Server) = server.Within(timeout)

    // An empty map adds no -e flags, the same as none.
    let private environment (values: Map<string, string>) : IReadOnlyDictionary<string, string> = values

    let private splitAll (cancellationToken: CancellationToken) (first: LibTmux.Pane) (splits: SplitSpec list) =
        backgroundTask {
            let mutable current = first

            for split in splits do
                let! next =
                    current.SplitAsync(
                        SplitPaneRequest(
                            Direction = Option.toNullable split.Direction,
                            Command = Option.toObj split.Command,
                            StartDirectory = Option.toObj split.Directory,
                            Size = Option.toObj split.Size,
                            Environment = environment split.Environment
                        ),
                        cancellationToken
                    )

                current <- next
        }

    let newSession (cancellationToken: CancellationToken) (spec: SessionSpec) (server: LibTmux.Server) =
        let first = List.tryHead spec.Windows
        let firstDirectory = first |> Option.bind (fun window -> window.Directory)

        match spec.Directory, firstDirectory with
        | Some session, Some window when session <> window ->
            raise (
                ArgumentException(
                    "The session and its first window name different directories; tmux starts that window in one.",
                    nameof spec
                )
            )
        | _ -> ()

        match first with
        | Some window when not window.Environment.IsEmpty ->
            raise (
                ArgumentException(
                    "tmux sets the first window's environment for the whole session; put it in the session's Environment.",
                    nameof spec
                )
            )
        | _ -> ()

        backgroundTask {
            let! session =
                server.CreateSessionAsync(
                    NewSessionRequest(
                        Name = spec.Name,
                        WindowName = (first |> Option.bind (fun window -> window.Name) |> Option.toObj),
                        Command = (first |> Option.bind (fun window -> window.Command) |> Option.toObj),
                        StartDirectory = (firstDirectory |> Option.orElse spec.Directory |> Option.toObj),
                        Environment = environment spec.Environment
                    ),
                    cancellationToken
                )

            match first with
            | Some window ->
                let! panes = session.GetPanesAsync(cancellationToken)
                do! splitAll cancellationToken panes[0] window.Splits
            | None -> ()

            for window in
                List.tail (
                    if spec.Windows.IsEmpty then
                        [ WindowSpec.empty ]
                    else
                        spec.Windows
                ) do
                let! created =
                    session.CreateWindowAsync(
                        NewWindowRequest(
                            Name = Option.toObj window.Name,
                            Command = Option.toObj window.Command,
                            StartDirectory = Option.toObj window.Directory,
                            Environment = environment window.Environment
                        ),
                        cancellationToken
                    )

                let! panes = created.GetPanesAsync(cancellationToken)
                do! splitAll cancellationToken panes[0] window.Splits

            return! session.RefreshAsync(cancellationToken)
        }

    let capture (cancellationToken: CancellationToken) depth (server: LibTmux.Server) =
        server.CaptureSnapshotAsync(depth, cancellationToken)

    let tryFindSession (cancellationToken: CancellationToken) id (server: LibTmux.Server) =
        backgroundTask {
            let! session = server.FindSessionAsync(id, cancellationToken)
            return Option.ofObj session
        }

    let tryFindWindow (cancellationToken: CancellationToken) id (server: LibTmux.Server) =
        backgroundTask {
            let! window = server.FindWindowAsync(id, cancellationToken)
            return Option.ofObj window
        }

    let tryFindPane (cancellationToken: CancellationToken) id (server: LibTmux.Server) =
        backgroundTask {
            let! pane = server.FindPaneAsync(id, cancellationToken)
            return Option.ofObj pane
        }

    let tryFindClient (cancellationToken: CancellationToken) name (server: LibTmux.Server) =
        backgroundTask {
            ArgumentException.ThrowIfNullOrWhiteSpace(name)
            let! clients = server.GetClientsAsync(cancellationToken)
            return clients |> Seq.tryFind (fun client -> client.Name = name)
        }

[<RequireQualifiedAccess>]
module Session =
    let windows (session: LibTmux.Session) =
        Query<LibTmux.Window>.Create(session.Server, QueryTarget.Window, Some session.Id, None)

    let panes (session: LibTmux.Session) =
        Query<LibTmux.Pane>.Create(session.Server, QueryTarget.Pane, Some session.Id, None)

[<RequireQualifiedAccess>]
module Window =
    let placementKey window = Placement.key window

    let panes (window: LibTmux.Window) =
        Query<LibTmux.Pane>.Create(window.Server, QueryTarget.Pane, None, Some window.Id)

[<RequireQualifiedAccess>]
module Pane =
    let currentPath (pane: LibTmux.Pane) = Option.ofObj pane.CurrentPath
    let currentCommand (pane: LibTmux.Pane) = Option.ofObj pane.CurrentCommand

    let capture (cancellationToken: CancellationToken) request (pane: LibTmux.Pane) =
        pane.CaptureAsync(request, cancellationToken)

    let findOnScreen (cancellationToken: CancellationToken) search (pane: LibTmux.Pane) =
        backgroundTask {
            let! row = pane.FindOnScreenAsync(ScreenSearch.toCore search, cancellationToken)
            return Option.ofNullable row
        }

    let waitForText (cancellationToken: CancellationToken) (timeout: TimeSpan) (text: string) (pane: LibTmux.Pane) =
        pane.WaitForTextAsync(text, timeout, cancellationToken)

    let waitFor (cancellationToken: CancellationToken) (request: PaneWaitRequest) (pane: LibTmux.Pane) =
        pane.WaitForTextAsync(request, cancellationToken)

    let sendAndWait
        (cancellationToken: CancellationToken)
        (timeout: TimeSpan)
        (line: string)
        (text: string)
        (pane: LibTmux.Pane)
        =
        pane.SendTextAndWaitAsync(line, text, timeout, cancellationToken)

    let sendAndWaitFor
        (cancellationToken: CancellationToken)
        (keys: SendKeysRequest)
        (request: PaneWaitRequest)
        (pane: LibTmux.Pane)
        =
        pane.SendKeysAndWaitAsync(keys, request, cancellationToken)

    let waitUntil
        (cancellationToken: CancellationToken)
        (timeout: TimeSpan)
        (condition: IReadOnlyList<string> -> bool)
        (pane: LibTmux.Pane)
        =
        pane.WaitUntilAsync(Func<_, _> condition, timeout, cancellationToken)

    let run (cancellationToken: CancellationToken) (timeout: TimeSpan) (command: string) (pane: LibTmux.Pane) =
        pane.RunAsync(command, timeout, cancellationToken)

    let sendLine (cancellationToken: CancellationToken) (line: string) (pane: LibTmux.Pane) =
        pane.SendTextAsync(line, true, cancellationToken)

    let sendText (cancellationToken: CancellationToken) (text: string) (pane: LibTmux.Pane) =
        pane.SendTextAsync(text, false, cancellationToken)

    let sendKeys (cancellationToken: CancellationToken) request (pane: LibTmux.Pane) =
        pane.SendKeysAsync(request, cancellationToken)

    let split (cancellationToken: CancellationToken) request (pane: LibTmux.Pane) =
        pane.SplitAsync(request, cancellationToken)

[<RequireQualifiedAccess>]
module Options =
    let get (cancellationToken: CancellationToken) (key: TmuxOptionKey<'T>) (options: TmuxOptions) =
        options.GetAsync(key, cancellationToken)

    let set (cancellationToken: CancellationToken) (key: TmuxOptionKey<'T>) (value: 'T) (options: TmuxOptions) =
        options.SetAsync(key, value, cancellationToken)

[<RequireQualifiedAccess>]
module Chain =
    let start (server: LibTmux.Server) = server.Chain()

    let newWindow (session: LibTmux.Session) (name: string) (chain: TmuxChain) =
        // The id is the session's only on the server it was read from.
        chain.Then(
            TmuxCommand(
                "new-window",
                [| "-t"; session.Id.ToString() + ":"; "-n"; name |],
                RequiredGeneration = Nullable session.Generation
            )
        )

    let splitLeftRight (chain: TmuxChain) = chain.Then("split-window", "-h")

    let splitTopBottom (chain: TmuxChain) = chain.Then("split-window", "-v")

    let sendLine (line: string) (chain: TmuxChain) =
        chain.Then("send-keys", "-l", "--", line + "\r")

    let arrange (layout: string) (chain: TmuxChain) =
        chain.Then(TmuxCommand("select-layout", [| layout |], ChecksLayout = true))

    let add (command: TmuxCommand) (chain: TmuxChain) = chain.Then(command)

    let run (cancellationToken: CancellationToken) (chain: TmuxChain) = chain.ExecuteAsync(cancellationToken)
