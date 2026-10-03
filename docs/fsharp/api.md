# F# API reference

Generated from compiled F# signatures and XML summaries. Regenerate with
`uv run python eng/docs/render_api_reference.py --fsharp`.

[Detailed member reference](../fsharp-reference/reference/index.md) includes
parameters, return types and source links.
Core handles and request types appear in the
[LibTmux API reference](../api/README.md).
Signatures assume `open System`, `open System.Threading`,
`open System.Threading.Tasks`, `open System.Collections.Generic`,
`open LibTmux` and `open LibTmux.FSharp`. A core type that shares its name
with a module here, such as `LibTmux.Pane`, keeps its prefix.

## By task

- **Find and filter:** [Query](#query), [Filter](#filter), [Field](#field), [Relation](#relation), [SessionFields](#sessionfields), [WindowFields](#windowfields), [PaneFields](#panefields), [ClientFields](#clientfields), [ScreenSearch](#screensearch), [Selection](#selection), [CardinalityError](#cardinalityerror)
- **Servers, sessions, windows and panes:** [Server](#server), [Session](#session), [Window](#window), [Pane](#pane), [SessionSpec](#sessionspec), [WindowSpec](#windowspec), [SplitSpec](#splitspec), [Chain](#chain), [Options](#options), [WindowPlacementKey](#windowplacementkey)
- **Wait, run and read results:** [PaneWait](#panewait), [PaneRun](#panerun)
- **Live state and events:** [Control](#control), [Mirror](#mirror), [StreamStep](#streamstep), [Snapshot](#snapshot), [CaptureState](#capturestate)
- **Failures and retries:** [TmuxFailure](#tmuxfailure), [Retry](#retry)

## CaptureState

| Signature | Summary |
|---|---|
| `Captured of value: 'T` | Contains the captured value, including an observed empty collection. |
| ``CaptureState`1`` | Distinguishes captured state from a relation the snapshot did not read. |
| `Uncaptured of relation: string * depth: SnapshotDepth` | Names the unread relation and the depth the snapshot reached. |

## CardinalityError

| Signature | Summary |
|---|---|
| `CardinalityError` | Describes a selection that does not contain exactly one match. |
| `MultipleMatches` | At least two elements matched. |
| `NoMatches` | No element matched. |

## Chain

| Signature | Summary |
|---|---|
| `module Chain` | Builds commands tmux runs together, each acting on what the one before made. |
| `val add: command: TmuxCommand -> chain: TmuxChain -> TmuxChain` | Appends any command, such as a typed request's ToCommand. |
| `val arrange: layout: string -> chain: TmuxChain -> TmuxChain` | Arranges the current window with a tmux layout; the chain checks the name before tmux sees it. |
| `val newWindow: session: LibTmux.Session -> name: string -> chain: TmuxChain -> TmuxChain` | Adds a window to a session and makes it the one following steps act on. |
| `val run: cancellationToken: CancellationToken -> chain: TmuxChain -> Task<TmuxCommandResult>` | Runs every command in one tmux invocation and returns tmux's combined answer. |
| `val sendLine: line: string -> chain: TmuxChain -> TmuxChain` | Types a line into the current pane and presses Enter. |
| `val splitLeftRight: chain: TmuxChain -> TmuxChain` | Splits the current pane into a left and a right one; the right becomes current. |
| `val splitTopBottom: chain: TmuxChain -> TmuxChain` | Splits the current pane into a top and a bottom one; the bottom becomes current. |
| `val start: server: LibTmux.Server -> TmuxChain` | Starts an empty chain against a server. |

## ClientFields

| Signature | Summary |
|---|---|
| `module ClientFields` | Provides supported client fields for portable filters. |
| `val controlMode: Field<Client,bool>` | Identifies whether the captured client uses control mode. |
| `val name: Field<Client,string>` | Identifies the client name. |

## Control

| Signature | Summary |
|---|---|
| `module Control` | Opens control clients and reads their event streams. |
| `val cleanupFailure: error: exn -> exn option` | Returns the cleanup failure attached to the exception a helper rethrew. |
| `val enter: cancellationToken: CancellationToken -> server: LibTmux.Server -> Task<IControlModeSession>` | Opens a control client attached to the most recently used session. |
| `val enterSession: cancellationToken: CancellationToken -> session: LibTmux.Session -> Task<IControlModeSession>` | Opens a control client attached to a session. |
| `val events: session: IControlModeSession -> IAsyncEnumerable<TmuxEvent>` | Streams every event a control client reports. |
| `val foldWhile: cancellationToken: CancellationToken -> folder: ('State -> 'T -> Task<StreamStep<'State>>) -> initial: 'State -> source: IAsyncEnumerable<'T> -> Task<'State>` | Folds items until the stream ends or the folder returns Stop. |
| `val iter: cancellationToken: CancellationToken -> handler: ('T -> Task) -> source: IAsyncEnumerable<'T> -> Task<unit>` | Awaits one handler at a time for each item until the stream ends. |
| `val useSession: work: (IControlModeSession -> Task<'State>) -> session: IControlModeSession -> Task<'State>` | Runs work with an owned control client and disposes it after the returned task completes. |
| `val watchPane: pane: LibTmux.Pane -> session: IControlModeSession -> IAsyncEnumerable<TmuxEvent>` | Streams one pane's output from a borrowed control client. |
| `val watchPanes: panes: LibTmux.Pane list -> session: IControlModeSession -> IAsyncEnumerable<TmuxEvent>` | Streams several panes' output from one borrowed control client. |
| `val withSession: cancellationToken: CancellationToken -> work: (IControlModeSession -> Task<'State>) -> server: LibTmux.Server -> Task<'State>` | Opens a control client, runs work, and disposes the client after the returned task completes. |

## Field

| Signature | Summary |
|---|---|
| ``Field`2`` | Identifies a supported scalar field and its query constant type. |

## Filter

| Signature | Summary |
|---|---|
| ``Filter`1`` | Describes a validated portable predicate over one entity type. |
| `module Filter` | Constructs portable predicates without reflection. |
| `val all: relation: Relation<'Parent,'Child> -> predicate: Filter<'Child> -> Filter<'Parent>` | Requires every child to match, returning true for an empty captured relation. |
| `val allOf: filters: Filter<'T> list -> Filter<'T>` | Requires every predicate in order; an empty list matches everything. |
| `val any: relation: Relation<'Parent,'Child> -> predicate: Filter<'Child> -> Filter<'Parent>` | Requires a matching child, returning false for an empty captured relation. |
| `val anyOf: filters: Filter<'T> list -> Filter<'T>` | Requires at least one predicate in order; an empty list matches nothing. |
| `val contains: text: string -> field: Field<'T,string> -> Filter<'T>` | Matches a string containing a substring. |
| `val containsIgnoreCase: text: string -> field: Field<'T,string> -> Filter<'T>` | Matches a string containing a substring ignoring case. |
| `val endsWith: suffix: string -> field: Field<'T,string> -> Filter<'T>` | Matches a string suffix. |
| `val endsWithIgnoreCase: suffix: string -> field: Field<'T,string> -> Filter<'T>` | Matches a string suffix ignoring case. |
| `val eq: value: 'Value -> field: Field<'T,'Value> -> Filter<'T>` | Matches a field equal to a constant. |
| `val eqIgnoreCase: value: string -> field: Field<'T,string> -> Filter<'T>` | Matches a string equal to a constant ignoring case. |
| `val ge: value: int -> field: Field<'T,int> -> Filter<'T>` | Matches a count at least a constant. |
| `val gt: value: int -> field: Field<'T,int> -> Filter<'T>` | Matches a count above a constant. |
| `val isNull: field: Field<'T,string> -> Filter<'T>` | Matches a captured null string without treating an uncaptured field as absent. |
| `val le: value: int -> field: Field<'T,int> -> Filter<'T>` | Matches a count at most a constant. |
| `val lt: value: int -> field: Field<'T,int> -> Filter<'T>` | Matches a count below a constant. |
| `val matches: pattern: string -> field: Field<'T,string> -> Filter<'T>` | Matches a string against a culture-invariant .NET regular expression. |
| `val matchesIgnoreCase: pattern: string -> field: Field<'T,string> -> Filter<'T>` | Matches a string against a culture-invariant .NET regular expression ignoring case. |
| `val ne: value: 'Value -> field: Field<'T,'Value> -> Filter<'T>` | Matches a field not equal to a constant. |
| `val negate: filter: Filter<'T> -> Filter<'T>` | Negates a portable predicate. |
| `val none: relation: Relation<'Parent,'Child> -> predicate: Filter<'Child> -> Filter<'Parent>` | Requires no matching child, returning true for an empty captured relation. |
| `val notOneOf: values: 'Value list -> field: Field<'T,'Value> -> Filter<'T>` | Matches no constant in a list; an empty list matches everything. |
| `val oneOf: values: 'Value list -> field: Field<'T,'Value> -> Filter<'T>` | Matches any constant in a list; an empty list matches nothing. |
| `val startsWith: prefix: string -> field: Field<'T,string> -> Filter<'T>` | Matches a string prefix. |
| `val startsWithIgnoreCase: prefix: string -> field: Field<'T,string> -> Filter<'T>` | Matches a string prefix ignoring case. |
| `val toDocument: filter: Filter<'T> -> LibTmux.Query.QueryDocument` | Returns the core document validated when the filter was constructed. |
| `val toPredicate: filter: Filter<'T> -> ('T -> bool)` | Returns a predicate compiled once for native lazy filtering. |

## Mirror

| Signature | Summary |
|---|---|
| `module Mirror` | Follows a server's sessions, windows, panes and clients as tmux announces changes. |
| `val current: mirror: ServerMirror -> ServerMirrorView` | Returns the latest published view. |
| `val start: cancellationToken: CancellationToken -> anchor: LibTmux.Session -> Task<ServerMirror>` | Mirrors the server an anchor session belongs to, capturing on each announcement. |
| `val startRefreshing: cancellationToken: CancellationToken -> every: TimeSpan -> anchor: LibTmux.Session -> Task<ServerMirror>` | Mirrors a server, also capturing whenever it has been quiet for an interval. |
| `val tryWaitUntil: cancellationToken: CancellationToken -> timeout: TimeSpan -> condition: (ServerMirrorView -> bool) -> mirror: ServerMirror -> Task<ServerMirrorView option>` | Waits until a view satisfies a condition, or returns None when none did in time. |
| `val views: mirror: ServerMirror -> IAsyncEnumerable<ServerMirrorView>` | Streams the current view and each newer one, skipping views published while the reader was busy. |
| `val waitUntil: cancellationToken: CancellationToken -> timeout: TimeSpan -> condition: (ServerMirrorView -> bool) -> mirror: ServerMirror -> Task<ServerMirrorView>` | Waits until a view satisfies a condition, testing the current view first. |

## Options

| Signature | Summary |
|---|---|
| `module Options` | Reads and writes options through keys that know their value's type. |
| `val get: cancellationToken: CancellationToken -> key: TmuxOptionKey<'T> -> options: TmuxOptions -> Task<'T> when 'T: not null` | Reads the value an option has in a scope, set there or inherited, as its key's type. |
| `val set: cancellationToken: CancellationToken -> key: TmuxOptionKey<'T> -> value: 'T -> options: TmuxOptions -> Task when 'T: not null` | Sets an option in a scope from a value of its key's type. |

## Pane

| Signature | Summary |
|---|---|
| `module Pane` | Reads captured pane fields and starts explicit pane operations. |
| `val capture: cancellationToken: CancellationToken -> request: CapturePaneRequest -> pane: LibTmux.Pane -> Task<IReadOnlyList<string>>` | Captures pane contents using the supplied core request. |
| `val currentCommand: pane: LibTmux.Pane -> string option` | Reads the captured command name, preserving an empty string. |
| `val currentPath: pane: LibTmux.Pane -> string option` | Reads the captured working directory, preserving an empty string. |
| `val findOnScreen: cancellationToken: CancellationToken -> search: ScreenSearch -> pane: LibTmux.Pane -> Task<int option>` | Returns the first visible row showing the text, counted from 1, or None. |
| `val run: cancellationToken: CancellationToken -> timeout: TimeSpan -> command: string -> pane: LibTmux.Pane -> Task<PaneRunResult>` | Runs a shell command in the pane and waits for its exit status and output. |
| `val sendAndWait: cancellationToken: CancellationToken -> timeout: TimeSpan -> line: string -> text: string -> pane: LibTmux.Pane -> Task<PaneWaitResult>` | Types a line, presses Enter, and waits for a later line to contain the text. |
| `val sendAndWaitFor: cancellationToken: CancellationToken -> keys: SendKeysRequest -> request: PaneWaitRequest -> pane: LibTmux.Pane -> Task<PaneWaitResult>` | Sends keys as the request describes, then waits as the wait request describes. |
| `val sendKeys: cancellationToken: CancellationToken -> request: SendKeysRequest -> pane: LibTmux.Pane -> Task` | Sends text or key names according to the request's literal and Enter settings. |
| `val split: cancellationToken: CancellationToken -> request: SplitPaneRequest -> pane: LibTmux.Pane -> Task<LibTmux.Pane>` | Splits the pane and returns the new pane handle. |
| `val waitFor: cancellationToken: CancellationToken -> request: PaneWaitRequest -> pane: LibTmux.Pane -> Task<PaneWaitResult>` | Waits as the request describes: patterns, stop patterns, or any output. |
| `val waitForText: cancellationToken: CancellationToken -> timeout: TimeSpan -> text: string -> pane: LibTmux.Pane -> Task<PaneWaitResult>` | Waits for a line the pane prints to contain the text. |
| `val waitUntil: cancellationToken: CancellationToken -> timeout: TimeSpan -> condition: (IReadOnlyList<string> -> bool) -> pane: LibTmux.Pane -> Task<PaneWaitResult>` | Waits until a condition holds over the rows the pane shows, top to bottom. |

## PaneFields

| Signature | Summary |
|---|---|
| `module PaneFields` | Provides supported pane fields for portable filters. |
| `val active: Field<LibTmux.Pane,bool>` | Identifies whether the pane is its window's active pane. |
| `val atBottom: Field<LibTmux.Pane,bool>` | Identifies whether the pane touches the bottom of its window. |
| `val atLeft: Field<LibTmux.Pane,bool>` | Identifies whether the pane touches the left of its window. |
| `val atRight: Field<LibTmux.Pane,bool>` | Identifies whether the pane touches the right of its window. |
| `val atTop: Field<LibTmux.Pane,bool>` | Identifies whether the pane touches the top of its window. |
| `val currentCommand: Field<LibTmux.Pane,string>` | Identifies the captured command, including a captured unavailable value. |
| `val currentPath: Field<LibTmux.Pane,string>` | Identifies the pane's working directory, as the text tmux reported. |
| `val dead: Field<LibTmux.Pane,bool>` | Identifies whether the pane's program has exited while the pane remains. |
| `val height: Field<LibTmux.Pane,int>` | Identifies the pane's height in cells. |
| `val id: Field<LibTmux.Pane,PaneId>` | Identifies the typed pane ID. |
| `val inMode: Field<LibTmux.Pane,bool>` | Identifies whether the pane is in a mode, such as copy mode. |
| `val index: Field<LibTmux.Pane,int>` | Identifies the pane's position in its window. |
| `val left: Field<LibTmux.Pane,int>` | Identifies the column of the pane's left edge in its window. |
| `val processId: Field<LibTmux.Pane,int>` | Identifies the process ID of the program the pane started. |
| `val synchronized: Field<LibTmux.Pane,bool>` | Identifies whether keys typed into the pane go to every synchronized pane in its window. |
| `val title: Field<LibTmux.Pane,string>` | Identifies the pane's title, which a program running in it can set. |
| `val top: Field<LibTmux.Pane,int>` | Identifies the row of the pane's top edge in its window. |
| `val width: Field<LibTmux.Pane,int>` | Identifies the pane's width in cells. |

## PaneRun

| Signature | Summary |
|---|---|
| `module PaneRun` | Recognises how a command run with Pane.run ended. |
| `val (|Exited|_|) : result: PaneRunResult -> int option` | Matches a command that exited, with its exit status. |
| `val (|NotStarted|_|) : result: PaneRunResult -> unit option` | Matches a command the pane's shell never ran. |
| `val (|TimedOut|_|) : result: PaneRunResult -> unit option` | Matches a command still running when the time allowed ran out. |

## PaneWait

| Signature | Summary |
|---|---|
| `module PaneWait` | Recognises how a wait on a pane's output ended. |
| `val (|Found|Printed|Stopped|TimedOut|Ended|) : result: PaneWaitResult -> Choice<unit,unit,string,unit,unit>` | Tells how a wait ended, one case per kind of ending, so a match that leaves one out draws a warning. |

## Query

| Signature | Summary |
|---|---|
| ``Query`1`` | Describes a tmux listing: a scope, filters and text panes must show. |
| `module Query` | Narrows and runs tmux queries, and filters captured objects locally. |
| `val atMostOne: cancellationToken: CancellationToken -> query: Query<'T> -> Task<'T option>` | Reads the sole match, or None when nothing matches; several matches raise. |
| `val exactlyOne: cancellationToken: CancellationToken -> query: Query<'T> -> Task<Result<'T,CardinalityError>>` | Reads the sole match, or why there is not exactly one. |
| `val list: cancellationToken: CancellationToken -> query: Query<'T> -> Task<IReadOnlyList<'T>>` | Reads the matching objects in tmux's listing order. |
| `val matching: filter: Filter<'T> -> source: 'T seq -> IReadOnlyList<'T>` | Filters captured objects locally, preserving input order and multiplicity. |
| `val showing: search: ScreenSearch -> query: Query<LibTmux.Pane> -> Query<LibTmux.Pane>` | Keeps panes whose visible rows show the searched text. |
| `val tryExactlyOne: cancellationToken: CancellationToken -> query: Query<'T> -> Task<'T option>` | Reads the sole match, or None when there are none or several. |
| `val where: filter: Filter<'T> -> query: Query<'T> -> Query<'T>` | Adds a portable filter every result satisfies. |
| `val whereUnsafe: filter: UnsafeTmuxFilter -> query: Query<'T> -> Query<'T>` | Adds a raw tmux filter, which tmux evaluates and nothing rechecks. |

## Relation

| Signature | Summary |
|---|---|
| ``Relation`2`` | Identifies a supported captured relation between two entity types. |

## Retry

| Signature | Summary |
|---|---|
| `module Retry` | Runs an operation again only when tmux never saw it. |
| `val ifNotSent: cancellationToken: CancellationToken -> retries: int -> operation: (CancellationToken -> Task<'T>) -> Task<'T>` | Runs an operation, and again up to retries times while nothing it sent reached tmux. |
| `val ifNotSentAfter: cancellationToken: CancellationToken -> delays: TimeSpan list -> operation: (CancellationToken -> Task<'T>) -> Task<'T>` | Runs an operation, and after each delay in turn runs it again while nothing it sent reached tmux. |

## ScreenSearch

| Signature | Summary |
|---|---|
| `ScreenSearch` | Describes text tmux searches for on a pane's visible rows. |
| `PosixRegex of pattern: string` | Matches a POSIX extended regular expression, which tmux evaluates. |
| `PosixRegexIgnoringCase of pattern: string` | Matches a POSIX extended regular expression ignoring case. |
| `Text of text: string` | Matches literal text. |
| `TextIgnoringCase of text: string` | Matches literal text ignoring case. |

## Selection

| Signature | Summary |
|---|---|
| `module Selection` | Selects values from ordinary F# sequences. |
| `val exactlyOne: source: 'T seq -> Result<'T,CardinalityError>` | Returns the sole match, examining at most two elements. |

## Server

| Signature | Summary |
|---|---|
| `module Server` | Starts server reads and queries with the caller's cancellation token. |
| `val capture: cancellationToken: CancellationToken -> depth: SnapshotDepth -> server: LibTmux.Server -> Task<LibTmux.Server>` | Returns a new server handle captured to the requested depth. |
| `val clients: server: LibTmux.Server -> Query<Client>` | Queries attached clients. |
| `val connect: cancellationToken: CancellationToken -> options: ServerConnectionOptions -> Task<LibTmux.Server>` | Attaches to a server already listening on the socket the options name. |
| `val createOwned: cancellationToken: CancellationToken -> options: ServerConnectionOptions -> Task<OwnedServerScope>` | Starts a server on the socket the options name and owns it; disposing the scope stops it. |
| `val newSession: cancellationToken: CancellationToken -> spec: SessionSpec -> server: LibTmux.Server -> Task<LibTmux.Session>` | Creates a session as described: its windows, and each window's splits. |
| `val panes: server: LibTmux.Server -> Query<LibTmux.Pane>` | Queries every pane. |
| `val sessions: server: LibTmux.Server -> Query<LibTmux.Session>` | Queries every session. |
| `val tryFindClient: cancellationToken: CancellationToken -> name: string -> server: LibTmux.Server -> Task<Client option>` | Returns the client with an exact name or None after a successful listing finds no match. |
| `val tryFindPane: cancellationToken: CancellationToken -> id: PaneId -> server: LibTmux.Server -> Task<LibTmux.Pane option>` | Returns a pane or None after a successful lookup establishes absence. |
| `val tryFindSession: cancellationToken: CancellationToken -> id: SessionId -> server: LibTmux.Server -> Task<LibTmux.Session option>` | Returns a session or None after a successful lookup establishes absence. |
| `val tryFindWindow: cancellationToken: CancellationToken -> id: WindowId -> server: LibTmux.Server -> Task<LibTmux.Window option>` | Returns a window or None after a successful lookup establishes absence. |
| `val windows: server: LibTmux.Server -> Query<LibTmux.Window>` | Queries window placements across all sessions. |
| `val within: timeout: TimeSpan -> server: LibTmux.Server -> LibTmux.Server` | Returns the server with every command bounded by a timeout, for it and every handle taken from it. |

## Session

| Signature | Summary |
|---|---|
| `module Session` | Starts queries confined to one session. |
| `val panes: session: LibTmux.Session -> Query<LibTmux.Pane>` | Queries the panes of every window in a session. |
| `val windows: session: LibTmux.Session -> Query<LibTmux.Window>` | Queries the window placements in a session. |

## SessionFields

| Signature | Summary |
|---|---|
| `module SessionFields` | Provides supported session fields and relations for portable filters. |
| `val attached: Field<LibTmux.Session,bool>` | Identifies whether the captured session has attached clients. |
| `val id: Field<LibTmux.Session,SessionId>` | Identifies the typed session ID. |
| `val name: Field<LibTmux.Session,string>` | Identifies the session name. |
| `val windowCount: Field<LibTmux.Session,int>` | Identifies the number of windows linked into the session. |
| `val windows: Relation<LibTmux.Session,LibTmux.Window>` | Identifies captured window placements within the session. |

## SessionSpec

| Signature | Summary |
|---|---|
| `Directory: string option` | The working directory of the session and its first window. |
| `Environment: Map<string,string>` | Variables added to the session's environment. |
| `SessionSpec` | Describes a session and its windows, for Server.newSession. |
| `Name: string` | The session's name. |
| `Windows: WindowSpec list` | The windows, in order; the first is the one tmux creates with the session. |
| `module SessionSpec` | Starts session descriptions. |
| `override ToString: unit -> string` | Names the session, without formatting through printf. |
| `val named: name: string -> SessionSpec` | A named session with tmux's single default window. |

## Snapshot

| Signature | Summary |
|---|---|
| `module Snapshot` | Reads captured values without contacting tmux. |
| `val relation: relation: CapturedRelation<'T> -> CaptureState<IReadOnlyList<'T>>` | Distinguishes captured children from an unread relation. |
| `val value: value: CapturedValue<'T> -> CaptureState<'T> when 'T: not struct and 'T: not null` | Distinguishes a captured child from an unread value. |

## SplitSpec

| Signature | Summary |
|---|---|
| `Command: string option` | The command the pane runs instead of the default shell. |
| `Direction: PaneDirection option` | Where the new pane goes, beside the pane before it; tmux puts it below when None. |
| `Directory: string option` | The pane's working directory. |
| `Environment: Map<string,string>` | Variables added to the pane's environment. |
| `SplitSpec` | Describes a pane split off the pane created before it. |
| `Size: string option` | The pane's size, in cells, or with a percent sign as a share of the space split. |
| `module SplitSpec` | Starts split descriptions. |
| `override ToString: unit -> string` | Names the split by its command, without formatting through printf. |
| `val empty: SplitSpec` | A split below the pane before it, running the default shell. |

## StreamStep

| Signature | Summary |
|---|---|
| `Continue of state: 'State` | Retains state and reads the next event. |
| ``StreamStep`1`` | Represents a decision to continue or stop an event fold. |
| `Stop of state: 'State` | Retains state and stops before reading another event. |

## TmuxFailure

| Signature | Summary |
|---|---|
| `module TmuxFailure` | Recognises tmux failures by whether running the operation again could repeat what it did. |
| `val (|MayHaveRun|_|) : error: exn -> exn option` | Matches a failure, or a cancellation, after which tmux may already have acted. |
| `val (|NotSent|_|) : error: exn -> LibTmuxException option` | Matches a failure whose command never reached tmux; running it again repeats nothing. |
| `val (|Ran|_|) : error: exn -> LibTmuxException option` | Matches a failure after tmux ran the command: tmux reported an error, or its answer could not be used. |

## Window

| Signature | Summary |
|---|---|
| `module Window` | Identifies window placements and starts queries confined to one window. |
| `val panes: window: LibTmux.Window -> Query<LibTmux.Pane>` | Queries the panes in a window. |
| `val placementKey: window: LibTmux.Window -> WindowPlacementKey` | Returns a comparable key including the captured session and window index. |

## WindowFields

| Signature | Summary |
|---|---|
| `module WindowFields` | Provides supported window fields and relations for portable filters. |
| `val active: Field<LibTmux.Window,bool>` | Identifies whether the window is the current window of the session it was read through. |
| `val height: Field<LibTmux.Window,int>` | Identifies the window's height in cells. |
| `val id: Field<LibTmux.Window,WindowId>` | Identifies the typed physical window ID. |
| `val index: Field<LibTmux.Window,int>` | Identifies where the window sits in its session. |
| `val name: Field<LibTmux.Window,string>` | Identifies the window name. |
| `val paneCount: Field<LibTmux.Window,int>` | Identifies the number of panes in the window. |
| `val panes: Relation<LibTmux.Window,LibTmux.Pane>` | Identifies panes captured through this window placement. |
| `val width: Field<LibTmux.Window,int>` | Identifies the window's width in cells. |
| `val zoomed: Field<LibTmux.Window,bool>` | Identifies whether one of the window's panes is zoomed to fill it. |

## WindowPlacementKey

| Signature | Summary |
|---|---|
| `WindowPlacementKey` | Identifies one indexed placement of a window within a server generation. |

## WindowSpec

| Signature | Summary |
|---|---|
| `Command: string option` | The command the first pane runs instead of the default shell. |
| `Directory: string option` | The first pane's working directory. |
| `Environment: Map<string,string>` | Variables added to the first pane's environment; a session's first window takes them from the session instead. |
| `WindowSpec` | Describes a window: its first pane, then each pane split off the one before. |
| `Name: string option` | The window's name; tmux names it after its command when None. |
| `Splits: SplitSpec list` | The panes split off in order, each beside the pane before it. |
| `module WindowSpec` | Starts window descriptions. |
| `override ToString: unit -> string` | Names the window, without formatting through printf. |
| `val empty: WindowSpec` | A window tmux names after its command, running the default shell. |
| `val named: name: string -> WindowSpec` | A named window running the default shell. |
