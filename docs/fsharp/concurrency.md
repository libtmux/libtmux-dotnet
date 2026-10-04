# Concurrency

Share one `Server` handle across a program and call it from as many tasks as
you like. What each call shares with others running at the same time:

| What | Shared across tasks | What to know |
| --- | --- | --- |
| `Server`, `Session`, `Window`, `Pane` handles | Yes | A handle is immutable; a call that changes tmux returns a new handle rather than altering the one you hold. |
| Commands, listings and queries | Yes | Each runs as its own tmux command, so calls from many tasks run at once and tmux orders them against the server. |
| Waits | Yes | Waits on the panes of one session share one control client, attached while any of them runs. `use! _ = Session.holdWaitClient ct session` keeps it between waits in a series. |
| `Pane.run` | One at a time per pane | Runs are not queued: a second command typed into the same shell before the first ends is read by that shell too. Run commands side by side in panes of their own. |
| A control client from `Control.withSession` | One reader at a time | A second reader while one is reading raises `InvalidOperationException`. `Control.watchPanes` follows several panes through one client; open another client to read independently. |
| A mirror from `Mirror.start` | Yes | It attaches a control client of its own, apart from the waits' client, and its views are immutable to share. |
| `Server.within` | Yes | The bound applies to every command through the handle it returns and to the sessions, windows and panes taken from it. |

Each call takes its own cancellation token, and cancelling one leaves the
others running. The [failure table](getting-started.md#which-wait) says what
each kind of call raises when cancelled.

Three waits started together on two panes of one session attach one control
client between them, and none is left once they end; twenty listings started
together return the same panes.
