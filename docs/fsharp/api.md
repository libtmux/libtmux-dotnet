# F# API reference

Generated from compiled F# signatures and XML summaries. Regenerate with
`uv run python eng/docs/render_api_reference.py --fsharp`.

[Detailed member reference](../fsharp-reference/reference/index.md) includes
parameters, return types and source links.
Core handles and request types appear in the
[LibTmux API reference](../api/README.md).

## CaptureState

| Signature | Summary |
|---|---|
| `Captured of value: 'T` | Contains the captured value, including an observed empty collection. |
| ``LibTmux.FSharp.CaptureState`1`` | Distinguishes captured state from a relation the snapshot did not read. |
| `Uncaptured of relation: Microsoft.FSharp.Core.string * depth: LibTmux.SnapshotDepth` | Names the unread relation and the depth the snapshot reached. |

## CardinalityError

| Signature | Summary |
|---|---|
| `LibTmux.FSharp.CardinalityError` | Describes a selection that does not contain exactly one match. |
| `MultipleMatches` | At least two elements matched. |
| `NoMatches` | No element matched. |

## ClientFields

| Signature | Summary |
|---|---|
| `LibTmux.FSharp.ClientFields` | Provides supported client fields for portable filters. |
| `val controlMode: LibTmux.FSharp.Field<LibTmux.Client,Microsoft.FSharp.Core.bool>` | Identifies whether the captured client uses control mode. |
| `val name: LibTmux.FSharp.Field<LibTmux.Client,Microsoft.FSharp.Core.string>` | Identifies the client name. |

## Control

| Signature | Summary |
|---|---|
| `LibTmux.FSharp.Control` | Opens control clients and reads their event streams. |
| `val cleanupFailure: error: Microsoft.FSharp.Core.exn -> Microsoft.FSharp.Core.exn Microsoft.FSharp.Core.option` | Returns the cleanup failure attached to the exception a helper rethrew. |
| `val enter: cancellationToken: System.Threading.CancellationToken -> server: LibTmux.Server -> System.Threading.Tasks.Task<LibTmux.IControlModeSession>` | Opens a control client attached to the most recently used session. |
| `val enterSession: cancellationToken: System.Threading.CancellationToken -> session: LibTmux.Session -> System.Threading.Tasks.Task<LibTmux.IControlModeSession>` | Opens a control client attached to a session. |
| `val events: session: LibTmux.IControlModeSession -> System.Collections.Generic.IAsyncEnumerable<LibTmux.TmuxEvent>` | Streams every event a control client reports. |
| `val foldWhile: cancellationToken: System.Threading.CancellationToken -> folder: ('State -> 'T -> System.Threading.Tasks.Task<LibTmux.FSharp.StreamStep<'State>>) -> initial: 'State -> source: System.Collections.Generic.IAsyncEnumerable<'T> -> System.Threading.Tasks.Task<'State>` | Folds items until the stream ends or the folder returns Stop. |
| `val iter: cancellationToken: System.Threading.CancellationToken -> handler: ('T -> System.Threading.Tasks.Task) -> source: System.Collections.Generic.IAsyncEnumerable<'T> -> System.Threading.Tasks.Task<Microsoft.FSharp.Core.unit>` | Awaits one handler at a time for each item until the stream ends. |
| `val useSession: work: (LibTmux.IControlModeSession -> System.Threading.Tasks.Task<'State>) -> session: LibTmux.IControlModeSession -> System.Threading.Tasks.Task<'State>` | Runs work with an owned control client and disposes it after the returned task completes. |
| `val watchPane: pane: LibTmux.Pane -> session: LibTmux.IControlModeSession -> System.Collections.Generic.IAsyncEnumerable<LibTmux.TmuxEvent>` | Streams one pane's output from a borrowed control client. |
| `val withSession: cancellationToken: System.Threading.CancellationToken -> work: (LibTmux.IControlModeSession -> System.Threading.Tasks.Task<'State>) -> server: LibTmux.Server -> System.Threading.Tasks.Task<'State>` | Opens a control client, runs work, and disposes the client after the returned task completes. |

## Field

| Signature | Summary |
|---|---|
| ``LibTmux.FSharp.Field`2`` | Identifies a supported scalar field and its query constant type. |

## Filter

| Signature | Summary |
|---|---|
| `LibTmux.FSharp.Filter` | Constructs portable predicates without reflection. |
| ``LibTmux.FSharp.Filter`1`` | Describes a validated portable predicate over one entity type. |
| `val all: relation: LibTmux.FSharp.Relation<'Parent,'Child> -> predicate: LibTmux.FSharp.Filter<'Child> -> LibTmux.FSharp.Filter<'Parent>` | Requires every child to match, returning true for an empty captured relation. |
| `val allOf: filters: LibTmux.FSharp.Filter<'T> Microsoft.FSharp.Collections.list -> LibTmux.FSharp.Filter<'T>` | Requires every predicate in order; an empty list matches everything. |
| `val any: relation: LibTmux.FSharp.Relation<'Parent,'Child> -> predicate: LibTmux.FSharp.Filter<'Child> -> LibTmux.FSharp.Filter<'Parent>` | Requires a matching child, returning false for an empty captured relation. |
| `val anyOf: filters: LibTmux.FSharp.Filter<'T> Microsoft.FSharp.Collections.list -> LibTmux.FSharp.Filter<'T>` | Requires at least one predicate in order; an empty list matches nothing. |
| `val contains: text: Microsoft.FSharp.Core.string -> field: LibTmux.FSharp.Field<'T,Microsoft.FSharp.Core.string> -> LibTmux.FSharp.Filter<'T>` | Matches a string containing a substring. |
| `val containsIgnoreCase: text: Microsoft.FSharp.Core.string -> field: LibTmux.FSharp.Field<'T,Microsoft.FSharp.Core.string> -> LibTmux.FSharp.Filter<'T>` | Matches a string containing a substring ignoring case. |
| `val endsWith: suffix: Microsoft.FSharp.Core.string -> field: LibTmux.FSharp.Field<'T,Microsoft.FSharp.Core.string> -> LibTmux.FSharp.Filter<'T>` | Matches a string suffix. |
| `val endsWithIgnoreCase: suffix: Microsoft.FSharp.Core.string -> field: LibTmux.FSharp.Field<'T,Microsoft.FSharp.Core.string> -> LibTmux.FSharp.Filter<'T>` | Matches a string suffix ignoring case. |
| `val eq: value: 'Value -> field: LibTmux.FSharp.Field<'T,'Value> -> LibTmux.FSharp.Filter<'T>` | Matches a field equal to a constant. |
| `val eqIgnoreCase: value: Microsoft.FSharp.Core.string -> field: LibTmux.FSharp.Field<'T,Microsoft.FSharp.Core.string> -> LibTmux.FSharp.Filter<'T>` | Matches a string equal to a constant ignoring case. |
| `val ge: value: Microsoft.FSharp.Core.int -> field: LibTmux.FSharp.Field<'T,Microsoft.FSharp.Core.int> -> LibTmux.FSharp.Filter<'T>` | Matches a count at least a constant. |
| `val gt: value: Microsoft.FSharp.Core.int -> field: LibTmux.FSharp.Field<'T,Microsoft.FSharp.Core.int> -> LibTmux.FSharp.Filter<'T>` | Matches a count above a constant. |
| `val isNull: field: LibTmux.FSharp.Field<'T,Microsoft.FSharp.Core.string> -> LibTmux.FSharp.Filter<'T>` | Matches a captured null string without treating an uncaptured field as absent. |
| `val le: value: Microsoft.FSharp.Core.int -> field: LibTmux.FSharp.Field<'T,Microsoft.FSharp.Core.int> -> LibTmux.FSharp.Filter<'T>` | Matches a count at most a constant. |
| `val lt: value: Microsoft.FSharp.Core.int -> field: LibTmux.FSharp.Field<'T,Microsoft.FSharp.Core.int> -> LibTmux.FSharp.Filter<'T>` | Matches a count below a constant. |
| `val matches: pattern: Microsoft.FSharp.Core.string -> field: LibTmux.FSharp.Field<'T,Microsoft.FSharp.Core.string> -> LibTmux.FSharp.Filter<'T>` | Matches a string against a culture-invariant .NET regular expression. |
| `val matchesIgnoreCase: pattern: Microsoft.FSharp.Core.string -> field: LibTmux.FSharp.Field<'T,Microsoft.FSharp.Core.string> -> LibTmux.FSharp.Filter<'T>` | Matches a string against a culture-invariant .NET regular expression ignoring case. |
| `val ne: value: 'Value -> field: LibTmux.FSharp.Field<'T,'Value> -> LibTmux.FSharp.Filter<'T>` | Matches a field not equal to a constant. |
| `val negate: filter: LibTmux.FSharp.Filter<'T> -> LibTmux.FSharp.Filter<'T>` | Negates a portable predicate. |
| `val none: relation: LibTmux.FSharp.Relation<'Parent,'Child> -> predicate: LibTmux.FSharp.Filter<'Child> -> LibTmux.FSharp.Filter<'Parent>` | Requires no matching child, returning true for an empty captured relation. |
| `val notOneOf: values: 'Value Microsoft.FSharp.Collections.list -> field: LibTmux.FSharp.Field<'T,'Value> -> LibTmux.FSharp.Filter<'T>` | Matches no constant in a list; an empty list matches everything. |
| `val oneOf: values: 'Value Microsoft.FSharp.Collections.list -> field: LibTmux.FSharp.Field<'T,'Value> -> LibTmux.FSharp.Filter<'T>` | Matches any constant in a list; an empty list matches nothing. |
| `val startsWith: prefix: Microsoft.FSharp.Core.string -> field: LibTmux.FSharp.Field<'T,Microsoft.FSharp.Core.string> -> LibTmux.FSharp.Filter<'T>` | Matches a string prefix. |
| `val startsWithIgnoreCase: prefix: Microsoft.FSharp.Core.string -> field: LibTmux.FSharp.Field<'T,Microsoft.FSharp.Core.string> -> LibTmux.FSharp.Filter<'T>` | Matches a string prefix ignoring case. |
| `val toDocument: filter: LibTmux.FSharp.Filter<'T> -> LibTmux.Query.QueryDocument` | Returns the core document validated when the filter was constructed. |
| `val toPredicate: filter: LibTmux.FSharp.Filter<'T> -> ('T -> Microsoft.FSharp.Core.bool)` | Returns a predicate compiled once for native lazy filtering. |

## Mirror

| Signature | Summary |
|---|---|
| `LibTmux.FSharp.Mirror` | Follows a server's sessions, windows, panes and clients as tmux announces changes. |
| `val current: mirror: LibTmux.ServerMirror -> LibTmux.ServerMirrorView` | Returns the latest published view. |
| `val start: cancellationToken: System.Threading.CancellationToken -> anchor: LibTmux.Session -> System.Threading.Tasks.Task<LibTmux.ServerMirror>` | Mirrors the server an anchor session belongs to, capturing on each announcement. |
| `val startRefreshing: cancellationToken: System.Threading.CancellationToken -> every: System.TimeSpan -> anchor: LibTmux.Session -> System.Threading.Tasks.Task<LibTmux.ServerMirror>` | Mirrors a server, also capturing whenever it has been quiet for an interval. |
| `val views: mirror: LibTmux.ServerMirror -> System.Collections.Generic.IAsyncEnumerable<LibTmux.ServerMirrorView>` | Streams the current view and each newer one, skipping views published while the reader was busy. |
| `val waitUntil: cancellationToken: System.Threading.CancellationToken -> timeout: System.TimeSpan -> condition: (LibTmux.ServerMirrorView -> Microsoft.FSharp.Core.bool) -> mirror: LibTmux.ServerMirror -> System.Threading.Tasks.Task<LibTmux.ServerMirrorView>` | Waits until a view satisfies a condition, testing the current view first. |

## Options

| Signature | Summary |
|---|---|
| `LibTmux.FSharp.Options` | Reads and writes options through keys that know their value's type. |
| `val get: cancellationToken: System.Threading.CancellationToken -> key: LibTmux.TmuxOptionKey<'T> -> options: LibTmux.TmuxOptions -> System.Threading.Tasks.Task<'T> when 'T: not null` | Reads the value an option has in a scope, set there or inherited, as its key's type. |
| `val set: cancellationToken: System.Threading.CancellationToken -> key: LibTmux.TmuxOptionKey<'T> -> value: 'T -> options: LibTmux.TmuxOptions -> System.Threading.Tasks.Task when 'T: not null` | Sets an option in a scope from a value of its key's type. |

## Pane

| Signature | Summary |
|---|---|
| `LibTmux.FSharp.Pane` | Reads captured pane fields and starts explicit pane operations. |
| `val capture: cancellationToken: System.Threading.CancellationToken -> request: LibTmux.CapturePaneRequest -> pane: LibTmux.Pane -> System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<Microsoft.FSharp.Core.string>>` | Captures pane contents using the supplied core request. |
| `val currentCommand: pane: LibTmux.Pane -> Microsoft.FSharp.Core.string Microsoft.FSharp.Core.option` | Reads the captured command name, preserving an empty string. |
| `val currentPath: pane: LibTmux.Pane -> Microsoft.FSharp.Core.string Microsoft.FSharp.Core.option` | Reads the captured working directory, preserving an empty string. |
| `val findOnScreen: cancellationToken: System.Threading.CancellationToken -> search: LibTmux.FSharp.ScreenSearch -> pane: LibTmux.Pane -> System.Threading.Tasks.Task<Microsoft.FSharp.Core.int Microsoft.FSharp.Core.option>` | Returns the first visible row showing the text, counted from 1, or None. |
| `val run: cancellationToken: System.Threading.CancellationToken -> timeout: System.TimeSpan -> command: Microsoft.FSharp.Core.string -> pane: LibTmux.Pane -> System.Threading.Tasks.Task<LibTmux.PaneRunResult>` | Runs a shell command in the pane and waits for its exit status and output. |
| `val sendKeys: cancellationToken: System.Threading.CancellationToken -> request: LibTmux.SendKeysRequest -> pane: LibTmux.Pane -> System.Threading.Tasks.Task` | Sends text or key names according to the request's literal and Enter settings. |
| `val split: cancellationToken: System.Threading.CancellationToken -> request: LibTmux.SplitPaneRequest -> pane: LibTmux.Pane -> System.Threading.Tasks.Task<LibTmux.Pane>` | Splits the pane and returns the new pane handle. |
| `val waitFor: cancellationToken: System.Threading.CancellationToken -> request: LibTmux.PaneWaitRequest -> pane: LibTmux.Pane -> System.Threading.Tasks.Task<LibTmux.PaneWaitResult>` | Waits as the request describes: patterns, stop patterns, or any output. |
| `val waitForText: cancellationToken: System.Threading.CancellationToken -> timeout: System.TimeSpan -> text: Microsoft.FSharp.Core.string -> pane: LibTmux.Pane -> System.Threading.Tasks.Task<LibTmux.PaneWaitResult>` | Waits for a line the pane prints to contain the text. |
| `val waitUntil: cancellationToken: System.Threading.CancellationToken -> timeout: System.TimeSpan -> condition: (System.Collections.Generic.IReadOnlyList<Microsoft.FSharp.Core.string> -> Microsoft.FSharp.Core.bool) -> pane: LibTmux.Pane -> System.Threading.Tasks.Task<LibTmux.PaneWaitResult>` | Waits until a condition holds over the rows the pane shows, top to bottom. |

## PaneFields

| Signature | Summary |
|---|---|
| `LibTmux.FSharp.PaneFields` | Provides supported pane fields for portable filters. |
| `val currentCommand: LibTmux.FSharp.Field<LibTmux.Pane,Microsoft.FSharp.Core.string>` | Identifies the captured command, including a captured unavailable value. |
| `val id: LibTmux.FSharp.Field<LibTmux.Pane,LibTmux.PaneId>` | Identifies the typed pane ID. |

## Query

| Signature | Summary |
|---|---|
| `LibTmux.FSharp.Query` | Narrows and runs tmux queries, and filters captured objects locally. |
| ``LibTmux.FSharp.Query`1`` | Describes a tmux listing: a scope, filters and text panes must show. |
| `val exactlyOne: cancellationToken: System.Threading.CancellationToken -> query: LibTmux.FSharp.Query<'T> -> System.Threading.Tasks.Task<Microsoft.FSharp.Core.Result<'T,LibTmux.FSharp.CardinalityError>>` | Reads the sole match, or why there is not exactly one. |
| `val list: cancellationToken: System.Threading.CancellationToken -> query: LibTmux.FSharp.Query<'T> -> System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<'T>>` | Reads the matching objects in tmux's listing order. |
| `val matching: filter: LibTmux.FSharp.Filter<'T> -> source: 'T Microsoft.FSharp.Collections.seq -> System.Collections.Generic.IReadOnlyList<'T>` | Filters captured objects locally, preserving input order and multiplicity. |
| `val showing: search: LibTmux.FSharp.ScreenSearch -> query: LibTmux.FSharp.Query<LibTmux.Pane> -> LibTmux.FSharp.Query<LibTmux.Pane>` | Keeps panes whose visible rows show the searched text. |
| `val tryExactlyOne: cancellationToken: System.Threading.CancellationToken -> query: LibTmux.FSharp.Query<'T> -> System.Threading.Tasks.Task<'T Microsoft.FSharp.Core.option>` | Reads the sole match, or None when there are none or several. |
| `val where: filter: LibTmux.FSharp.Filter<'T> -> query: LibTmux.FSharp.Query<'T> -> LibTmux.FSharp.Query<'T>` | Adds a portable filter every result satisfies. |
| `val whereUnsafe: filter: LibTmux.UnsafeTmuxFilter -> query: LibTmux.FSharp.Query<'T> -> LibTmux.FSharp.Query<'T>` | Adds a raw tmux filter, which tmux evaluates and nothing rechecks. |

## Relation

| Signature | Summary |
|---|---|
| ``LibTmux.FSharp.Relation`2`` | Identifies a supported captured relation between two entity types. |

## ScreenSearch

| Signature | Summary |
|---|---|
| `LibTmux.FSharp.ScreenSearch` | Describes text tmux searches for on a pane's visible rows. |
| `PosixRegex of pattern: Microsoft.FSharp.Core.string` | Matches a POSIX extended regular expression, which tmux evaluates. |
| `PosixRegexIgnoringCase of pattern: Microsoft.FSharp.Core.string` | Matches a POSIX extended regular expression ignoring case. |
| `Text of text: Microsoft.FSharp.Core.string` | Matches literal text. |
| `TextIgnoringCase of text: Microsoft.FSharp.Core.string` | Matches literal text ignoring case. |

## Selection

| Signature | Summary |
|---|---|
| `LibTmux.FSharp.Selection` | Selects values from ordinary F# sequences. |
| `val exactlyOne: source: 'T Microsoft.FSharp.Collections.seq -> Microsoft.FSharp.Core.Result<'T,LibTmux.FSharp.CardinalityError>` | Returns the sole match, examining at most two elements. |

## Server

| Signature | Summary |
|---|---|
| `LibTmux.FSharp.Server` | Starts server reads and queries with the caller's cancellation token. |
| `val capture: cancellationToken: System.Threading.CancellationToken -> depth: LibTmux.SnapshotDepth -> server: LibTmux.Server -> System.Threading.Tasks.Task<LibTmux.Server>` | Returns a new server handle captured to the requested depth. |
| `val clients: server: LibTmux.Server -> LibTmux.FSharp.Query<LibTmux.Client>` | Queries attached clients. |
| `val panes: server: LibTmux.Server -> LibTmux.FSharp.Query<LibTmux.Pane>` | Queries every pane. |
| `val sessions: server: LibTmux.Server -> LibTmux.FSharp.Query<LibTmux.Session>` | Queries every session. |
| `val tryFindClient: cancellationToken: System.Threading.CancellationToken -> name: Microsoft.FSharp.Core.string -> server: LibTmux.Server -> System.Threading.Tasks.Task<LibTmux.Client Microsoft.FSharp.Core.option>` | Returns the client with an exact name or None after a successful listing finds no match. |
| `val tryFindPane: cancellationToken: System.Threading.CancellationToken -> id: LibTmux.PaneId -> server: LibTmux.Server -> System.Threading.Tasks.Task<LibTmux.Pane Microsoft.FSharp.Core.option>` | Returns a pane or None after a successful lookup establishes absence. |
| `val tryFindSession: cancellationToken: System.Threading.CancellationToken -> id: LibTmux.SessionId -> server: LibTmux.Server -> System.Threading.Tasks.Task<LibTmux.Session Microsoft.FSharp.Core.option>` | Returns a session or None after a successful lookup establishes absence. |
| `val tryFindWindow: cancellationToken: System.Threading.CancellationToken -> id: LibTmux.WindowId -> server: LibTmux.Server -> System.Threading.Tasks.Task<LibTmux.Window Microsoft.FSharp.Core.option>` | Returns a window or None after a successful lookup establishes absence. |
| `val windows: server: LibTmux.Server -> LibTmux.FSharp.Query<LibTmux.Window>` | Queries window placements across all sessions. |

## Session

| Signature | Summary |
|---|---|
| `LibTmux.FSharp.Session` | Starts queries confined to one session. |
| `val panes: session: LibTmux.Session -> LibTmux.FSharp.Query<LibTmux.Pane>` | Queries the panes of every window in a session. |
| `val windows: session: LibTmux.Session -> LibTmux.FSharp.Query<LibTmux.Window>` | Queries the window placements in a session. |

## SessionFields

| Signature | Summary |
|---|---|
| `LibTmux.FSharp.SessionFields` | Provides supported session fields and relations for portable filters. |
| `val attached: LibTmux.FSharp.Field<LibTmux.Session,Microsoft.FSharp.Core.bool>` | Identifies whether the captured session has attached clients. |
| `val id: LibTmux.FSharp.Field<LibTmux.Session,LibTmux.SessionId>` | Identifies the typed session ID. |
| `val name: LibTmux.FSharp.Field<LibTmux.Session,Microsoft.FSharp.Core.string>` | Identifies the session name. |
| `val windowCount: LibTmux.FSharp.Field<LibTmux.Session,Microsoft.FSharp.Core.int>` | Identifies the number of windows linked into the session. |
| `val windows: LibTmux.FSharp.Relation<LibTmux.Session,LibTmux.Window>` | Identifies captured window placements within the session. |

## Snapshot

| Signature | Summary |
|---|---|
| `LibTmux.FSharp.Snapshot` | Reads captured values without contacting tmux. |
| `val relation: relation: LibTmux.CapturedRelation<'T> -> LibTmux.FSharp.CaptureState<System.Collections.Generic.IReadOnlyList<'T>>` | Distinguishes captured children from an unread relation. |
| `val value: value: LibTmux.CapturedValue<'T> -> LibTmux.FSharp.CaptureState<'T> when 'T: not struct and 'T: not null` | Distinguishes a captured child from an unread value. |

## StreamStep

| Signature | Summary |
|---|---|
| `Continue of state: 'State` | Retains state and reads the next event. |
| ``LibTmux.FSharp.StreamStep`1`` | Represents a decision to continue or stop an event fold. |
| `Stop of state: 'State` | Retains state and stops before reading another event. |

## Window

| Signature | Summary |
|---|---|
| `LibTmux.FSharp.Window` | Identifies window placements and starts queries confined to one window. |
| `val panes: window: LibTmux.Window -> LibTmux.FSharp.Query<LibTmux.Pane>` | Queries the panes in a window. |
| `val placementKey: window: LibTmux.Window -> LibTmux.FSharp.WindowPlacementKey` | Returns a comparable key including the captured session and window index. |

## WindowFields

| Signature | Summary |
|---|---|
| `LibTmux.FSharp.WindowFields` | Provides supported window fields and relations for portable filters. |
| `val id: LibTmux.FSharp.Field<LibTmux.Window,LibTmux.WindowId>` | Identifies the typed physical window ID. |
| `val name: LibTmux.FSharp.Field<LibTmux.Window,Microsoft.FSharp.Core.string>` | Identifies the window name. |
| `val paneCount: LibTmux.FSharp.Field<LibTmux.Window,Microsoft.FSharp.Core.int>` | Identifies the number of panes in the window. |
| `val panes: LibTmux.FSharp.Relation<LibTmux.Window,LibTmux.Pane>` | Identifies panes captured through this window placement. |

## WindowPlacementKey

| Signature | Summary |
|---|---|
| `LibTmux.FSharp.WindowPlacementKey` | Identifies one indexed placement of a window within a server generation. |
