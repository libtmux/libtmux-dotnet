# API reference

Generated from compiler XML summaries and gated by the approved public
contract, so documented internal helpers never render. Regenerate with
`uv run python eng/docs/render_api_reference.py`.

See [choosing a mode](../modes/matrix.md) for how the three execution
modes differ.

## Types

| Member | Summary |
|---|---|
| `LibTmux.AttachSessionRequest` | Describes one attach-session invocation. |
| `LibTmux.BindKeyRequest` | Describes one bind-key invocation. |
| `LibTmux.CapturePanePosition` | Names one end of a capture range. |
| `LibTmux.CapturePaneRequest` | Describes one capture-pane invocation. |
| ``LibTmux.CapturedRelation`1`` | Holds the children a snapshot captured for one relation. |
| `LibTmux.ChooseTreeRequest` | Describes one choose-tree invocation. |
| `LibTmux.ChooseTreeSort` | Names how a chooser orders its rows. |
| `LibTmux.Client` | Identifies a client and resolves what it is looking at. |
| `LibTmux.ClientAttachment` | What one client is looking at. |
| `LibTmux.CommandPromptRequest` | Describes one command-prompt invocation. |
| `LibTmux.ConfirmBeforeRequest` | Describes one confirm-before invocation. |
| `LibTmux.ControlModeCommandException` | Reports a command rejected by a live tmux control client. |
| `LibTmux.CopyModeRequest` | Describes one copy-mode invocation. |
| `LibTmux.DisplayMenuRequest` | Describes one display-menu invocation. |
| `LibTmux.DisplayMessageRequest` | Describes one display-message invocation. |
| `LibTmux.DisplayPopupRequest` | Describes one display-popup invocation. |
| `LibTmux.FindWindowRequest` | Describes one find-window invocation. |
| `LibTmux.GetOptionRequest` | Describes one show-options invocation for a single option. |
| `LibTmux.GetOptionsRequest` | Describes one show-options invocation for every option in a scope. |
| `LibTmux.HookRequest` | Describes one hook to read, run, or unset. |
| `LibTmux.IControlModeSession` | A live tmux control client. |
| `LibTmux.IfShellRequest` | Describes one if-shell invocation. |
| `LibTmux.IncompleteSnapshotException` | Thrown when a snapshot never captured the requested relation. |
| `LibTmux.LibTmuxException` | Provides the base exception for remote tmux failures. |
| `LibTmux.LibTmuxInfo` | Reports package identity and supported tmux range. |
| `LibTmux.LinkWindowRequest` | Describes one link-window invocation. |
| `LibTmux.ListBuffersRequest` | Describes one list-buffers invocation. |
| `LibTmux.ListHooksRequest` | Describes one show-hooks invocation. |
| `LibTmux.MovePaneRequest` | Describes one move-pane or join-pane invocation. |
| `LibTmux.MoveWindowRequest` | Describes one move-window invocation. |
| `LibTmux.NewPaneRequest` | Describes one new-pane invocation. |
| `LibTmux.NewSessionRequest` | Describes one new-session invocation. |
| `LibTmux.NewWindowRequest` | Describes one new-window invocation. |
| `LibTmux.OptionScope` | Defines tmux option scopes. |
| `LibTmux.OwnedServerScope` | Owns a server and stops it when disposed. |
| `LibTmux.OwnedSessionScope` | Owns a session and stops it when disposed. |
| `LibTmux.OwnedWindowScope` | Owns a window and stops it when disposed. |
| `LibTmux.Pane` | Represents an immutable pane handle and snapshot. |
| `LibTmux.PaneDirection` | Defines pane placement directions. |
| `LibTmux.PaneId` | Represents a generation-independent tmux pane identifier. |
| `LibTmux.PaneInputMode` | Names whether a pane accepts input. |
| `LibTmux.PaneSelectDirection` | Names which pane a selection moves to. |
| `LibTmux.PaneSwapDirection` | Names which neighbouring pane a swap uses. |
| `LibTmux.PasteBufferRequest` | Describes one paste-buffer invocation. |
| `LibTmux.PipePaneRequest` | Describes one pipe-pane invocation. |
| `LibTmux.PopupCloseMode` | Names when a popup closes on its own. |
| `LibTmux.PromptType` | What a command prompt is asking for. |
| `LibTmux.PsmuxCaptureOptions` | Chooses the psmux pane text that can be captured safely. |
| `LibTmux.PsmuxConnectionOptions` | Configures the bounded psmux query preview. |
| `LibTmux.PsmuxPane` | An immutable observation of one psmux pane. |
| `LibTmux.PsmuxServer` | Reads one isolated, single-session psmux namespace. |
| `LibTmux.PsmuxSession` | An immutable observation of the sole psmux session. |
| `LibTmux.PsmuxWindow` | An immutable observation of one psmux window. |
| `LibTmux.Query.AndNode` | The conjunction of ordered operands. |
| `LibTmux.Query.BooleanConstant` | A boolean literal. |
| `LibTmux.Query.ComparisonNode` | An ordering or equality comparison. |
| `LibTmux.Query.ConstantNode` | A literal operand. |
| `LibTmux.Query.FieldNode` | A tmux format field operand. |
| `LibTmux.Query.Int64Constant` | A 64-bit integer literal. |
| `LibTmux.Query.NotNode` | The negation of one predicate. |
| `LibTmux.Query.NullConstant` | The absence of a value. |
| `LibTmux.Query.OrNode` | The disjunction of ordered operands. |
| `LibTmux.Query.QuantifierNode` | A quantifier over a relation field. |
| `LibTmux.Query.QueryComparison` | Names an ordering or equality comparison. |
| `LibTmux.Query.QueryConstant` | One literal value in a query predicate. |
| `LibTmux.Query.QueryDocument` | One translated query predicate and its wire schema. |
| `LibTmux.Query.QueryEdgeParser` | Parses the one legacy lookup spelling this port still carries. |
| `LibTmux.Query.QueryExtensions` | Translates, compiles, and applies declarative query predicates. |
| `LibTmux.Query.QueryNode` | One node of a translated query predicate. |
| `LibTmux.Query.QueryQuantifier` | Names how a quantifier folds a relation. |
| `LibTmux.Query.QueryStringOperation` | Names a string comparison, always ordinal. |
| `LibTmux.Query.QueryTarget` | Names the tmux object a field or quantifier reads. |
| `LibTmux.Query.RegexNode` | A constant-pattern regular expression match. |
| `LibTmux.Query.StringConstant` | A string literal. |
| `LibTmux.Query.StringNode` | An ordinal string comparison. |
| `LibTmux.Query.TypedIdConstant` | A typed tmux identifier literal. |
| `LibTmux.ResizeDirection` | Defines pane resize directions. |
| `LibTmux.ResizePaneRequest` | Describes one resize-pane invocation. |
| `LibTmux.ResizeWindowRequest` | Describes one resize-window invocation. |
| `LibTmux.RespawnRequest` | Describes one respawn-window or respawn-pane invocation. |
| `LibTmux.RunShellRequest` | Describes one run-shell invocation. |
| `LibTmux.SelectLayoutMode` | Names a layout change that needs no layout string. |
| `LibTmux.SelectLayoutRequest` | Describes one select-layout invocation. |
| `LibTmux.SelectPaneRequest` | Describes one select-pane invocation. |
| `LibTmux.SendKeysRequest` | Describes one send-keys invocation. |
| `LibTmux.Server` | Represents an immutable server handle and snapshot. |
| `LibTmux.ServerAccessRequest` | Describes one server-access invocation. |
| `LibTmux.ServerConnectionOptions` | Configures a tmux server connection without mutating process-wide state. |
| `LibTmux.ServerGeneration` | Identifies one tmux daemon generation. |
| `LibTmux.Session` | Represents an immutable session handle and snapshot. |
| `LibTmux.SessionId` | Represents a generation-independent tmux session identifier. |
| `LibTmux.SessionWindowEdge` | Places one window at one index inside one session. |
| `LibTmux.SetHookRequest` | Describes one set-hook invocation. |
| `LibTmux.SetHooksRequest` | Describes setting several entries of one hook at once. |
| `LibTmux.SetOptionRequest` | Describes one set-option invocation. |
| `LibTmux.ShowMessagesMode` | What show-messages should list. |
| `LibTmux.SnapshotDepth` | Names how far down the tmux hierarchy a snapshot captured. |
| `LibTmux.SplitPaneRequest` | Describes one split-window invocation. |
| `LibTmux.StaleServerGenerationException` | Reports a stale server generation. |
| `LibTmux.SwapPaneRequest` | Describes one swap-pane invocation. |
| `LibTmux.Testing.TemporaryHierarchyScope` | A server, session, window, and pane a test owns together. |
| `LibTmux.Testing.TemporaryServerScope` | Creates a throwaway server for a test and stops it afterwards. |
| `LibTmux.Testing.TemporarySessionScope` | Owns a throwaway session and any private server created with it. |
| `LibTmux.Testing.TemporaryWindowScope` | Owns a throwaway window and any private session and server created with it. |
| `LibTmux.Testing.TestEnvironment` | The directory and variables a test's tmux runs with. |
| `LibTmux.Testing.TmuxNameGenerator` | Makes names no other test is using. |
| `LibTmux.Testing.TmuxTestContext` | A tmux server a test owns, and the environment it runs in. |
| `LibTmux.Testing.TmuxTestFactory` | Makes the tmux objects a test needs, each owning its own cleanup. |
| `LibTmux.Testing.TmuxTestOptions` | How a test's tmux is reached and how long it is waited on. |
| `LibTmux.Testing.TmuxWait` | Waits for tmux to reach a state instead of sleeping. |
| `LibTmux.TmuxBuffer` | One tmux paste buffer. |
| `LibTmux.TmuxChain` | Commands tmux runs together, in one process. |
| `LibTmux.TmuxChaining` | Turns a request record into a command a chain can carry. |
| `LibTmux.TmuxCleanupException` | Reports a failure to clean up a canceled tmux client. |
| `LibTmux.TmuxColorMode` | Defines the tmux client color mode. |
| `LibTmux.TmuxCommand` | One tmux command and the arguments it carries. |
| `LibTmux.TmuxCommandException` | Reports a command-policy failure. |
| `LibTmux.TmuxCommandNotFoundException` | Reports a missing tmux executable. |
| `LibTmux.TmuxCommandResult` | Contains the inspectable result of one raw tmux command. |
| `LibTmux.TmuxDispatchState` | Says whether a failed command reached tmux, which is what decides if retrying is safe. |
| `LibTmux.TmuxEnvironment` | The environment tmux gives to the processes it spawns. |
| `LibTmux.TmuxEnvironmentEntry` | One variable in a tmux environment. |
| `LibTmux.TmuxEvent` | One thing a tmux control client reported without being asked. |
| `LibTmux.TmuxEventsDroppedEvent` | Reports notifications discarded because the bounded event buffer was full. |
| `LibTmux.TmuxExitEvent` | The control client ended. |
| `LibTmux.TmuxHook` | One hook and every command it runs. |
| `LibTmux.TmuxHookEntry` | One command a hook runs, and where it sits in the order. |
| `LibTmux.TmuxHooks` | The hooks of one server, session, window, or pane. |
| `LibTmux.TmuxMenuItem` | One line of a tmux menu. |
| `LibTmux.TmuxNotificationEvent` | A notification this library does not parse further. |
| `LibTmux.TmuxObjectNotFoundException` | Reports a missing tmux object. |
| `LibTmux.TmuxOperationCanceledException` | Reports cancellation after a tmux client started. |
| `LibTmux.TmuxOption` | One option, with its array index when tmux gave it one. |
| `LibTmux.TmuxOptionException` | Thrown when tmux refuses an option name or value. |
| `LibTmux.TmuxOptionState` | What tmux reported for an option. |
| `LibTmux.TmuxOptionValue` | One option value as tmux reported it. |
| `LibTmux.TmuxOptions` | The options of one server, session, window, or pane. |
| `LibTmux.TmuxOutputEvent` | Bytes a pane wrote. |
| `LibTmux.TmuxPaneException` | Thrown when a pane operation is refused before tmux sees it. |
| `LibTmux.TmuxSessionExistsException` | Thrown when a session name is already taken. |
| `LibTmux.TmuxTransportException` | Reports a process-transport failure. |
| `LibTmux.TmuxVersion` | Represents one lossless parsed tmux version. |
| `LibTmux.TmuxVersionTooLowException` | Reports an unsupported tmux version. |
| `LibTmux.TmuxWaitChannel` | An open wait on a tmux wait-for channel. |
| `LibTmux.TmuxWaitMode` | What to do with a wait-for channel. |
| `LibTmux.TmuxWaitTimeoutException` | Reports an expired bounded wait. |
| `LibTmux.TmuxWindowException` | Thrown when a window operation is refused before tmux sees it. |
| `LibTmux.UnbindKeyRequest` | Describes one unbind-key invocation. |
| `LibTmux.UnsafeTmuxFilter` | A tmux filter expression passed through without translation. |
| `LibTmux.UnsetOptionRequest` | Describes one set-option -u invocation. |
| `LibTmux.UnsupportedQueryExpressionException` | Thrown when an expression cannot be translated to a query. |
| `LibTmux.WaitForRequest` | Describes one wait-for invocation. |
| `LibTmux.Window` | Represents an immutable window handle and snapshot. |
| `LibTmux.WindowDirection` | Defines relative window placement. |
| `LibTmux.WindowEntityKey` | Identifies one window as it appears inside one session. |
| `LibTmux.WindowId` | Represents a generation-independent tmux window identifier. |
| `LibTmux.WindowResizeMode` | Names how a window is resized against its clients. |
| `LibTmux.WindowRotationDirection` | Names which way a window's panes rotate. |

## Methods

| Member | Summary |
|---|---|
| `LibTmux.AttachSessionRequest.#ctor(System.String,System.Boolean,System.Boolean,System.Boolean,System.Collections.Generic.IReadOnlyList{System.String})` | Initializes a session-attachment request. |
| `LibTmux.BindKeyRequest.#ctor(System.String,System.Collections.Generic.IReadOnlyList{System.String},System.String,System.String,System.Boolean)` | Initializes a key binding. |
| `LibTmux.CapturePanePosition.#ctor(System.Int32)` | Initializes a position at one line. |
| `LibTmux.CapturePaneRequest.#ctor(System.Nullable{LibTmux.CapturePanePosition},System.Nullable{LibTmux.CapturePanePosition},System.Boolean,System.Boolean,System.Boolean,System.Boolean,System.Boolean,System.Boolean,System.Boolean,System.Boolean,System.Boolean,System.Boolean,System.Boolean,System.Boolean)` | Initializes a capture request. |
| ``LibTmux.CapturedRelation`1.OrEmpty`` | Returns the captured children, or an empty list when unread. |
| `LibTmux.ChooseTreeRequest.#ctor(System.Boolean,System.Boolean,System.String,LibTmux.UnsafeTmuxFilter,System.Nullable{LibTmux.ChooseTreeSort},System.Boolean,System.Boolean)` | Initializes a tree-chooser request. |
| `LibTmux.Client.GetAsync(LibTmux.Server,System.String,System.Threading.CancellationToken)` | Reads one client by name. |
| `LibTmux.Client.GetAttachedPaneAsync(System.Threading.CancellationToken)` | Reads the pane this client has active now. |
| `LibTmux.Client.GetAttachedSessionAsync(System.Threading.CancellationToken)` | Reads the session this client is attached to now. |
| `LibTmux.Client.GetAttachedWindowAsync(System.Threading.CancellationToken)` | Reads the window this client is showing now. |
| `LibTmux.Client.RefreshAsync(System.Threading.CancellationToken)` | Re-reads this client from tmux. |
| `LibTmux.Client.ResolveAttachmentAsync(System.Threading.CancellationToken)` | Reads where this client is looking now. |
| `LibTmux.ClientAttachment.#ctor(LibTmux.Session,LibTmux.Window,LibTmux.Pane)` | What one client is looking at. |
| `LibTmux.CommandPromptRequest.#ctor(System.String,System.String,System.String,System.String,System.Boolean,System.Boolean,System.Boolean,System.Boolean,System.Nullable{LibTmux.PromptType},System.Boolean,System.Boolean,System.Boolean,System.Boolean)` | Initializes a command prompt. |
| `LibTmux.ConfirmBeforeRequest.#ctor(System.Collections.Generic.IReadOnlyList{System.String},System.String,System.String,System.Boolean,System.String)` | Initializes a confirmation. |
| `LibTmux.ControlModeCommandException.#ctor(System.String,LibTmux.TmuxCommand,System.Collections.Generic.IReadOnlyList{System.String},System.Collections.Generic.IReadOnlyList{System.String},System.Exception)` | Initializes a control-mode command exception. |
| `LibTmux.CopyModeRequest.#ctor(System.Boolean,System.Boolean,System.Boolean,System.Boolean,System.Boolean,System.String)` | Initializes a copy-mode request. |
| `LibTmux.DisplayMenuRequest.#ctor(System.Collections.Generic.IReadOnlyList{LibTmux.TmuxMenuItem},System.String,System.String,System.String,System.String,System.String,System.String,System.String,System.String,System.String,System.String,System.Boolean,System.Boolean)` | Initializes a menu. |
| `LibTmux.DisplayMessageRequest.#ctor(System.String,System.Boolean,System.String,System.Boolean,System.Boolean,System.Boolean,System.String,System.Nullable{System.TimeSpan},System.Boolean,System.Boolean)` | Initializes a display-message request. |
| `LibTmux.DisplayPopupRequest.#ctor(System.String,System.Nullable{LibTmux.PopupCloseMode},System.Boolean,System.String,System.String,System.String,System.String,System.String,System.String,System.String,System.String,System.String,System.String,System.Collections.Generic.IReadOnlyDictionary{System.String,System.String},System.Boolean,System.Boolean,System.Boolean)` | Initializes a popup request. |
| `LibTmux.FindWindowRequest.#ctor(System.String,System.Boolean,System.Boolean,System.Boolean,System.Boolean,System.Boolean)` | Initializes a window-search request. |
| `LibTmux.GetOptionRequest.#ctor(System.String,System.Nullable{LibTmux.OptionScope},System.Boolean,System.Boolean,System.Boolean,System.Boolean)` | Initializes a request for one option. |
| `LibTmux.GetOptionsRequest.#ctor(System.Nullable{LibTmux.OptionScope},System.Boolean,System.Boolean,System.Boolean,System.Boolean)` | Initializes a request for every option in a scope. |
| `LibTmux.HookRequest.#ctor(System.String,System.Nullable{LibTmux.OptionScope},System.Boolean)` | Initializes a request naming one hook. |
| `LibTmux.IControlModeSession.SendAsync(LibTmux.TmuxCommand,System.Threading.CancellationToken)` | Runs one command on this client and reads what it answered. |
| `LibTmux.IfShellRequest.#ctor(System.String,System.Collections.Generic.IReadOnlyList{System.String},System.Collections.Generic.IReadOnlyList{System.String},System.Boolean,System.String)` | Initializes a conditional command. |
| `LibTmux.IncompleteSnapshotException.#ctor(System.String,LibTmux.SnapshotDepth)` | Initializes the exception for one uncaptured relation. |
| `LibTmux.LibTmuxException.#ctor(System.String,LibTmux.TmuxDispatchState,System.Exception)` | Initializes a LibTmux exception that knows whether tmux ran the command. |
| `LibTmux.LibTmuxException.#ctor(System.String,System.Exception)` | Initializes a LibTmux exception whose dispatch state is unknown. |
| `LibTmux.LinkWindowRequest.#ctor(System.String,System.String,System.Nullable{LibTmux.WindowDirection},System.Boolean,System.Boolean)` | Initializes a window-link request. |
| `LibTmux.ListBuffersRequest.#ctor(System.String,LibTmux.UnsafeTmuxFilter)` | Initializes a buffer listing. |
| `LibTmux.ListHooksRequest.#ctor(System.Nullable{LibTmux.OptionScope},System.Boolean)` | Initializes a request for every hook in a scope. |
| `LibTmux.MovePaneRequest.#ctor(System.String,LibTmux.PaneDirection,System.String,System.Boolean,System.Boolean,System.Boolean)` | Initializes a pane-move request. |
| `LibTmux.MoveWindowRequest.#ctor(System.String,System.String,System.Nullable{LibTmux.WindowDirection},System.Boolean,System.Boolean,System.Boolean)` | Initializes a window-move request. |
| `LibTmux.NewPaneRequest.#ctor(System.String,System.String,System.Boolean,System.String,System.Collections.Generic.IReadOnlyDictionary{System.String,System.String},System.Nullable{System.Int32},System.Nullable{System.Int32},System.Nullable{System.Int32},System.Nullable{System.Int32},System.Boolean,System.Boolean,System.String,System.String,System.String,System.String,System.Boolean)` | Initializes a pane-creation request. |
| `LibTmux.NewSessionRequest.#ctor(System.String,System.Boolean,System.Boolean,System.String,System.String,System.String,System.String,System.String,System.Collections.Generic.IReadOnlyDictionary{System.String,System.String},System.Boolean,System.Boolean,System.String)` | Initializes a session-creation request. |
| `LibTmux.NewWindowRequest.#ctor(System.String,System.String,System.Boolean,System.String,System.String,System.Collections.Generic.IReadOnlyDictionary{System.String,System.String},System.Nullable{LibTmux.WindowDirection},System.String,System.Boolean,System.Boolean)` | Initializes a window-creation request. |
| `LibTmux.OwnedServerScope.DisposeAsync` | Stops the owned server. |
| `LibTmux.OwnedSessionScope.DisposeAsync` | Stops the owned session. |
| `LibTmux.OwnedWindowScope.DisposeAsync` | Stops the owned window. |
| `LibTmux.Pane.BreakAsync(System.String,System.Boolean,System.Threading.CancellationToken)` | Moves this pane out into a window of its own. |
| `LibTmux.Pane.CaptureAsync(LibTmux.CapturePaneRequest,System.Threading.CancellationToken)` | Reads the pane's contents. |
| `LibTmux.Pane.CaptureToBufferAsync(System.String,LibTmux.CapturePaneRequest,System.Threading.CancellationToken)` | Captures the pane's contents into a tmux buffer. |
| `LibTmux.Pane.ChooseBufferAsync(System.Threading.CancellationToken)` | Opens the buffer chooser in this pane. |
| `LibTmux.Pane.ChooseClientAsync(System.Threading.CancellationToken)` | Opens the client chooser in this pane. |
| `LibTmux.Pane.ChooseTreeAsync(LibTmux.ChooseTreeRequest,System.Threading.CancellationToken)` | Opens the session tree chooser in this pane. |
| `LibTmux.Pane.ClearAsync(System.Threading.CancellationToken)` | Clears the pane by running the shell's reset. |
| `LibTmux.Pane.ClearHistoryAsync(System.Boolean,System.Threading.CancellationToken)` | Drops the pane's scrollback history. |
| `LibTmux.Pane.CreatePaneAsync(LibTmux.NewPaneRequest,System.Threading.CancellationToken)` | Creates a floating pane against this one. |
| `LibTmux.Pane.DisplayMessageAsync(LibTmux.DisplayMessageRequest,System.Threading.CancellationToken)` | Shows a message on the client viewing this pane. |
| `LibTmux.Pane.DisplayPaneNumbersAsync(System.Nullable{System.TimeSpan},System.Boolean,System.Threading.CancellationToken)` | Shows the pane numbers on every client. |
| `LibTmux.Pane.DisplayPopupAsync(LibTmux.DisplayPopupRequest,System.Threading.CancellationToken)` | Shows a popup over the client viewing this pane. |
| `LibTmux.Pane.EnterAsync(System.Threading.CancellationToken)` | Presses Enter in the pane. |
| `LibTmux.Pane.EnterClockModeAsync(System.Threading.CancellationToken)` | Puts the pane into clock mode. |
| `LibTmux.Pane.EnterCopyModeAsync(LibTmux.CopyModeRequest,System.Threading.CancellationToken)` | Puts the pane into copy mode. |
| `LibTmux.Pane.EnterCustomizeModeAsync(System.Threading.CancellationToken)` | Puts the pane into customize mode. |
| `LibTmux.Pane.ExecuteCommandAsync(System.Collections.Generic.IReadOnlyList{System.String},System.String,System.Threading.CancellationToken)` | Executes one raw tmux command against this pane. |
| `LibTmux.Pane.FindWindowAsync(LibTmux.FindWindowRequest,System.Threading.CancellationToken)` | Opens the window finder in this pane. |
| `LibTmux.Pane.FromEnvironmentAsync(System.Collections.Generic.IReadOnlyDictionary{System.String,System.String},System.Threading.CancellationToken)` | Returns the pane this process was spawned in. |
| `LibTmux.Pane.JoinAsync(LibTmux.MovePaneRequest,System.Threading.CancellationToken)` | Joins this pane into another window. |
| `LibTmux.Pane.KillAsync(System.Boolean,System.Threading.CancellationToken)` | Stops this pane. |
| `LibTmux.Pane.MoveAsync(LibTmux.MovePaneRequest,System.Threading.CancellationToken)` | Moves this pane to another position. |
| `LibTmux.Pane.PasteBufferAsync(LibTmux.PasteBufferRequest,System.Threading.CancellationToken)` | Pastes a tmux buffer into the pane. |
| `LibTmux.Pane.PipeAsync(LibTmux.PipePaneRequest,System.Threading.CancellationToken)` | Pipes the pane's input or output through a command. |
| `LibTmux.Pane.RefreshAsync(System.Threading.CancellationToken)` | Re-reads this pane from tmux. |
| `LibTmux.Pane.ResetAsync(System.Threading.CancellationToken)` | Resets the pane's terminal state and drops its history. |
| `LibTmux.Pane.ResizeAsync(LibTmux.ResizePaneRequest,System.Threading.CancellationToken)` | Resizes this pane. |
| `LibTmux.Pane.RespawnAsync(LibTmux.RespawnRequest,System.Threading.CancellationToken)` | Restarts the command running in this pane. |
| `LibTmux.Pane.SelectAsync(LibTmux.SelectPaneRequest,System.Threading.CancellationToken)` | Selects this pane. |
| `LibTmux.Pane.SendKeysAsync(LibTmux.SendKeysRequest,System.Threading.CancellationToken)` | Sends keys to the pane. |
| `LibTmux.Pane.SendPrefixAsync(System.Boolean,System.Threading.CancellationToken)` | Sends the configured prefix key to the pane. |
| `LibTmux.Pane.SendTextAsync(System.String,System.Boolean,System.Threading.CancellationToken)` | Types text into the pane. |
| `LibTmux.Pane.SetHeightAsync(System.Int32,System.Threading.CancellationToken)` | Sets this pane's height. |
| `LibTmux.Pane.SetTitleAsync(System.String,System.Threading.CancellationToken)` | Sets this pane's title. |
| `LibTmux.Pane.SetWidthAsync(System.Int32,System.Threading.CancellationToken)` | Sets this pane's width. |
| `LibTmux.Pane.SplitAsync(LibTmux.SplitPaneRequest,System.Threading.CancellationToken)` | Splits this pane. |
| `LibTmux.Pane.SwapAsync(LibTmux.SwapPaneRequest,System.Threading.CancellationToken)` | Swaps this pane with another. |
| `LibTmux.PaneId.#ctor(System.Int32)` | Initializes a pane identifier. |
| `LibTmux.PaneId.Parse(System.String)` | Parses a prefixed pane identifier. |
| `LibTmux.PaneId.ToString` | Returns the canonical prefixed identifier. |
| `LibTmux.PaneId.TryParse(System.String,LibTmux.PaneId@)` | Tries to parse a prefixed pane identifier. |
| `LibTmux.PasteBufferRequest.#ctor(System.String,System.Boolean,System.Boolean,System.Boolean,System.String,System.Boolean)` | Initializes a buffer-paste request. |
| `LibTmux.PipePaneRequest.#ctor(System.String,System.Boolean,System.Boolean,System.Boolean)` | Initializes a pane-piping request. |
| `LibTmux.PsmuxCaptureOptions.#ctor(System.Nullable{LibTmux.CapturePanePosition},System.Nullable{LibTmux.CapturePanePosition},System.Boolean,System.Boolean)` | Initializes a bounded psmux capture. |
| `LibTmux.PsmuxConnectionOptions.#ctor(System.String,System.String,System.String,System.String,Microsoft.Extensions.Logging.ILogger)` | Initializes one explicit psmux endpoint. |
| `LibTmux.PsmuxPane.CaptureAsync(LibTmux.PsmuxCaptureOptions,System.Threading.CancellationToken)` | Reads this pane's text through the audited capture subset. |
| `LibTmux.PsmuxServer.ConnectAsync(LibTmux.PsmuxConnectionOptions,System.Threading.CancellationToken)` | Connects to a separately provisioned psmux namespace. |
| `LibTmux.PsmuxServer.GetPanesAsync(System.Threading.CancellationToken)` | Reads every pane in the sole session. |
| `LibTmux.PsmuxServer.GetSessionAsync(System.Threading.CancellationToken)` | Reads the sole visible session. |
| `LibTmux.PsmuxServer.GetWindowsAsync(System.Threading.CancellationToken)` | Reads every window in the sole session. |
| `LibTmux.PsmuxServer.RefreshAsync(System.Threading.CancellationToken)` | Reconnects and returns a fresh server observation. |
| `LibTmux.PsmuxSession.GetPanesAsync(System.Threading.CancellationToken)` | Reads the session's current panes. |
| `LibTmux.PsmuxSession.GetWindowsAsync(System.Threading.CancellationToken)` | Reads the session's current windows. |
| `LibTmux.PsmuxWindow.GetPanesAsync(System.Threading.CancellationToken)` | Reads the window's current panes. |
| `LibTmux.Query.AndNode.#ctor(System.Collections.Generic.IReadOnlyList{LibTmux.Query.QueryNode})` | Initializes a conjunction. |
| `LibTmux.Query.BooleanConstant.#ctor(System.Boolean)` | A boolean literal. |
| `LibTmux.Query.ComparisonNode.#ctor(LibTmux.Query.QueryComparison,LibTmux.Query.QueryNode,LibTmux.Query.QueryNode)` | An ordering or equality comparison. |
| `LibTmux.Query.ConstantNode.#ctor(LibTmux.Query.QueryConstant)` | A literal operand. |
| `LibTmux.Query.FieldNode.#ctor(LibTmux.Query.QueryTarget,System.String)` | A tmux format field operand. |
| `LibTmux.Query.Int64Constant.#ctor(System.Int64)` | A 64-bit integer literal. |
| `LibTmux.Query.NotNode.#ctor(LibTmux.Query.QueryNode)` | The negation of one predicate. |
| `LibTmux.Query.OrNode.#ctor(System.Collections.Generic.IReadOnlyList{LibTmux.Query.QueryNode})` | Initializes a disjunction. |
| `LibTmux.Query.QuantifierNode.#ctor(LibTmux.Query.QueryQuantifier,LibTmux.Query.FieldNode,LibTmux.Query.QueryNode)` | A quantifier over a relation field. |
| `LibTmux.Query.QueryDocument.#ctor(System.String,System.Int32,LibTmux.Query.QueryTarget,LibTmux.Query.QueryNode)` | One translated query predicate and its wire schema. |
| `LibTmux.Query.QueryEdgeParser.ParseNameContains(LibTmux.Query.QueryTarget,System.String)` | Parses a name__contains lookup into a query document. |
| ```LibTmux.Query.QueryExtensions.Compile``1(LibTmux.Query.QueryDocument)``` | Compiles a document into an in-memory predicate. |
| ```LibTmux.Query.QueryExtensions.Matching``1(System.Collections.Generic.IEnumerable{``0},LibTmux.Query.QueryDocument)``` | Filters a snapshot with an already translated document. |
| ```LibTmux.Query.QueryExtensions.Matching``1(System.Collections.Generic.IEnumerable{``0},LibTmux.Query.QueryDocument,System.Threading.CancellationToken)``` | Filters a snapshot with a cancellable translated document. |
| ```LibTmux.Query.QueryExtensions.Matching``1(System.Collections.Generic.IEnumerable{``0},System.Linq.Expressions.Expression{System.Func{``0,System.Boolean}})``` | Filters a snapshot with a declarative predicate. |
| ```LibTmux.Query.QueryExtensions.Translate``1(System.Linq.Expressions.Expression{System.Func{``0,System.Boolean}})``` | Translates an expression into a wire document. |
| `LibTmux.Query.RegexNode.#ctor(LibTmux.Query.QueryNode,System.String,System.String,System.Text.RegularExpressions.RegexOptions)` | A constant-pattern regular expression match. |
| `LibTmux.Query.StringConstant.#ctor(System.String)` | A string literal. |
| `LibTmux.Query.StringNode.#ctor(LibTmux.Query.QueryStringOperation,LibTmux.Query.QueryNode,LibTmux.Query.QueryNode)` | An ordinal string comparison. |
| `LibTmux.Query.TypedIdConstant.#ctor(LibTmux.Query.QueryTarget,System.String)` | A typed tmux identifier literal. |
| `LibTmux.ResizePaneRequest.#ctor(System.Nullable{LibTmux.ResizeDirection},System.Nullable{System.Int32},System.String,System.String,System.Boolean,System.Boolean,System.Boolean)` | Initializes a pane-resize request. |
| `LibTmux.ResizeWindowRequest.#ctor(System.Nullable{LibTmux.ResizeDirection},System.Nullable{System.Int32},System.Nullable{System.Int32},System.Nullable{System.Int32},System.Nullable{LibTmux.WindowResizeMode})` | Initializes a window-resize request. |
| `LibTmux.RespawnRequest.#ctor(System.String,System.String,System.Collections.Generic.IReadOnlyDictionary{System.String,System.String},System.Boolean)` | Initializes a respawn request. |
| `LibTmux.RunShellRequest.#ctor(System.String,System.Collections.Generic.IReadOnlyList{System.String},System.Boolean,System.Nullable{System.TimeSpan},System.Boolean,System.String,System.String,System.Boolean)` | Initializes a shell command. |
| `LibTmux.SelectLayoutRequest.#ctor(System.String,System.Nullable{LibTmux.SelectLayoutMode})` | Initializes a layout-selection request. |
| `LibTmux.SelectPaneRequest.#ctor(System.Nullable{LibTmux.PaneSelectDirection},System.Boolean,System.Nullable{System.Boolean},System.Nullable{System.Boolean},System.Boolean)` | Initializes a pane-selection request. |
| `LibTmux.SendKeysRequest.#ctor(System.String,System.Boolean,System.Boolean,System.Boolean,System.Boolean,System.String,System.Nullable{System.Int32},System.Boolean,System.Boolean,System.String,System.Boolean)` | Initializes a key-sending request. |
| `LibTmux.Server.AttachSessionAsync(LibTmux.AttachSessionRequest,System.Threading.CancellationToken)` | Attaches a client to a session on this server. |
| `LibTmux.Server.BindKeyAsync(LibTmux.BindKeyRequest,System.Threading.CancellationToken)` | Binds a key to a tmux command. |
| `LibTmux.Server.CaptureSnapshotAsync(LibTmux.SnapshotDepth,System.Threading.CancellationToken)` | Reads the server and answers a handle carrying what it found. |
| `LibTmux.Server.Chain` | Begins a chain that runs its commands in one tmux invocation. |
| `LibTmux.Server.ClearPromptHistoryAsync(System.Nullable{LibTmux.PromptType},System.Threading.CancellationToken)` | Forgets what has been typed at command prompts. |
| `LibTmux.Server.ConfigureAccessAsync(LibTmux.ServerAccessRequest,System.Threading.CancellationToken)` | Grants or withdraws another user's access to this server. |
| `LibTmux.Server.ConfirmBeforeAsync(LibTmux.ConfirmBeforeRequest,System.Threading.CancellationToken)` | Asks a client to confirm before running a command. |
| `LibTmux.Server.ConnectAsync(LibTmux.ServerConnectionOptions,System.Threading.CancellationToken)` | Connects to a configured tmux endpoint. |
| `LibTmux.Server.ConnectAsync(System.Threading.CancellationToken)` | Materializes this connection and returns its immutable replacement. |
| `LibTmux.Server.CreateOwnedAsync(LibTmux.ServerConnectionOptions,System.Threading.CancellationToken)` | Starts a server and takes ownership of it. |
| `LibTmux.Server.CreateOwnedSessionAsync(LibTmux.NewSessionRequest,System.Threading.CancellationToken)` | Creates a session and takes ownership of it. |
| `LibTmux.Server.CreateSessionAsync(LibTmux.NewSessionRequest,System.Threading.CancellationToken)` | Creates a session. |
| `LibTmux.Server.DeleteBufferAsync(System.String,System.Threading.CancellationToken)` | Forgets a paste buffer. |
| `LibTmux.Server.DetachAllClientsAsync(System.String,System.String,System.Threading.CancellationToken)` | Detaches every client except one. |
| `LibTmux.Server.DetachClientAsync(System.String,System.String,System.Threading.CancellationToken)` | Detaches one client. |
| `LibTmux.Server.DisplayMessageAsync(LibTmux.DisplayMessageRequest,System.Threading.CancellationToken)` | Shows a message on a client. |
| `LibTmux.Server.EnterControlModeAsync(System.String,System.Threading.CancellationToken)` | Starts a tmux control client and keeps it running. |
| `LibTmux.Server.ExecuteCommandAsync(System.Collections.Generic.IReadOnlyList{System.String},System.Threading.CancellationToken)` | Executes one raw tmux command. |
| `LibTmux.Server.FromEnvironment(System.Collections.Generic.IReadOnlyDictionary{System.String,System.String})` | Returns the server whose pane this process was spawned in. |
| `LibTmux.Server.GetAttachedSessionsAsync(System.Threading.CancellationToken)` | Reads every session with at least one attached client. |
| `LibTmux.Server.GetBufferAsync(System.String,System.Threading.CancellationToken)` | Reads a paste buffer in full. |
| `LibTmux.Server.GetBufferLinesAsync(LibTmux.ListBuffersRequest,System.Threading.CancellationToken)` | Reads the paste buffers as tmux rendered them. |
| `LibTmux.Server.GetBuffersAsync(System.Threading.CancellationToken)` | Reads the paste buffers. |
| `LibTmux.Server.GetClientsAsync(System.Threading.CancellationToken)` | Reads the clients attached to this server. |
| `LibTmux.Server.GetCommandsAsync(System.String,System.Threading.CancellationToken)` | Reads the commands this tmux knows. |
| `LibTmux.Server.GetKeysAsync(System.String,System.String,System.Threading.CancellationToken)` | Reads the key bindings. |
| `LibTmux.Server.GetMessagesAsync(System.String,LibTmux.ShowMessagesMode,System.Threading.CancellationToken)` | Reads what the server has been logging. |
| `LibTmux.Server.GetPaneAsync(LibTmux.PaneId,System.Threading.CancellationToken)` | Gets one pane by its typed identifier. |
| `LibTmux.Server.GetPanesAsync(System.Threading.CancellationToken)` | Reads every pane on this server. |
| `LibTmux.Server.GetPromptHistoryAsync(System.Nullable{LibTmux.PromptType},System.Threading.CancellationToken)` | Reads what has been typed at command prompts. |
| `LibTmux.Server.GetSessionAsync(LibTmux.SessionId,System.Threading.CancellationToken)` | Gets one session by its typed identifier. |
| `LibTmux.Server.GetSessionsAsync(System.Threading.CancellationToken)` | Reads every session on this server. |
| `LibTmux.Server.GetWindowAsync(LibTmux.WindowId,System.Threading.CancellationToken)` | Gets one window by its typed identifier. |
| `LibTmux.Server.GetWindowsAsync(System.Threading.CancellationToken)` | Reads every window on this server. |
| `LibTmux.Server.HasSessionAsync(System.String,System.Boolean,System.Threading.CancellationToken)` | Reports whether a session exists. |
| `LibTmux.Server.IfShellAsync(LibTmux.IfShellRequest,System.Threading.CancellationToken)` | Runs one tmux command or another depending on a shell command. |
| `LibTmux.Server.IsAliveAsync(System.Threading.CancellationToken)` | Reports whether a tmux server is answering. |
| `LibTmux.Server.KillAsync(System.Threading.CancellationToken)` | Stops the tmux server. |
| `LibTmux.Server.KillSessionAsync(System.String,System.Threading.CancellationToken)` | Stops one session. |
| `LibTmux.Server.LoadBufferAsync(System.String,System.String,System.Threading.CancellationToken)` | Puts a file's contents into a paste buffer. |
| `LibTmux.Server.LockAsync(System.Threading.CancellationToken)` | Locks every client attached to this server. |
| `LibTmux.Server.LockClientAsync(System.String,System.Threading.CancellationToken)` | Locks one client. |
| `LibTmux.Server.Open(LibTmux.ServerConnectionOptions)` | Opens an unmaterialized server connection handle. |
| `LibTmux.Server.OpenWaitChannel(System.String)` | Opens a wait on a channel that survives a timed attempt. |
| `LibTmux.Server.RaiseIfDeadAsync(System.Threading.CancellationToken)` | Throws unless a tmux server is answering. |
| `LibTmux.Server.RefreshClientAsync(System.String,System.Boolean,System.Threading.CancellationToken)` | Redraws one client. |
| `LibTmux.Server.RunShellAsync(LibTmux.RunShellRequest,System.Threading.CancellationToken)` | Runs a shell command and reports what it printed. |
| `LibTmux.Server.SaveBufferAsync(System.String,System.String,System.Boolean,System.Threading.CancellationToken)` | Writes a paste buffer to a file. |
| `LibTmux.Server.SearchPanesAsync(LibTmux.UnsafeTmuxFilter,System.Threading.CancellationToken)` | Runs a tmux-side filter over every pane. |
| `LibTmux.Server.SearchSessionsAsync(LibTmux.UnsafeTmuxFilter,System.Threading.CancellationToken)` | Runs a tmux-side filter over every session. |
| `LibTmux.Server.SearchWindowsAsync(LibTmux.UnsafeTmuxFilter,System.Threading.CancellationToken)` | Runs a tmux-side filter over every window. |
| `LibTmux.Server.SetBufferAsync(System.String,System.String,System.Boolean,System.Threading.CancellationToken)` | Puts text into a paste buffer. |
| `LibTmux.Server.ShowCommandPromptAsync(LibTmux.CommandPromptRequest,System.Threading.CancellationToken)` | Asks a client for input and runs a command with the answer. |
| `LibTmux.Server.ShowMenuAsync(LibTmux.DisplayMenuRequest,System.Threading.CancellationToken)` | Shows a menu on a client. |
| `LibTmux.Server.SourceFileAsync(System.String,System.Boolean,System.Boolean,System.Boolean,System.Threading.CancellationToken)` | Reads a tmux configuration file. |
| `LibTmux.Server.StartServerAsync(System.Threading.CancellationToken)` | Starts the tmux server without creating a session. |
| `LibTmux.Server.SuspendClientAsync(System.String,System.Threading.CancellationToken)` | Suspends one client. |
| `LibTmux.Server.SwitchClientAsync(System.String,System.Threading.CancellationToken)` | Switches the caller's client to another session. |
| `LibTmux.Server.UnbindKeyAsync(LibTmux.UnbindKeyRequest,System.Threading.CancellationToken)` | Removes a key binding. |
| `LibTmux.Server.WaitForAsync(LibTmux.WaitForRequest,System.Threading.CancellationToken)` | Waits on, signals, or locks a tmux channel. |
| `LibTmux.ServerAccessRequest.#ctor(System.String,System.String,System.Boolean,System.Boolean,System.Boolean)` | Initializes an access change. |
| `LibTmux.ServerConnectionOptions.#ctor(System.String,System.String,System.String,System.Func{System.String},System.String,LibTmux.TmuxColorMode,System.Func{LibTmux.Server,System.Threading.CancellationToken,System.Threading.Tasks.ValueTask},System.Collections.Generic.IReadOnlyDictionary{System.String,System.String},Microsoft.Extensions.Logging.ILogger)` | Initializes connection options. |
| `LibTmux.ServerGeneration.#ctor(System.Int32,System.Int64)` | Initializes a server generation. |
| `LibTmux.Session.AttachAsync(LibTmux.AttachSessionRequest,System.Threading.CancellationToken)` | Attaches a client to this session. |
| `LibTmux.Session.CreateOwnedWindowAsync(LibTmux.NewWindowRequest,System.Threading.CancellationToken)` | Creates a window in this session and takes ownership of it. |
| `LibTmux.Session.CreateWindowAsync(LibTmux.NewWindowRequest,System.Threading.CancellationToken)` | Creates a window in this session. |
| `LibTmux.Session.DetachClientAsync(System.String,System.Threading.CancellationToken)` | Detaches every client attached to this session. |
| `LibTmux.Session.ExecuteCommandAsync(System.Collections.Generic.IReadOnlyList{System.String},System.String,System.Threading.CancellationToken)` | Executes one raw tmux command against this session. |
| `LibTmux.Session.FromEnvironmentAsync(System.Collections.Generic.IReadOnlyDictionary{System.String,System.String},System.Threading.CancellationToken)` | Returns the session holding the pane this process runs in. |
| `LibTmux.Session.GetPanesAsync(System.Threading.CancellationToken)` | Reads this session's panes from tmux. |
| `LibTmux.Session.GetWindowAsync(System.String,System.Threading.CancellationToken)` | Reads one of this session's windows by target. |
| `LibTmux.Session.GetWindowsAsync(System.Threading.CancellationToken)` | Reads this session's windows from tmux. |
| `LibTmux.Session.KillAsync(System.Boolean,System.Boolean,System.Boolean,System.Threading.CancellationToken)` | Stops this session. |
| `LibTmux.Session.KillWindowAsync(System.String,System.Threading.CancellationToken)` | Stops one window in this session. |
| `LibTmux.Session.LockAsync(System.Threading.CancellationToken)` | Locks this session. |
| `LibTmux.Session.RefreshAsync(System.Threading.CancellationToken)` | Re-reads this session from tmux. |
| `LibTmux.Session.RenameAsync(System.String,System.Threading.CancellationToken)` | Renames this session. |
| `LibTmux.Session.SearchPanesAsync(LibTmux.UnsafeTmuxFilter,System.Threading.CancellationToken)` | Runs a tmux-side filter over this session's panes. |
| `LibTmux.Session.SearchWindowsAsync(LibTmux.UnsafeTmuxFilter,System.Threading.CancellationToken)` | Runs a tmux-side filter over this session's windows. |
| `LibTmux.Session.SelectLastWindowAsync(System.Threading.CancellationToken)` | Selects the window that was last active. |
| `LibTmux.Session.SelectNextWindowAsync(System.Threading.CancellationToken)` | Selects the next window. |
| `LibTmux.Session.SelectPreviousWindowAsync(System.Threading.CancellationToken)` | Selects the previous window. |
| `LibTmux.Session.SelectWindowAsync(System.String,System.Threading.CancellationToken)` | Selects a window in this session. |
| `LibTmux.Session.SwitchClientAsync(System.Threading.CancellationToken)` | Switches the current client to this session. |
| `LibTmux.SessionId.#ctor(System.Int32)` | Initializes a session identifier. |
| `LibTmux.SessionId.Parse(System.String)` | Parses a prefixed session identifier. |
| `LibTmux.SessionId.ToString` | Returns the canonical prefixed identifier. |
| `LibTmux.SessionId.TryParse(System.String,LibTmux.SessionId@)` | Tries to parse a prefixed session identifier. |
| `LibTmux.SetHookRequest.#ctor(System.String,System.String,System.Nullable{LibTmux.OptionScope},System.Boolean,System.Boolean,System.Boolean,System.Boolean)` | Initializes a request to set one hook. |
| `LibTmux.SetHooksRequest.#ctor(System.String,System.Collections.Generic.IReadOnlyDictionary{System.Int32,System.String},System.Nullable{LibTmux.OptionScope},System.Boolean,System.Boolean)` | Initializes a request to set several entries of one hook. |
| `LibTmux.SetOptionRequest.#ctor(System.String,System.String,System.Nullable{LibTmux.OptionScope},System.Boolean,System.Boolean,System.Boolean,System.Boolean,System.Boolean)` | Initializes a request to set one option. |
| `LibTmux.SplitPaneRequest.#ctor(System.String,System.String,System.Boolean,System.Nullable{LibTmux.PaneDirection},System.Boolean,System.Boolean,System.String,System.String,System.Nullable{System.Int32},System.Collections.Generic.IReadOnlyDictionary{System.String,System.String},System.Boolean,System.String,System.String,System.String,System.String,System.Boolean)` | Initializes a pane-split request. |
| `LibTmux.StaleServerGenerationException.#ctor(System.String,LibTmux.ServerGeneration,LibTmux.ServerGeneration,System.Exception)` | Initializes a stale-generation exception. |
| `LibTmux.StaleServerGenerationException.#ctor(System.String,LibTmux.ServerGeneration,System.Exception)` | Initializes a stale-generation exception when the replacement is unknown. |
| `LibTmux.SwapPaneRequest.#ctor(System.String,System.Nullable{LibTmux.PaneSwapDirection},System.Boolean,System.Boolean)` | Initializes a pane-swap request. |
| `LibTmux.Testing.TestEnvironment.#ctor(System.String,System.Collections.Generic.IReadOnlyDictionary{System.String,System.String})` | Initializes a test environment. |
| `LibTmux.Testing.TestEnvironment.WithVariable(System.String,System.String)` | Answers a copy that also sets one variable. |
| `LibTmux.Testing.TestEnvironment.WithoutVariable(System.String)` | Answers a copy that removes one variable. |
| `LibTmux.Testing.TmuxNameGenerator.#ctor(System.String)` | Initializes a generator. |
| `LibTmux.Testing.TmuxNameGenerator.CreateAvailableSessionNameAsync(LibTmux.Server,System.String,System.Threading.CancellationToken)` | Makes a session name the server does not already hold. |
| `LibTmux.Testing.TmuxNameGenerator.CreateAvailableWindowNameAsync(LibTmux.Session,System.String,System.Threading.CancellationToken)` | Makes a window name the session does not already hold. |
| `LibTmux.Testing.TmuxNameGenerator.CreateSessionName` | Makes a session name. |
| `LibTmux.Testing.TmuxNameGenerator.CreateWindowName` | Makes a window name. |
| `LibTmux.Testing.TmuxTestFactory.#ctor` | Initializes a factory. |
| `LibTmux.Testing.TmuxTestFactory.CreateContextAsync(LibTmux.Testing.TmuxTestOptions,System.Threading.CancellationToken)` | Starts a server this test owns, with its environment. |
| `LibTmux.Testing.TmuxTestFactory.CreateHierarchyAsync(LibTmux.Testing.TmuxTestOptions,System.Threading.CancellationToken)` | Starts a server, session, window, and pane a test can type into. |
| `LibTmux.Testing.TmuxTestFactory.CreateServerAsync(LibTmux.Testing.TmuxTestOptions,System.Threading.CancellationToken)` | Starts a server this test owns. |
| `LibTmux.Testing.TmuxTestFactory.CreateSessionAsync(LibTmux.Server,LibTmux.Testing.TmuxTestOptions,System.Threading.CancellationToken)` | Starts a session on a server the caller already has. |
| `LibTmux.Testing.TmuxTestFactory.CreateSessionAsync(LibTmux.Testing.TmuxTestOptions,System.Threading.CancellationToken)` | Starts a server and a session in it, both owned by this test. |
| `LibTmux.Testing.TmuxTestFactory.CreateWindowAsync(LibTmux.Session,LibTmux.Testing.TmuxTestOptions,System.Threading.CancellationToken)` | Starts a window in a session the caller already has. |
| `LibTmux.Testing.TmuxTestFactory.CreateWindowAsync(LibTmux.Testing.TmuxTestOptions,System.Threading.CancellationToken)` | Starts a server, a session, and a window, all owned by this test. |
| `LibTmux.Testing.TmuxTestOptions.#ctor(LibTmux.ServerConnectionOptions,System.Nullable{System.TimeSpan},System.Nullable{System.TimeSpan},System.String)` | Initializes test options. |
| `LibTmux.Testing.TmuxWait.UntilAsync(System.Func{System.Threading.CancellationToken,System.Threading.Tasks.Task{System.Boolean}},System.TimeSpan,System.TimeSpan,System.Boolean,System.Threading.CancellationToken)` | Waits until a probe reports the state was reached. |
| ```LibTmux.Testing.TmuxWait.UntilAsync``1(System.Func{System.Threading.CancellationToken,System.Threading.Tasks.Task{``0}},System.Func{``0,System.Boolean},System.TimeSpan,System.TimeSpan,System.Threading.CancellationToken)``` | Waits until a reading satisfies a predicate, and answers it. |
| `LibTmux.TmuxBuffer.#ctor(System.String,System.Int64,System.String)` | Initializes one buffer. |
| `LibTmux.TmuxChain.ExecuteAsync(System.Threading.CancellationToken)` | Runs every command in one tmux invocation. |
| `LibTmux.TmuxChain.Then(LibTmux.TmuxCommand)` | Adds one command and returns the longer chain. |
| `LibTmux.TmuxChain.Then(System.Collections.Generic.IEnumerable{LibTmux.TmuxCommand})` | Adds every command in order and returns the longer chain. |
| `LibTmux.TmuxChain.Then(System.String,System.String[])` | Adds one command by name and returns the longer chain. |
| `LibTmux.TmuxChaining.ExecuteAsync(LibTmux.AttachSessionRequest,LibTmux.Session,System.Threading.CancellationToken)` | Runs an attach request on its own. |
| `LibTmux.TmuxChaining.ExecuteAsync(LibTmux.BindKeyRequest,LibTmux.Server,System.Threading.CancellationToken)` | Runs a key-binding request on its own. |
| `LibTmux.TmuxChaining.ExecuteAsync(LibTmux.CapturePaneRequest,LibTmux.Pane,System.Threading.CancellationToken)` | Runs a capture request on its own. |
| `LibTmux.TmuxChaining.ExecuteAsync(LibTmux.ChooseTreeRequest,LibTmux.Pane,System.Threading.CancellationToken)` | Runs a chooser request on its own. |
| `LibTmux.TmuxChaining.ExecuteAsync(LibTmux.CommandPromptRequest,LibTmux.Server,System.Threading.CancellationToken)` | Runs a prompt request on its own. |
| `LibTmux.TmuxChaining.ExecuteAsync(LibTmux.ConfirmBeforeRequest,LibTmux.Server,System.Threading.CancellationToken)` | Runs a confirmation request on its own. |
| `LibTmux.TmuxChaining.ExecuteAsync(LibTmux.CopyModeRequest,LibTmux.Pane,System.Threading.CancellationToken)` | Runs a copy-mode request on its own. |
| `LibTmux.TmuxChaining.ExecuteAsync(LibTmux.DisplayMenuRequest,LibTmux.Server,System.Threading.CancellationToken)` | Runs a menu request on its own. |
| `LibTmux.TmuxChaining.ExecuteAsync(LibTmux.DisplayMessageRequest,LibTmux.Server,System.Threading.CancellationToken)` | Runs a message request on its own. |
| `LibTmux.TmuxChaining.ExecuteAsync(LibTmux.DisplayPopupRequest,LibTmux.Pane,System.Threading.CancellationToken)` | Runs a popup request on its own. |
| `LibTmux.TmuxChaining.ExecuteAsync(LibTmux.FindWindowRequest,LibTmux.Pane,System.Threading.CancellationToken)` | Runs a window-search request on its own. |
| `LibTmux.TmuxChaining.ExecuteAsync(LibTmux.GetOptionRequest,LibTmux.TmuxOptions,LibTmux.Server,System.Threading.CancellationToken)` | Runs a named option read on its own. |
| `LibTmux.TmuxChaining.ExecuteAsync(LibTmux.GetOptionsRequest,LibTmux.TmuxOptions,LibTmux.Server,System.Threading.CancellationToken)` | Runs a whole-scope option read on its own. |
| `LibTmux.TmuxChaining.ExecuteAsync(LibTmux.HookRequest,LibTmux.TmuxHooks,LibTmux.Server,System.Threading.CancellationToken)` | Runs a hook on its own. |
| `LibTmux.TmuxChaining.ExecuteAsync(LibTmux.IfShellRequest,LibTmux.Server,System.Threading.CancellationToken)` | Runs a conditional request on its own. |
| `LibTmux.TmuxChaining.ExecuteAsync(LibTmux.LinkWindowRequest,LibTmux.Window,System.Threading.CancellationToken)` | Runs a link request on its own. |
| `LibTmux.TmuxChaining.ExecuteAsync(LibTmux.ListBuffersRequest,LibTmux.Server,System.Threading.CancellationToken)` | Runs a buffer-listing request on its own. |
| `LibTmux.TmuxChaining.ExecuteAsync(LibTmux.ListHooksRequest,LibTmux.TmuxHooks,LibTmux.Server,System.Threading.CancellationToken)` | Runs a hook listing on its own. |
| `LibTmux.TmuxChaining.ExecuteAsync(LibTmux.MovePaneRequest,LibTmux.Pane,System.Threading.CancellationToken)` | Runs a pane-move request on its own. |
| `LibTmux.TmuxChaining.ExecuteAsync(LibTmux.MoveWindowRequest,LibTmux.Window,System.Threading.CancellationToken)` | Runs a window-move request on its own. |
| `LibTmux.TmuxChaining.ExecuteAsync(LibTmux.NewPaneRequest,LibTmux.Pane,System.Threading.CancellationToken)` | Runs a floating-pane request on its own. |
| `LibTmux.TmuxChaining.ExecuteAsync(LibTmux.NewSessionRequest,LibTmux.Server,System.Threading.CancellationToken)` | Runs a session request on its own. |
| `LibTmux.TmuxChaining.ExecuteAsync(LibTmux.NewWindowRequest,LibTmux.Session,System.Threading.CancellationToken)` | Runs a window request on its own. |
| `LibTmux.TmuxChaining.ExecuteAsync(LibTmux.PasteBufferRequest,LibTmux.Pane,System.Threading.CancellationToken)` | Runs a paste request on its own. |
| `LibTmux.TmuxChaining.ExecuteAsync(LibTmux.PipePaneRequest,LibTmux.Pane,System.Threading.CancellationToken)` | Runs a pane-piping request on its own. |
| `LibTmux.TmuxChaining.ExecuteAsync(LibTmux.ResizePaneRequest,LibTmux.Pane,System.Threading.CancellationToken)` | Runs a pane-resize request on its own. |
| `LibTmux.TmuxChaining.ExecuteAsync(LibTmux.ResizeWindowRequest,LibTmux.Window,System.Threading.CancellationToken)` | Runs a window-resize request on its own. |
| `LibTmux.TmuxChaining.ExecuteAsync(LibTmux.RespawnRequest,LibTmux.Pane,System.Threading.CancellationToken)` | Runs a respawn request on its own. |
| `LibTmux.TmuxChaining.ExecuteAsync(LibTmux.RunShellRequest,LibTmux.Server,System.Threading.CancellationToken)` | Runs a shell request on its own. |
| `LibTmux.TmuxChaining.ExecuteAsync(LibTmux.SelectLayoutRequest,LibTmux.Window,System.Threading.CancellationToken)` | Runs a layout request on its own. |
| `LibTmux.TmuxChaining.ExecuteAsync(LibTmux.SelectPaneRequest,LibTmux.Pane,System.Threading.CancellationToken)` | Runs a pane-selection request on its own. |
| `LibTmux.TmuxChaining.ExecuteAsync(LibTmux.SendKeysRequest,LibTmux.Pane,System.Threading.CancellationToken)` | Runs a key request on its own. |
| `LibTmux.TmuxChaining.ExecuteAsync(LibTmux.ServerAccessRequest,LibTmux.Server,System.Threading.CancellationToken)` | Runs an access request on its own. |
| `LibTmux.TmuxChaining.ExecuteAsync(LibTmux.SetHookRequest,LibTmux.TmuxHooks,LibTmux.Server,System.Threading.CancellationToken)` | Runs a hook request on its own. |
| `LibTmux.TmuxChaining.ExecuteAsync(LibTmux.SetHooksRequest,LibTmux.TmuxHooks,LibTmux.Server,System.Threading.CancellationToken)` | Runs a multi-entry hook request in one invocation. |
| `LibTmux.TmuxChaining.ExecuteAsync(LibTmux.SetOptionRequest,LibTmux.TmuxOptions,LibTmux.Server,System.Threading.CancellationToken)` | Runs an option request on its own. |
| `LibTmux.TmuxChaining.ExecuteAsync(LibTmux.SplitPaneRequest,LibTmux.Pane,System.Threading.CancellationToken)` | Runs a split request on its own. |
| `LibTmux.TmuxChaining.ExecuteAsync(LibTmux.SwapPaneRequest,LibTmux.Pane,System.Threading.CancellationToken)` | Runs a pane-swap request on its own. |
| `LibTmux.TmuxChaining.ExecuteAsync(LibTmux.UnbindKeyRequest,LibTmux.Server,System.Threading.CancellationToken)` | Runs a key-unbinding request on its own. |
| `LibTmux.TmuxChaining.ExecuteAsync(LibTmux.UnsetOptionRequest,LibTmux.TmuxOptions,LibTmux.Server,System.Threading.CancellationToken)` | Runs an unset request on its own. |
| `LibTmux.TmuxChaining.ExecuteAsync(LibTmux.WaitForRequest,LibTmux.Server,System.Threading.CancellationToken)` | Runs a channel request on its own. |
| `LibTmux.TmuxChaining.ToCommand(LibTmux.AttachSessionRequest,LibTmux.Session)` | Returns an attach request as one tmux command. |
| `LibTmux.TmuxChaining.ToCommand(LibTmux.BindKeyRequest)` | Returns a key-binding request as one tmux command. |
| `LibTmux.TmuxChaining.ToCommand(LibTmux.CapturePaneRequest,LibTmux.Pane)` | Returns a capture request as one tmux command. |
| `LibTmux.TmuxChaining.ToCommand(LibTmux.ChooseTreeRequest,LibTmux.Pane)` | Returns a chooser request as one tmux command. |
| `LibTmux.TmuxChaining.ToCommand(LibTmux.CommandPromptRequest,LibTmux.Server)` | Returns a prompt request as one tmux command. |
| `LibTmux.TmuxChaining.ToCommand(LibTmux.ConfirmBeforeRequest,LibTmux.Server)` | Returns a confirmation request as one tmux command. |
| `LibTmux.TmuxChaining.ToCommand(LibTmux.CopyModeRequest,LibTmux.Pane)` | Returns a copy-mode request as one tmux command. |
| `LibTmux.TmuxChaining.ToCommand(LibTmux.DisplayMenuRequest,LibTmux.Server)` | Returns a menu request as one tmux command. |
| `LibTmux.TmuxChaining.ToCommand(LibTmux.DisplayMessageRequest,LibTmux.Server)` | Returns a message request as one tmux command. |
| `LibTmux.TmuxChaining.ToCommand(LibTmux.DisplayPopupRequest,LibTmux.Pane)` | Returns a popup request as one tmux command. |
| `LibTmux.TmuxChaining.ToCommand(LibTmux.FindWindowRequest,LibTmux.Pane)` | Returns a window-search request as one tmux command. |
| `LibTmux.TmuxChaining.ToCommand(LibTmux.GetOptionRequest,LibTmux.TmuxOptions)` | Returns a named option read as one tmux command. |
| `LibTmux.TmuxChaining.ToCommand(LibTmux.GetOptionsRequest,LibTmux.TmuxOptions)` | Returns a whole-scope option read as one tmux command. |
| `LibTmux.TmuxChaining.ToCommand(LibTmux.IfShellRequest)` | Returns a conditional request as one tmux command. |
| `LibTmux.TmuxChaining.ToCommand(LibTmux.LinkWindowRequest,LibTmux.Window)` | Returns a link request as one tmux command. |
| `LibTmux.TmuxChaining.ToCommand(LibTmux.ListBuffersRequest)` | Returns a buffer-listing request as one tmux command. |
| `LibTmux.TmuxChaining.ToCommand(LibTmux.ListHooksRequest,LibTmux.TmuxHooks)` | Returns a hook listing as one tmux command. |
| `LibTmux.TmuxChaining.ToCommand(LibTmux.MovePaneRequest,LibTmux.Pane)` | Returns a pane-move request as one tmux command. |
| `LibTmux.TmuxChaining.ToCommand(LibTmux.MoveWindowRequest,LibTmux.Window)` | Returns a window-move request as one tmux command. |
| `LibTmux.TmuxChaining.ToCommand(LibTmux.NewPaneRequest,LibTmux.Pane)` | Returns a floating-pane request as one tmux command. |
| `LibTmux.TmuxChaining.ToCommand(LibTmux.NewSessionRequest)` | Returns a session request as one tmux command. |
| `LibTmux.TmuxChaining.ToCommand(LibTmux.NewWindowRequest,LibTmux.Session)` | Returns a window request as one tmux command. |
| `LibTmux.TmuxChaining.ToCommand(LibTmux.PasteBufferRequest,LibTmux.Pane)` | Returns a paste request as one tmux command. |
| `LibTmux.TmuxChaining.ToCommand(LibTmux.PipePaneRequest,LibTmux.Pane)` | Returns a pane-piping request as one tmux command. |
| `LibTmux.TmuxChaining.ToCommand(LibTmux.ResizePaneRequest,LibTmux.Pane)` | Returns a pane-resize request as one tmux command. |
| `LibTmux.TmuxChaining.ToCommand(LibTmux.ResizeWindowRequest,LibTmux.Window)` | Returns a window-resize request as one tmux command. |
| `LibTmux.TmuxChaining.ToCommand(LibTmux.RespawnRequest,LibTmux.Pane)` | Returns a respawn request as one tmux command for a pane. |
| `LibTmux.TmuxChaining.ToCommand(LibTmux.RunShellRequest,LibTmux.Server)` | Returns a shell request as one tmux command. |
| `LibTmux.TmuxChaining.ToCommand(LibTmux.SelectLayoutRequest,LibTmux.Window)` | Returns a layout request as one tmux command for a window. |
| `LibTmux.TmuxChaining.ToCommand(LibTmux.SelectPaneRequest,LibTmux.Pane)` | Returns a pane-selection request as one tmux command. |
| `LibTmux.TmuxChaining.ToCommand(LibTmux.SendKeysRequest,LibTmux.Pane)` | Returns a key request as one tmux command for a pane. |
| `LibTmux.TmuxChaining.ToCommand(LibTmux.ServerAccessRequest,LibTmux.Server)` | Returns an access request as one tmux command. |
| `LibTmux.TmuxChaining.ToCommand(LibTmux.SetHookRequest,LibTmux.TmuxHooks)` | Returns a hook request as one tmux command. |
| `LibTmux.TmuxChaining.ToCommand(LibTmux.SetOptionRequest,LibTmux.TmuxOptions)` | Returns an option request as one tmux command. |
| `LibTmux.TmuxChaining.ToCommand(LibTmux.SplitPaneRequest,LibTmux.Pane)` | Returns a split request as one tmux command. |
| `LibTmux.TmuxChaining.ToCommand(LibTmux.SwapPaneRequest,LibTmux.Pane)` | Returns a pane-swap request as one tmux command. |
| `LibTmux.TmuxChaining.ToCommand(LibTmux.UnbindKeyRequest)` | Returns a key-unbinding request as one tmux command. |
| `LibTmux.TmuxChaining.ToCommand(LibTmux.UnsetOptionRequest,LibTmux.TmuxOptions)` | Returns an unset request as one tmux command. |
| `LibTmux.TmuxChaining.ToCommand(LibTmux.WaitForRequest)` | Returns a channel request as one tmux command. |
| `LibTmux.TmuxChaining.ToCommands(LibTmux.SetHooksRequest,LibTmux.TmuxHooks)` | Returns every command a multi-entry hook request sends. |
| `LibTmux.TmuxChaining.ToRunCommand(LibTmux.HookRequest,LibTmux.TmuxHooks)` | Returns running a hook as one tmux command. |
| `LibTmux.TmuxChaining.ToUnsetCommand(LibTmux.HookRequest,LibTmux.TmuxHooks)` | Returns removing a hook as one tmux command. |
| `LibTmux.TmuxCleanupException.#ctor(System.String,System.OperationCanceledException,System.Int32,System.Exception)` | Initializes a cleanup exception. |
| `LibTmux.TmuxCommand.#ctor(System.String,System.Collections.Generic.IReadOnlyList{System.String})` | Initializes a tmux command. |
| `LibTmux.TmuxCommand.Create(System.String,System.String[])` | Creates a command from its name and arguments. |
| `LibTmux.TmuxCommand.ToArguments` | Returns this command the way tmux receives it. |
| `LibTmux.TmuxCommandException.#ctor(System.String,LibTmux.TmuxCommandResult,System.Exception)` | Initializes a command exception. |
| `LibTmux.TmuxCommandNotFoundException.#ctor(System.String,System.String,System.Exception)` | Initializes a command-not-found exception. |
| `LibTmux.TmuxCommandResult.#ctor(System.Collections.Generic.IReadOnlyList{System.String},System.Int32,System.ReadOnlyMemory{System.Byte},System.ReadOnlyMemory{System.Byte},System.Collections.Generic.IReadOnlyList{System.String},System.Collections.Generic.IReadOnlyList{System.String})` | Initializes a command result. |
| `LibTmux.TmuxEnvironment.GetAllAsync(System.Threading.CancellationToken)` | Reads every variable in this environment. |
| `LibTmux.TmuxEnvironment.GetAsync(System.String,System.Threading.CancellationToken)` | Reads one variable. |
| `LibTmux.TmuxEnvironment.RemoveAsync(System.String,System.Threading.CancellationToken)` | Marks a variable removed for the panes tmux spawns. |
| `LibTmux.TmuxEnvironment.SetAsync(System.String,System.String,System.Boolean,System.Boolean,System.Threading.CancellationToken)` | Sets one variable. |
| `LibTmux.TmuxEnvironment.UnsetAsync(System.String,System.Threading.CancellationToken)` | Forgets a variable entirely. |
| `LibTmux.TmuxEnvironmentEntry.#ctor(System.String,System.String,System.Boolean)` | Initializes one environment variable. |
| `LibTmux.TmuxEventsDroppedEvent.#ctor(System.Int64,System.Int64)` | Reports notifications discarded because the bounded event buffer was full. |
| `LibTmux.TmuxExitEvent.#ctor(System.String)` | The control client ended. |
| `LibTmux.TmuxHook.#ctor(System.String,System.Collections.Generic.IReadOnlyList{LibTmux.TmuxHookEntry})` | Initializes one hook. |
| `LibTmux.TmuxHookEntry.#ctor(System.Int32,System.String)` | Initializes one hook entry. |
| `LibTmux.TmuxHooks.GetAllAsync(LibTmux.ListHooksRequest,System.Threading.CancellationToken)` | Reads every hook in the scope. |
| `LibTmux.TmuxHooks.GetAsync(LibTmux.HookRequest,System.Threading.CancellationToken)` | Reads one hook. |
| `LibTmux.TmuxHooks.RunAsync(LibTmux.HookRequest,System.Threading.CancellationToken)` | Runs a hook's commands now, without waiting for it to fire. |
| `LibTmux.TmuxHooks.SetAsync(LibTmux.SetHookRequest,System.Threading.CancellationToken)` | Sets one hook entry. |
| `LibTmux.TmuxHooks.SetAsync(LibTmux.SetHooksRequest,System.Threading.CancellationToken)` | Sets several entries of one hook. |
| `LibTmux.TmuxHooks.UnsetAsync(LibTmux.HookRequest,System.Threading.CancellationToken)` | Removes a hook. |
| `LibTmux.TmuxMenuItem.#ctor(System.String,System.String,System.String)` | Initializes one menu item. |
| `LibTmux.TmuxNotificationEvent.#ctor(System.String,System.Collections.Generic.IReadOnlyList{System.String})` | A notification this library does not parse further. |
| `LibTmux.TmuxObjectNotFoundException.#ctor(System.String,System.String,System.Exception)` | Initializes a missing-object exception. |
| `LibTmux.TmuxOperationCanceledException.#ctor(System.String,System.Threading.CancellationToken,System.Boolean,System.Int32,System.Exception)` | Initializes a tmux cancellation exception. |
| `LibTmux.TmuxOption.#ctor(System.String,LibTmux.TmuxOptionValue,System.Nullable{System.Int32})` | Initializes an option. |
| `LibTmux.TmuxOptionException.#ctor(System.String,System.String,System.Exception)` | Initializes the exception for one rejected option. |
| `LibTmux.TmuxOptionValue.#ctor(System.String,LibTmux.TmuxOptionState,System.Nullable{System.Boolean},System.Nullable{System.Int64})` | Initializes an option value. |
| `LibTmux.TmuxOptions.GetAllAsync(LibTmux.GetOptionsRequest,System.Threading.CancellationToken)` | Reads every option in the scope. |
| `LibTmux.TmuxOptions.GetAsync(LibTmux.GetOptionRequest,System.Threading.CancellationToken)` | Reads one option. |
| `LibTmux.TmuxOptions.SetAsync(LibTmux.SetOptionRequest,System.Threading.CancellationToken)` | Sets one option. |
| `LibTmux.TmuxOptions.UnsetAsync(LibTmux.UnsetOptionRequest,System.Threading.CancellationToken)` | Unsets one option, returning it to what it inherits. |
| `LibTmux.TmuxOutputEvent.#ctor(System.String,System.String)` | Bytes a pane wrote. |
| `LibTmux.TmuxPaneException.#ctor(System.String,LibTmux.PaneId,System.Exception)` | Initializes the exception for one pane. |
| `LibTmux.TmuxSessionExistsException.#ctor(System.String,System.String,System.Exception)` | Initializes the exception for one taken session name. |
| `LibTmux.TmuxTransportException.#ctor(System.String,System.Collections.Generic.IReadOnlyList{System.String},LibTmux.TmuxDispatchState,System.Exception)` | Initializes a transport exception that knows whether tmux was started. |
| `LibTmux.TmuxTransportException.#ctor(System.String,System.Collections.Generic.IReadOnlyList{System.String},System.Exception)` | Initializes a transport exception whose dispatch state is unknown. |
| `LibTmux.TmuxVersion.#ctor(System.String)` | Initializes a tmux version. |
| `LibTmux.TmuxVersion.CheckMinimumSupportedVersionAsync(System.Boolean,System.String,System.Threading.CancellationToken)` | Checks the package minimum and optionally throws. |
| `LibTmux.TmuxVersion.CompareTo(LibTmux.TmuxVersion)` | Compares parsed tmux versions. |
| `LibTmux.TmuxVersion.DetectAsync(System.String,System.Threading.CancellationToken)` | Detects the selected tmux executable version. |
| `LibTmux.TmuxVersion.DetectStringAsync(System.String,System.Threading.CancellationToken)` | Detects the selected tmux executable version string. |
| `LibTmux.TmuxVersion.EnsureAtLeast(LibTmux.TmuxVersion)` | Throws when this version is below a minimum. |
| `LibTmux.TmuxVersion.EnsureMinimumSupportedVersionAsync(System.String,System.Threading.CancellationToken)` | Throws when installed tmux is below the package minimum. |
| `LibTmux.TmuxVersion.IsAtLeast(LibTmux.TmuxVersion)` | Reports whether this version meets a minimum. |
| `LibTmux.TmuxVersion.IsInstalledAtLeastAsync(LibTmux.TmuxVersion,System.String,System.Threading.CancellationToken)` | Checks whether installed tmux meets a minimum. |
| `LibTmux.TmuxVersion.IsInstalledAtMostAsync(LibTmux.TmuxVersion,System.String,System.Threading.CancellationToken)` | Checks whether installed tmux is at most a maximum. |
| `LibTmux.TmuxVersion.IsInstalledNewerThanAsync(LibTmux.TmuxVersion,System.String,System.Threading.CancellationToken)` | Checks whether installed tmux is newer. |
| `LibTmux.TmuxVersion.IsInstalledOlderThanAsync(LibTmux.TmuxVersion,System.String,System.Threading.CancellationToken)` | Checks whether installed tmux is older. |
| `LibTmux.TmuxVersion.IsInstalledVersionAsync(LibTmux.TmuxVersion,System.String,System.Threading.CancellationToken)` | Checks exact installed version equality. |
| `LibTmux.TmuxVersion.IsMinimumSupportedVersionInstalledAsync(System.String,System.Threading.CancellationToken)` | Reports whether installed tmux meets the package minimum. |
| `LibTmux.TmuxVersion.Parse(System.String)` | Parses a tmux version string. |
| `LibTmux.TmuxVersion.TryParse(System.String,LibTmux.TmuxVersion@)` | Tries to parse a tmux version string. |
| `LibTmux.TmuxVersion.op_GreaterThan(LibTmux.TmuxVersion,LibTmux.TmuxVersion)` | Reports whether the left version is newer. |
| `LibTmux.TmuxVersion.op_GreaterThanOrEqual(LibTmux.TmuxVersion,LibTmux.TmuxVersion)` | Reports whether the left version is at least the right version. |
| `LibTmux.TmuxVersion.op_LessThan(LibTmux.TmuxVersion,LibTmux.TmuxVersion)` | Reports whether the left version is older. |
| `LibTmux.TmuxVersion.op_LessThanOrEqual(LibTmux.TmuxVersion,LibTmux.TmuxVersion)` | Reports whether the left version is at most the right version. |
| `LibTmux.TmuxVersionTooLowException.#ctor(System.String,LibTmux.TmuxVersion,LibTmux.TmuxVersion,System.Exception)` | Initializes an unsupported-version exception. |
| `LibTmux.TmuxWaitChannel.DisposeAsync` | Withdraws the waiter from tmux. |
| `LibTmux.TmuxWaitChannel.WaitAsync(System.TimeSpan,System.Threading.CancellationToken)` | Waits for the signal, giving this attempt a budget. |
| `LibTmux.TmuxWaitTimeoutException.#ctor(System.String,System.TimeSpan,System.Exception)` | Initializes a wait-timeout exception. |
| `LibTmux.TmuxWindowException.#ctor(System.String,LibTmux.WindowId,System.Exception)` | Initializes the exception for one window. |
| `LibTmux.UnbindKeyRequest.#ctor(System.String,System.String,System.Boolean,System.Boolean)` | Initializes a request to remove a binding. |
| `LibTmux.UnsafeTmuxFilter.#ctor(System.String)` | A tmux filter expression passed through without translation. |
| `LibTmux.UnsetOptionRequest.#ctor(System.String,System.Nullable{LibTmux.OptionScope},System.Boolean,System.Boolean,System.Boolean)` | Initializes a request to unset one option. |
| `LibTmux.UnsupportedQueryExpressionException.#ctor(System.String)` | Initializes the exception for one untranslatable expression. |
| `LibTmux.UnsupportedQueryExpressionException.#ctor(System.String,System.String,System.Exception)` | Initializes the exception naming the expression it refused. |
| `LibTmux.WaitForRequest.#ctor(System.String,LibTmux.TmuxWaitMode)` | Initializes a channel request. |
| `LibTmux.Window.CreatePaneAsync(LibTmux.NewPaneRequest,System.Threading.CancellationToken)` | Creates a floating pane in this window. |
| `LibTmux.Window.CreateWindowAsync(LibTmux.NewWindowRequest,System.Threading.CancellationToken)` | Creates a window next to this one. |
| `LibTmux.Window.DisplayMessageAsync(LibTmux.DisplayMessageRequest,System.Threading.CancellationToken)` | Shows a message on the client viewing this window. |
| `LibTmux.Window.ExecuteCommandAsync(System.Collections.Generic.IReadOnlyList{System.String},System.String,System.Threading.CancellationToken)` | Executes one raw tmux command against this window. |
| `LibTmux.Window.FromEnvironmentAsync(System.Collections.Generic.IReadOnlyDictionary{System.String,System.String},System.Threading.CancellationToken)` | Returns the window holding the pane this process runs in. |
| `LibTmux.Window.GetLinkedSessionsAsync(System.Threading.CancellationToken)` | Reads every session this window is linked into. |
| `LibTmux.Window.GetPaneAsync(System.String,System.Threading.CancellationToken)` | Reads one pane in this window. |
| `LibTmux.Window.GetPanesAsync(System.Threading.CancellationToken)` | Reads this window's panes from tmux. |
| `LibTmux.Window.KillAsync(System.Boolean,System.Threading.CancellationToken)` | Stops this window. |
| `LibTmux.Window.LinkAsync(LibTmux.LinkWindowRequest,System.Threading.CancellationToken)` | Links this window into another session. |
| `LibTmux.Window.MoveAsync(LibTmux.MoveWindowRequest,System.Threading.CancellationToken)` | Moves this window to another index or session. |
| `LibTmux.Window.RefreshAsync(System.Threading.CancellationToken)` | Re-reads this window from tmux. |
| `LibTmux.Window.RenameAsync(System.String,System.Threading.CancellationToken)` | Renames this window. |
| `LibTmux.Window.ResizeAsync(LibTmux.ResizeWindowRequest,System.Threading.CancellationToken)` | Resizes this window. |
| `LibTmux.Window.RespawnAsync(LibTmux.RespawnRequest,System.Threading.CancellationToken)` | Restarts the command running in this window. |
| `LibTmux.Window.RotateAsync(System.Nullable{LibTmux.WindowRotationDirection},System.Boolean,System.Threading.CancellationToken)` | Rotates the panes in this window. |
| `LibTmux.Window.SearchPanesAsync(LibTmux.UnsafeTmuxFilter,System.Threading.CancellationToken)` | Runs a tmux-side filter over this window's panes. |
| `LibTmux.Window.SelectAsync(System.Threading.CancellationToken)` | Selects this window in its session. |
| `LibTmux.Window.SelectLastPaneAsync(System.Nullable{LibTmux.PaneInputMode},System.Boolean,System.Threading.CancellationToken)` | Selects the pane that was last active. |
| `LibTmux.Window.SelectLayoutAsync(LibTmux.SelectLayoutRequest,System.Threading.CancellationToken)` | Applies a layout to this window. |
| `LibTmux.Window.SelectNextLayoutAsync(System.Threading.CancellationToken)` | Moves to the next layout. |
| `LibTmux.Window.SelectPaneAsync(System.String,System.Threading.CancellationToken)` | Selects a pane in this window. |
| `LibTmux.Window.SelectPreviousLayoutAsync(System.Threading.CancellationToken)` | Moves to the previous layout. |
| `LibTmux.Window.SplitPaneAsync(LibTmux.SplitPaneRequest,System.Threading.CancellationToken)` | Splits a pane in this window. |
| `LibTmux.Window.SwapAsync(LibTmux.WindowId,System.Boolean,System.Threading.CancellationToken)` | Swaps this window with another. |
| `LibTmux.Window.UnlinkAsync(System.Boolean,System.Threading.CancellationToken)` | Removes this window's link to the session it was read through. |
| `LibTmux.WindowEntityKey.#ctor(LibTmux.SessionId,LibTmux.WindowId)` | Identifies one window as it appears inside one session. |
| `LibTmux.WindowId.#ctor(System.Int32)` | Initializes a window identifier. |
| `LibTmux.WindowId.Parse(System.String)` | Parses a prefixed window identifier. |
| `LibTmux.WindowId.ToString` | Returns the canonical prefixed identifier. |
| `LibTmux.WindowId.TryParse(System.String,LibTmux.WindowId@)` | Tries to parse a prefixed window identifier. |

