# Execution modes

The F# companion forwards task-based core operations. It does not select a
transport, control mode, or command chain on a caller's behalf.

Use ordinary core one-shot calls when their task lifetime matches the work.
Use the core control-mode API when a caller owns a live control client and its
asynchronous disposal. Control output is consumptive and fallible; it is not a
replayable `seq`.

No F# control-mode wrapper is documented yet. The core deferred-output and
stream-lifecycle contracts must be proved before such a helper becomes public.
