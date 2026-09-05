# MCP agent instructions

Keep this package a semantic, detached-safe tmux interface.

- Put semantic tmux operations that are safe without an attached client in the
  MCP.
- Leave modal UI to human clients: key-table navigation, selections, prompts,
  menus, popups, clock mode, choose-tree, and mouse gestures.
- Expose observational mode state and mode-screen capture only when doing so
  does not enter or cancel a mode.
- Check the current delivery recipients on every input path and fail closed if
  any recipient cannot safely receive input.
- Make capability rows follow ADR CM-1 through CM-7 and derive registration,
  dispatch, disclosure, and generated documentation from one native registry.
- Preserve README structure, links, examples, and compiled snippets when
  documentation changes.
