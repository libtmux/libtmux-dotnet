namespace LibTmux.FSharp

open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open LibTmux

/// <summary>Describes a selection that does not contain exactly one match.</summary>
type CardinalityError =
    /// <summary>No element matched.</summary>
    | NoMatches
    /// <summary>At least two elements matched.</summary>
    | MultipleMatches

/// <summary>Distinguishes captured state from a relation the snapshot did not read.</summary>
type CaptureState<'T> =
    /// <summary>Contains the captured value, including an observed empty collection.</summary>
    | Captured of value: 'T
    /// <summary>Names the unread relation and the depth the snapshot reached.</summary>
    | Uncaptured of relation: string * depth: SnapshotDepth

/// <summary>Identifies one indexed placement of a window within a server generation.</summary>
[<StructuralEquality; StructuralComparison>]
type WindowPlacementKey =
    private
        {
            ServerProcessId: int
            ServerStartTime: int64
            SessionId: int
            WindowId: int
            WindowIndex: int
        }

/// <summary>Reads captured values without contacting tmux.</summary>
[<RequireQualifiedAccess>]
module Snapshot =
    /// <summary>Distinguishes captured children from an unread relation.</summary>
    val relation: relation: CapturedRelation<'T> -> CaptureState<IReadOnlyList<'T>>

    /// <summary>Distinguishes a captured child from an unread value.</summary>
    val value: value: CapturedValue<'T> -> CaptureState<'T> when 'T: not struct

/// <summary>Selects values from ordinary F# sequences.</summary>
[<RequireQualifiedAccess>]
module Selection =
    /// <summary>Returns the sole match, examining at most two elements.</summary>
    /// <remarks>Disposes the enumerator on success, multiple matches or failure.</remarks>
    val exactlyOne: source: seq<'T> -> Result<'T, CardinalityError>

/// <summary>Identifies window placements without refreshing their captured state.</summary>
[<RequireQualifiedAccess>]
module Window =
    /// <summary>Returns a comparable key including the captured session and window index.</summary>
    /// <exception cref="T:LibTmux.IncompleteSnapshotException">The placement was not captured.</exception>
    val placementKey: window: LibTmux.Window -> WindowPlacementKey

/// <summary>Starts explicit server reads with the caller's cancellation token.</summary>
[<RequireQualifiedAccess>]
module Server =
    /// <summary>Lists panes and captures their scalar fields.</summary>
    val listPanes: cancellationToken: CancellationToken -> server: LibTmux.Server -> Task<IReadOnlyList<LibTmux.Pane>>

    /// <summary>Returns a new server handle captured to the requested depth.</summary>
    /// <remarks>Acquisition is not atomic; retained handles do not refresh themselves.</remarks>
    val capture:
        cancellationToken: CancellationToken -> depth: SnapshotDepth -> server: LibTmux.Server -> Task<LibTmux.Server>

    /// <summary>Returns a pane or None after a successful lookup establishes absence.</summary>
    /// <remarks>Connection, command and cancellation errors propagate unchanged.</remarks>
    val tryFindPane:
        cancellationToken: CancellationToken -> id: PaneId -> server: LibTmux.Server -> Task<LibTmux.Pane option>

/// <summary>Reads captured pane fields and starts explicit pane operations.</summary>
[<RequireQualifiedAccess>]
module Pane =
    /// <summary>Reads the captured working directory, preserving an empty string.</summary>
    /// <exception cref="T:LibTmux.IncompleteSnapshotException">The path field was not captured.</exception>
    val currentPath: pane: LibTmux.Pane -> string option

    /// <summary>Reads the captured command name, preserving an empty string.</summary>
    /// <exception cref="T:LibTmux.IncompleteSnapshotException">The command field was not captured.</exception>
    val currentCommand: pane: LibTmux.Pane -> string option

    /// <summary>Captures pane contents using the supplied core request.</summary>
    val capture:
        cancellationToken: CancellationToken ->
        request: CapturePaneRequest ->
        pane: LibTmux.Pane ->
            Task<IReadOnlyList<string>>

    /// <summary>Sends text or key names according to the request's literal and Enter settings.</summary>
    /// <remarks>Cancellation can occur after dispatch; it does not undo sent keys.</remarks>
    val sendKeys: cancellationToken: CancellationToken -> request: SendKeysRequest -> pane: LibTmux.Pane -> Task

    /// <summary>Splits the pane and returns the new pane handle.</summary>
    /// <remarks>Cancellation can leave the split applied; do not retry automatically.</remarks>
    val split:
        cancellationToken: CancellationToken -> request: SplitPaneRequest -> pane: LibTmux.Pane -> Task<LibTmux.Pane>
