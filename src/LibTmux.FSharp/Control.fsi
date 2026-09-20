namespace LibTmux.FSharp

open System.Threading
open System.Threading.Tasks
open LibTmux

/// <summary>Represents a decision to continue or stop an event fold.</summary>
[<RequireQualifiedAccess>]
type StreamStep<'State> =
    /// <summary>Retains state and reads the next event.</summary>
    | Continue of state: 'State
    /// <summary>Retains state and stops before reading another event.</summary>
    | Stop of state: 'State

/// <summary>Provides scoped access to core control-mode event streams.</summary>
[<RequireQualifiedAccess>]
module Control =
    /// <summary>Opens a core control client with the caller's cancellation token.</summary>
    /// <remarks>The caller owns and asynchronously disposes the returned client.</remarks>
    val enter: cancellationToken: CancellationToken -> server: LibTmux.Server -> Task<IControlModeSession>

    /// <summary>Runs work with an owned control client and disposes it after the returned task completes.</summary>
    /// <remarks>The work function receives the client and must forward its own cancellation token.</remarks>
    val useSession: work: (IControlModeSession -> Task<'State>) -> session: IControlModeSession -> Task<'State>

    /// <summary>Opens a control client, runs work, and disposes the client after the returned task completes.</summary>
    /// <remarks>The cancellation token starts the client; the work function forwards its own token.</remarks>
    val withSession:
        cancellationToken: CancellationToken ->
        work: (IControlModeSession -> Task<'State>) ->
        server: LibTmux.Server ->
            Task<'State>

    /// <summary>Awaits one handler at a time for each event from a borrowed control client.</summary>
    /// <remarks>The helper disposes its enumerator but leaves the control client open.</remarks>
    val iterEvents:
        cancellationToken: CancellationToken ->
        handler: (TmuxEvent -> Task) ->
        session: IControlModeSession ->
            Task<unit>

    /// <summary>Folds events until the source ends or the folder returns Stop.</summary>
    /// <remarks>The helper disposes its enumerator but leaves the control client open.</remarks>
    val foldEventsWhile:
        cancellationToken: CancellationToken ->
        folder: ('State -> TmuxEvent -> Task<StreamStep<'State>>) ->
        initial: 'State ->
        session: IControlModeSession ->
            Task<'State>
