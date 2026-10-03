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

/// <summary>Recognises tmux failures by whether running the operation again could repeat what it did.</summary>
/// <remarks>
/// Every <c>LibTmuxException</c> says whether its command reached tmux, so match
/// on that rather than on the exception type. <c>NotSent</c> is the only failure
/// after which running the same operation again is always safe.
/// </remarks>
[<RequireQualifiedAccess>]
module TmuxFailure =
    /// <summary>Matches a failure whose command never reached tmux; running it again repeats nothing.</summary>
    val (|NotSent|_|): error: exn -> LibTmuxException option

    /// <summary>Matches a failure tmux answered: it ran the command, which refused or reported an error.</summary>
    val (|Refused|_|): error: exn -> LibTmuxException option

    /// <summary>Matches a failure, or a cancellation, after which tmux may already have acted.</summary>
    val (|MayHaveRun|_|): error: exn -> exn option

/// <summary>Runs an operation again only when tmux never saw it.</summary>
[<RequireQualifiedAccess>]
module Retry =
    /// <summary>Runs an operation, and again up to <c>retries</c> times while it fails before reaching tmux.</summary>
    /// <remarks>
    /// Any other failure, and cancellation, propagates at once. A read that is
    /// safe to repeat whatever happened belongs in the caller's own retry policy.
    /// </remarks>
    /// <exception cref="T:System.ArgumentOutOfRangeException">The retry count is negative.</exception>
    val ifNotSent:
        cancellationToken: CancellationToken -> retries: int -> operation: (CancellationToken -> Task<'T>) -> Task<'T>

module internal Placement =
    val key: window: LibTmux.Window -> WindowPlacementKey
