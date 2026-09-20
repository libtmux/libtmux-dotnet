namespace LibTmux.FSharp.Tests

open System
open System.Collections.Generic
open System.Linq
open System.Linq.Expressions
open System.Threading
open System.Threading.Tasks
open Microsoft.FSharp.Linq.RuntimeHelpers
open LibTmux
open LibTmux.Internal
open LibTmux.Query
open LibTmux.FSharp
open Xunit

module QueryTests =
    let private translate (quotation: Quotations.Expr<Func<'T, bool>>) =
        quotation
        |> LeafExpressionConverter.QuotationToExpression
        |> fun expression -> QueryExtensions.Translate(expression :?> Expression<Func<'T, bool>>)

    [<Fact>]
    let ``named filters preserve core documents and relation depth`` () =
        let expected =
            translate
                <@
                    Func<LibTmux.Session, bool>(fun session ->
                        session.Name.StartsWith("dev-", StringComparison.Ordinal)
                        && session.Attached
                        && session.Windows.Any(fun window ->
                            window.Panes.Any(fun pane -> pane.CurrentCommand = "nvim" || pane.CurrentCommand = "vim")))
                @>

        let editors =
            Filter.oneOf [ "nvim"; "vim" ] PaneFields.currentCommand
            |> Filter.any WindowFields.panes
            |> Filter.any SessionFields.windows

        let actual =
            Filter.allOf
                [
                    Filter.startsWith "dev-" SessionFields.name
                    Filter.eq true SessionFields.attached
                    editors
                ]
            |> Filter.toDocument

        Assert.Equal(expected, actual)
        Assert.Equal(SnapshotDepth.Panes, actual.RequiredSnapshotDepth)

    [<Fact>]
    let ``all and none preserve core quantifiers and disjunction preserves operand order`` () =
        let id = PaneId 17
        let pane = Filter.eq id PaneFields.id

        let all =
            translate <@ Func<LibTmux.Window, bool>(fun window -> window.Panes.All(fun child -> child.Id = id)) @>

        let none =
            translate <@ Func<LibTmux.Window, bool>(fun window -> not (window.Panes.Any(fun child -> child.Id = id))) @>

        Assert.Equal(all, pane |> Filter.all WindowFields.panes |> Filter.toDocument)
        Assert.Equal(none, pane |> Filter.none WindowFields.panes |> Filter.toDocument)
        let other = Filter.eq (PaneId 18) PaneFields.id

        Assert.NotEqual(
            Filter.anyOf [ pane; other ] |> Filter.toDocument,
            Filter.anyOf [ other; pane ] |> Filter.toDocument
        )

    [<Fact>]
    let ``construction retains core structural limits`` () =
        let pane = Filter.eq (PaneId 17) PaneFields.id

        Assert.Throws<UnsupportedQueryExpressionException>(fun () -> Filter.allOf (List.replicate 180 pane) |> ignore)
        |> ignore

        Assert.Throws<UnsupportedQueryExpressionException>(fun () ->
            [ 1..40 ] |> List.fold (fun filter _ -> Filter.negate filter) pane |> ignore)
        |> ignore

    [<Fact>]
    let ``empty combinators reject unsupported identities and singleton keeps its document`` () =
        Assert.Throws<ArgumentException>(fun () -> Filter.allOf<LibTmux.Session> [] |> ignore)
        |> ignore

        Assert.Throws<ArgumentException>(fun () -> Filter.anyOf<LibTmux.Session> [] |> ignore)
        |> ignore

        Assert.Throws<ArgumentException>(fun () -> Filter.oneOf [] PaneFields.id |> ignore)
        |> ignore

        let filter = Filter.eq (PaneId 17) PaneFields.id
        Assert.Same(Filter.toDocument filter, Filter.toDocument (Filter.allOf [ filter ]))
        Assert.Same(Filter.toDocument filter, Filter.toDocument (Filter.anyOf [ filter ]))

    [<Fact>]
    let ``native predicate compilation is cached per filter`` () =
        let filter = Filter.eq (PaneId 17) PaneFields.id

        Assert.Same(Filter.toPredicate filter, Filter.toPredicate filter)

    [<Fact>]
    let ``null filters preserve captured unavailable values and reject uncaptured fields`` () =
        let generation = ServerGeneration(93, 903)

        let connection =
            TmuxConnection(
                ServerConnectionOptions(SocketName = "fsharp-null-filter-unit"),
                Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>>(fun _ _ ->
                    raise (InvalidOperationException "A captured field reached tmux."))
            )

        let server = Server(connection, generation, "tmux 3.7")

        let missing =
            Pane(server, connection, generation, PaneId 0, Dictionary<string, string>())

        let unavailableFields = Dictionary<string, string>()
        unavailableFields.Add("pane_current_command", Unchecked.defaultof<string>)

        let unavailable = Pane(server, connection, generation, PaneId 0, unavailableFields)
        let emptyFields = Dictionary<string, string>()
        emptyFields.Add("pane_current_command", String.Empty)

        let empty = Pane(server, connection, generation, PaneId 0, emptyFields)
        let isNull = Filter.isNull PaneFields.currentCommand |> Filter.toPredicate

        Assert.Throws<IncompleteSnapshotException>(fun () -> isNull missing |> ignore)
        |> ignore

        Assert.True(isNull unavailable)
        Assert.False(isNull empty)

    [<Fact>]
    let ``nullable and typed ID constants use core semantics and cancellation precedes enumeration`` () =
        let expected =
            translate <@ Func<LibTmux.Pane, bool>(fun pane -> pane.CurrentCommand = null) @>

        Assert.Equal(expected, Filter.isNull PaneFields.currentCommand |> Filter.toDocument)
        let id = PaneId 17
        let expectedId = translate <@ Func<LibTmux.Pane, bool>(fun pane -> pane.Id = id) @>
        Assert.Equal(expectedId, Filter.eq id PaneFields.id |> Filter.toDocument)

        let source =
            seq {
                failwith "A canceled match enumerated its input."
                yield Unchecked.defaultof<LibTmux.Pane>
            }

        use canceled = new CancellationTokenSource()
        canceled.Cancel()

        Assert.Throws<OperationCanceledException>(fun () ->
            source
            |> Query.matchingWithCancellation canceled.Token (Filter.eq id PaneFields.id)
            |> ignore)
        |> ignore
