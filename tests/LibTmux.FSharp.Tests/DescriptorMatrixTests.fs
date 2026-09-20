namespace LibTmux.FSharp.Tests

open System
open System.IO
open LibTmux
open LibTmux.FSharp
open LibTmux.Query
open LibTmux.Query.Json
open Xunit

module DescriptorMatrixTests =
    type private Descriptor =
        {
            Name: string
            CoreProperty: string
            WireName: string
            ValueType: string
            Operators: string
            Depth: SnapshotDepth
            Document: QueryDocument
        }

    let private descriptors =
        [
            {
                Name = "SessionFields.name"
                CoreProperty = "Session.Name"
                WireName = "session_name"
                ValueType = "string"
                Operators = "eq, isNull, startsWith, oneOf"
                Depth = SnapshotDepth.Sessions
                Document = Filter.eq "build" SessionFields.name |> Filter.toDocument
            }
            {
                Name = "SessionFields.id"
                CoreProperty = "Session.Id"
                WireName = "session_id"
                ValueType = "SessionId"
                Operators = "eq, oneOf"
                Depth = SnapshotDepth.Sessions
                Document = Filter.eq (SessionId 1) SessionFields.id |> Filter.toDocument
            }
            {
                Name = "SessionFields.attached"
                CoreProperty = "Session.Attached"
                WireName = "session_attached"
                ValueType = "bool"
                Operators = "eq, oneOf"
                Depth = SnapshotDepth.Sessions
                Document = Filter.eq true SessionFields.attached |> Filter.toDocument
            }
            {
                Name = "SessionFields.windows"
                CoreProperty = "Session.Windows"
                WireName = "session_windows"
                ValueType = "Relation<Session, Window>"
                Operators = "any, all, none"
                Depth = SnapshotDepth.Windows
                Document =
                    Filter.eq "build" WindowFields.name
                    |> Filter.any SessionFields.windows
                    |> Filter.toDocument
            }
            {
                Name = "WindowFields.name"
                CoreProperty = "Window.Name"
                WireName = "window_name"
                ValueType = "string"
                Operators = "eq, isNull, startsWith, oneOf"
                Depth = SnapshotDepth.Windows
                Document = Filter.eq "build" WindowFields.name |> Filter.toDocument
            }
            {
                Name = "WindowFields.id"
                CoreProperty = "Window.Id"
                WireName = "window_id"
                ValueType = "WindowId"
                Operators = "eq, oneOf"
                Depth = SnapshotDepth.Windows
                Document = Filter.eq (WindowId 1) WindowFields.id |> Filter.toDocument
            }
            {
                Name = "WindowFields.panes"
                CoreProperty = "Window.Panes"
                WireName = "window_panes"
                ValueType = "Relation<Window, Pane>"
                Operators = "any, all, none"
                Depth = SnapshotDepth.Panes
                Document =
                    Filter.eq "nvim" PaneFields.currentCommand
                    |> Filter.any WindowFields.panes
                    |> Filter.toDocument
            }
            {
                Name = "PaneFields.currentCommand"
                CoreProperty = "Pane.CurrentCommand"
                WireName = "pane_command"
                ValueType = "string"
                Operators = "eq, isNull, startsWith, oneOf"
                Depth = SnapshotDepth.Panes
                Document = Filter.eq "nvim" PaneFields.currentCommand |> Filter.toDocument
            }
            {
                Name = "PaneFields.id"
                CoreProperty = "Pane.Id"
                WireName = "pane_id"
                ValueType = "PaneId"
                Operators = "eq, oneOf"
                Depth = SnapshotDepth.Panes
                Document = Filter.eq (PaneId 1) PaneFields.id |> Filter.toDocument
            }
            {
                Name = "ClientFields.name"
                CoreProperty = "Client.Name"
                WireName = "client_name"
                ValueType = "string"
                Operators = "eq, isNull, startsWith, oneOf"
                Depth = SnapshotDepth.Sessions
                Document = Filter.eq "client" ClientFields.name |> Filter.toDocument
            }
            {
                Name = "ClientFields.controlMode"
                CoreProperty = "Client.IsControlClient"
                WireName = "client_control_mode"
                ValueType = "bool"
                Operators = "eq, oneOf"
                Depth = SnapshotDepth.Sessions
                Document = Filter.eq true ClientFields.controlMode |> Filter.toDocument
            }
        ]

    let private matrixRows (content: string) =
        let start = "<!-- descriptor-matrix-start -->"
        let finish = "<!-- descriptor-matrix-end -->"
        let beginIndex = content.IndexOf(start, StringComparison.Ordinal)
        let endIndex = content.IndexOf(finish, StringComparison.Ordinal)

        if beginIndex < 0 || endIndex <= beginIndex then
            invalidOp "The F# descriptor matrix markers are missing or reversed."

        content[(beginIndex + start.Length) .. (endIndex - 1)]
            .Split('\n', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
        |> Array.skip 2
        |> Array.map (fun row ->
            let values = row.Trim('|').Split('|', StringSplitOptions.TrimEntries)

            if values.Length <> 7 then
                invalidOp $"The F# descriptor matrix row has {values.Length} columns: {row}"

            values)
        |> Array.toList

    let private expectedRows =
        descriptors
        |> List.map (fun descriptor ->
            [|
                descriptor.Name
                descriptor.CoreProperty
                descriptor.WireName
                descriptor.ValueType
                descriptor.Operators
                string descriptor.Depth
                string QueryDocument.CurrentVersion
            |])

    let private validateMatrix content =
        if expectedRows <> matrixRows content then
            invalidOp "The F# descriptor matrix does not match the exposed descriptors."

        for descriptor in descriptors do
            let wire = QueryJson.Serialize(descriptor.Document)
            Assert.Contains($"\"wireName\":\"{descriptor.WireName}\"", wire, StringComparison.Ordinal)
            Assert.Equal(QueryDocument.CurrentVersion, descriptor.Document.Version)
            Assert.Equal(descriptor.Depth, descriptor.Document.RequiredSnapshotDepth)

    [<Fact>]
    let ``descriptor matrix names only translated v1 fields`` () =
        let path = Path.Combine(AppContext.BaseDirectory, "supported-query-fields.md")
        validateMatrix (File.ReadAllText(path))

    [<Fact>]
    let ``descriptor matrix rejects an advertised unsupported field`` () =
        let path = Path.Combine(AppContext.BaseDirectory, "supported-query-fields.md")

        let drifted =
            File.ReadAllText(path).Replace("pane_command", "pane_current_path", StringComparison.Ordinal)

        Assert.Throws<InvalidOperationException>(fun () -> validateMatrix drifted)
        |> ignore

    [<Fact>]
    let ``optional JSON package round trips a filter document`` () =
        let document =
            Filter.oneOf [ "nvim"; "vim" ] PaneFields.currentCommand
            |> Filter.any WindowFields.panes
            |> Filter.any SessionFields.windows
            |> Filter.toDocument

        let wire = QueryJson.Serialize(document)
        let restored = QueryJson.Deserialize(wire)
        Assert.Equal(document, restored)
        Assert.Contains("\"schema\":\"libtmux-query\"", wire, StringComparison.Ordinal)
