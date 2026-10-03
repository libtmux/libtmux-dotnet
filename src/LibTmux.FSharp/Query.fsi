namespace LibTmux.FSharp

open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
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

/// <summary>Constructs portable predicates without reflection.</summary>
/// <remarks>
/// <para>
/// String operations compare ordinally; the <c>IgnoreCase</c> forms use ordinal
/// case-insensitive comparison. tmux evaluates the case-sensitive string,
/// equality, flag, identifier and count operations itself when a listing
/// pushes the filter down; every result is rechecked with these semantics.
/// </para>
/// </remarks>
[<RequireQualifiedAccess>]
module Filter =
    /// <summary>Matches a field equal to a constant.</summary>
    val eq: value: 'Value -> field: Field<'T, 'Value> -> Filter<'T>

    /// <summary>Matches a field not equal to a constant.</summary>
    val ne: value: 'Value -> field: Field<'T, 'Value> -> Filter<'T>

    /// <summary>Matches a string equal to a constant ignoring case.</summary>
    val eqIgnoreCase: value: string -> field: Field<'T, string> -> Filter<'T>

    /// <summary>Matches a captured null string without treating an uncaptured field as absent.</summary>
    val isNull: field: Field<'T, string> -> Filter<'T>

    /// <summary>Matches a string prefix.</summary>
    val startsWith: prefix: string -> field: Field<'T, string> -> Filter<'T>

    /// <summary>Matches a string prefix ignoring case.</summary>
    val startsWithIgnoreCase: prefix: string -> field: Field<'T, string> -> Filter<'T>

    /// <summary>Matches a string suffix.</summary>
    val endsWith: suffix: string -> field: Field<'T, string> -> Filter<'T>

    /// <summary>Matches a string suffix ignoring case.</summary>
    val endsWithIgnoreCase: suffix: string -> field: Field<'T, string> -> Filter<'T>

    /// <summary>Matches a string containing a substring.</summary>
    val contains: text: string -> field: Field<'T, string> -> Filter<'T>

    /// <summary>Matches a string containing a substring ignoring case.</summary>
    val containsIgnoreCase: text: string -> field: Field<'T, string> -> Filter<'T>

    /// <summary>Matches a string against a culture-invariant .NET regular expression.</summary>
    /// <remarks>The pattern is unanchored, as <c>Regex.IsMatch</c> is; one match may run for one second.</remarks>
    /// <exception cref="T:LibTmux.UnsupportedQueryExpressionException">The pattern is invalid or longer than 1024 characters.</exception>
    val matches: pattern: string -> field: Field<'T, string> -> Filter<'T>

    /// <summary>Matches a string against a culture-invariant .NET regular expression ignoring case.</summary>
    /// <exception cref="T:LibTmux.UnsupportedQueryExpressionException">The pattern is invalid or longer than 1024 characters.</exception>
    val matchesIgnoreCase: pattern: string -> field: Field<'T, string> -> Filter<'T>

    /// <summary>Matches a count below a constant.</summary>
    val lt: value: int -> field: Field<'T, int> -> Filter<'T>

    /// <summary>Matches a count at most a constant.</summary>
    val le: value: int -> field: Field<'T, int> -> Filter<'T>

    /// <summary>Matches a count above a constant.</summary>
    val gt: value: int -> field: Field<'T, int> -> Filter<'T>

    /// <summary>Matches a count at least a constant.</summary>
    val ge: value: int -> field: Field<'T, int> -> Filter<'T>

    /// <summary>Matches any constant in a list; an empty list matches nothing.</summary>
    val oneOf: values: 'Value list -> field: Field<'T, 'Value> -> Filter<'T>

    /// <summary>Matches no constant in a list; an empty list matches everything.</summary>
    val notOneOf: values: 'Value list -> field: Field<'T, 'Value> -> Filter<'T>

    /// <summary>Requires every predicate in order; an empty list matches everything.</summary>
    val allOf: filters: Filter<'T> list -> Filter<'T>

    /// <summary>Requires at least one predicate in order; an empty list matches nothing.</summary>
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

    /// <summary>Returns a predicate compiled once for native lazy filtering.</summary>
    val toPredicate: filter: Filter<'T> -> ('T -> bool)

