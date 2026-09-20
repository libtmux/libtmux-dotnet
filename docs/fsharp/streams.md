# Streams and cleanup

Captured snapshots are replayable local observations. Control-mode events are
live, consumptive observations with loss and terminal-failure semantics. Do
not expose a control stream as a replayable sequence or retry a canceled
mutation automatically.

The companion currently has no stream wrapper. Use the core control client
directly and dispose it according to its `IAsyncDisposable` contract. A later
F# stream helper must preserve event order, loss notices, terminal faults,
bounded consumption, and borrowed versus owned client lifetime.
