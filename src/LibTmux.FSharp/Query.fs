namespace LibTmux.FSharp

open System
open System.Linq
open System.Linq.Expressions
open System.Reflection
open System.Threading
open LibTmux
open LibTmux.Query

[<Sealed>]
type Filter<'T> internal (expression: Expression<Func<'T, bool>>, document: QueryDocument) =
    let predicate =
        lazy
            let compiled = QueryExtensions.Compile<'T>(document)
            fun value -> compiled.Invoke(value)

    member internal _.Expression = expression
    member internal _.Document = document
    member internal _.Predicate = predicate.Value

[<Sealed>]
type Field<'T, 'Value> internal (property: PropertyInfo) =
    member internal _.Property = property

[<Sealed>]
type Relation<'Parent, 'Child> internal (property: PropertyInfo) =
    member internal _.Property = property

module private Construction =
    let make<'T> parameter body =
        let expression = Expression.Lambda<Func<'T, bool>>(body, [| parameter |])
        Filter<'T>(expression, QueryExtensions.Translate(expression))

    let replace (parameter: ParameterExpression) (filter: Filter<'T>) =
        let visitor =
            { new ExpressionVisitor() with
                override _.VisitParameter(node) =
                    if obj.ReferenceEquals(node, filter.Expression.Parameters[0]) then
                        parameter
                    else
                        node
            }

        visitor.Visit(filter.Expression.Body) |> nonNull

    let combine operation (filters: Filter<'T> list) =
        match filters with
        | [] -> invalidArg "filters" "The filters list must contain at least one predicate."
        | [ filter ] -> filter
        | _ ->
            let parameter = Expression.Parameter(typeof<'T>, "entity")
            let operands = filters |> List.map (replace parameter) |> List.toArray
            // Balance the expression tree before the core flattens it, avoiding
            // a linear recursion depth for a large caller-supplied operand list.
            let rec build first count =
                if count = 1 then
                    operands[first]
                else
                    let leftCount = count / 2
                    operation (build first leftCount) (build (first + leftCount) (count - leftCount)) :> Expression

            make<'T> parameter (build 0 operands.Length)

    let quantify methodName (relation: Relation<'Parent, 'Child>) (predicate: Filter<'Child>) =
        let parameter = Expression.Parameter(typeof<'Parent>, "parent")
        let children = Expression.Property(parameter, relation.Property)

        let body =
            Expression.Call(typeof<Enumerable>, methodName, [| typeof<'Child> |], children, predicate.Expression)

        make<'Parent> parameter body

[<RequireQualifiedAccess>]
module Filter =
    let eq (value: 'Value) (field: Field<'T, 'Value>) =
        let parameter = Expression.Parameter(typeof<'T>, "entity")

        let body =
            Expression.Equal(Expression.Property(parameter, field.Property), Expression.Constant(value, typeof<'Value>))

        Construction.make<'T> parameter body

    let isNull (field: Field<'T, string>) =
        let parameter = Expression.Parameter(typeof<'T>, "entity")

        let body =
            Expression.Equal(Expression.Property(parameter, field.Property), Expression.Constant(null, typeof<string>))

        Construction.make<'T> parameter body

    let startsWith (prefix: string) (field: Field<'T, string>) =
        let parameter = Expression.Parameter(typeof<'T>, "entity")

        let method' =
            typeof<string>.GetMethod("StartsWith", [| typeof<string>; typeof<StringComparison> |])
            |> nonNull

        let body =
            Expression.Call(
                Expression.Property(parameter, field.Property),
                method',
                Expression.Constant(prefix, typeof<string>),
                Expression.Constant(StringComparison.Ordinal)
            )

        Construction.make<'T> parameter body

    let allOf filters =
        Construction.combine (fun left right -> Expression.AndAlso(left, right)) filters

    let anyOf filters =
        Construction.combine (fun left right -> Expression.OrElse(left, right)) filters

    let oneOf values field =
        if List.isEmpty values then
            invalidArg "values" "The values list must contain at least one constant."

        values |> List.map (fun value -> eq value field) |> anyOf

    let negate (filter: Filter<'T>) =
        Construction.make<'T> filter.Expression.Parameters[0] (Expression.Not(filter.Expression.Body))

    let any relation predicate =
        Construction.quantify "Any" relation predicate

    let all relation predicate =
        Construction.quantify "All" relation predicate

    let none relation predicate = any relation predicate |> negate
    let toDocument (filter: Filter<'T>) = filter.Document

    let toPredicate (filter: Filter<'T>) = filter.Predicate

[<RequireQualifiedAccess>]
module Query =
    let matching (filter: Filter<'T>) (source: seq<'T>) =
        QueryExtensions.Matching(source, filter.Document)

    let matchingWithCancellation cancellationToken (filter: Filter<'T>) (source: seq<'T>) =
        QueryExtensions.Matching(source, filter.Document, cancellationToken)

[<RequireQualifiedAccess>]
module SessionFields =
    let name =
        Field<LibTmux.Session, string>(
            typeof<LibTmux.Session>.GetProperty(nameof (Unchecked.defaultof<LibTmux.Session>.Name))
            |> nonNull
        )

    let id =
        Field<LibTmux.Session, SessionId>(
            typeof<LibTmux.Session>.GetProperty(nameof (Unchecked.defaultof<LibTmux.Session>.Id))
            |> nonNull
        )

    let attached =
        Field<LibTmux.Session, bool>(
            typeof<LibTmux.Session>.GetProperty(nameof (Unchecked.defaultof<LibTmux.Session>.Attached))
            |> nonNull
        )

    let windows =
        Relation<LibTmux.Session, LibTmux.Window>(
            typeof<LibTmux.Session>.GetProperty(nameof (Unchecked.defaultof<LibTmux.Session>.Windows))
            |> nonNull
        )

[<RequireQualifiedAccess>]
module WindowFields =
    let name =
        Field<LibTmux.Window, string>(
            typeof<LibTmux.Window>.GetProperty(nameof (Unchecked.defaultof<LibTmux.Window>.Name))
            |> nonNull
        )

    let id =
        Field<LibTmux.Window, WindowId>(
            typeof<LibTmux.Window>.GetProperty(nameof (Unchecked.defaultof<LibTmux.Window>.Id))
            |> nonNull
        )

    let panes =
        Relation<LibTmux.Window, LibTmux.Pane>(
            typeof<LibTmux.Window>.GetProperty(nameof (Unchecked.defaultof<LibTmux.Window>.Panes))
            |> nonNull
        )

[<RequireQualifiedAccess>]
module PaneFields =
    let currentCommand =
        Field<LibTmux.Pane, string>(
            typeof<LibTmux.Pane>.GetProperty(nameof (Unchecked.defaultof<LibTmux.Pane>.CurrentCommand))
            |> nonNull
        )

    let id =
        Field<LibTmux.Pane, PaneId>(
            typeof<LibTmux.Pane>.GetProperty(nameof (Unchecked.defaultof<LibTmux.Pane>.Id))
            |> nonNull
        )

[<RequireQualifiedAccess>]
module ClientFields =
    let name =
        Field<LibTmux.Client, string>(
            typeof<LibTmux.Client>.GetProperty(nameof (Unchecked.defaultof<LibTmux.Client>.Name))
            |> nonNull
        )

    let controlMode =
        Field<LibTmux.Client, bool>(
            typeof<LibTmux.Client>.GetProperty(nameof (Unchecked.defaultof<LibTmux.Client>.IsControlClient))
            |> nonNull
        )