/// <summary>Describes text tmux searches for on a pane's visible rows.</summary>
/// <remarks>
/// tmux evaluates the search itself, as <c>find-window -C</c> does: it reads
/// only the rows on screen, with trailing spaces removed. To search history,
/// capture the pane and filter its lines.
/// </remarks>
[<RequireQualifiedAccess>]
type ScreenSearch =
    /// <summary>Matches literal text.</summary>
    | Text of text: string
    /// <summary>Matches literal text ignoring case.</summary>
    | TextIgnoringCase of text: string
    /// <summary>Matches a POSIX extended regular expression, which tmux evaluates.</summary>
    | PosixRegex of pattern: string
    /// <summary>Matches a POSIX extended regular expression ignoring case.</summary>
    | PosixRegexIgnoringCase of pattern: string

module internal ScreenSearch =
    val toCore: search: ScreenSearch -> PaneScreenSearch

/// <summary>Describes a tmux listing: a scope, filters and text panes must show.</summary>
/// <remarks>
/// Building a query reads nothing; each run reads tmux again. tmux narrows the
/// listing with its own filter where it can evaluate one exactly, and every
/// row is then checked against the portable filters.
/// </remarks>
[<Sealed>]
type Query<'T> =
    static member internal Create:
        server: LibTmux.Server * target: QueryTarget * session: SessionId option * window: WindowId option -> Query<'T>

/// <summary>Narrows and runs tmux queries, and filters captured objects locally.</summary>
[<RequireQualifiedAccess>]
module Query =
    /// <summary>Adds a portable filter every result satisfies.</summary>
    val where: filter: Filter<'T> -> query: Query<'T> -> Query<'T>

    /// <summary>Adds a raw tmux filter, which tmux evaluates and nothing rechecks.</summary>
    /// <remarks>A malformed or unknown token makes tmux keep no rows rather than report an error.</remarks>
    val whereUnsafe: filter: UnsafeTmuxFilter -> query: Query<'T> -> Query<'T>

    /// <summary>Keeps panes whose visible rows show the searched text.</summary>
    val showing: search: ScreenSearch -> query: Query<LibTmux.Pane> -> Query<LibTmux.Pane>

    /// <summary>Reads the matching objects in tmux's listing order.</summary>
    /// <remarks>A filter over a relation reads a snapshot of only the sessions that can match.</remarks>
    /// <exception cref="T:LibTmux.TmuxVersionTooLowException">A raw client filter needs tmux 3.4.</exception>
    val list: cancellationToken: CancellationToken -> query: Query<'T> -> Task<IReadOnlyList<'T>>

    /// <summary>Reads the sole match, or why there is not exactly one.</summary>
    val exactlyOne: cancellationToken: CancellationToken -> query: Query<'T> -> Task<Result<'T, CardinalityError>>

    /// <summary>Reads the sole match, or None when there are none or several.</summary>
    val tryExactlyOne: cancellationToken: CancellationToken -> query: Query<'T> -> Task<'T option>

    /// <summary>Filters captured objects locally, preserving input order and multiplicity.</summary>
    val matching: filter: Filter<'T> -> source: seq<'T> -> IReadOnlyList<'T>

/// <summary>Provides supported session fields and relations for portable filters.</summary>
[<RequireQualifiedAccess>]
module SessionFields =
    /// <summary>Identifies the session name.</summary>
    val name: Field<LibTmux.Session, string>
    /// <summary>Identifies the typed session ID.</summary>
    val id: Field<LibTmux.Session, SessionId>
    /// <summary>Identifies whether the captured session has attached clients.</summary>
    val attached: Field<LibTmux.Session, bool>
    /// <summary>Identifies the number of windows linked into the session.</summary>
    val windowCount: Field<LibTmux.Session, int>
    /// <summary>Identifies captured window placements within the session.</summary>
    val windows: Relation<LibTmux.Session, LibTmux.Window>

/// <summary>Provides supported window fields and relations for portable filters.</summary>
[<RequireQualifiedAccess>]
module WindowFields =
    /// <summary>Identifies the window name.</summary>
    val name: Field<LibTmux.Window, string>
    /// <summary>Identifies the typed physical window ID.</summary>
    val id: Field<LibTmux.Window, WindowId>
    /// <summary>Identifies the number of panes in the window.</summary>
    val paneCount: Field<LibTmux.Window, int>
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
    /// <summary>Identifies the client name.</summary>
    val name: Field<LibTmux.Client, string>
    /// <summary>Identifies whether the captured client uses control mode.</summary>
    val controlMode: Field<LibTmux.Client, bool>
