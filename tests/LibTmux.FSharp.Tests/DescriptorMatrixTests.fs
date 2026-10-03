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
                Operators =
                    "eq, ne, eqIgnoreCase, isNull, startsWith, startsWithIgnoreCase, endsWith, endsWithIgnoreCase, contains, containsIgnoreCase, matches, matchesIgnoreCase, oneOf, notOneOf"
                Depth = SnapshotDepth.Sessions
                Document = Filter.eq "build" SessionFields.name |> Filter.toDocument
            }
            {
                Name = "SessionFields.id"
                CoreProperty = "Session.Id"
                WireName = "session_id"
                ValueType = "SessionId"
                Operators = "eq, ne, oneOf, notOneOf"
                Depth = SnapshotDepth.Sessions
                Document = Filter.eq (SessionId 1) SessionFields.id |> Filter.toDocument
            }
            {
                Name = "SessionFields.attached"
                CoreProperty = "Session.Attached"
                WireName = "session_attached"
                ValueType = "bool"
                Operators = "eq, ne, oneOf, notOneOf"
                Depth = SnapshotDepth.Sessions
                Document = Filter.eq true SessionFields.attached |> Filter.toDocument
            }
            {
                Name = "SessionFields.windowCount"
                CoreProperty = "Session.Windows"
                WireName = "session_windows"
                ValueType = "int"
                Operators = "eq, ne, lt, le, gt, ge, oneOf, notOneOf"
                Depth = SnapshotDepth.Windows
                Document = Filter.gt 1 SessionFields.windowCount |> Filter.toDocument
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
                Operators =
                    "eq, ne, eqIgnoreCase, isNull, startsWith, startsWithIgnoreCase, endsWith, endsWithIgnoreCase, contains, containsIgnoreCase, matches, matchesIgnoreCase, oneOf, notOneOf"
                Depth = SnapshotDepth.Windows
                Document = Filter.eq "build" WindowFields.name |> Filter.toDocument
            }
            {
                Name = "WindowFields.id"
                CoreProperty = "Window.Id"
                WireName = "window_id"
                ValueType = "WindowId"
                Operators = "eq, ne, oneOf, notOneOf"
                Depth = SnapshotDepth.Windows
                Document = Filter.eq (WindowId 1) WindowFields.id |> Filter.toDocument
            }
            {
                Name = "WindowFields.index"
                CoreProperty = "Window.Index"
                WireName = "window_index"
                ValueType = "int"
                Operators = "eq, ne, lt, le, gt, ge, oneOf, notOneOf"
                Depth = SnapshotDepth.Windows
                Document = Filter.gt 1 WindowFields.index |> Filter.toDocument
            }
            {
                Name = "WindowFields.width"
                CoreProperty = "Window.Width"
                WireName = "window_width"
                ValueType = "int"
                Operators = "eq, ne, lt, le, gt, ge, oneOf, notOneOf"
                Depth = SnapshotDepth.Windows
                Document = Filter.gt 1 WindowFields.width |> Filter.toDocument
            }
            {
                Name = "WindowFields.height"
                CoreProperty = "Window.Height"
                WireName = "window_height"
                ValueType = "int"
                Operators = "eq, ne, lt, le, gt, ge, oneOf, notOneOf"
                Depth = SnapshotDepth.Windows
                Document = Filter.gt 1 WindowFields.height |> Filter.toDocument
            }
            {
                Name = "WindowFields.paneCount"
                CoreProperty = "Window.Panes"
                WireName = "window_panes"
                ValueType = "int"
                Operators = "eq, ne, lt, le, gt, ge, oneOf, notOneOf"
                Depth = SnapshotDepth.Panes
                Document = Filter.gt 1 WindowFields.paneCount |> Filter.toDocument
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
                Operators =
                    "eq, ne, eqIgnoreCase, isNull, startsWith, startsWithIgnoreCase, endsWith, endsWithIgnoreCase, contains, containsIgnoreCase, matches, matchesIgnoreCase, oneOf, notOneOf"
                Depth = SnapshotDepth.Panes
                Document = Filter.eq "nvim" PaneFields.currentCommand |> Filter.toDocument
            }
            {
                Name = "PaneFields.id"
                CoreProperty = "Pane.Id"
                WireName = "pane_id"
                ValueType = "PaneId"
                Operators = "eq, ne, oneOf, notOneOf"
                Depth = SnapshotDepth.Panes
                Document = Filter.eq (PaneId 1) PaneFields.id |> Filter.toDocument
            }
            {
                Name = "PaneFields.index"
                CoreProperty = "Pane.Index"
                WireName = "pane_index"
                ValueType = "int"
                Operators = "eq, ne, lt, le, gt, ge, oneOf, notOneOf"
                Depth = SnapshotDepth.Panes
                Document = Filter.gt 1 PaneFields.index |> Filter.toDocument
            }
            {
                Name = "PaneFields.title"
                CoreProperty = "Pane.Title"
                WireName = "pane_title"
                ValueType = "string"
                Operators =
                    "eq, ne, eqIgnoreCase, isNull, startsWith, startsWithIgnoreCase, endsWith, endsWithIgnoreCase, contains, containsIgnoreCase, matches, matchesIgnoreCase, oneOf, notOneOf"
                Depth = SnapshotDepth.Panes
                Document = Filter.eq "x" PaneFields.title |> Filter.toDocument
            }
            {
                Name = "PaneFields.currentPath"
                CoreProperty = "Pane.CurrentPath"
                WireName = "pane_current_path"
                ValueType = "string"
                Operators =
                    "eq, ne, eqIgnoreCase, isNull, startsWith, startsWithIgnoreCase, endsWith, endsWithIgnoreCase, contains, containsIgnoreCase, matches, matchesIgnoreCase, oneOf, notOneOf"
                Depth = SnapshotDepth.Panes
                Document = Filter.eq "x" PaneFields.currentPath |> Filter.toDocument
            }
            {
                Name = "PaneFields.width"
                CoreProperty = "Pane.Width"
                WireName = "pane_width"
                ValueType = "int"
                Operators = "eq, ne, lt, le, gt, ge, oneOf, notOneOf"
                Depth = SnapshotDepth.Panes
                Document = Filter.gt 1 PaneFields.width |> Filter.toDocument
            }
            {
                Name = "PaneFields.height"
                CoreProperty = "Pane.Height"
                WireName = "pane_height"
                ValueType = "int"
                Operators = "eq, ne, lt, le, gt, ge, oneOf, notOneOf"
                Depth = SnapshotDepth.Panes
                Document = Filter.gt 1 PaneFields.height |> Filter.toDocument
            }
            {
                Name = "PaneFields.left"
                CoreProperty = "Pane.Left"
                WireName = "pane_left"
                ValueType = "int"
                Operators = "eq, ne, lt, le, gt, ge, oneOf, notOneOf"
                Depth = SnapshotDepth.Panes
                Document = Filter.gt 1 PaneFields.left |> Filter.toDocument
            }
            {
                Name = "PaneFields.top"
                CoreProperty = "Pane.Top"
                WireName = "pane_top"
                ValueType = "int"
                Operators = "eq, ne, lt, le, gt, ge, oneOf, notOneOf"
                Depth = SnapshotDepth.Panes
                Document = Filter.gt 1 PaneFields.top |> Filter.toDocument
            }
            {
                Name = "PaneFields.atTop"
                CoreProperty = "Pane.AtTop"
                WireName = "pane_at_top"
                ValueType = "bool"
                Operators = "eq, ne, oneOf, notOneOf"
                Depth = SnapshotDepth.Panes
                Document = Filter.eq true PaneFields.atTop |> Filter.toDocument
            }
            {
                Name = "PaneFields.atBottom"
                CoreProperty = "Pane.AtBottom"
                WireName = "pane_at_bottom"
                ValueType = "bool"
                Operators = "eq, ne, oneOf, notOneOf"
                Depth = SnapshotDepth.Panes
                Document = Filter.eq true PaneFields.atBottom |> Filter.toDocument
            }
            {
                Name = "PaneFields.atLeft"
                CoreProperty = "Pane.AtLeft"
                WireName = "pane_at_left"
                ValueType = "bool"
                Operators = "eq, ne, oneOf, notOneOf"
                Depth = SnapshotDepth.Panes
                Document = Filter.eq true PaneFields.atLeft |> Filter.toDocument
            }
            {
                Name = "PaneFields.atRight"
                CoreProperty = "Pane.AtRight"
                WireName = "pane_at_right"
                ValueType = "bool"
                Operators = "eq, ne, oneOf, notOneOf"
                Depth = SnapshotDepth.Panes
                Document = Filter.eq true PaneFields.atRight |> Filter.toDocument
            }
            {
                Name = "ClientFields.name"
                CoreProperty = "Client.Name"
                WireName = "client_name"
                ValueType = "string"
                Operators =
                    "eq, ne, eqIgnoreCase, isNull, startsWith, startsWithIgnoreCase, endsWith, endsWithIgnoreCase, contains, containsIgnoreCase, matches, matchesIgnoreCase, oneOf, notOneOf"
                Depth = SnapshotDepth.Sessions
                Document = Filter.eq "client" ClientFields.name |> Filter.toDocument
            }
            {
                Name = "ClientFields.controlMode"
                CoreProperty = "Client.IsControlClient"
                WireName = "client_control_mode"
                ValueType = "bool"
                Operators = "eq, ne, oneOf, notOneOf"
                Depth = SnapshotDepth.Sessions
                Document = Filter.eq true ClientFields.controlMode |> Filter.toDocument
            }
        ]

    let private documentedFields (content: string) =
        let start = "<!-- descriptor-fields-start -->"
        let finish = "<!-- descriptor-fields-end -->"
        let beginIndex = content.IndexOf(start, StringComparison.Ordinal)
        let endIndex = content.IndexOf(finish, StringComparison.Ordinal)

        if beginIndex < 0 || endIndex <= beginIndex then
            invalidOp "The F# descriptor field markers are missing or reversed."

        content[(beginIndex + start.Length) .. (endIndex - 1)]
            .Split('\n', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
        |> Array.filter (fun line -> not (line.StartsWith("## ", StringComparison.Ordinal)))
        |> Array.toList

    let private expectedFields =
        descriptors
        |> List.collect (fun descriptor ->
            let operators =
                descriptor.Operators.Split(", ", StringSplitOptions.None)
                |> Array.map (fun name -> $"`{name}`")
                |> String.concat ", "

            [
                $"### `{descriptor.Name}`"
                $"- Core property: `{descriptor.CoreProperty}`"
                $"- Wire name: `{descriptor.WireName}`"
                $"- Value type: `{descriptor.ValueType}`"
                $"- Operators: {operators}"
                $"- Required depth: `{descriptor.Depth}`"
                $"- Schema version: `{QueryDocument.CurrentVersion}`"
            ])

    let private validateFields content =
        if expectedFields <> documentedFields content then
            invalidOp "The F# descriptor guide does not match the exposed descriptors."

        for descriptor in descriptors do
            let wire = QueryJson.Serialize(descriptor.Document)
            Assert.Contains($"\"wireName\":\"{descriptor.WireName}\"", wire, StringComparison.Ordinal)
            Assert.Equal(QueryDocument.CurrentVersion, descriptor.Document.Version)
            Assert.Equal(descriptor.Depth, descriptor.Document.RequiredSnapshotDepth)

    [<Fact>]
    let ``descriptor guide names only translated v1 fields`` () =
        let path = Path.Combine(AppContext.BaseDirectory, "supported-query-fields.md")
        validateFields (File.ReadAllText(path))

    [<Fact>]
    let ``every exposed field and relation has a descriptor row`` () =
        let exposed =
            [ typeof<Field<LibTmux.Session, string>>.Assembly.GetTypes() ]
            |> Seq.concat
            |> Seq.filter (fun moduleType -> moduleType.Name.EndsWith("Fields", StringComparison.Ordinal))
            |> Seq.collect (fun moduleType ->
                moduleType.GetProperties(Reflection.BindingFlags.Public ||| Reflection.BindingFlags.Static)
                |> Seq.map (fun property -> $"{moduleType.Name}.{property.Name}"))
            |> Set.ofSeq

        Assert.Equal<Set<string>>(exposed, descriptors |> List.map (fun descriptor -> descriptor.Name) |> Set.ofList)

    [<Theory>]
    [<InlineData("pane_command", "pane_current_path")>]
    [<InlineData("- Schema version: `1`", "- Schema version: `2`")>]
    [<InlineData("- Required depth: `Panes`", "- Required depth: `Windows`")>]
    let ``descriptor guide rejects field contract drift`` (original: string) (replacement: string) =
        let path = Path.Combine(AppContext.BaseDirectory, "supported-query-fields.md")

        let drifted =
            File.ReadAllText(path).Replace(original, replacement, StringComparison.Ordinal)

        Assert.Throws<InvalidOperationException>(fun () -> validateFields drifted)
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
