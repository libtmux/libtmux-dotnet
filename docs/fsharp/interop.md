# .NET interoperation

The companion is built on
[LibTmux](https://github.com/libtmux/libtmux-dotnet/) in the same `libtmux`
organization and maintained by the same primary author. It uses the existing
entities, IDs, requests, exceptions, snapshots, and query documents. Pass a
`Server`, `Session`, `Window`, or `Pane` between F# and C# without conversion.

`Task<'T>` remains the default asynchronous contract. Pass the cancellation
token to the façade function explicitly and preserve core exceptions. Use
`option` only for a completed lookup that found no entity; use
`Selection.exactlyOne` when zero and multiple local matches need different
outcomes.

`LibTmux.Query.Json` remains optional. Serialize `Filter.toDocument` through
that package when a portable filter must cross a process or language boundary.
