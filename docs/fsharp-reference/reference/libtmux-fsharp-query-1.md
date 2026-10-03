## Query<'T> type

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Base Type: <code>obj</code>

Describes a tmux listing: a scope, filters and text panes must show.

Building a query reads nothing; each run reads tmux again. tmux narrows the
 listing with its own filter where it can evaluate one exactly, and every
 row is then checked against the portable filters.
