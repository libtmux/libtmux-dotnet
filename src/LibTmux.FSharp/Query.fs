namespace LibTmux.FSharp

open System
open System.Collections.Generic
open System.Text.RegularExpressions
open System.Threading
open LibTmux
open LibTmux.Query

module internal Targets =
    let ofType<'T> () =
        if typeof<'T> = typeof<LibTmux.Session> then
            QueryTarget.Session
        elif typeof<'T> = typeof<LibTmux.Window> then
            QueryTarget.Window
        elif typeof<'T> = typeof<LibTmux.Pane> then
            QueryTarget.Pane
        elif typeof<'T> = typeof<LibTmux.Client> then
            QueryTarget.Client
        else
            invalidArg "T" $"Type '{typeof<'T>.Name}' is not a session, window, pane or client."

[<Sealed>]
type Filter<'T> internal (node: QueryNode) =
    let document =
        let document =
            QueryDocument(QueryDocument.CurrentSchema, QueryDocument.CurrentVersion, Targets.ofType<'T>(), node)

        QueryDocumentValidator.Validate(document) |> ignore
        document

    let predicate =
        lazy
            let compiled = QueryInterpreter.CompileEntity<'T>(document, CancellationToken.None)
            fun value -> compiled.Invoke(value)

    member internal _.Node = node
    member internal _.Document = document
    member internal _.Predicate = predicate.Value

[<Sealed>]
type Field<'T, 'Value> internal (wireName: string) =
    member internal _.Node = FieldNode(Targets.ofType<'T>(), wireName)

[<Sealed>]
type Relation<'Parent, 'Child> internal (wireName: string) =
    member internal _.Node = FieldNode(Targets.ofType<'Parent>(), wireName)

module private Nodes =
    let constant (value: obj) : QueryNode =
        let literal: QueryConstant =
            match value with
            | null -> NullConstant()
            | :? string as text -> StringConstant text
            | :? bool as flag -> BooleanConstant flag
            | :? int as number -> Int64Constant(int64 number)
            | :? SessionId as id -> TypedIdConstant(QueryTarget.Session, id.ToString())
            | :? WindowId as id -> TypedIdConstant(QueryTarget.Window, id.ToString())
            | :? PaneId as id -> TypedIdConstant(QueryTarget.Pane, id.ToString())
            | other -> invalidArg "value" $"A constant of type '{other.GetType().Name}' has no query form."

        ConstantNode literal

    let text operation (value: string) (field: Field<'T, string>) =
        ArgumentNullException.ThrowIfNull(value)
        Filter<'T>(StringNode(operation, field.Node, constant value))

    let regex (pattern: string) options (field: Field<'T, string>) =
        ArgumentNullException.ThrowIfNull(pattern)
        Filter<'T>(RegexNode(field.Node, "dotnet", pattern, options ||| RegexOptions.CultureInvariant))

    let compare operation (value: int) (field: Field<'T, int>) =
        Filter<'T>(ComparisonNode(operation, field.Node, constant value))

[<RequireQualifiedAccess>]
module Filter =
    let eq (value: 'Value) (field: Field<'T, 'Value>) =
        match box value with
        | :? string as text ->
            Filter<'T>(StringNode(QueryStringOperation.EqualsOrdinal, field.Node, Nodes.constant text))
        | boxed -> Filter<'T>(ComparisonNode(QueryComparison.Equal, field.Node, Nodes.constant boxed))

    let negate (filter: Filter<'T>) = Filter<'T>(NotNode filter.Node)

    let ne (value: 'Value) (field: Field<'T, 'Value>) =
        match box value with
        | :? string -> eq value field |> negate
        | boxed -> Filter<'T>(ComparisonNode(QueryComparison.NotEqual, field.Node, Nodes.constant boxed))

    let eqIgnoreCase value field =
        Nodes.text QueryStringOperation.EqualsOrdinalIgnoreCase value field

    let isNull (field: Field<'T, string>) =
        Filter<'T>(ComparisonNode(QueryComparison.Equal, field.Node, Nodes.constant null))

    let startsWith prefix field =
        Nodes.text QueryStringOperation.StartsWithOrdinal prefix field

    let startsWithIgnoreCase prefix field =
        Nodes.text QueryStringOperation.StartsWithOrdinalIgnoreCase prefix field

    let endsWith suffix field =
        Nodes.text QueryStringOperation.EndsWithOrdinal suffix field

    let endsWithIgnoreCase suffix field =
        Nodes.text QueryStringOperation.EndsWithOrdinalIgnoreCase suffix field

    let contains text field =
        Nodes.text QueryStringOperation.ContainsOrdinal text field

    let containsIgnoreCase text field =
        Nodes.text QueryStringOperation.ContainsOrdinalIgnoreCase text field

    let matches pattern field =
        Nodes.regex pattern RegexOptions.None field

    let matchesIgnoreCase pattern field =
        Nodes.regex pattern RegexOptions.IgnoreCase field

    let lt value field =
        Nodes.compare QueryComparison.LessThan value field

    let le value field =
        Nodes.compare QueryComparison.LessThanOrEqual value field

    let gt value field =
        Nodes.compare QueryComparison.GreaterThan value field

    let ge value field =
        Nodes.compare QueryComparison.GreaterThanOrEqual value field

    let allOf (filters: Filter<'T> list) =
        match filters with
        | [] -> Filter<'T>(ConstantNode(BooleanConstant true))
        | [ filter ] -> filter
        | _ -> Filter<'T>(AndNode([ for filter in filters -> filter.Node ]))

    let anyOf (filters: Filter<'T> list) =
        match filters with
        | [] -> Filter<'T>(ConstantNode(BooleanConstant false))
        | [ filter ] -> filter
        | _ -> Filter<'T>(OrNode([ for filter in filters -> filter.Node ]))

    let oneOf values field =
        values |> List.map (fun value -> eq value field) |> anyOf

    let notOneOf values field = oneOf values field |> negate

    let any (relation: Relation<'Parent, 'Child>) (predicate: Filter<'Child>) =
        Filter<'Parent>(QuantifierNode(QueryQuantifier.Any, relation.Node, predicate.Node))

    let all (relation: Relation<'Parent, 'Child>) (predicate: Filter<'Child>) =
        Filter<'Parent>(QuantifierNode(QueryQuantifier.All, relation.Node, predicate.Node))

    let none relation predicate = any relation predicate |> negate
    let toDocument (filter: Filter<'T>) = filter.Document
    let toPredicate (filter: Filter<'T>) = filter.Predicate

[<RequireQualifiedAccess>]
type ScreenSearch =
    | Text of text: string
    | TextIgnoringCase of text: string
    | PosixRegex of pattern: string
    | PosixRegexIgnoringCase of pattern: string

module internal ScreenSearch =
    let toCore search =
        match search with
        | ScreenSearch.Text text -> PaneScreenSearch(text, false, false)
        | ScreenSearch.TextIgnoringCase text -> PaneScreenSearch(text, false, true)
        | ScreenSearch.PosixRegex pattern -> PaneScreenSearch(pattern, true, false)
        | ScreenSearch.PosixRegexIgnoringCase pattern -> PaneScreenSearch(pattern, true, true)

[<Sealed>]
type Query<'T>
    internal
    (
        server: LibTmux.Server,
        target: QueryTarget,
        session: SessionId option,
        window: WindowId option,
        filters: Filter<'T> list,
        native: string list
    ) =
    static member internal Create
        (server: LibTmux.Server, target: QueryTarget, session: SessionId option, window: WindowId option)
        =
        ArgumentNullException.ThrowIfNull(server)
        Query<'T>(server, target, session, window, [], [])

    member internal _.Server = server
    member internal _.Filters = filters
    member internal _.Native = native

    member internal _.With(filters, native) =
        Query<'T>(server, target, session, window, filters, native)

    member internal _.Request =
        let filter =
            match List.rev filters with
            | [] -> null
            | [ only ] -> only.Document
            | many -> (Filter<'T>(AndNode([ for filter in many -> filter.Node ]))).Document

        let unsafeFilter =
            match List.rev native with
            | [] -> null
            | [ only ] -> UnsafeTmuxFilter only
            | first :: rest ->
                for each in first :: rest do
                    TmuxFilterRenderer.RequireSingleExpression each

                UnsafeTmuxFilter(rest |> List.fold (fun combined next -> $"#{{&&:{combined},{next}}}") first)

        ListingRequest(target, Option.toNullable session, Option.toNullable window, filter, unsafeFilter, null)

[<RequireQualifiedAccess>]
module Query =
    let where (filter: Filter<'T>) (query: Query<'T>) =
        query.With(filter :: query.Filters, query.Native)

    let whereUnsafe (filter: UnsafeTmuxFilter) (query: Query<'T>) =
        ArgumentNullException.ThrowIfNull(filter)
        query.With(query.Filters, filter.Value :: query.Native)

    let showing search (query: Query<LibTmux.Pane>) =
        query.With(query.Filters, (ScreenSearch.toCore search).Render() :: query.Native)

    let list (cancellationToken: CancellationToken) (query: Query<'T>) =
        query.Server.QueryAsync<'T>(query.Request, cancellationToken)

    let exactlyOne cancellationToken (query: Query<'T>) =
        backgroundTask {
            let! items = list cancellationToken query

            return
                match items.Count with
                | 0 -> Error NoMatches
                | 1 -> Ok items[0]
                | _ -> Error MultipleMatches
        }

    let tryExactlyOne cancellationToken (query: Query<'T>) =
        backgroundTask {
            let! items = list cancellationToken query
            return if items.Count = 1 then Some items[0] else None
        }

    let matching (filter: Filter<'T>) (source: seq<'T>) : IReadOnlyList<'T> =
        ArgumentNullException.ThrowIfNull(source)
        ResizeArray(Seq.filter filter.Predicate source)

[<RequireQualifiedAccess>]
module SessionFields =
    let name = Field<LibTmux.Session, string>("session_name")
    let id = Field<LibTmux.Session, SessionId>("session_id")
    let attached = Field<LibTmux.Session, bool>("session_attached")
    let windowCount = Field<LibTmux.Session, int>("session_windows")
    let windows = Relation<LibTmux.Session, LibTmux.Window>("session_windows")

[<RequireQualifiedAccess>]
module WindowFields =
    let name = Field<LibTmux.Window, string>("window_name")
    let id = Field<LibTmux.Window, WindowId>("window_id")
    let index = Field<LibTmux.Window, int>("window_index")
    let width = Field<LibTmux.Window, int>("window_width")
    let height = Field<LibTmux.Window, int>("window_height")
    let paneCount = Field<LibTmux.Window, int>("window_panes")
    let panes = Relation<LibTmux.Window, LibTmux.Pane>("window_panes")

[<RequireQualifiedAccess>]
module PaneFields =
    let currentCommand = Field<LibTmux.Pane, string>("pane_command")
    let id = Field<LibTmux.Pane, PaneId>("pane_id")
    let index = Field<LibTmux.Pane, int>("pane_index")
    let title = Field<LibTmux.Pane, string>("pane_title")
    let currentPath = Field<LibTmux.Pane, string>("pane_current_path")
    let width = Field<LibTmux.Pane, int>("pane_width")
    let height = Field<LibTmux.Pane, int>("pane_height")
    let left = Field<LibTmux.Pane, int>("pane_left")
    let top = Field<LibTmux.Pane, int>("pane_top")
    let atTop = Field<LibTmux.Pane, bool>("pane_at_top")
    let atBottom = Field<LibTmux.Pane, bool>("pane_at_bottom")
    let atLeft = Field<LibTmux.Pane, bool>("pane_at_left")
    let atRight = Field<LibTmux.Pane, bool>("pane_at_right")

[<RequireQualifiedAccess>]
module ClientFields =
    let name = Field<LibTmux.Client, string>("client_name")
    let controlMode = Field<LibTmux.Client, bool>("client_control_mode")
