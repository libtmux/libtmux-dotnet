# tmux MCP tools

Generated from the server itself — a table nobody generates is wrong the
first time somebody adds a tool. Regenerate after changing the surface:

```console
$ uv run eng/mcp/dump_tools.py
```

42 tools, 4 resources and 2 resource templates, and 4 prompts.

`tier` is the lowest `LIBTMUX_SAFETY` that registers the tool. `read` marks
a tool annotated read-only, which a client may use to skip a confirmation.

| Tool | Tier | Read | Does |
|---|---|---|---|
| `tmux_cancel_job` | mutating |  | Interrupt a background job by sending its pane Ctrl-C. |
| `tmux_capture_pane` | readonly | yes | Read the text a pane is showing, and optionally its scrollback. |
| `tmux_clear_pane` | mutating |  | Clear a pane's visible screen, and optionally its scrollback too. |
| `tmux_create_session` | mutating |  | Create a detached tmux session and return its ids. |
| `tmux_create_window` | mutating |  | Create a window in a tmux session and return its ids. |
| `tmux_display_message` | readonly | yes | Expand a tmux FORMAT string, such as '#{pane_current_command}' or '#{window_layout}', and return the text. |
| `tmux_hierarchy` | readonly | yes | Read every tmux session, window and pane at once. |
| `tmux_job` | mutating | yes | Read a background job's state, exit status, and whatever its pane has printed SINCE THE LAST TIME you asked. |
| `tmux_kill_pane` | destructive |  | Kill a pane and everything running in it. |
| `tmux_kill_server` | destructive |  | Kill the entire tmux server: every session, window and pane on that socket, including work nobody here started. |
| `tmux_kill_session` | destructive |  | Kill a session, every window in it, and everything running in those windows. |
| `tmux_kill_window` | destructive |  | Kill a window and every pane in it. |
| `tmux_list_buffers` | readonly | yes | List tmux paste buffers by name and size, without their contents. |
| `tmux_list_jobs` | mutating | yes | List the background jobs this server started and still remembers, newest first. |
| `tmux_list_panes` | readonly | yes | List tmux panes, optionally within one session or window. |
| `tmux_list_servers` | readonly | yes | Find the tmux servers running for this user, by socket. |
| `tmux_list_sessions` | readonly | yes | List the tmux sessions. |
| `tmux_list_windows` | readonly | yes | List tmux windows, optionally within one session. |
| `tmux_paste_text` | mutating |  | Paste a block of text into a pane through a tmux buffer. |
| `tmux_rename_session` | mutating |  | Rename a tmux session. |
| `tmux_rename_window` | mutating |  | Rename a tmux window. |
| `tmux_resize_pane` | mutating |  | Resize a pane, or zoom it to fill its window. |
| `tmux_respawn_pane` | mutating |  | Restart the program in a pane, keeping the pane and its id. |
| `tmux_run` | mutating |  | Run a shell command in a pane, wait for it to finish, and report its real exit status and output. |
| `tmux_search_panes` | readonly | yes | Find which panes are showing text matching a regular expression. |
| `tmux_select_layout` | mutating |  | Arrange a window's panes with a named layout — even-horizontal, even-vertical, main-horizontal, main-vertical, tiled — or a layout string read from tmux_list_windows. |
| `tmux_select_pane` | mutating |  | Make a pane the active one in its window. |
| `tmux_select_window` | mutating |  | Make a window the current one in its session. |
| `tmux_send_keys` | mutating |  | Send raw keystrokes to a pane and return immediately. |
| `tmux_send_keys_batch` | mutating |  | Send several keystrokes to one pane in order, in a single call. |
| `tmux_server_info` | readonly | yes | Read the tmux server's version and how many sessions, windows and panes it holds. |
| `tmux_set_pane_title` | mutating |  | Set a pane's title. |
| `tmux_show_environment` | readonly | yes | Read the environment tmux gives to new panes, at the server or session level. |
| `tmux_show_hooks` | readonly | yes | Read the hooks tmux will run on its own events. |
| `tmux_show_options` | readonly | yes | Read tmux options at the server, session, window or pane level. |
| `tmux_snapshot_pane` | readonly | yes | Read a pane's visible content together with its cursor position, size and running command, in one call. |
| `tmux_split_pane` | mutating |  | Split a pane and return the NEW pane's id. |
| `tmux_start_job` | mutating |  | Start a shell command in a pane and return a job handle IMMEDIATELY, without waiting. |
| `tmux_tail_pane` | readonly | yes | Read only what a pane has printed since the last call. |
| `tmux_wait_for_channel` | mutating |  | Block until something signals a tmux wait-for channel with 'tmux wait-for -S <channel>'. |
| `tmux_wait_for_text` | readonly | yes | Wait until a pane prints something matching one of these patterns, then return. |
| `tmux_whoami` | readonly | yes | Answer which pane this MCP server is running inside, or null when it is not running in tmux. |

## Resources

| URI | Does |
|---|---|
| `tmux://hierarchy` | Every tmux session, window and pane on the default server. |
| `tmux://self` | The tmux pane this MCP server is running inside, or null. |
| `tmux://servers` | The tmux servers running for this user, by socket. |
| `tmux://sessions` | The tmux sessions on the default server. |

## Resource templates

| URI | Does |
|---|---|
| `tmux://panes/{paneId}/content` | The text one tmux pane is currently showing. |
| `tmux://sessions/{sessionId}/panes` | The panes belonging to one tmux session. |

## Prompts

| Prompt | Does |
|---|---|
| `tmux_build_workspace` | Create a tmux session with an editor, a shell and a log pane. |
| `tmux_diagnose_pane` | Gather what a tmux pane is doing and propose a cause, without changing anything. |
| `tmux_interrupt_pane` | Interrupt whatever is running in a tmux pane and confirm it stopped. |
| `tmux_run_and_report` | Run a shell command in a tmux pane and report whether it succeeded. |
