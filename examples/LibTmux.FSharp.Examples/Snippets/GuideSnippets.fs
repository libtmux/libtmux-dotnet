namespace LibTmux.FSharp.Examples

module internal GuideSnippets =
    // fsharp-snippet: CaptureAndFilter
    open System.Threading
    open LibTmux
    open LibTmux.FSharp

    let readMatchingSessionNamesAsync (commands: string list) (cancellationToken: CancellationToken) (server: Server) =
        task {
            let hasCommand =
                Filter.oneOf commands PaneFields.currentCommand
                |> Filter.any WindowFields.panes
                |> Filter.any SessionFields.windows

            let document = Filter.toDocument hasCommand

            let! captured =
                server |> Server.capture cancellationToken document.RequiredSnapshotDepth

            return
                captured.Sessions
                |> Query.matching hasCommand
                |> Seq.map (fun session -> session.Name)
                |> Seq.toList
        }

    let readEditorSessionNamesAsync cancellationToken server =
        readMatchingSessionNamesAsync [ "nvim"; "vim" ] cancellationToken server
    // endfsharp-snippet

    // fsharp-snippet: NativeQuery
    open System
    open LibTmux
    open LibTmux.FSharp

    let editorPaneIds (captured: Server) =
        captured.Panes
        |> Seq.filter (fun pane -> String.Equals(pane.CurrentCommand, "nvim", StringComparison.Ordinal))
        |> Seq.map (fun pane -> pane.Id)
        |> Seq.toList
    // endfsharp-snippet

    // fsharp-snippet: PortableFilter
    open LibTmux
    open LibTmux.FSharp

    let editor = Filter.oneOf [ "nvim"; "vim" ] PaneFields.currentCommand

    let acceptsEditor = editor |> Filter.toPredicate

    let firstEditor (captured: Server) =
        captured.Panes |> Seq.filter acceptsEditor |> Seq.tryHead
    // endfsharp-snippet

    // fsharp-snippet: LookupAndCardinality
    open System.Threading
    open LibTmux
    open LibTmux.FSharp

    let tryFindPaneAsync (cancellationToken: CancellationToken) (server: Server) =
        task {
            let! pane = server |> Server.tryFindPane cancellationToken (PaneId 42)

            return pane
        }

    let selectCapturedPane (captured: Server) = captured.Panes |> Selection.exactlyOne
    // endfsharp-snippet

    // fsharp-snippet: ReadPaneCommands
    open System.Threading
    open LibTmux
    open LibTmux.FSharp

    let readPaneCommandsAsync (cancellationToken: CancellationToken) (server: Server) =
        task {
            let! captured = server |> Server.capture cancellationToken SnapshotDepth.Panes

            return captured.Panes |> Seq.choose Pane.currentCommand |> Seq.toList
        }
    // endfsharp-snippet

    // fsharp-snippet: MatchingSessions
    open LibTmux
    open LibTmux.FSharp

    let editorSessions =
        Filter.oneOf [ "nvim"; "vim" ] PaneFields.currentCommand
        |> Filter.any WindowFields.panes
        |> Filter.any SessionFields.windows

    let matchingSessions (captured: Server) =
        captured.Sessions |> Query.matching editorSessions
    // endfsharp-snippet
