# .NET interoperation

The companion is built on
[LibTmux](https://github.com/libtmux/libtmux-dotnet/) and uses its entities,
IDs, requests, exceptions, snapshots, and query documents. Pass a
`Server`, `Session`, `Window`, or `Pane` between F# and C# without conversion.

`Task<'T>` remains the default asynchronous contract. Pass the cancellation
token to the façade function explicitly and preserve core exceptions. Use
`option` only for a completed lookup that found no entity; use
`Selection.exactlyOne` when zero and multiple local matches need different
outcomes.

A task starts when called; an `Async` workflow starts when run. If cancellation
details matter, await the core task directly. Passing it through
`Async.AwaitTask` and `Async.StartAsTask` can replace
`TmuxOperationCanceledException` with `TaskCanceledException`, losing
`CommandMayHaveExecuted` and `ClientProcessId`. Depending on continuation
timing, the outer task can be canceled or faulted. The companion does not
expose an `Async` adapter with an unproved cancellation contract.

`LibTmux.Query.Json` remains optional. Add it only when a portable filter must
cross a process or language boundary. It serializes the core `QueryDocument`;
the F# package does not define a second format.

## Failures and retries

Every `LibTmuxException` says whether its command reached tmux. The
`TmuxFailure` patterns match on that. `NotSent` means tmux never saw the
command, so running it again repeats nothing. `Ran` means tmux ran it and then
reported an error or gave an answer that could not be used. `MayHaveRun`
covers a failure or cancellation after which tmux may already have acted.
`Retry.ifNotSentAfter` runs an operation again only for `NotSent`, and only
when no command the attempt sent before that failure reached tmux, waiting each
delay in turn first:

<!-- fsharp-snippet: SafeRetry run -->
```fsharp run
open System
open System.Threading
open LibTmux
open LibTmux.FSharp

let readSessionNamesAsync (cancellationToken: CancellationToken) (server: Server) =
    task {
        try
            // Runs again only when tmux never received the command, after
            // each delay in turn, so a server still starting can answer.
            let! sessions =
                Retry.ifNotSentAfter
                    cancellationToken
                    [ TimeSpan.FromMilliseconds 100.; TimeSpan.FromMilliseconds 400. ]
                    (fun token -> server.GetSessionsAsync(token))

            return Ok [ for session in sessions -> session.Name ]
        with
        | TmuxFailure.Ran failure -> return Error $"tmux ran the command, then: {failure.Message}"
        | TmuxFailure.MayHaveRun failure -> return Error $"tmux may have acted: {failure.Message}"
    }
```
<!-- endfsharp-snippet -->

`NotSent` speaks for one command. An operation that creates a window and then
fails to send a second command has already created the window, so
`Retry.ifNotSent` counts every command an attempt sends, through whatever it
awaits, and repeats the attempt only if none reached tmux. A read that is safe
to repeat whatever happened can use any retry policy; a command that changes
tmux should be retried only this way.

`Retry.ifNotSent ct retries operation` retries under the same rule without
waiting, for a failure that waiting does not change.

## Bound how long tmux may take

Bound one call with its token: pass `(new CancellationTokenSource(timeout)).Token`.
A call cancelled that way raises `TmuxOperationCanceledException`, whose
`CommandMayHaveExecuted` says whether tmux may already have run it, and which
`TmuxFailure.MayHaveRun` matches.

Bound every command a handle sends with `Server.within timeout server`. It
returns a handle to the same server, over the same connection, whose commands,
and those of every session, window and pane taken from it, give tmux that long.
A command that outlasts it raises `TmuxTransportException` with an `Unknown`
dispatch state. `ServerConnectionOptions.CommandTimeout` sets the same bound
for a whole connection.

## Typed options

`Options.get` and `Options.set` take a `TmuxOptionKey` that knows its value's
type. The named keys, such as `TmuxOptionKey.HistoryLimit` and
`TmuxOptionKey.Mouse`, have the same type on every supported tmux; declare
others with `TmuxOptionKey.Text`, `Number` or `Flag`. A read returns the value
tmux applies, including one inherited from a parent scope, and raises
`TmuxOptionException` when tmux reports none or one the key cannot read.

<!-- fsharp-snippet: TypedOptions run -->
```fsharp run
open System.Threading
open LibTmux
open LibTmux.FSharp

let tuneAsync (cancellationToken: CancellationToken) (session: Session) (window: Window) =
    task {
        do!
            session.Options
            |> Options.set cancellationToken TmuxOptionKey.HistoryLimit 50_000

        do!
            session.Options
            |> Options.set cancellationToken (TmuxOptionKey.Text "@stage") "build"

        let! history =
            session.Options |> Options.get cancellationToken TmuxOptionKey.HistoryLimit

        let! stage =
            session.Options |> Options.get cancellationToken (TmuxOptionKey.Text "@stage")

        // Never set on the window, so this is tmux's inherited default.
        let! renames =
            window.Options |> Options.get cancellationToken TmuxOptionKey.AutomaticRename

        return history, stage, renames
    }
```
<!-- endfsharp-snippet -->

## Configuration, hooks and formats

Core configuration and format APIs remain available on the same handles. This
function takes an existing server and session, makes scoped changes, and checks
what tmux reports. The [example runner](../../examples/LibTmux.FSharp.Examples/Program.fs)
passes handles from an owned tmux hierarchy.

<!-- fsharp-snippet: CoreInterop run -->
```fsharp run
open System
open System.Collections.Generic
open System.Threading
open LibTmux

let inspectCoreSettingsAsync (cancellationToken: CancellationToken) (server: Server) (session: Session) =
    task {
        // A global value, overridden locally, shows through again once the
        // local value is unset.
        let! _ =
            session.Options.SetAsync(SetOptionRequest("status-keys", "vi", Global = true), cancellationToken)

        let! _ =
            session.Options.SetAsync(SetOptionRequest("status-keys", "emacs"), cancellationToken)

        do! session.Options.UnsetAsync(UnsetOptionRequest("status-keys"), cancellationToken)

        let! statusKeys =
            session.Options.GetAsync(GetOptionRequest("status-keys", IncludeInherited = true), cancellationToken)

        // An array option and a hook keep each entry's index.
        let! _ =
            server.Options.SetAsync(
                SetOptionRequest("command-alias[40]", "fsharp-window=new-window"),
                cancellationToken
            )

        let! aliases =
            server.Options.GetAsync(GetOptionRequest("command-alias"), cancellationToken)

        let entries = Dictionary<int, string>()
        entries[3] <- "display-message fsharp-hook"

        let! hook =
            server.Hooks.SetAsync(SetHooksRequest("alert-bell", entries, ClearExisting = true), cancellationToken)

        let! _ =
            session.Environment.SetAsync("LIBTMUX_FSHARP_EXAMPLE", "ready", cancellationToken = cancellationToken)

        let! variable =
            session.Environment.GetAsync("LIBTMUX_FSHARP_EXAMPLE", cancellationToken)

        let! rendered =
            server.DisplayMessageAsync(
                DisplayMessageRequest(Format = "fsharp-#{pid}", ReturnText = true),
                cancellationToken
            )

        return
            {|
                StatusKeys = [ for option in statusKeys -> option.Value.Raw, option.Inherited ]
                Alias =
                    aliases
                    |> Seq.tryFind (fun alias -> alias.Index = Nullable 40)
                    |> Option.map (fun alias -> alias.Value.Raw)
                HookIndexes = [ for value in hook.Values -> value.Index ]
                Variable = variable |> Option.ofObj |> Option.map (fun entry -> entry.Value)
                Rendered = rendered |> Option.ofObj |> Option.map List.ofSeq
            |}
    }
```
<!-- endfsharp-snippet -->

## Core operations

Call the core APIs directly for window placement, pane sizing, layouts,
buffers, and copy mode. This example uses handles from the [owned tmux example
runner](../../examples/LibTmux.FSharp.Examples/Program.fs). `MoveAsync` returns
the new window placement; the original link remains after the moved one is
unlinked. Layout and resize calls return refreshed handles. The buffer belongs
to the server, while copy mode belongs to the pane.

<!-- fsharp-snippet: CoreOperations run -->
```fsharp run
open System.Threading
open LibTmux

let exerciseCoreOperationsAsync
    (cancellationToken: CancellationToken)
    (server: Server)
    (session: Session)
    (window: Window)
    (pane: Pane)
    =
    task {
        // Link the window at index 5 as well, move that placement to 3,
        // then unlink it; the original placement stays.
        do!
            window.LinkAsync(
                LinkWindowRequest(session.Id.ToString(), TargetIndex = "5", Detach = true),
                cancellationToken
            )

        let! placements = session.GetWindowsAsync(cancellationToken)

        let linked =
            placements |> Seq.find (fun item -> item.Id = window.Id && item.Index = 5)

        let! moved =
            linked.MoveAsync(MoveWindowRequest(Destination = "3", NoSelect = true), cancellationToken)

        do! moved.UnlinkAsync(cancellationToken = cancellationToken)
        let! remaining = session.GetWindowsAsync(cancellationToken)

        // Layout and resize return the handle they changed.
        let! split = window.SplitPaneAsync(cancellationToken = cancellationToken)

        let! laidOut =
            window.SelectLayoutAsync(SelectLayoutRequest(Layout = "even-horizontal"), cancellationToken)

        let! resized = split.ResizeAsync(ResizePaneRequest(Height = "10"), cancellationToken)

        do! server.Buffers.SetAsync("fsharp-ready", "fsharp-guide", cancellationToken = cancellationToken)
        let! contents = server.Buffers.GetAsync("fsharp-guide", cancellationToken)
        do! server.Buffers.DeleteAsync("fsharp-guide", cancellationToken)

        do! pane.EnterCopyModeAsync(cancellationToken = cancellationToken)
        let! copying = pane.RefreshAsync(cancellationToken)
        do! pane.EnterCopyModeAsync(CopyModeRequest(Cancel = true), cancellationToken)
        let! normal = pane.RefreshAsync(cancellationToken)

        return
            {|
                Moved = moved
                Remaining = remaining
                Split = split
                LaidOut = laidOut
                Resized = resized
                Buffer = contents
                InModeWhileCopying = copying.RawFormatFields["pane_in_mode"]
                InModeAfter = normal.RawFormatFields["pane_in_mode"]
            |}
    }
```
<!-- endfsharp-snippet -->

## Window input

Core window creation returns a handle that the F# pane helpers can use
directly. A literal `"Enter"` types five characters; a key-name `"Enter"`
presses the key. `Enter = true` sends a separate key command after literal
text. The example checks those command shapes, sends each form to a temporary
window, and kills that window.

<!-- fsharp-snippet: WindowInput run -->
```fsharp run
open System.Threading
open LibTmux
open LibTmux.FSharp

let exerciseWindowInputAsync (cancellationToken: CancellationToken) (session: Session) =
    task {
        let! window =
            session.CreateWindowAsync(
                NewWindowRequest(Name = "fsharp-input", Command = "/bin/cat", Attach = false),
                cancellationToken
            )

        let! panes = window.GetPanesAsync(cancellationToken)
        let pane = panes[0]

        // Literal text is typed as written; a key name is pressed. Text
        // followed by Enter is two commands: the text, then the key.
        let literal = SendKeysRequest(Text = "Enter", Literal = true, Enter = false)
        let keyName = SendKeysRequest(Text = "Enter", Literal = false, Enter = false)

        let textThenEnter =
            SendKeysRequest(Text = "fsharp-input", Literal = true, Enter = true)

        do! pane |> Pane.sendKeys cancellationToken literal
        do! pane |> Pane.sendKeys cancellationToken keyName
        do! pane |> Pane.sendKeys cancellationToken textThenEnter
        do! window.KillAsync(cancellationToken = cancellationToken)

        let arguments (request: SendKeysRequest) =
            [
                for command in request.ToCommands(pane) -> List.ofSeq (command.ToArguments())
            ]

        return
            {|
                Window = window
                Panes = panes.Count
                Literal = arguments literal
                KeyName = arguments keyName
                TextThenEnter = arguments textThenEnter
            |}
    }
```
<!-- endfsharp-snippet -->

<!-- fsharp-snippet: PortableFilterJson run -->
```fsharp run
open LibTmux
open LibTmux.FSharp
open LibTmux.Query.Json

let encodeEditorPaneFilter () =
    Filter.oneOf [ "nvim"; "vim" ] PaneFields.currentCommand
    |> Filter.toDocument
    |> QueryJson.Serialize

let decodeFilter json = QueryJson.Deserialize json
```
<!-- endfsharp-snippet -->

Validate and apply the decoded document with the core query APIs. A JSON round
trip does not turn a portable filter into a tmux format expression.
