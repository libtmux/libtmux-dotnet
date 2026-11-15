namespace LibTmux;

/// <summary>Represents an immutable pane handle and snapshot.</summary>
/// <remarks>
/// <para>
/// Thread-safe: the handle is immutable once constructed and may be shared
/// freely, including as a singleton. Every mutating call answers a new handle
/// rather than changing this one.
/// </para>
/// <para>
/// <b>What is on here</b>
/// </para>
/// <para>
/// A tmux pane runs one program and has a good many members of its own. They fall
/// into a few groups:
/// </para>
/// <para>
/// <b>Reaching a pane.</b> Ask for the pane this process was spawned in. It
/// throws when the environment does not name a pane or tmux no longer has it.
/// </para>
/// <para>
/// <see cref="Pane.FromEnvironmentAsync" />
/// </para>
/// <para>
/// <b>Identity and state.</b> A handle carries what tmux reported when it was
/// made, so reading its title, size, command, or flags is local and never
/// reaches tmux; re-read the pane to see current state. Two handles are equal
/// when they name the same pane on the same server generation. A pane outlives
/// its program only while <c>remain-on-exit</c> is on, and then
/// <see cref="Pane.Dead" /> is true; a dead pane keeps the process ID of the program
/// that exited, and tmux 3.8 reports none, which reads as 0.
/// </para>
/// <list type="bullet">
/// <item>
/// <description>Identity: <see cref="Pane.Id" />,
/// <see cref="Pane.Index" />,
/// <see cref="Pane.Generation" /></description>
/// </item>
/// <item>
/// <description>Process: <see cref="Pane.CurrentCommand" />,
/// <see cref="Pane.CurrentPath" />,
/// <see cref="Pane.ProcessId" />,
/// <see cref="Pane.Tty" />,
/// <see cref="Pane.StartCommand" /></description>
/// </item>
/// <item>
/// <description>Dead panes: <see cref="Pane.Dead" />,
/// <see cref="Pane.DeadStatus" /></description>
/// </item>
/// <item>
/// <description>Geometry: <see cref="Pane.Width" />,
/// <see cref="Pane.Height" />,
/// <see cref="Pane.Left" />,
/// <see cref="Pane.Top" /></description>
/// </item>
/// <item>
/// <description>State: <see cref="Pane.Active" />,
/// <see cref="Pane.InMode" />,
/// <see cref="Pane.Synchronized" />,
/// <see cref="Pane.Title" />,
/// <see cref="Pane.HistorySize" /></description>
/// </item>
/// <item>
/// <description>Raw: <see cref="Pane.RawFormatFields" /></description>
/// </item>
/// <item>
/// <description>Owner: <see cref="Pane.Server" />,
/// <see cref="Pane.Session" />,
/// <see cref="Pane.Window" /></description>
/// </item>
/// <item>
/// <description>Re-read: <see cref="Pane.RefreshAsync" /></description>
/// </item>
/// </list>
/// <para>
/// <b>Sending input.</b> Text is typed into the pane, with or without Enter,
/// and a request can send key names or literal text. A request that sends
/// nothing, or text containing NUL, throws <see cref="ArgumentException" />.
/// When the text was sent but Enter failed, the dispatch state is unknown and
/// the whole request must not be retried.
/// </para>
/// <list type="bullet">
/// <item>
/// <description>Text: <see cref="Pane.SendTextAsync" /></description>
/// </item>
/// <item>
/// <description>Keys: <see cref="Pane.SendKeysAsync" />,
/// <see cref="Pane.SendPrefixAsync" />,
/// <see cref="Pane.EnterAsync" /></description>
/// </item>
/// <item>
/// <description>Paste: <see cref="Pane.PasteBufferAsync" /></description>
/// </item>
/// <item>
/// <description>Clear: <see cref="Pane.ClearAsync" />,
/// <see cref="Pane.ResetAsync" />,
/// <see cref="Pane.ClearHistoryAsync" /></description>
/// </item>
/// </list>
/// <para>
/// <b>Reading output.</b> Capturing reads the pane at one instant. Reading
/// since a position returns only what the pane printed after it, and the
/// position to pass next time, and a read without a position returns none.
/// Lines a program rewrote in place are reported again when they change.
/// </para>
/// <list type="bullet">
/// <item>
/// <description>Capture: <see cref="Pane.CaptureAsync" />,
/// <see cref="Pane.CaptureToBufferAsync" /></description>
/// </item>
/// <item>
/// <description>Since a position: <see cref="Pane.ReadOutputSinceAsync" /></description>
/// </item>
/// <item>
/// <description>Pipe: <see cref="Pane.PipeAsync" /></description>
/// </item>
/// </list>
/// <para>
/// <b>Waiting.</b> A wait ends with a result that says how it ended rather than
/// throwing when time runs out. Text already on screen ends a wait for text at
/// once, and a line the pane wrapped is matched as one line. Sending and then
/// waiting judges only what the pane prints afterwards and discounts the
/// shell's echo of literal text, so waiting for <c>done</c> after typing
/// <c>echo done</c> waits for the command's output. The pattern wait sleeps on
/// the pane's output through an owned control-mode client, and falling back to
/// timed reads needs <c>AllowPollingFallback</c>. Each throws
/// <see cref="TmuxObjectNotFoundException" /> when tmux no longer has the pane.
/// </para>
/// <list type="bullet">
/// <item>
/// <description>Text: <see cref="Pane.WaitForTextAsync(string, TimeSpan, CancellationToken)" />,
/// <see cref="Pane.WaitForTextAsync(PaneWaitRequest, CancellationToken)"
/// /></description>
/// </item>
/// <item>
/// <description>Send, then wait: <see cref="Pane.SendTextAndWaitAsync" />,
/// <see cref="Pane.SendKeysAndWaitAsync" /></description>
/// </item>
/// <item>
/// <description>A condition: <see cref="Pane.WaitUntilAsync" /></description>
/// </item>
/// </list>
/// <para>
/// <b>Running a command.</b> Run a shell command in the pane and wait for its
/// exit status and what it printed. The pane must sit at a POSIX shell prompt,
/// a command that outlasts the timeout may keep running, and one already sent
/// when the call is cancelled raises <see cref="LibTmuxException" /> saying it
/// may have run, so inspect the pane before a retry. The command runs in a
/// subshell, so <c>cd</c> and <c>export</c> do not persist, and it needs tmux
/// 3.4.
/// </para>
/// <para>
/// <see cref="Pane.RunAsync(string, TimeSpan, CancellationToken)" />,
/// <see cref="Pane.RunAsync(PaneRunRequest, CancellationToken)" />
/// </para>
/// <para>
/// <b>Splitting and arranging.</b> Splitting makes a tiled pane beside this one
/// and creating a pane makes a floating one, which needs tmux 3.7. Resizing
/// answers a replacement handle, and tmux clamps a size that does not fit
/// rather than refusing it. Breaking out moves the pane to a window of its own.
/// </para>
/// <list type="bullet">
/// <item>
/// <description>Create: <see cref="Pane.SplitAsync" />,
/// <see cref="Pane.CreatePaneAsync" /></description>
/// </item>
/// <item>
/// <description>Move: <see cref="Pane.BreakAsync" />,
/// <see cref="Pane.JoinAsync" />,
/// <see cref="Pane.MoveAsync" />,
/// <see cref="Pane.SwapAsync" /></description>
/// </item>
/// <item>
/// <description>Size: <see cref="Pane.ResizeAsync" />,
/// <see cref="Pane.SetWidthAsync" />,
/// <see cref="Pane.SetHeightAsync" /></description>
/// </item>
/// <item>
/// <description>Focus: <see cref="Pane.SelectAsync" /></description>
/// </item>
/// <item>
/// <description>Title: <see cref="Pane.SetTitleAsync" /></description>
/// </item>
/// </list>
/// <para>
/// <b>Modes and choosers.</b> Copy mode, clock mode, and customize mode put the
/// pane into that mode. The choosers and the finder open in this pane. A popup
/// waits for tmux to close it, so one whose command never exits keeps the call
/// waiting until it is canceled. The pane numbers show on every client, because
/// the command's target names a client.
/// </para>
/// <list type="bullet">
/// <item>
/// <description>Modes: <see cref="Pane.EnterCopyModeAsync" />,
/// <see cref="Pane.EnterClockModeAsync" />,
/// <see cref="Pane.EnterCustomizeModeAsync" /></description>
/// </item>
/// <item>
/// <description>Choosers: <see cref="Pane.ChooseTreeAsync" />,
/// <see cref="Pane.ChooseBufferAsync" />,
/// <see cref="Pane.ChooseClientAsync" />,
/// <see cref="Pane.FindWindowAsync" /></description>
/// </item>
/// <item>
/// <description>Overlays: <see cref="Pane.DisplayPopupAsync" />,
/// <see cref="Pane.DisplayPaneNumbersAsync" />,
/// <see cref="Pane.DisplayMessageAsync" /></description>
/// </item>
/// </list>
/// <para>
/// <b>Restarting and ending.</b> Respawning restarts the pane's command, and
/// tmux refuses a pane that is still running unless the request kills it first.
/// Stopping a pane can stop every other pane in its window instead.
/// </para>
/// <para>
/// <see cref="Pane.RespawnAsync" />,
/// <see cref="Pane.KillAsync" />
/// </para>
/// <para>
/// <b>Options and hooks.</b> The pane's options and its hooks are objects of
/// their own: <see cref="TmuxOptions" /> and <see cref="TmuxHooks" />.
/// </para>
/// <para>
/// <see cref="Pane.Options" />,
/// <see cref="Pane.Hooks" />
/// </para>
/// <para>
/// Anything tmux can do to a pane that is not here is reachable through
/// <see cref="Pane.ExecuteCommandAsync" />, which runs one raw command
/// against this pane.
/// </para>
/// </remarks>
public sealed partial class Pane
{
}
