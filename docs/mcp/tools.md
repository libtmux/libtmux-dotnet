# tmux MCP tools

Generated from the server itself — a table nobody generates is wrong the
first time somebody adds a tool. Regenerate after changing the surface:

```console
$ uv run eng/mcp/dump_tools.py
```

45 tools and 1 static resource. No dynamic resource
templates or prompts are registered (0 templates, 0 prompts).

Every row is the capability object advertised with the tool under
`_meta["com.git-pull.libtmux-mcp/capability"]`. Effects and output classes are sets.
All protocol annotations are conservative: read-only false, destructive true,
idempotent false, and open-world true.
Earlier tier-era names and removed families are mapped in the
[migration table](README.md#migrating-from-the-tiered-surface).

| Tool | Toolset | Reach | Effects | Output classes | Does |
|---|---|---|---|---|---|
| `call_read_tools_batch` | inspect | none | observe | configured-command, process-environment, terminal-content, tmux-metadata | Execute up to 16 declared inspect operations serially; inner operations receive no separate approval. |
| `capture_pane` | inspect | none | observe | terminal-content, tmux-metadata | Read the text a pane is showing, and optionally its scrollback. |
| `capture_since` | inspect | none | observe | terminal-content, tmux-metadata | Read only what a pane has printed since the last call. |
| `clear_pane_scrollback` | teardown | none | delete | tmux-metadata | Delete a pane's scrollback history, keeping what the screen shows. |
| `create_session` | execute | configured-process | change, observe | tmux-metadata | Create a detached tmux session and return its ids. |
| `create_window` | execute | configured-process | change, observe | tmux-metadata | Create a window in a tmux session and return its ids. |
| `find_pane_by_position` | inspect | none | observe | tmux-metadata | Find the pane sitting at an index within a window. |
| `get_pane_info` | inspect | none | observe | tmux-metadata | Read one pane's size, title, running command, working directory, process ID, history size and limit, and whether it is active, dead, zoomed, in a mode or the pane this server runs in. |
| `get_server_info` | inspect | none | observe | tmux-metadata | Read the tmux server's version and how many sessions, windows and panes it holds. |
| `get_session_info` | inspect | none | observe | tmux-metadata | Read one session's name, ID, window count and whether a client is attached, without listing every session. |
| `get_tmux_variables` | inspect | none | observe | configured-command, tmux-metadata | Expand named tmux format variables for a pane, such as session_name or window_width. |
| `get_window_info` | inspect | none | observe | tmux-metadata | Read one window's name, index, size, layout, pane count and whether it is its session's current window, without listing every window. |
| `kill_pane` | teardown | none | delete, observe | tmux-metadata | Close a pane and end its program. |
| `kill_session` | teardown | none | delete, observe | tmux-metadata | Close a session with all its windows and panes. |
| `kill_window` | teardown | none | delete, observe | tmux-metadata | Close a window and every pane in it. |
| `list_panes` | inspect | none | observe | tmux-metadata | List tmux panes, optionally within one session or window. |
| `list_sessions` | inspect | none | observe | tmux-metadata | List the tmux sessions. |
| `list_windows` | inspect | none | observe | tmux-metadata | List tmux windows, optionally within one session. |
| `move_window` | manage | none | change, observe | tmux-metadata | Move a window to another index, or into another session. |
| `paste_text` | execute | pane-input | change, observe | tmux-metadata | Paste a block of text into exactly one pane through a tmux buffer. |
| `rename_session` | manage | none | change, observe | tmux-metadata | Rename a tmux session. |
| `rename_window` | manage | none | change, observe | tmux-metadata | Rename a tmux window. |
| `resize_pane` | manage | none | change, observe | tmux-metadata | Resize a pane, or zoom it to fill its window. |
| `resize_window` | manage | none | change, observe | tmux-metadata | Resize a window to a width and height in cells; its panes resize with it. |
| `respawn_pane` | execute | configured-process | change, delete, observe | tmux-metadata | Only restarts a pane whose command has ALREADY EXITED. |
| `run_shell_command` | execute | pane-command | change, observe | terminal-content, tmux-metadata | Run a shell command in a pane with your user's permissions. |
| `search_panes` | inspect | none | observe | terminal-content, tmux-metadata | Find which panes are showing text matching a regular expression. |
| `select_layout` | manage | none | change, observe | tmux-metadata | Arrange a window's panes with a named layout — even-horizontal, even-vertical, main-horizontal, main-vertical, tiled — or a layout string read from list_windows. |
| `select_pane` | manage | none | change, observe | tmux-metadata | Make a pane the active one in its window. |
| `select_window` | manage | none | change, observe | tmux-metadata | Make a window the current one in its session. |
| `send_keys` | execute | pane-input | change, observe | tmux-metadata | Send raw keystrokes to a pane and return immediately. |
| `send_keys_batch` | execute | pane-input | change, observe | tmux-metadata | Send several keystrokes to one pane in order, in a single call. |
| `set_history_limit` | manage | none | change | tmux-metadata | Set how many scrollback lines tmux keeps. |
| `set_mouse_enabled` | manage | none | change | tmux-metadata | Turn tmux mouse support on or off. |
| `set_pane_title` | manage | none | change, observe | tmux-metadata | Set a pane's title. |
| `set_synchronize_panes` | execute | none | change | tmux-metadata | Turn synchronize-panes on or off for a window. |
| `show_environment` | inspect | none | observe | process-environment | Read the tmux environment; accepts no client-supplied executable input. |
| `show_hooks` | inspect | none | observe | configured-command | Read the hooks tmux will run on its own events. |
| `show_option` | inspect | none | observe | configured-command, tmux-metadata | Read tmux options at the server, session, window or pane level. |
| `signal_channel` | manage | none | change | tmux-metadata | Signal a tmux wait-for channel, releasing whatever waits on it. |
| `snapshot_pane` | inspect | none | observe | terminal-content, tmux-metadata | Read a pane's visible content together with its cursor position, size and running command, in one call. |
| `split_window` | execute | configured-process | change, observe | tmux-metadata | Split a pane and return the NEW pane's id. |
| `swap_pane` | manage | none | change, observe | tmux-metadata | Swap two panes' positions; each keeps its program, its content and its ID. |
| `wait_for_channel` | manage | none | change | tmux-metadata | Block until something signals a tmux wait-for channel with 'tmux wait-for -S <channel>'. |
| `wait_for_text` | inspect | none | observe | terminal-content, tmux-metadata | Wait until a pane prints something matching one of these patterns, then return. |

## Parameters

Each tool's input schema describes its parameters; `tools/list` returns it.
A `?` marks a parameter that accepts null, and `=` gives its default.

| Tool | Required | Optional |
|---|---|---|
| `call_read_tools_batch` | `operations`: object[] of 1 to 16 | `onError`: "continue" or "stop" = "stop" |
| `capture_pane` | none | `paneId`: string?, `includeHistory`: boolean = false, `maxLines`: integer?, `joinWrappedLines`: boolean = false |
| `capture_since` | none | `paneId`: string?, `cursor`: string?, `maxLines`: integer? |
| `clear_pane_scrollback` | none | `paneId`: string? |
| `create_session` | none | `name`: string?, `startDirectory`: string?, `width`: integer?, `height`: integer? |
| `create_window` | none | `session`: string?, `name`: string?, `startDirectory`: string? |
| `find_pane_by_position` | `windowId`: string, `position`: integer | none |
| `get_pane_info` | `paneId`: string | none |
| `get_server_info` | none | none |
| `get_session_info` | `session`: string | none |
| `get_tmux_variables` | `names`: string[] | `paneId`: string? |
| `get_window_info` | `windowId`: string | none |
| `kill_pane` | `paneId`: string | none |
| `kill_session` | `session`: string | none |
| `kill_window` | `windowId`: string | none |
| `list_panes` | none | `session`: string?, `windowId`: string? |
| `list_sessions` | none | none |
| `list_windows` | none | `session`: string? |
| `move_window` | `windowId`: string | `destination`: string = "", `session`: string?, `replaceExisting`: boolean = false |
| `paste_text` | `text`: string | `paneId`: string?, `bracketed`: boolean = true, `enter`: boolean = false |
| `rename_session` | `name`: string | `session`: string? |
| `rename_window` | `name`: string | `windowId`: string? |
| `resize_pane` | none | `paneId`: string?, `width`: integer?, `height`: integer?, `zoom`: boolean = false |
| `resize_window` | none | `windowId`: string?, `width`: integer?, `height`: integer? |
| `respawn_pane` | none | `paneId`: string?, `startDirectory`: string?, `killExistingProcess`: boolean = false |
| `run_shell_command` | `command`: string | `paneId`: string?, `timeoutSeconds`: number?, `maxLines`: integer?, `suppressHistory`: boolean = false |
| `search_panes` | `pattern`: string | `session`: string?, `includeHistory`: boolean = false, `ignoreCase`: boolean = true, `maxMatchesPerPane`: integer = 20 |
| `select_layout` | none | `windowId`: string?, `layout`: string? |
| `select_pane` | `paneId`: string | none |
| `select_window` | `windowId`: string | none |
| `send_keys` | `keys`: string | `paneId`: string?, `enter`: boolean = false, `literal`: boolean = true, `suppressHistory`: boolean = false |
| `send_keys_batch` | `operations`: object[] of 1 to 64 | `onError`: "continue" or "stop" = "stop" |
| `set_history_limit` | `lines`: integer, `session`: string | none |
| `set_mouse_enabled` | `enabled`: boolean | none |
| `set_pane_title` | `title`: string | `paneId`: string? |
| `set_synchronize_panes` | `enabled`: boolean | `windowId`: string? |
| `show_environment` | none | `name`: string?, `session`: string? |
| `show_hooks` | none | `scope`: "Server" or "Session" or "Window" or "Pane" = "Session", `paneId`: string? |
| `show_option` | `name`: string | `scope`: "Server" or "Session" or "Window" or "Pane" = "Pane", `paneId`: string? |
| `signal_channel` | `channel`: string | none |
| `snapshot_pane` | none | `paneId`: string?, `maxLines`: integer? |
| `split_window` | none | `paneId`: string?, `direction`: "Above" or "Below" or "Left" or "Right" = "Below", `startDirectory`: string?, `percentage`: integer? |
| `swap_pane` | `paneId`: string, `targetPaneId`: string | `detach`: boolean = false, `keepZoom`: boolean = false |
| `wait_for_channel` | `channel`: string | `timeoutSeconds`: number? |
| `wait_for_text` | none | `paneId`: string?, `patterns`: object[]?, `stopPatterns`: object[]?, `timeoutSeconds`: number?, `ignoreCase`: boolean = true |

An omitted parameter means:

| Parameter | When omitted | Tools |
|---|---|---|
| `cursor` | start from what is on screen now | `capture_since` |
| `destination` | the next free index, as an empty one does | `move_window` |
| `height` | keep the current height | `resize_pane`, `resize_window` |
| `height` | tmux's default of 24 | `create_session` |
| `layout` | reapply the window's last preset layout, if it has had one | `select_layout` |
| `maxLines` | the server default, 500 unless LIBTMUX_MCP_MAX_LINES sets another | `capture_pane`, `capture_since`, `run_shell_command`, `snapshot_pane` |
| `name` | every name with hasValue instead of values | `show_environment` |
| `name` | tmux names it after the program it runs | `create_window` |
| `name` | tmux names it with a number | `create_session` |
| `paneId` | this server's own pane, or else the one the first session shows | `capture_pane`, `capture_since`, `clear_pane_scrollback`, `get_tmux_variables`, `paste_text`, `resize_pane`, `respawn_pane`, `run_shell_command`, `send_keys`, `set_pane_title`, `show_hooks`, `show_option`, `snapshot_pane`, `split_window`, `wait_for_text` |
| `patterns` | return on any new output | `wait_for_text` |
| `percentage` | half | `split_window` |
| `session` | every session | `list_panes`, `list_windows`, `search_panes` |
| `session` | stay in the window's session | `move_window` |
| `session` | the first session | `create_window`, `rename_session` |
| `session` | the server environment | `show_environment` |
| `startDirectory` | the MCP server's own working directory | `create_session`, `create_window`, `split_window` |
| `startDirectory` | the directory the pane started in before | `respawn_pane` |
| `stopPatterns` | none | `wait_for_text` |
| `timeoutSeconds` | wait the whole ceiling, 30 unless LIBTMUX_MCP_WAIT_MAX_SECONDS sets another | `run_shell_command`, `wait_for_channel`, `wait_for_text` |
| `width` | keep the current width | `resize_pane`, `resize_window` |
| `width` | tmux's default of 80 | `create_session` |
| `windowId` | every window | `list_panes` |
| `windowId` | this server's own window, or else the one the first session shows | `rename_window`, `resize_window`, `select_layout`, `set_synchronize_panes` |

`call_read_tools_batch` runs any of: `capture_pane`, `capture_since`, `find_pane_by_position`, `get_pane_info`, `get_server_info`, `get_session_info`, `get_tmux_variables`, `get_window_info`, `list_panes`, `list_sessions`, `list_windows`, `search_panes`, `show_environment`, `show_hooks`, `show_option`, `snapshot_pane`.

## Results

Each tool's output schema describes its result; these are its fields.

| Tool | Fields |
|---|---|
| `call_read_tools_batch` | `results`[] (`index`, `tool`, `success`, `error`, `result`, `resultTruncated`), `succeeded`, `failed`, `stoppedAt`, `truncated`, `truncatedBytes`, `onError` |
| `capture_pane` | `paneId`, `content` (`lines`, `truncated`, `droppedLines`, `droppedBytes`) |
| `capture_since` | `paneId`, `content` (`lines`, `truncated`, `droppedLines`, `droppedBytes`), `cursor`, `linesMissed`, `anchorLost` |
| `clear_pane_scrollback` | `changed`, `paneId`, `windowId`, `sessionId` |
| `create_session` | `changed`, `paneId`, `windowId`, `sessionId` |
| `create_window` | `changed`, `paneId`, `windowId`, `sessionId` |
| `find_pane_by_position` | `paneId`, `windowId`, `sessionId`, `index`, `width`, `height`, `title`, `active`, `dead`, `zoomed`, `inMode`, `currentCommand`, `currentPath`, `pid`, `historySize`, `historyLimit`, `isCaller` |
| `get_pane_info` | `paneId`, `windowId`, `sessionId`, `index`, `width`, `height`, `title`, `active`, `dead`, `zoomed`, `inMode`, `currentCommand`, `currentPath`, `pid`, `historySize`, `historyLimit`, `isCaller` |
| `get_server_info` | `socketName`, `socketPath`, `version`, `sessionCount`, `windowCount`, `paneCount`, `callerPaneId`, `callerPaneSocket` |
| `get_session_info` | `sessionId`, `name`, `attached`, `windowCount` |
| `get_tmux_variables` | a map from each requested name to its value |
| `get_window_info` | `windowId`, `sessionId`, `index`, `name`, `width`, `height`, `active`, `paneCount`, `layout` |
| `kill_pane` | `changed`, `paneId`, `windowId`, `sessionId` |
| `kill_session` | `changed`, `paneId`, `windowId`, `sessionId` |
| `kill_window` | `changed`, `paneId`, `windowId`, `sessionId` |
| `list_panes` | `result`[] (`paneId`, `windowId`, `sessionId`, `index`, `width`, `height`, `title`, `active`, `dead`, `zoomed`, `inMode`, `currentCommand`, `currentPath`, `pid`, `historySize`, `historyLimit`, `isCaller`) |
| `list_sessions` | `result`[] (`sessionId`, `name`, `attached`, `windowCount`) |
| `list_windows` | `result`[] (`windowId`, `sessionId`, `index`, `name`, `width`, `height`, `active`, `paneCount`, `layout`) |
| `move_window` | `changed`, `paneId`, `windowId`, `sessionId` |
| `paste_text` | `changed`, `paneId`, `windowId`, `sessionId` |
| `rename_session` | `changed`, `paneId`, `windowId`, `sessionId` |
| `rename_window` | `changed`, `paneId`, `windowId`, `sessionId` |
| `resize_pane` | `changed`, `paneId`, `windowId`, `sessionId` |
| `resize_window` | `changed`, `paneId`, `windowId`, `sessionId` |
| `respawn_pane` | `changed`, `paneId`, `windowId`, `sessionId` |
| `run_shell_command` | `paneId`, `exitStatus`, `timedOut`, `output` (`lines`, `truncated`, `droppedLines`, `droppedBytes`), `elapsedSeconds`, `effectiveTimeoutSeconds`, `linesMissed`, `anchorLost`, `started`, `paneExited` |
| `search_panes` | `pattern`, `panesSearched`, `panes`[] (`paneId`, `windowId`, `sessionId`, `matches`), `truncated` |
| `select_layout` | `changed`, `paneId`, `windowId`, `sessionId` |
| `select_pane` | `changed`, `paneId`, `windowId`, `sessionId` |
| `select_window` | `changed`, `paneId`, `windowId`, `sessionId` |
| `send_keys` | `changed`, `paneId`, `targetPaneIds`[] |
| `send_keys_batch` | `results`[] (`index`, `paneId`, `success`, `error`, `targetPaneIds`), `succeeded`, `failed`, `stoppedAt`, `onError` |
| `set_history_limit` | `changed`, `paneId`, `windowId`, `sessionId` |
| `set_mouse_enabled` | `changed`, `paneId`, `windowId`, `sessionId` |
| `set_pane_title` | `changed`, `paneId`, `windowId`, `sessionId` |
| `set_synchronize_panes` | `changed`, `paneId`, `windowId`, `sessionId` |
| `show_environment` | `result`[] (`name`, `value`, `isRemoved`, `hasValue`, `withheld`) |
| `show_hooks` | `result`[] (`name`, `index`, `command`, `scope`) |
| `show_option` | `result`[] (`name`, `value`, `scope`, `inherited`) |
| `signal_channel` | `changed`, `paneId`, `windowId`, `sessionId` |
| `snapshot_pane` | `pane` (`paneId`, `windowId`, `sessionId`, `index`, `width`, `height`, `title`, `active`, `dead`, `zoomed`, `inMode`, `currentCommand`, `currentPath`, `pid`, `historySize`, `historyLimit`, `isCaller`), `content` (`lines`, `truncated`, `droppedLines`, `droppedBytes`), `cursorX`, `cursorY`, `alternateScreen` |
| `split_window` | `changed`, `paneId`, `windowId`, `sessionId` |
| `swap_pane` | `changed`, `paneId`, `windowId`, `sessionId` |
| `wait_for_channel` | `changed`, `channel`, `signalled`, `elapsedSeconds`, `effectiveTimeoutSeconds` |
| `wait_for_text` | `paneId`, `outcome`, `matchedPattern`, `tail` (`lines`, `truncated`, `droppedLines`, `droppedBytes`), `elapsedSeconds`, `effectiveTimeoutSeconds` |

## Resources

| URI | Does |
|---|---|
| `tmux://capabilities` | The startup-frozen tmux connection and effective MCP tool capabilities. |
