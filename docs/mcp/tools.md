# tmux MCP tools

Generated from the server itself — a table nobody generates is wrong the
first time somebody adds a tool. Regenerate after changing the surface:

```console
$ uv run eng/mcp/dump_tools.py
```

47 tools and 1 static resource. No dynamic resource
templates or prompts are registered (0 templates, 0 prompts).

Every row is the capability object advertised with the tool under
`_meta["com.git-pull.libtmux-mcp/capability"]`. Effects and output classes are sets.
All protocol annotations are conservative: read-only false, destructive true,
idempotent false, and open-world true.

| Tool | Toolset | Reach | Effects | Output classes | Does |
|---|---|---|---|---|---|
| `call_read_tools_batch` | inspect | none | observe | configured-command, process-environment, terminal-content, tmux-metadata | Read pane output; accepts no client-supplied executable input. |
| `capture_pane` | inspect | none | observe | terminal-content, tmux-metadata | Read pane output; accepts no client-supplied executable input. |
| `capture_since` | inspect | none | observe | terminal-content, tmux-metadata | Read pane output; accepts no client-supplied executable input. |
| `clear_pane_scrollback` | teardown | none | delete | tmux-metadata | Delete tmux state; accepts no command payload. |
| `create_session` | execute | configured-process | change, observe | tmux-metadata | Start a pane's configured process; accepts no command payload. |
| `create_window` | execute | configured-process | change, observe | tmux-metadata | Start a pane's configured process; accepts no command payload. |
| `enter_copy_mode` | manage | none | change, observe | tmux-metadata | Change tmux state; no client-supplied executable input. |
| `exit_copy_mode` | manage | none | change, observe | tmux-metadata | Change tmux state; no client-supplied executable input. |
| `find_pane_by_position` | inspect | none | observe | tmux-metadata | Inspect tmux metadata; accepts no client-supplied executable input. |
| `get_pane_info` | inspect | none | observe | tmux-metadata | Inspect tmux metadata; accepts no client-supplied executable input. |
| `get_server_info` | inspect | none | observe | tmux-metadata | Inspect tmux metadata; accepts no client-supplied executable input. |
| `get_session_info` | inspect | none | observe | tmux-metadata | Inspect tmux metadata; accepts no client-supplied executable input. |
| `get_tmux_variables` | inspect | none | observe | configured-command, tmux-metadata | Read configured tmux commands; accepts no client-supplied executable input. |
| `get_window_info` | inspect | none | observe | tmux-metadata | Inspect tmux metadata; accepts no client-supplied executable input. |
| `kill_pane` | teardown | none | delete, observe | tmux-metadata | Delete tmux state; accepts no command payload. |
| `kill_session` | teardown | none | delete, observe | tmux-metadata | Delete tmux state; accepts no command payload. |
| `kill_window` | teardown | none | delete, observe | tmux-metadata | Delete tmux state; accepts no command payload. |
| `list_panes` | inspect | none | observe | tmux-metadata | Inspect tmux metadata; accepts no client-supplied executable input. |
| `list_sessions` | inspect | none | observe | tmux-metadata | Inspect tmux metadata; accepts no client-supplied executable input. |
| `list_windows` | inspect | none | observe | tmux-metadata | Inspect tmux metadata; accepts no client-supplied executable input. |
| `move_window` | manage | none | change, observe | tmux-metadata | Change tmux state; no client-supplied executable input. |
| `paste_text` | execute | pane-input | change, observe | tmux-metadata | Send input to a pane's program; a shell that receives it runs it with your user's permissions. |
| `rename_session` | manage | none | change, observe | tmux-metadata | Change tmux state; no client-supplied executable input. |
| `rename_window` | manage | none | change, observe | tmux-metadata | Change tmux state; no client-supplied executable input. |
| `resize_pane` | manage | none | change, observe | tmux-metadata | Change tmux state; no client-supplied executable input. |
| `resize_window` | manage | none | change, observe | tmux-metadata | Change tmux state; no client-supplied executable input. |
| `respawn_pane` | execute | configured-process | change, delete, observe | tmux-metadata | Start a pane's configured process; accepts no command payload. |
| `run_shell_command` | execute | pane-command | change, observe | terminal-content, tmux-metadata | Run a shell command in a pane with your user's permissions. |
| `search_panes` | inspect | none | observe | terminal-content, tmux-metadata | Read pane output; accepts no client-supplied executable input. |
| `select_layout` | manage | none | change, observe | tmux-metadata | Change tmux state; no client-supplied executable input. |
| `select_pane` | manage | none | change, observe | tmux-metadata | Change tmux state; no client-supplied executable input. |
| `select_window` | manage | none | change, observe | tmux-metadata | Change tmux state; no client-supplied executable input. |
| `send_keys` | execute | pane-input | change, observe | tmux-metadata | Send input to a pane's program; a shell that receives it runs it with your user's permissions. |
| `send_keys_batch` | execute | pane-input | change, observe | tmux-metadata | Send input to a pane's program; a shell that receives it runs it with your user's permissions. |
| `set_history_limit` | manage | none | change | tmux-metadata | Change tmux state; no client-supplied executable input. |
| `set_mouse_enabled` | manage | none | change | tmux-metadata | Change tmux state; no client-supplied executable input. |
| `set_pane_title` | manage | none | change, observe | tmux-metadata | Change tmux state; no client-supplied executable input. |
| `set_synchronize_panes` | execute | none | change | tmux-metadata | Change tmux state; no client-supplied executable input. |
| `show_environment` | inspect | none | observe | process-environment | Read the tmux environment; accepts no client-supplied executable input. |
| `show_hooks` | inspect | none | observe | configured-command | Read configured tmux commands; accepts no client-supplied executable input. |
| `show_option` | inspect | none | observe | configured-command, tmux-metadata | Read configured tmux commands; accepts no client-supplied executable input. |
| `signal_channel` | manage | none | change | tmux-metadata | Change tmux state; no client-supplied executable input. |
| `snapshot_pane` | inspect | none | observe | terminal-content, tmux-metadata | Read pane output; accepts no client-supplied executable input. |
| `split_window` | execute | configured-process | change, observe | tmux-metadata | Start a pane's configured process; accepts no command payload. |
| `swap_pane` | manage | none | change, observe | tmux-metadata | Change tmux state; no client-supplied executable input. |
| `wait_for_channel` | manage | none | change | tmux-metadata | Change tmux state; no client-supplied executable input. |
| `wait_for_text` | inspect | none | observe | terminal-content, tmux-metadata | Read pane output; accepts no client-supplied executable input. |

## Resources

| URI | Does |
|---|---|
| `tmux://capabilities` | The startup-frozen tmux connection and effective MCP tool capabilities. |