## Properties

| Member | Summary |
|---|---|
| `LibTmux.AttachSessionRequest.ClientFlags` | Gets the client flags sent with -f. |
| `LibTmux.AttachSessionRequest.DetachOthers` | Gets whether other clients are detached. |
| `LibTmux.AttachSessionRequest.ExitOnDetach` | Gets whether the client exits when the session is destroyed. |
| `LibTmux.AttachSessionRequest.ReadOnly` | Gets whether the client attaches read-only. |
| `LibTmux.AttachSessionRequest.Target` | Gets the session to attach, or null for the caller's own. |
| `LibTmux.BindKeyRequest.Command` | Gets the tmux command and its arguments. |
| `LibTmux.BindKeyRequest.Key` | Gets the key to bind. |
| `LibTmux.BindKeyRequest.KeyTable` | Gets the key table, or null for the prefix table. |
| `LibTmux.BindKeyRequest.Note` | Gets the note describing the binding. |
| `LibTmux.BindKeyRequest.Repeat` | Gets whether the key may repeat without the prefix. |
| `LibTmux.CapturePanePosition.BeginningOfHistory` | Gets the oldest line tmux still holds. |
| `LibTmux.CapturePanePosition.EndOfVisiblePane` | Gets the last line of the visible pane. |
| `LibTmux.CapturePanePosition.LineNumber` | Gets the line, or null for the extreme tmux writes as -. |
| `LibTmux.CapturePaneRequest.AlternateScreen` | Gets whether the alternate screen is captured. |
| `LibTmux.CapturePaneRequest.EndLine` | Gets the last line to capture. |
| `LibTmux.CapturePaneRequest.EscapeNonPrintable` | Gets whether unprintable bytes are escaped as octal. |
| `LibTmux.CapturePaneRequest.EscapeSequences` | Gets whether escape sequences are preserved. |
| `LibTmux.CapturePaneRequest.Hyperlinks` | Gets whether hyperlinks are captured. |
| `LibTmux.CapturePaneRequest.JoinWrappedLines` | Gets whether wrapped lines are joined. |
| `LibTmux.CapturePaneRequest.LineFlags` | Gets whether each line carries its flags. |
| `LibTmux.CapturePaneRequest.LineNumbers` | Gets whether each line carries its number. |
| `LibTmux.CapturePaneRequest.ModeScreen` | Gets whether the pane's mode screen is captured. |
| `LibTmux.CapturePaneRequest.Pending` | Gets whether pending output is captured. |
| `LibTmux.CapturePaneRequest.PreserveTrailingSpaces` | Gets whether trailing spaces are kept. |
| `LibTmux.CapturePaneRequest.Quiet` | Gets whether a missing alternate screen is not an error. |
| `LibTmux.CapturePaneRequest.StartLine` | Gets the first line to capture. |
| `LibTmux.CapturePaneRequest.TrimTrailingSpaces` | Gets whether trailing spaces are removed. |
| ``LibTmux.CapturedRelation`1.IsCaptured`` | Gets whether the snapshot read this relation. |
| `LibTmux.ChooseTreeRequest.Format` | Gets the format each row renders with. |
| `LibTmux.ChooseTreeRequest.NativeFilter` | Gets the raw tmux filter limiting the rows. |
| `LibTmux.ChooseTreeRequest.Reverse` | Gets whether the order is reversed. |
| `LibTmux.ChooseTreeRequest.SessionsCollapsed` | Gets whether sessions start collapsed. |
| `LibTmux.ChooseTreeRequest.Sort` | Gets how the rows are ordered. |
| `LibTmux.ChooseTreeRequest.WindowsCollapsed` | Gets whether windows start collapsed. |
| `LibTmux.ChooseTreeRequest.Zoom` | Gets whether the chooser pane is zoomed. |
| `LibTmux.Client.AttachedSessionId` | Gets the session the client was attached to when it was read. |
| `LibTmux.Client.Generation` | Gets the server generation captured with this client. |
| `LibTmux.Client.IsControlClient` | Gets whether the client speaks tmux's control protocol. |
| `LibTmux.Client.Name` | Gets the client name tmux knows it by. |
| `LibTmux.Client.RawFormatFields` | Gets the tmux fields captured when this handle materialized. |
| `LibTmux.Client.Server` | Gets the server that owns this client. |
| `LibTmux.Client.Tty` | Gets the terminal the client is on, when it has one. |
| `LibTmux.ClientAttachment.Pane` | The pane that window has active. |
| `LibTmux.ClientAttachment.Session` | The session the client is attached to. |
| `LibTmux.ClientAttachment.Window` | The window that session is showing. |
| `LibTmux.CommandPromptRequest.BackspaceExits` | Gets whether backspace on an empty prompt closes it. |
| `LibTmux.CommandPromptRequest.ExpandFormat` | Gets whether the template is expanded as a format. |
| `LibTmux.CommandPromptRequest.Inputs` | Gets the answer the prompt starts with. |
| `LibTmux.CommandPromptRequest.KeyOnly` | Gets whether the answer is the key itself rather than text. |
| `LibTmux.CommandPromptRequest.Literal` | Gets whether the answer is taken literally. |
| `LibTmux.CommandPromptRequest.NoFreeze` | Gets whether the client keeps redrawing while prompting. |
| `LibTmux.CommandPromptRequest.Numeric` | Gets whether only digits are accepted. |
| `LibTmux.CommandPromptRequest.OnInputChange` | Gets whether the command runs on every keystroke. |
| `LibTmux.CommandPromptRequest.OneKey` | Gets whether one keypress answers it. |
| `LibTmux.CommandPromptRequest.Prompt` | Gets the text shown to the person answering. |
| `LibTmux.CommandPromptRequest.TargetClient` | Gets the client to prompt, or null for the caller's own. |
| `LibTmux.CommandPromptRequest.Template` | Gets the command to run, with the answer substituted in. |
| `LibTmux.CommandPromptRequest.Type` | Gets what the prompt is asking for. |
| `LibTmux.ConfirmBeforeRequest.Command` | Gets the tmux command to run once confirmed. |
| `LibTmux.ConfirmBeforeRequest.ConfirmKey` | Gets the key that confirms, or null for tmux's default. |
| `LibTmux.ConfirmBeforeRequest.DefaultYes` | Gets whether pressing enter confirms rather than cancels. |
| `LibTmux.ConfirmBeforeRequest.Prompt` | Gets the question shown, or null for tmux's own wording. |
| `LibTmux.ConfirmBeforeRequest.TargetClient` | Gets the client to ask, or null for the caller's own. |
| `LibTmux.ControlModeCommandException.Command` | Gets the command tmux rejected. |
| `LibTmux.ControlModeCommandException.ErrorLines` | Gets the error lines tmux reported. |
| `LibTmux.ControlModeCommandException.OutputLines` | Gets output produced before tmux rejected the command. |
| `LibTmux.CopyModeRequest.Cancel` | Gets whether copy mode is left instead of entered. |
| `LibTmux.CopyModeRequest.ExitOnBottom` | Gets whether reaching the bottom leaves copy mode. |
| `LibTmux.CopyModeRequest.MouseDrag` | Gets whether the mode is entered for a mouse drag. |
| `LibTmux.CopyModeRequest.PageDown` | Gets whether the pane scrolls down one page on entry. |
| `LibTmux.CopyModeRequest.ScrollUp` | Gets whether the pane scrolls up one page on entry. |
| `LibTmux.CopyModeRequest.SourcePane` | Gets the pane whose content is shown instead. |
| `LibTmux.DisplayMenuRequest.BorderLines` | Gets which line style draws the border. |
| `LibTmux.DisplayMenuRequest.BorderStyle` | Gets the style of its border. |
| `LibTmux.DisplayMenuRequest.Items` | Gets the lines the menu offers. |
| `LibTmux.DisplayMenuRequest.Mouse` | Gets whether the mouse can choose an item. |
| `LibTmux.DisplayMenuRequest.SelectedStyle` | Gets the style of the selected line. |
| `LibTmux.DisplayMenuRequest.StartingChoice` | Gets the item selected when it opens. |
| `LibTmux.DisplayMenuRequest.StayOpen` | Gets whether the menu stays open after a choice. |
| `LibTmux.DisplayMenuRequest.Style` | Gets the style of the menu itself. |
| `LibTmux.DisplayMenuRequest.TargetClient` | Gets the client shown the menu. |
| `LibTmux.DisplayMenuRequest.TargetPane` | Gets the pane the menu belongs to. |
| `LibTmux.DisplayMenuRequest.Title` | Gets the title shown above them. |
| `LibTmux.DisplayMenuRequest.X` | Gets where the menu sits across the screen. |
| `LibTmux.DisplayMenuRequest.Y` | Gets where the menu sits down the screen. |
| `LibTmux.DisplayMessageRequest.AllFormats` | Gets whether every format variable is listed. |
| `LibTmux.DisplayMessageRequest.Delay` | Gets how long the message stays up. |
| `LibTmux.DisplayMessageRequest.Format` | Gets the format string used in place of the message. |
| `LibTmux.DisplayMessageRequest.Message` | Gets the message, which tmux expands as a format. |
| `LibTmux.DisplayMessageRequest.NoExpand` | Gets whether the message is sent without format expansion. |
| `LibTmux.DisplayMessageRequest.Notify` | Gets whether the message is delivered as a notification. |
| `LibTmux.DisplayMessageRequest.ReturnText` | Gets whether the message is printed rather than shown. |
| `LibTmux.DisplayMessageRequest.TargetClient` | Gets the client to show the message on. |
| `LibTmux.DisplayMessageRequest.UpdatePane` | Gets whether the pane is redrawn while the message is shown. |
| `LibTmux.DisplayMessageRequest.Verbose` | Gets whether format expansion is reported. |
| `LibTmux.DisplayPopupRequest.BorderLines` | Gets the border line style. |
| `LibTmux.DisplayPopupRequest.BorderStyle` | Gets the popup border style. |
| `LibTmux.DisplayPopupRequest.CloseExisting` | Gets whether an open popup is closed instead. |
| `LibTmux.DisplayPopupRequest.CloseMode` | Gets when the popup closes on its own. |
| `LibTmux.DisplayPopupRequest.CloseOnAnyKey` | Gets whether any key closes the popup. |
| `LibTmux.DisplayPopupRequest.Command` | Gets the command the popup runs. |
| `LibTmux.DisplayPopupRequest.Environment` | Gets the environment entries set on the command. |
| `LibTmux.DisplayPopupRequest.Height` | Gets the popup height. |
| `LibTmux.DisplayPopupRequest.NoBorder` | Gets whether the popup has no border. |
| `LibTmux.DisplayPopupRequest.NoKeys` | Gets whether the popup ignores keys. |
| `LibTmux.DisplayPopupRequest.StartDirectory` | Gets the working directory for the command. |
| `LibTmux.DisplayPopupRequest.Style` | Gets the popup style. |
| `LibTmux.DisplayPopupRequest.TargetClient` | Gets the client to show the popup on. |
| `LibTmux.DisplayPopupRequest.Title` | Gets the popup title. |
| `LibTmux.DisplayPopupRequest.Width` | Gets the popup width. |
| `LibTmux.DisplayPopupRequest.X` | Gets the column to place the popup at. |
| `LibTmux.DisplayPopupRequest.Y` | Gets the row to place the popup at. |
| `LibTmux.FindWindowRequest.IgnoreCase` | Gets whether the search ignores case. |
| `LibTmux.FindWindowRequest.MatchContent` | Gets whether pane content is searched. |
| `LibTmux.FindWindowRequest.MatchName` | Gets whether window names are searched. |
| `LibTmux.FindWindowRequest.MatchTitle` | Gets whether pane titles are searched. |
| `LibTmux.FindWindowRequest.Pattern` | Gets the text to look for. |
| `LibTmux.FindWindowRequest.Regex` | Gets whether the pattern is a regular expression. |
| `LibTmux.GetOptionRequest.Global` | Gets whether the global table is read instead of the local one. |
| `LibTmux.GetOptionRequest.IncludeHooks` | Gets whether hooks are listed alongside options. |
| `LibTmux.GetOptionRequest.IncludeInherited` | Gets whether values inherited from a parent scope are included. |
| `LibTmux.GetOptionRequest.Name` | Gets the option to read. |
| `LibTmux.GetOptionRequest.Quiet` | Gets whether a missing option is answered with nothing instead of an error. |
| `LibTmux.GetOptionRequest.Scope` | Gets the scope to read in, or null for the owner's own. |
| `LibTmux.GetOptionsRequest.Global` | Gets whether the global table is read instead of the local one. |
| `LibTmux.GetOptionsRequest.IncludeHooks` | Gets whether hooks are listed alongside options. |
| `LibTmux.GetOptionsRequest.IncludeInherited` | Gets whether values inherited from a parent scope are included. |
| `LibTmux.GetOptionsRequest.Quiet` | Gets whether an empty table is answered with nothing instead of an error. |
| `LibTmux.GetOptionsRequest.Scope` | Gets the scope to read in, or null for the owner's own. |
| `LibTmux.HookRequest.Global` | Gets whether the global table is used instead of the local one. |
| `LibTmux.HookRequest.Name` | Gets the hook name. |
| `LibTmux.HookRequest.Scope` | Gets the scope to reach it in, or null for the owner's own. |
| `LibTmux.IControlModeSession.Events` | Reads what tmux reports for as long as the client runs. |
| `LibTmux.IControlModeSession.IsRunning` | Gets whether the client is still running. |
| `LibTmux.IfShellRequest.Background` | Gets whether tmux runs the shell command without waiting. |
| `LibTmux.IfShellRequest.ElseCommand` | Gets the tmux command run when it fails, when any. |
| `LibTmux.IfShellRequest.ShellCommand` | Gets the shell command whose success decides. |
| `LibTmux.IfShellRequest.TargetPane` | Gets the pane the commands run against. |
| `LibTmux.IfShellRequest.ThenCommand` | Gets the tmux command run when it succeeds. |
| `LibTmux.IncompleteSnapshotException.CapturedDepth` | Gets the depth the snapshot actually reached. |
| `LibTmux.IncompleteSnapshotException.Relation` | Gets the relation the caller asked for. |
| `LibTmux.LibTmuxException.Dispatch` | Gets whether the command reached tmux, and so whether a retry is safe. |
| `LibTmux.LibTmuxInfo.MaximumTestedTmuxVersion` | Gets the highest required tested tmux version. |
| `LibTmux.LibTmuxInfo.MinimumTmuxVersion` | Gets the minimum supported tmux version. |
| `LibTmux.LibTmuxInfo.Version` | Gets the library assembly version. |
| `LibTmux.LinkWindowRequest.Detach` | Gets whether the linked window is left unselected. |
| `LibTmux.LinkWindowRequest.Direction` | Gets whether to insert before or after the target. |
| `LibTmux.LinkWindowRequest.ReplaceExisting` | Gets whether a window already at the index is replaced. |
| `LibTmux.LinkWindowRequest.TargetIndex` | Gets the index to link at, or null for the next free one. |
| `LibTmux.LinkWindowRequest.TargetSession` | Gets the session the window is linked into. |
| `LibTmux.ListBuffersRequest.Filter` | Gets the tmux filter expression, kept as written. |
| `LibTmux.ListBuffersRequest.Format` | Gets the tmux format each buffer is rendered with. |
| `LibTmux.ListHooksRequest.Global` | Gets whether the global table is read instead of the local one. |
| `LibTmux.ListHooksRequest.Scope` | Gets the scope to read, or null for the owner's own. |
| `LibTmux.MovePaneRequest.Before` | Gets whether the pane lands before the target. |
| `LibTmux.MovePaneRequest.Detach` | Gets whether the moved pane is left unselected. |
| `LibTmux.MovePaneRequest.Direction` | Gets which side of the target the pane lands on. |
| `LibTmux.MovePaneRequest.FullWindow` | Gets whether the split spans the whole window. |
| `LibTmux.MovePaneRequest.Size` | Gets the size in cells or as a percentage. |
| `LibTmux.MovePaneRequest.Target` | Gets the pane or window to move against. |
| `LibTmux.MoveWindowRequest.Destination` | Gets the window part of the target, empty for the next free index. |
| `LibTmux.MoveWindowRequest.Direction` | Gets whether to insert before or after the destination. |
| `LibTmux.MoveWindowRequest.NoSelect` | Gets whether the moved window is left unselected. |
| `LibTmux.MoveWindowRequest.Renumber` | Gets whether the destination session's windows are renumbered. |
| `LibTmux.MoveWindowRequest.ReplaceExisting` | Gets whether a window already at the index is replaced. |
| `LibTmux.MoveWindowRequest.Session` | Gets the destination session, or null for the window's own. |
| `LibTmux.NewPaneRequest.ActiveBorderStyle` | Gets the border style while the pane is active. |
| `LibTmux.NewPaneRequest.Attach` | Gets whether the new pane becomes active. |
| `LibTmux.NewPaneRequest.Command` | Gets the command the new pane runs. |
| `LibTmux.NewPaneRequest.Empty` | Gets whether the pane starts with no command. |
| `LibTmux.NewPaneRequest.Environment` | Gets the environment entries set on the new pane. |
| `LibTmux.NewPaneRequest.Height` | Gets the pane height in cells. |
| `LibTmux.NewPaneRequest.InactiveBorderStyle` | Gets the border style while the pane is not active. |
| `LibTmux.NewPaneRequest.KeepOpen` | Gets whether the pane stays after its command exits. |
| `LibTmux.NewPaneRequest.Message` | Gets the message shown in the pane. |
| `LibTmux.NewPaneRequest.StartDirectory` | Gets the working directory for the new pane. |
| `LibTmux.NewPaneRequest.Style` | Gets the pane style. |
| `LibTmux.NewPaneRequest.Target` | Gets the window or pane to place against. |
| `LibTmux.NewPaneRequest.Width` | Gets the pane width in cells. |
| `LibTmux.NewPaneRequest.X` | Gets the column to place the pane at. |
| `LibTmux.NewPaneRequest.Y` | Gets the row to place the pane at. |
| `LibTmux.NewPaneRequest.Zoom` | Gets whether the new pane is zoomed. |
| `LibTmux.NewSessionRequest.Attach` | Gets whether the new session is attached rather than detached. |
| `LibTmux.NewSessionRequest.ClientFlags` | Gets the comma-separated client flags passed with -f. |
| `LibTmux.NewSessionRequest.Command` | Gets the command the first pane runs. |
| `LibTmux.NewSessionRequest.DetachOthers` | Gets whether other clients are detached on attach. |
| `LibTmux.NewSessionRequest.Environment` | Gets the environment entries set on the session. |
| `LibTmux.NewSessionRequest.Height` | Gets the requested height. |
| `LibTmux.NewSessionRequest.Name` | Gets the session name, or null to let tmux choose. |
| `LibTmux.NewSessionRequest.NoSize` | Gets whether tmux may ignore the requested size. |
| `LibTmux.NewSessionRequest.ReplaceExisting` | Gets whether a session of the same name is removed first. |
| `LibTmux.NewSessionRequest.StartDirectory` | Gets the working directory for the first pane. |
| `LibTmux.NewSessionRequest.Width` | Gets the requested width. |
| `LibTmux.NewSessionRequest.WindowName` | Gets the name of the first window. |
| `LibTmux.NewWindowRequest.Attach` | Gets whether the new window becomes current. |
| `LibTmux.NewWindowRequest.Command` | Gets the command the first pane runs. |
| `LibTmux.NewWindowRequest.Direction` | Gets whether to insert before or after the target. |
| `LibTmux.NewWindowRequest.Environment` | Gets the environment entries set on the window. |
| `LibTmux.NewWindowRequest.Index` | Gets the window index to create at. |
| `LibTmux.NewWindowRequest.KillExisting` | Gets whether an existing window at the index is replaced. |
| `LibTmux.NewWindowRequest.Name` | Gets the window name. |
| `LibTmux.NewWindowRequest.SelectExisting` | Gets whether an existing window is selected instead. |
| `LibTmux.NewWindowRequest.StartDirectory` | Gets the working directory for the first pane. |
| `LibTmux.NewWindowRequest.TargetWindow` | Gets the window to insert relative to. |
| `LibTmux.OwnedServerScope.Value` | Gets the owned server. |
| `LibTmux.OwnedSessionScope.Value` | Gets the owned session. |
| `LibTmux.OwnedWindowScope.Value` | Gets the owned window. |
| `LibTmux.Pane.AtBottom` | Gets whether the pane touches the bottom of its window. |
| `LibTmux.Pane.AtLeft` | Gets whether the pane touches the left of its window. |
| `LibTmux.Pane.AtRight` | Gets whether the pane touches the right of its window. |
| `LibTmux.Pane.AtTop` | Gets whether the pane touches the top of its window. |
| `LibTmux.Pane.Generation` | Gets the server generation captured with this pane. |
| `LibTmux.Pane.Height` | Gets the pane height captured with this handle. |
| `LibTmux.Pane.Hooks` | Gets the hooks of this pane. |
| `LibTmux.Pane.Id` | Gets the pane identifier. |
| `LibTmux.Pane.Index` | Gets the index this pane holds in its window. |
| `LibTmux.Pane.Options` | Gets the options of this pane. |
| `LibTmux.Pane.RawFormatFields` | Gets the tmux fields captured when this handle materialized. |
| `LibTmux.Pane.Server` | Gets the server that owns this pane. |
| `LibTmux.Pane.Session` | Gets the session containing this pane. |
| `LibTmux.Pane.Title` | Gets the pane title captured with this handle. |
| `LibTmux.Pane.Width` | Gets the pane width captured with this handle. |
| `LibTmux.Pane.Window` | Gets the window containing this pane. |
| `LibTmux.PaneId.Value` | Gets the nonnegative numeric value. |
| `LibTmux.PasteBufferRequest.Bracketed` | Gets whether the paste is bracketed. |
| `LibTmux.PasteBufferRequest.DeleteAfter` | Gets whether the buffer is deleted once pasted. |
| `LibTmux.PasteBufferRequest.Name` | Gets the buffer to paste, or null for the most recent. |
| `LibTmux.PasteBufferRequest.RawBytes` | Gets whether the bytes are pasted without translation. |
| `LibTmux.PasteBufferRequest.Separator` | Gets the separator used between lines. |
| `LibTmux.PasteBufferRequest.UseLineFeedSeparator` | Gets whether line feeds separate the lines. |
| `LibTmux.PipePaneRequest.Command` | Gets the command to pipe through, or null to stop piping. |
| `LibTmux.PipePaneRequest.InputOnly` | Gets whether only pane input is piped. |
| `LibTmux.PipePaneRequest.OutputOnly` | Gets whether only pane output is piped. |
| `LibTmux.PipePaneRequest.Toggle` | Gets whether an identical existing pipe is stopped instead. |
| `LibTmux.PsmuxCaptureOptions.EndLine` | Gets the last line to capture. |
| `LibTmux.PsmuxCaptureOptions.EscapeSequences` | Gets whether terminal escape sequences are preserved. |
| `LibTmux.PsmuxCaptureOptions.JoinWrappedLines` | Gets whether wrapped screen rows are joined. |
| `LibTmux.PsmuxCaptureOptions.StartLine` | Gets the first line to capture. |
| `LibTmux.PsmuxConnectionOptions.DataDirectory` | Gets the canonical isolated data-directory path on a fixed local Windows drive. |
| `LibTmux.PsmuxConnectionOptions.ExecutablePath` | Gets the local absolute psmux client executable path. |
| `LibTmux.PsmuxConnectionOptions.ExpectedBinarySha256` | Gets the expected executable SHA-256 in lowercase hexadecimal. |
| `LibTmux.PsmuxConnectionOptions.Logger` | Gets the optional connection logger. |
| `LibTmux.PsmuxConnectionOptions.NamespaceName` | Gets the explicit non-default psmux namespace. |
| `LibTmux.PsmuxPane.Height` | Gets the captured height in rows. |
| `LibTmux.PsmuxPane.Id` | Gets the captured pane identifier. |
| `LibTmux.PsmuxPane.Index` | Gets the captured pane index. |
| `LibTmux.PsmuxPane.Server` | Gets the psmux endpoint that produced this observation. |
| `LibTmux.PsmuxPane.SessionId` | Gets the captured parent session identifier. |
| `LibTmux.PsmuxPane.Title` | Gets the captured pane title. |
| `LibTmux.PsmuxPane.Width` | Gets the captured width in columns. |
| `LibTmux.PsmuxPane.WindowId` | Gets the captured parent window identifier. |
| `LibTmux.PsmuxServer.ConnectionOptions` | Gets the connection settings used for this observation. |
| `LibTmux.PsmuxServer.Version` | Gets the psmux compatibility version reported at connection time. |
| `LibTmux.PsmuxSession.Attached` | Gets whether a client was attached when the session was read. |
| `LibTmux.PsmuxSession.Id` | Gets the captured session identifier. |
| `LibTmux.PsmuxSession.Name` | Gets the captured session name. |
| `LibTmux.PsmuxSession.Server` | Gets the psmux endpoint that produced this observation. |
| `LibTmux.PsmuxWindow.Height` | Gets the captured height in rows. |
| `LibTmux.PsmuxWindow.Id` | Gets the captured window identifier. |
| `LibTmux.PsmuxWindow.Index` | Gets the captured window index. |
| `LibTmux.PsmuxWindow.Name` | Gets the captured window name. |
| `LibTmux.PsmuxWindow.Server` | Gets the psmux endpoint that produced this observation. |
| `LibTmux.PsmuxWindow.SessionId` | Gets the captured parent session identifier. |
| `LibTmux.PsmuxWindow.Width` | Gets the captured width in columns. |
| `LibTmux.Query.AndNode.Operands` | Gets the ordered operands. |
| `LibTmux.Query.BooleanConstant.Value` | The literal value. |
| `LibTmux.Query.ComparisonNode.Left` | The left operand. |
| `LibTmux.Query.ComparisonNode.Operator` | The comparison. |
| `LibTmux.Query.ComparisonNode.Right` | The right operand. |
| `LibTmux.Query.ConstantNode.Value` | The literal. |
| `LibTmux.Query.FieldNode.Target` | The object that owns the field. |
| `LibTmux.Query.FieldNode.WireName` | The tmux format token name. |
| `LibTmux.Query.Int64Constant.Value` | The literal value. |
| `LibTmux.Query.NotNode.Operand` | The negated predicate. |
| `LibTmux.Query.OrNode.Operands` | Gets the ordered operands. |
| `LibTmux.Query.QuantifierNode.Predicate` | The predicate applied to each child. |
| `LibTmux.Query.QuantifierNode.Quantifier` | How the relation is folded. |
| `LibTmux.Query.QuantifierNode.Relation` | The relation field to fold. |
| `LibTmux.Query.QueryDocument.Predicate` | The translated predicate. |
| `LibTmux.Query.QueryDocument.RequiredSnapshotDepth` | Gets the snapshot depth this predicate needs to evaluate. |
| `LibTmux.Query.QueryDocument.Schema` | The wire schema identifier. |
| `LibTmux.Query.QueryDocument.Target` | The object the predicate selects. |
| `LibTmux.Query.QueryDocument.Version` | The wire schema version. |
| `LibTmux.Query.RegexNode.Dialect` | The regex dialect the pattern is written in. |
| `LibTmux.Query.RegexNode.Input` | The operand to match. |
| `LibTmux.Query.RegexNode.Pattern` | The constant pattern. |
| `LibTmux.Query.RegexNode.SemanticOptions` | Options that change what the pattern means. |
| `LibTmux.Query.StringConstant.Value` | The literal value. |
| `LibTmux.Query.StringNode.Left` | The left operand. |
| `LibTmux.Query.StringNode.Operator` | The string operation. |
| `LibTmux.Query.StringNode.Right` | The right operand. |
| `LibTmux.Query.TypedIdConstant.Target` | The object the identifier names. |
| `LibTmux.Query.TypedIdConstant.Value` | The identifier text. |
| `LibTmux.ResizePaneRequest.Adjustment` | Gets how many cells to move the edge by. |
| `LibTmux.ResizePaneRequest.Direction` | Gets the edge to move. |
| `LibTmux.ResizePaneRequest.Height` | Gets the explicit height in cells or as a percentage. |
| `LibTmux.ResizePaneRequest.Mouse` | Gets whether the resize follows the mouse. |
| `LibTmux.ResizePaneRequest.TrimBelow` | Gets whether lines below the cursor are trimmed. |
| `LibTmux.ResizePaneRequest.Width` | Gets the explicit width in cells or as a percentage. |
| `LibTmux.ResizePaneRequest.Zoom` | Gets whether the pane's zoom is toggled. |
| `LibTmux.ResizeWindowRequest.Adjustment` | Gets how many cells to move the edge by. |
| `LibTmux.ResizeWindowRequest.Direction` | Gets the edge to move. |
| `LibTmux.ResizeWindowRequest.Height` | Gets the explicit height. |
| `LibTmux.ResizeWindowRequest.Mode` | Gets the sizing to follow against the window's clients. |
| `LibTmux.ResizeWindowRequest.Width` | Gets the explicit width. |
| `LibTmux.RespawnRequest.Command` | Gets the command to run, or null to reuse the original. |
| `LibTmux.RespawnRequest.Environment` | Gets the environment entries set on the respawned target. |
| `LibTmux.RespawnRequest.KillExistingProcess` | Gets whether a running process is killed first. |
| `LibTmux.RespawnRequest.StartDirectory` | Gets the working directory to respawn in. |
| `LibTmux.RunShellRequest.Arguments` | Gets arguments passed to it without a shell in between. |
| `LibTmux.RunShellRequest.AsTmuxCommand` | Gets whether the text is a tmux command rather than a shell one. |
| `LibTmux.RunShellRequest.Background` | Gets whether tmux returns without waiting for it. |
| `LibTmux.RunShellRequest.Command` | Gets the command to run. |
| `LibTmux.RunShellRequest.Delay` | Gets how long tmux waits before starting it. |
| `LibTmux.RunShellRequest.ShowStandardError` | Gets whether its error output is shown too. |
| `LibTmux.RunShellRequest.TargetPane` | Gets the pane the command runs against. |
| `LibTmux.RunShellRequest.WorkingDirectory` | Gets the directory it starts in. |
| `LibTmux.SelectLayoutRequest.Layout` | Gets the named layout, or a layout string tmux dumped. |
| `LibTmux.SelectLayoutRequest.Mode` | Gets the layout change that needs no name. |
| `LibTmux.SelectPaneRequest.Direction` | Gets which pane to move to. |
| `LibTmux.SelectPaneRequest.InputEnabled` | Gets whether input is enabled, disabled, or left alone. |
| `LibTmux.SelectPaneRequest.KeepZoom` | Gets whether a zoomed pane stays zoomed. |
| `LibTmux.SelectPaneRequest.Last` | Gets whether the last active pane is selected. |
| `LibTmux.SelectPaneRequest.Mark` | Gets whether the pane is marked, unmarked, or left alone. |
| `LibTmux.SendKeysRequest.CopyModeCommand` | Gets the copy-mode command to send instead of text. |
| `LibTmux.SendKeysRequest.Enter` | Gets whether Enter follows the text. |
| `LibTmux.SendKeysRequest.ExpandFormats` | Gets whether the text is expanded as a format. |
| `LibTmux.SendKeysRequest.HexKeys` | Gets whether key names are read as hexadecimal. |
| `LibTmux.SendKeysRequest.KeyName` | Gets whether the text names a key rather than a string. |
| `LibTmux.SendKeysRequest.Literal` | Gets whether the text is sent verbatim rather than as key names. |
| `LibTmux.SendKeysRequest.Repeat` | Gets how many times the keys repeat. |
| `LibTmux.SendKeysRequest.Reset` | Gets whether the pane's terminal state is reset first. |
| `LibTmux.SendKeysRequest.SuppressHistory` | Gets whether the shell is asked not to record the line. |
| `LibTmux.SendKeysRequest.TargetClient` | Gets the client whose keys are sent. |
| `LibTmux.SendKeysRequest.Text` | Gets the text or key names to send. |
| `LibTmux.Server.Clients` | Gets the clients this handle captured. |
| `LibTmux.Server.ConnectionOptions` | Gets the connection options. |
| `LibTmux.Server.Environment` | Gets the environment new sessions inherit from. |
| `LibTmux.Server.Generation` | Gets the materialized server generation. |
| `LibTmux.Server.Hooks` | Gets the hooks of this server. |
| `LibTmux.Server.IsMaterialized` | Gets whether this handle has discovered a live server. |
| `LibTmux.Server.Options` | Gets the options of this server. |
| `LibTmux.Server.Panes` | Gets the panes this handle captured, across every window. |
| `LibTmux.Server.Sessions` | Gets the sessions this handle captured. |
| `LibTmux.Server.Version` | Gets the captured tmux version. |
| `LibTmux.Server.Windows` | Gets the windows this handle captured, across every session. |
| `LibTmux.ServerAccessRequest.AllowUser` | Gets the user to grant access to. |
| `LibTmux.ServerAccessRequest.DenyUser` | Gets the user to take access from. |
| `LibTmux.ServerAccessRequest.List` | Gets whether the current list is reported. |
| `LibTmux.ServerAccessRequest.ReadOnly` | Gets whether the granted user may only look. |
| `LibTmux.ServerAccessRequest.ReadWrite` | Gets whether the granted user may also act. |
| `LibTmux.ServerConnectionOptions.ChildEnvironment` | Gets the child-process environment overrides. |
| `LibTmux.ServerConnectionOptions.ColorMode` | Gets the requested tmux color mode. |
| `LibTmux.ServerConnectionOptions.ConfigurationFile` | Gets the tmux configuration file. |
| `LibTmux.ServerConnectionOptions.Default` | Gets conventional connection defaults. |
| `LibTmux.ServerConnectionOptions.InitializeAsync` | Gets the post-connect initializer. |
| `LibTmux.ServerConnectionOptions.Logger` | Gets the connection logger. |
| `LibTmux.ServerConnectionOptions.SocketName` | Gets the explicit socket name. |
| `LibTmux.ServerConnectionOptions.SocketNameFactory` | Gets the deferred socket-name factory. |
| `LibTmux.ServerConnectionOptions.SocketPath` | Gets the explicit socket path. |
| `LibTmux.ServerConnectionOptions.TmuxBinaryPath` | Gets the tmux executable path. |
| `LibTmux.ServerGeneration.ProcessId` | Gets the tmux daemon process identifier. |
| `LibTmux.ServerGeneration.StartTime` | Gets the tmux daemon start time. |
| `LibTmux.Session.ActivePane` | Gets the active pane recorded when this session was read. |
| `LibTmux.Session.ActiveWindow` | Gets the active window recorded when this session was read. |
| `LibTmux.Session.Attached` | Gets whether a client was attached when this session was read. |
| `LibTmux.Session.Environment` | Gets the environment panes created in this session inherit from. |
| `LibTmux.Session.Generation` | Gets the server generation captured with this session. |
| `LibTmux.Session.Hooks` | Gets the hooks of this session. |
| `LibTmux.Session.Id` | Gets the session identifier. |
| `LibTmux.Session.Name` | Gets the session name captured with this handle. |
| `LibTmux.Session.Options` | Gets the options of this session. |
| `LibTmux.Session.Panes` | Gets the panes the capture found in this session. |
| `LibTmux.Session.RawFormatFields` | Gets the tmux fields captured when this handle materialized. |
| `LibTmux.Session.Server` | Gets the server that owns this session. |
| `LibTmux.Session.Windows` | Gets the windows the capture found in this session. |
| `LibTmux.SessionId.Value` | Gets the nonnegative numeric value. |
| `LibTmux.SessionWindowEdge.Key` | Gets the session and window this edge joins. |
| `LibTmux.SessionWindowEdge.Ordinal` | Gets the edge's position in the session's window order. |
| `LibTmux.SessionWindowEdge.SessionId` | Gets the session the window is linked into. |
| `LibTmux.SessionWindowEdge.WindowId` | Gets the linked window. |
| `LibTmux.SessionWindowEdge.WindowIndex` | Gets the tmux window index inside the session. |
| `LibTmux.SetHookRequest.Append` | Gets whether the command joins the hook's existing entries. |
| `LibTmux.SetHookRequest.Global` | Gets whether the global table is set instead of the local one. |
| `LibTmux.SetHookRequest.Name` | Gets the hook name, optionally with an array index. |
| `LibTmux.SetHookRequest.RunImmediately` | Gets whether tmux also runs the command now. |
| `LibTmux.SetHookRequest.Scope` | Gets the scope to set in, or null for the owner's own. |
| `LibTmux.SetHookRequest.Unset` | Gets whether the hook is removed rather than set. |
| `LibTmux.SetHookRequest.Value` | Gets the tmux command to run when the hook fires. |
| `LibTmux.SetHooksRequest.ClearExisting` | Gets whether entries already there are removed first. |
| `LibTmux.SetHooksRequest.Global` | Gets whether the global table is set instead of the local one. |
| `LibTmux.SetHooksRequest.Name` | Gets the hook name, without an index. |
| `LibTmux.SetHooksRequest.Scope` | Gets the scope to set in, or null for the owner's own. |
| `LibTmux.SetHooksRequest.Values` | Gets the command to place at each index. |
| `LibTmux.SetOptionRequest.Append` | Gets whether the value is appended to the existing one. |
| `LibTmux.SetOptionRequest.ExpandFormat` | Gets whether tmux expands the value as a format before storing it. |
| `LibTmux.SetOptionRequest.Global` | Gets whether the global table is set instead of the local one. |
| `LibTmux.SetOptionRequest.Name` | Gets the option to set, optionally with an array index. |
| `LibTmux.SetOptionRequest.PreventOverwrite` | Gets whether an already-set option is left alone. |
| `LibTmux.SetOptionRequest.Quiet` | Gets whether a rejected option is answered with nothing instead of an error. |
| `LibTmux.SetOptionRequest.Scope` | Gets the scope to set in, or null for the owner's own. |
| `LibTmux.SetOptionRequest.Value` | Gets the value to store. |
| `LibTmux.SplitPaneRequest.ActiveBorderStyle` | Gets the border style while the pane is active. |
| `LibTmux.SplitPaneRequest.Attach` | Gets whether the new pane becomes active. |
| `LibTmux.SplitPaneRequest.Command` | Gets the command the new pane runs. |
| `LibTmux.SplitPaneRequest.Direction` | Gets where the new pane goes. |
| `LibTmux.SplitPaneRequest.Empty` | Gets whether the pane starts with no command. |
| `LibTmux.SplitPaneRequest.Environment` | Gets the environment entries set on the new pane. |
| `LibTmux.SplitPaneRequest.FullWindow` | Gets whether the split spans the whole window. |
| `LibTmux.SplitPaneRequest.InactiveBorderStyle` | Gets the border style while the pane is not active. |
| `LibTmux.SplitPaneRequest.KeepOpen` | Gets whether the pane stays after its command exits. |
| `LibTmux.SplitPaneRequest.Message` | Gets the message shown in the pane. |
| `LibTmux.SplitPaneRequest.Percentage` | Gets the size as a percentage of the window. |
| `LibTmux.SplitPaneRequest.Size` | Gets the explicit size in cells. |
| `LibTmux.SplitPaneRequest.StartDirectory` | Gets the working directory for the new pane. |
| `LibTmux.SplitPaneRequest.Style` | Gets the pane style. |
| `LibTmux.SplitPaneRequest.Target` | Gets the pane to split, or null for the active one. |
| `LibTmux.SplitPaneRequest.Zoom` | Gets whether the new pane is zoomed. |
| `LibTmux.StaleServerGenerationException.Actual` | Gets the generation currently serving the endpoint, or when it could not be observed. |
| `LibTmux.StaleServerGenerationException.Expected` | Gets the generation expected by the stale handle. |
| `LibTmux.SwapPaneRequest.Detach` | Gets whether the swapped pane is left unselected. |
| `LibTmux.SwapPaneRequest.Direction` | Gets the neighbour to swap with instead. |
| `LibTmux.SwapPaneRequest.KeepZoom` | Gets whether a zoomed pane stays zoomed. |
| `LibTmux.SwapPaneRequest.Target` | Gets the pane to swap with. |
| `LibTmux.Testing.TemporaryHierarchyScope.Pane` | Gets the pane. |
| `LibTmux.Testing.TemporaryHierarchyScope.Server` | Gets the server the rest live in. |
| `LibTmux.Testing.TemporaryHierarchyScope.Session` | Gets the session. |
| `LibTmux.Testing.TemporaryHierarchyScope.Window` | Gets the window. |
| `LibTmux.Testing.TemporaryServerScope.Server` | Gets the temporary server. |
| `LibTmux.Testing.TemporarySessionScope.Session` | Gets the temporary session. |
| `LibTmux.Testing.TemporaryWindowScope.Window` | Gets the temporary window. |
| `LibTmux.Testing.TestEnvironment.Variables` | Gets the variables to set, with null meaning remove. |
| `LibTmux.Testing.TestEnvironment.WorkingDirectory` | Gets the directory tmux starts in. |
| `LibTmux.Testing.TmuxTestContext.Environment` | Gets the directory and variables the server was started with. |
| `LibTmux.Testing.TmuxTestContext.Server` | Gets the server this test owns. |
| `LibTmux.Testing.TmuxTestOptions.ConnectionOptions` | Gets how to reach tmux. |
| `LibTmux.Testing.TmuxTestOptions.Default` | Gets options a test can use without choosing anything. |
| `LibTmux.Testing.TmuxTestOptions.PollInterval` | Gets how long a wait pauses between askings. |
| `LibTmux.Testing.TmuxTestOptions.SessionNamePrefix` | Gets what generated names start with. |
| `LibTmux.Testing.TmuxTestOptions.Timeout` | Gets how long a wait keeps asking. |
| `LibTmux.TmuxBuffer.Name` | Gets the buffer name. |
| `LibTmux.TmuxBuffer.Sample` | Gets the start of its contents, as tmux chose to show it. |
| `LibTmux.TmuxBuffer.Size` | Gets how many bytes it holds. |
| `LibTmux.TmuxChain.Commands` | Gets the commands this chain will run, in order. |
| `LibTmux.TmuxCleanupException.CleanupFailure` | Gets the cleanup failure. |
| `LibTmux.TmuxCleanupException.ClientProcessId` | Gets the disposable client process identifier. |
| `LibTmux.TmuxCleanupException.OriginalCancellation` | Gets the original cancellation. |
| `LibTmux.TmuxCommand.Arguments` | Gets the arguments, separated as tmux will receive them. |
| `LibTmux.TmuxCommand.Name` | Gets the tmux command name. |
| `LibTmux.TmuxCommandException.Result` | Gets the inspectable command result. |
| `LibTmux.TmuxCommandNotFoundException.TmuxBinaryPath` | Gets the configured tmux executable path. |
| `LibTmux.TmuxCommandResult.Arguments` | Gets the logical tmux arguments. |
| `LibTmux.TmuxCommandResult.ExitCode` | Gets the client exit code. |
| `LibTmux.TmuxCommandResult.StandardError` | Gets the exact standard-error bytes. |
| `LibTmux.TmuxCommandResult.StandardErrorLines` | Gets the projected standard-error lines. |
| `LibTmux.TmuxCommandResult.StandardOutput` | Gets the exact standard-output bytes. |
| `LibTmux.TmuxCommandResult.StandardOutputLines` | Gets the projected standard-output lines. |
| `LibTmux.TmuxEnvironmentEntry.IsRemoved` | Gets whether tmux strips this variable from new panes. |
| `LibTmux.TmuxEnvironmentEntry.Name` | Gets the variable name. |
| `LibTmux.TmuxEnvironmentEntry.Value` | Gets the value, or null when the variable is marked removed. |
| `LibTmux.TmuxEventsDroppedEvent.Count` | The events discarded since the previous loss report. |
| `LibTmux.TmuxEventsDroppedEvent.TotalDropped` | The events discarded over this control client's lifetime. |
| `LibTmux.TmuxExitEvent.Reason` | Why tmux said it ended, when it said anything. It is silent for an ordinary exit and names a reason when the server went away underneath the client. |
| `LibTmux.TmuxHook.Name` | Gets the hook name, without an index. |
| `LibTmux.TmuxHook.Values` | Gets the commands it runs, in the order tmux reported. |
| `LibTmux.TmuxHookEntry.Command` | Gets the tmux command, as tmux prints it. |
| `LibTmux.TmuxHookEntry.Index` | Gets where the command sits in the hook's order. |
| `LibTmux.TmuxHooks.Scope` | Gets the scope these hooks are read and written in by default. |
| `LibTmux.TmuxMenuItem.Command` | Gets the tmux command it runs. |
| `LibTmux.TmuxMenuItem.Key` | Gets the key that chooses it. |
| `LibTmux.TmuxMenuItem.Name` | Gets the text shown for the item. |
| `LibTmux.TmuxNotificationEvent.Arguments` | The words tmux printed after the name, unparsed. |
| `LibTmux.TmuxNotificationEvent.Name` | The notification name without its leading percent, such as window-add. |
| `LibTmux.TmuxObjectNotFoundException.Target` | Gets the missing tmux target. |
| `LibTmux.TmuxOperationCanceledException.ClientProcessId` | Gets the disposable client process identifier. |
| `LibTmux.TmuxOperationCanceledException.CommandMayHaveExecuted` | Gets whether tmux may have observed the command. |
| `LibTmux.TmuxOption.Index` | Gets the array index, or null for an option that is not an array. |
| `LibTmux.TmuxOption.Name` | Gets the option name, without index or inheritance marker. |
| `LibTmux.TmuxOption.Value` | Gets the value tmux reported. |
| `LibTmux.TmuxOptionException.OptionName` | Gets the option tmux was asked about. |
| `LibTmux.TmuxOptionValue.Boolean` | Gets the flag reading, or null when the value is not a flag. |
| `LibTmux.TmuxOptionValue.Integer` | Gets the whole-number reading, or null when the value is not one. |
| `LibTmux.TmuxOptionValue.Raw` | Gets the unescaped text tmux reported, or null when it reported none. |
| `LibTmux.TmuxOptionValue.State` | Gets whether the value is absent, a flag, or ordinary text. |
| `LibTmux.TmuxOptions.Scope` | Gets the scope these options are read and written in by default. |
| `LibTmux.TmuxOutputEvent.Data` | The text, with tmux's escaping already decoded. It is a fragment of a stream rather than a line: tmux sends whatever it has, so a single write by the program in the pane can arrive split across events and one event can carry several lines. |
| `LibTmux.TmuxOutputEvent.PaneId` | The pane that produced the output, such as %0. |
| `LibTmux.TmuxPaneException.PaneId` | Gets the pane the request named. |
| `LibTmux.TmuxSessionExistsException.SessionName` | Gets the session name that is already in use. |
| `LibTmux.TmuxTransportException.Arguments` | Gets the logical tmux arguments. |
| `LibTmux.TmuxVersion.IsValid` | Gets whether this value contains a parsed tmux version. |
| `LibTmux.TmuxVersion.Major` | Gets the parsed major version. |
| `LibTmux.TmuxVersion.Minor` | Gets the parsed minor version. |
| `LibTmux.TmuxVersion.Raw` | Gets the exact normalized tmux version text. |
| `LibTmux.TmuxVersion.Suffix` | Gets the exact preserved suffix projection. |
| `LibTmux.TmuxVersionTooLowException.ActualVersion` | Gets the actual tmux version. |
| `LibTmux.TmuxVersionTooLowException.RequiredVersion` | Gets the required tmux version. |
| `LibTmux.TmuxWaitChannel.Channel` | Gets the channel being waited on. |
| `LibTmux.TmuxWaitChannel.Signalled` | Gets whether the wait completed before withdrawal began. |
| `LibTmux.TmuxWaitTimeoutException.Timeout` | Gets the expired timeout. |
| `LibTmux.TmuxWindowException.WindowId` | Gets the window the request named. |
| `LibTmux.UnbindKeyRequest.All` | Gets whether every binding in the table goes. |
| `LibTmux.UnbindKeyRequest.Key` | Gets the key to unbind, or null when removing them all. |
| `LibTmux.UnbindKeyRequest.KeyTable` | Gets the key table, or null for the prefix table. |
| `LibTmux.UnbindKeyRequest.Quiet` | Gets whether an absent binding is passed over in silence. |
| `LibTmux.UnsafeTmuxFilter.Value` | The raw tmux -f filter text. |
| `LibTmux.UnsetOptionRequest.Global` | Gets whether the global table is unset instead of the local one. |
| `LibTmux.UnsetOptionRequest.Name` | Gets the option to unset, optionally with an array index. |
| `LibTmux.UnsetOptionRequest.Quiet` | Gets whether a missing option is answered with nothing instead of an error. |
| `LibTmux.UnsetOptionRequest.Scope` | Gets the scope to unset in, or null for the owner's own. |
| `LibTmux.UnsetOptionRequest.UnsetPaneOverrides` | Gets whether every pane's override of the option goes too. |
| `LibTmux.UnsupportedQueryExpressionException.Expression` | Gets the expression that could not be translated. |
| `LibTmux.WaitForRequest.Channel` | Gets the channel name. |
| `LibTmux.WaitForRequest.Mode` | Gets what to do with it. |
| `LibTmux.Window.ActivePane` | Gets the active pane recorded when this window was read. |
| `LibTmux.Window.Edge` | Gets where this window sits in the session it was read from. |
| `LibTmux.Window.EntityKey` | Gets the session and window this handle names together. |
| `LibTmux.Window.Generation` | Gets the server generation captured with this window. |
| `LibTmux.Window.Height` | Gets the window height captured with this handle. |
| `LibTmux.Window.Hooks` | Gets the hooks of this window. |
| `LibTmux.Window.Id` | Gets the window identifier. |
| `LibTmux.Window.Index` | Gets the index this window holds in its session. |
| `LibTmux.Window.LinkedSessions` | Gets the sessions the capture found this window linked into. |
| `LibTmux.Window.Name` | Gets the window name captured with this handle. |
| `LibTmux.Window.Options` | Gets the options of this window. |
| `LibTmux.Window.Panes` | Gets the panes the capture found in this window. |
| `LibTmux.Window.RawFormatFields` | Gets the tmux fields captured when this handle materialized. |
| `LibTmux.Window.Server` | Gets the server that owns this window. |
| `LibTmux.Window.Session` | Gets the session this window was read through. |
| `LibTmux.Window.Width` | Gets the window width captured with this handle. |
| `LibTmux.WindowEntityKey.SessionId` | The session the window is linked into. |
| `LibTmux.WindowEntityKey.WindowId` | The linked window. |
| `LibTmux.WindowId.Value` | Gets the nonnegative numeric value. |

## Fields

| Member | Summary |
|---|---|
| `LibTmux.ChooseTreeSort.Index` | Order by index. |
| `LibTmux.ChooseTreeSort.Name` | Order by name. |
| `LibTmux.ChooseTreeSort.Size` | Order by size. |
| `LibTmux.ChooseTreeSort.Time` | Order by activity time. |
| `LibTmux.OptionScope.Pane` | Uses pane scope. |
| `LibTmux.OptionScope.Server` | Uses server scope. |
| `LibTmux.OptionScope.Session` | Uses session scope. |
| `LibTmux.OptionScope.Window` | Uses window scope. |
| `LibTmux.PaneDirection.Above` | Places the pane above. |
| `LibTmux.PaneDirection.Below` | Places the pane below. |
| `LibTmux.PaneDirection.Left` | Places the pane to the left. |
| `LibTmux.PaneDirection.Right` | Places the pane to the right. |
| `LibTmux.PaneInputMode.Disable` | The pane ignores input. |
| `LibTmux.PaneInputMode.Enable` | The pane accepts input. |
| `LibTmux.PaneSelectDirection.Down` | The pane below. |
| `LibTmux.PaneSelectDirection.Last` | The pane that was last active. |
| `LibTmux.PaneSelectDirection.Left` | The pane to the left. |
| `LibTmux.PaneSelectDirection.Right` | The pane to the right. |
| `LibTmux.PaneSelectDirection.Up` | The pane above. |
| `LibTmux.PaneSwapDirection.Down` | The pane below. |
| `LibTmux.PaneSwapDirection.Up` | The pane above. |
| `LibTmux.PopupCloseMode.AnyExit` | Close when the command exits, however it exits. |
| `LibTmux.PopupCloseMode.SuccessfulExit` | Close only when the command exits successfully. |
| `LibTmux.PromptType.Command` | A tmux command. |
| `LibTmux.PromptType.Search` | Text to search for. |
| `LibTmux.PromptType.Target` | A target to act on. |
| `LibTmux.PromptType.WindowTarget` | A window to act on. |
| `LibTmux.PsmuxServer.SupportedBinarySha256` | Gets the exact psmux client executable SHA-256 accepted by this preview. |
| `LibTmux.PsmuxServer.SupportedCommit` | Gets the exact psmux source commit accepted by this preview. |
| `LibTmux.PsmuxServer.SupportedImplementationBanner` | Gets the exact clean implementation banner accepted by this preview. |
| `LibTmux.Query.QueryComparison.Equal` | Operands are equal. |
| `LibTmux.Query.QueryComparison.GreaterThan` | The left operand is larger. |
| `LibTmux.Query.QueryComparison.GreaterThanOrEqual` | The left operand is not smaller. |
| `LibTmux.Query.QueryComparison.LessThan` | The left operand is smaller. |
| `LibTmux.Query.QueryComparison.LessThanOrEqual` | The left operand is not larger. |
| `LibTmux.Query.QueryComparison.NotEqual` | Operands differ. |
| `LibTmux.Query.QueryQuantifier.All` | True when every child matches; true when empty. |
| `LibTmux.Query.QueryQuantifier.Any` | True when at least one child matches; false when empty. |
| `LibTmux.Query.QueryStringOperation.ContainsOrdinal` | Ordinal substring match. |
| `LibTmux.Query.QueryStringOperation.EndsWithOrdinal` | Ordinal suffix match. |
| `LibTmux.Query.QueryStringOperation.EqualsOrdinal` | Ordinal equality. |
| `LibTmux.Query.QueryStringOperation.EqualsOrdinalIgnoreCase` | Case-insensitive ordinal equality. |
| `LibTmux.Query.QueryStringOperation.StartsWithOrdinal` | Ordinal prefix match. |
| `LibTmux.Query.QueryTarget.Client` | A tmux client. |
| `LibTmux.Query.QueryTarget.Pane` | A tmux pane. |
| `LibTmux.Query.QueryTarget.Session` | A tmux session. |
| `LibTmux.Query.QueryTarget.Window` | A tmux window. |
| `LibTmux.ResizeDirection.Down` | Resizes downward. |
| `LibTmux.ResizeDirection.Left` | Resizes leftward. |
| `LibTmux.ResizeDirection.Right` | Resizes rightward. |
| `LibTmux.ResizeDirection.Up` | Resizes upward. |
| `LibTmux.SelectLayoutMode.Next` | Move to the next layout. |
| `LibTmux.SelectLayoutMode.Previous` | Move to the previous layout. |
| `LibTmux.SelectLayoutMode.Spread` | Spread the panes out evenly. |
| `LibTmux.ShowMessagesMode.Jobs` | The jobs the server is running. |
| `LibTmux.ShowMessagesMode.Messages` | The server's own message log. |
| `LibTmux.ShowMessagesMode.Terminals` | What the server knows about attached terminals. |
| `LibTmux.SnapshotDepth.Panes` | Sessions, windows, and their panes were captured. |
| `LibTmux.SnapshotDepth.Server` | Only the server itself was captured. |
| `LibTmux.SnapshotDepth.Sessions` | Sessions were captured, but not their windows. |
| `LibTmux.SnapshotDepth.Windows` | Sessions and their windows were captured. |
| `LibTmux.TmuxColorMode.Colors256` | Requests 256-color mode. |
| `LibTmux.TmuxColorMode.Default` | Uses tmux's default color behavior. |
| `LibTmux.TmuxColorMode.TrueColor` | Requests RGB true-color mode. |
| `LibTmux.TmuxDispatchState.Dispatched` | tmux ran the command and answered. The failure is tmux refusing or reporting an error, not the command going missing, so any side effect it had before failing has already happened. |
| `LibTmux.TmuxDispatchState.NotDispatched` | The command never reached tmux, so nothing was done and a retry repeats nothing. This is the only state in which retrying is unconditionally safe. |
| `LibTmux.TmuxDispatchState.Unknown` | Whether tmux acted on the command cannot be determined. Treat a retry as capable of repeating whatever the command does. |
| `LibTmux.TmuxOptionState.Absent` | tmux named the option but gave it no value. |
| `LibTmux.TmuxOptionState.Off` | tmux reported the flag value off. |
| `LibTmux.TmuxOptionState.On` | tmux reported the flag value on. |
| `LibTmux.TmuxOptionState.Value` | tmux reported a value that is neither on nor off. |
| `LibTmux.TmuxWaitMode.Lock` | Take the channel's lock, blocking until it is free. |
| `LibTmux.TmuxWaitMode.Signal` | Release everything waiting on the channel. |
| `LibTmux.TmuxWaitMode.Unlock` | Release the channel's lock. |
| `LibTmux.TmuxWaitMode.Wait` | Block until the channel is signalled. |
| `LibTmux.WindowDirection.After` | Places the window after the target. |
| `LibTmux.WindowDirection.Before` | Places the window before the target. |
| `LibTmux.WindowResizeMode.Expand` | Size the window to its largest client. |
| `LibTmux.WindowResizeMode.Shrink` | Size the window to its smallest client. |
| `LibTmux.WindowRotationDirection.Down` | Rotate panes towards the bottom of the window. |
| `LibTmux.WindowRotationDirection.Up` | Rotate panes towards the top of the window. |
