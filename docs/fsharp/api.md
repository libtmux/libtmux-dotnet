# F# API reference

Generated from compiled F# signatures and XML summaries. Regenerate with
`uv run python eng/docs/render_api_reference.py --fsharp`.

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
| `val name: LibTmux.FSharp.Field<LibTmux.Client,Microsoft.FSharp.Core.string>` | Identifies the captured client name for ordinal string and null comparisons. |

## Control

| Signature | Summary |
|---|---|
| `LibTmux.FSharp.Control` | Provides scoped access to core control-mode event streams. |
| `val enter: cancellationToken: System.Threading.CancellationToken -> server: LibTmux.Server -> System.Threading.Tasks.Task<LibTmux.IControlModeSession>` | Opens a core control client with the caller's cancellation token. |
| `val foldEventsWhile: cancellationToken: System.Threading.CancellationToken -> folder: ('State -> LibTmux.TmuxEvent -> System.Threading.Tasks.Task<LibTmux.FSharp.StreamStep<'State>>) -> initial: 'State -> session: LibTmux.IControlModeSession -> System.Threading.Tasks.Task<'State>` | Folds events until the source ends or the folder returns Stop. |
| `val iterEvents: cancellationToken: System.Threading.CancellationToken -> handler: (LibTmux.TmuxEvent -> System.Threading.Tasks.Task) -> session: LibTmux.IControlModeSession -> System.Threading.Tasks.Task<Microsoft.FSharp.Core.unit>` | Awaits one handler at a time for each event from a borrowed control client. |
| `val useSession: work: (LibTmux.IControlModeSession -> System.Threading.Tasks.Task<'State>) -> session: LibTmux.IControlModeSession -> System.Threading.Tasks.Task<'State>` | Runs work with an owned control client and disposes it after the returned task completes. |
| `val withSession: cancellationToken: System.Threading.CancellationToken -> work: (LibTmux.IControlModeSession -> System.Threading.Tasks.Task<'State>) -> server: LibTmux.Server -> System.Threading.Tasks.Task<'State>` | Opens a control client, runs work, and disposes the client after the returned task completes. |

## Field

| Signature | Summary |
|---|---|
| ``LibTmux.FSharp.Field`2`` | Identifies a supported scalar field and its query constant type. |

## Filter

| Signature | Summary |
|---|---|
| `LibTmux.FSharp.Filter` | Constructs portable predicates through the core query translator. |
| ``LibTmux.FSharp.Filter`1`` | Describes a validated portable predicate over one entity type. |
| `val all: relation: LibTmux.FSharp.Relation<'Parent,'Child> -> predicate: LibTmux.FSharp.Filter<'Child> -> LibTmux.FSharp.Filter<'Parent>` | Requires every child to match, returning true for an empty captured relation. |
| `val allOf: filters: LibTmux.FSharp.Filter<'T> Microsoft.FSharp.Collections.list -> LibTmux.FSharp.Filter<'T>` | Requires every predicate in a nonempty list, in input order. |
| `val any: relation: LibTmux.FSharp.Relation<'Parent,'Child> -> predicate: LibTmux.FSharp.Filter<'Child> -> LibTmux.FSharp.Filter<'Parent>` | Requires a matching child, returning false for an empty captured relation. |
| `val anyOf: filters: LibTmux.FSharp.Filter<'T> Microsoft.FSharp.Collections.list -> LibTmux.FSharp.Filter<'T>` | Requires at least one predicate in a nonempty list, in input order. |
| `val eq: value: 'Value -> field: LibTmux.FSharp.Field<'T,'Value> -> LibTmux.FSharp.Filter<'T>` | Matches a field against a constant using the core's equality semantics. |
| `val isNull: field: LibTmux.FSharp.Field<'T,Microsoft.FSharp.Core.string> -> LibTmux.FSharp.Filter<'T>` | Matches a captured null string without treating an uncaptured field as absent. |
| `val negate: filter: LibTmux.FSharp.Filter<'T> -> LibTmux.FSharp.Filter<'T>` | Negates a portable predicate. |
| `val none: relation: LibTmux.FSharp.Relation<'Parent,'Child> -> predicate: LibTmux.FSharp.Filter<'Child> -> LibTmux.FSharp.Filter<'Parent>` | Requires no matching child, returning true for an empty captured relation. |
| `val oneOf: values: 'Value Microsoft.FSharp.Collections.list -> field: LibTmux.FSharp.Field<'T,'Value> -> LibTmux.FSharp.Filter<'T>` | Matches any constant in a nonempty list, preserving operand order. |
| `val startsWith: prefix: Microsoft.FSharp.Core.string -> field: LibTmux.FSharp.Field<'T,Microsoft.FSharp.Core.string> -> LibTmux.FSharp.Filter<'T>` | Matches a string prefix using ordinal comparison. |
| `val toDocument: filter: LibTmux.FSharp.Filter<'T> -> LibTmux.Query.QueryDocument` | Returns the core document validated when the filter was constructed. |
| `val toPredicate: filter: LibTmux.FSharp.Filter<'T> -> ('T -> Microsoft.FSharp.Core.bool)` | Compiles once and returns a predicate for native lazy filtering. |

## Pane

