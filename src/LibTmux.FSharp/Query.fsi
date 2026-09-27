namespace LibTmux.FSharp

open System.Collections.Generic
open System.Diagnostics.CodeAnalysis
open System.Threading
open LibTmux
open LibTmux.Query

/// <summary>Describes a validated portable predicate over one entity type.</summary>
[<Sealed>]
type Filter<'T>

/// <summary>Identifies a supported scalar field and its query constant type.</summary>
[<Sealed>]
type Field<'T, 'Value>

/// <summary>Identifies a supported captured relation between two entity types.</summary>
[<Sealed>]
type Relation<'Parent, 'Child>

/// <summary>Constructs portable predicates through the core query translator.</summary>
[<RequireQualifiedAccess>]
module Filter =
    /// <summary>Matches a field against a constant using the core's equality semantics.</summary>
    val eq: value: 'Value -> field: Field<'T, 'Value> -> Filter<'T>

    /// <summary>Matches a captured null string without treating an uncaptured field as absent.</summary>
    val isNull: field: Field<'T, string> -> Filter<'T>

    /// <summary>Matches a string prefix using ordinal comparison.</summary>
    val startsWith: prefix: string -> field: Field<'T, string> -> Filter<'T>

    /// <summary>Matches any constant in a nonempty list, preserving operand order.</summary>
    /// <exception cref="T:System.ArgumentException">The values list is empty.</exception>
    val oneOf: values: 'Value list -> field: Field<'T, 'Value> -> Filter<'T>

    /// <summary>Requires every predicate in a nonempty list, in input order.</summary>
    /// <exception cref="T:System.ArgumentException">The filters list is empty.</exception>
    val allOf: filters: Filter<'T> list -> Filter<'T>

    /// <summary>Requires at least one predicate in a nonempty list, in input order.</summary>
    /// <exception cref="T:System.ArgumentException">The filters list is empty.</exception>
    val anyOf: filters: Filter<'T> list -> Filter<'T>

    /// <summary>Negates a portable predicate.</summary>
    val negate: filter: Filter<'T> -> Filter<'T>

    /// <summary>Requires a matching child, returning false for an empty captured relation.</summary>
    val any: relation: Relation<'Parent, 'Child> -> predicate: Filter<'Child> -> Filter<'Parent>

    /// <summary>Requires every child to match, returning true for an empty captured relation.</summary>
    val all: relation: Relation<'Parent, 'Child> -> predicate: Filter<'Child> -> Filter<'Parent>

    /// <summary>Requires no matching child, returning true for an empty captured relation.</summary>
    val none: relation: Relation<'Parent, 'Child> -> predicate: Filter<'Child> -> Filter<'Parent>

    /// <summary>Returns the core document validated when the filter was constructed.</summary>
    val toDocument: filter: Filter<'T> -> QueryDocument

    /// <summary>Compiles once and returns a predicate for native lazy filtering.</summary>
    /// <remarks>The predicate has no node-level cancellation token.</remarks>
    [<RequiresUnreferencedCode("Query compilation uses reflection over queried public properties.")>]
    val toPredicate: filter: Filter<'T> -> ('T -> bool)

/// <summary>Applies portable filters locally to captured objects.</summary>
[<RequireQualifiedAccess>]
module Query =
    /// <summary>Materializes matching elements, preserving input order and multiplicity.</summary>
    [<RequiresUnreferencedCode("Query compilation uses reflection over queried public properties.")>]
    val matching: filter: Filter<'T> -> source: seq<'T> -> IReadOnlyList<'T>

    /// <summary>Materializes matches with cancellation between elements and predicate nodes.</summary>
    [<RequiresUnreferencedCode("Query compilation uses reflection over queried public properties.")>]
    val matchingWithCancellation:
        cancellationToken: CancellationToken -> filter: Filter<'T> -> source: seq<'T> -> IReadOnlyList<'T>

/// <summary>Provides supported session fields and relations for portable filters.</summary>
[<RequireQualifiedAccess>]
module SessionFields =
    /// <summary>Identifies the session name for ordinal string and null comparisons.</summary>
    val name: Field<LibTmux.Session, string>
    /// <summary>Identifies the typed session ID.</summary>
    val id: Field<LibTmux.Session, SessionId>
    /// <summary>Identifies whether the captured session has attached clients.</summary>
    val attached: Field<LibTmux.Session, bool>
    /// <summary>Identifies captured window placements within the session.</summary>
    val windows: Relation<LibTmux.Session, LibTmux.Window>

/// <summary>Provides supported window fields and relations for portable filters.</summary>
[<RequireQualifiedAccess>]
module WindowFields =
    /// <summary>Identifies the window name for ordinal string and null comparisons.</summary>
    val name: Field<LibTmux.Window, string>
    /// <summary>Identifies the typed physical window ID.</summary>
    val id: Field<LibTmux.Window, WindowId>
    /// <summary>Identifies panes captured through this window placement.</summary>
    val panes: Relation<LibTmux.Window, LibTmux.Pane>

/// <summary>Provides supported pane fields for portable filters.</summary>
[<RequireQualifiedAccess>]
module PaneFields =
    /// <summary>Identifies the captured command, including a captured unavailable value.</summary>
    val currentCommand: Field<LibTmux.Pane, string>
    /// <summary>Identifies the typed pane ID.</summary>
    val id: Field<LibTmux.Pane, PaneId>

/// <summary>Provides supported client fields for portable filters.</summary>
[<RequireQualifiedAccess>]
module ClientFields =
    /// <summary>Identifies the captured client name for ordinal string and null comparisons.</summary>
    val name: Field<LibTmux.Client, string>
    /// <summary>Identifies whether the captured client uses control mode.</summary>
    val controlMode: Field<LibTmux.Client, bool>
