namespace LibTmux.FSharp

open System
open System.Threading
open LibTmux
open LibTmux.Query

[<RequireQualifiedAccess>]
module Server =
    let sessions (server: LibTmux.Server) =
        Query<LibTmux.Session>.Create(server, QueryTarget.Session, None, None)

    let windows (server: LibTmux.Server) =
        Query<LibTmux.Window>.Create(server, QueryTarget.Window, None, None)

    let panes (server: LibTmux.Server) =
        Query<LibTmux.Pane>.Create(server, QueryTarget.Pane, None, None)

    let clients (server: LibTmux.Server) =
        Query<LibTmux.Client>.Create(server, QueryTarget.Client, None, None)

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

    let sendKeys (cancellationToken: CancellationToken) request (pane: LibTmux.Pane) =
        pane.SendKeysAsync(request, cancellationToken)

    let split (cancellationToken: CancellationToken) request (pane: LibTmux.Pane) =
        pane.SplitAsync(request, cancellationToken)