| Signature | Summary |
|---|---|
| `LibTmux.FSharp.Pane` | Reads captured pane fields and starts explicit pane operations. |
| `val capture: cancellationToken: System.Threading.CancellationToken -> request: LibTmux.CapturePaneRequest -> pane: LibTmux.Pane -> System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<Microsoft.FSharp.Core.string>>` | Captures pane contents using the supplied core request. |
| `val currentCommand: pane: LibTmux.Pane -> Microsoft.FSharp.Core.string Microsoft.FSharp.Core.option` | Reads the captured command name, preserving an empty string. |
| `val currentPath: pane: LibTmux.Pane -> Microsoft.FSharp.Core.string Microsoft.FSharp.Core.option` | Reads the captured working directory, preserving an empty string. |
| `val sendKeys: cancellationToken: System.Threading.CancellationToken -> request: LibTmux.SendKeysRequest -> pane: LibTmux.Pane -> System.Threading.Tasks.Task` | Sends text or key names according to the request's literal and Enter settings. |
| `val split: cancellationToken: System.Threading.CancellationToken -> request: LibTmux.SplitPaneRequest -> pane: LibTmux.Pane -> System.Threading.Tasks.Task<LibTmux.Pane>` | Splits the pane and returns the new pane handle. |

## PaneFields

| Signature | Summary |
|---|---|
| `LibTmux.FSharp.PaneFields` | Provides supported pane fields for portable filters. |
| `val currentCommand: LibTmux.FSharp.Field<LibTmux.Pane,Microsoft.FSharp.Core.string>` | Identifies the captured command, including a captured unavailable value. |
| `val id: LibTmux.FSharp.Field<LibTmux.Pane,LibTmux.PaneId>` | Identifies the typed pane ID. |

## Query

| Signature | Summary |
|---|---|
| `LibTmux.FSharp.Query` | Applies portable filters locally to captured objects. |
| `val matching: filter: LibTmux.FSharp.Filter<'T> -> source: 'T Microsoft.FSharp.Collections.seq -> System.Collections.Generic.IReadOnlyList<'T>` | Materializes matching elements, preserving input order and multiplicity. |
| `val matchingWithCancellation: cancellationToken: System.Threading.CancellationToken -> filter: LibTmux.FSharp.Filter<'T> -> source: 'T Microsoft.FSharp.Collections.seq -> System.Collections.Generic.IReadOnlyList<'T>` | Materializes matches with cancellation between elements and predicate nodes. |

## Relation

| Signature | Summary |
|---|---|
| ``LibTmux.FSharp.Relation`2`` | Identifies a supported captured relation between two entity types. |

## Selection

| Signature | Summary |
|---|---|
| `LibTmux.FSharp.Selection` | Selects values from ordinary F# sequences. |
| `val exactlyOne: source: 'T Microsoft.FSharp.Collections.seq -> Microsoft.FSharp.Core.Result<'T,LibTmux.FSharp.CardinalityError>` | Returns the sole match, examining at most two elements. |

## Server

| Signature | Summary |
|---|---|
| `LibTmux.FSharp.Server` | Starts explicit server reads with the caller's cancellation token. |
| `val capture: cancellationToken: System.Threading.CancellationToken -> depth: LibTmux.SnapshotDepth -> server: LibTmux.Server -> System.Threading.Tasks.Task<LibTmux.Server>` | Returns a new server handle captured to the requested depth. |
| `val listPanes: cancellationToken: System.Threading.CancellationToken -> server: LibTmux.Server -> System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<LibTmux.Pane>>` | Lists panes and captures their scalar fields. |
| `val tryFindPane: cancellationToken: System.Threading.CancellationToken -> id: LibTmux.PaneId -> server: LibTmux.Server -> System.Threading.Tasks.Task<LibTmux.Pane Microsoft.FSharp.Core.option>` | Returns a pane or None after a successful lookup establishes absence. |

## SessionFields

| Signature | Summary |
|---|---|
| `LibTmux.FSharp.SessionFields` | Provides supported session fields and relations for portable filters. |
| `val attached: LibTmux.FSharp.Field<LibTmux.Session,Microsoft.FSharp.Core.bool>` | Identifies whether the captured session has attached clients. |
| `val id: LibTmux.FSharp.Field<LibTmux.Session,LibTmux.SessionId>` | Identifies the typed session ID. |
| `val name: LibTmux.FSharp.Field<LibTmux.Session,Microsoft.FSharp.Core.string>` | Identifies the session name for ordinal string and null comparisons. |
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
| `LibTmux.FSharp.Window` | Identifies window placements without refreshing their captured state. |
| `val placementKey: window: LibTmux.Window -> LibTmux.FSharp.WindowPlacementKey` | Returns a comparable key including the captured session and window index. |

## WindowFields

| Signature | Summary |
|---|---|
| `LibTmux.FSharp.WindowFields` | Provides supported window fields and relations for portable filters. |
| `val id: LibTmux.FSharp.Field<LibTmux.Window,LibTmux.WindowId>` | Identifies the typed physical window ID. |
| `val name: LibTmux.FSharp.Field<LibTmux.Window,Microsoft.FSharp.Core.string>` | Identifies the window name for ordinal string and null comparisons. |
| `val panes: LibTmux.FSharp.Relation<LibTmux.Window,LibTmux.Pane>` | Identifies panes captured through this window placement. |

## WindowPlacementKey

| Signature | Summary |
|---|---|
| `LibTmux.FSharp.WindowPlacementKey` | Identifies one indexed placement of a window within a server generation. |
