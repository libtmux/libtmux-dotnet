# Complete API programs

These programs run as standalone package consumers. Each file contains its
imports, entry point, owned tmux server, cancellation deadline, assertions and
cleanup. The manifest identifies whole files, including their final newline,
for API pages to publish from a recorded source revision. Every import and
helper is present in the displayed file.

The F# programs use `LibTmux.Server.CreateOwnedAsync` and the core session and
window methods for construction. The F# facade supplies task, snapshot, query
and option helpers; it has no separate server constructor.

| Program | Task |
| --- | --- |
| [ServerListings.fs](../LibTmux.FSharp.Examples/Programs/ServerListings.fs) | Read sessions, windows, panes and clients |
| [ServerLookups.fs](../LibTmux.FSharp.Examples/Programs/ServerLookups.fs) | Find live entities and handle `None` |
| [ServerFilters.fs](../LibTmux.FSharp.Examples/Programs/ServerFilters.fs) | Use native sequences, portable filters and captured relations |
| [SnapshotAccess.fs](../LibTmux.FSharp.Examples/Programs/SnapshotAccess.fs) | Distinguish uncaptured, captured and empty state |
| [SelectionResults.fs](../LibTmux.FSharp.Examples/Programs/SelectionResults.fs) | Match all `Selection.exactlyOne` results |
| [LookupFailures.fs](../LibTmux.FSharp.Examples/Programs/LookupFailures.fs) | Keep cancellation, invalid input and failed reads distinct from absence |
| [CreateWindowPane.fs](../LibTmux.FSharp.Examples/Programs/CreateWindowPane.fs) | Own a session and window, then split a pane |
| [InputCapture.fs](../LibTmux.FSharp.Examples/Programs/InputCapture.fs) | Send literal text and Enter, then capture signalled output |

## Run a complete F# program

Use Linux or macOS with Git, tmux 3.2a or newer, .NET SDK 10.0.302 and the
.NET 8 and 10 runtimes. From a checkout containing these files, make a separate
consumer directory. This clone selects the current committed revision. An API
page must supply an HTTPS clone and an exact checkout revision.

```console
$ consumer=$(mktemp -d)
$ git clone --no-hardlinks . "$consumer/libtmux-source"
$ git -C "$consumer/libtmux-source" checkout --detach "$(git rev-parse HEAD)"
$ cd "$consumer"
$ cp libtmux-source/global.json .
$ cp libtmux-source/examples/api/NuGet.config .
$ cp libtmux-source/examples/api/fsharp/Example.fsproj .
$ cp libtmux-source/examples/LibTmux.FSharp.Examples/Programs/InputCapture.fs Program.fs
$ export NUGET_PACKAGES="$consumer/packages"
$ dotnet restore libtmux-source/src/LibTmux.FSharp/LibTmux.FSharp.fsproj --locked-mode
$ dotnet pack libtmux-source/src/LibTmux/LibTmux.csproj \
    --configuration Release --no-restore -p:ContinuousIntegrationBuild=true \
    --output "$consumer/libtmux-source/artifacts/api-example-packages"
$ dotnet pack libtmux-source/src/LibTmux.FSharp/LibTmux.FSharp.fsproj \
    --configuration Release --no-restore -p:ContinuousIntegrationBuild=true \
    --output "$consumer/libtmux-source/artifacts/api-example-packages"
$ dotnet restore Example.fsproj --configfile NuGet.config
$ dotnet build Example.fsproj --configuration Release --no-restore --warnaserror
$ export LIBTMUX_TMUX="$(command -v tmux)"
$ export TMUX_TMPDIR=/tmp/libtmux-dotnet-dev
$ mkdir -p "$TMUX_TMPDIR"
$ unset TMUX TMUX_PANE
$ dotnet bin/Release/net8.0/Example.dll
api capture ready
$ dotnet bin/Release/net10.0/Example.dll
api capture ready
```

Copy another complete program to `Program.fs` to run it with the same setup.
The project references only `LibTmux.FSharp` and its FSharp.Core dependency;
it receives the core `LibTmux` package transitively. The NuGet configuration
maps library packages to the local source build so an unrelated published
package with the same version cannot satisfy the restore.

The programs create private socket names. `use!` disposes their ownership
scopes in reverse order, including after cancellation or an exception. Scope
cleanup uses its own bounded token. Cancellation stops waiting for an
operation; it does not undo input or mutations already sent to tmux. An
unhandled error exits the program unsuccessfully after cleanup.

`InputCapture.fs` waits for a private signal sent by the pane's shell after
printing. Its capture assertion compares a whole line after removing terminal
padding, so the echoed command cannot satisfy it. The wait channel is also
disposed. `SnapshotAccess.fs` turns off `exit-empty` on its own server to show
an empty captured relation after disposing the last session; the outer server
scope still stops that daemon.

## Run a complete C# program

The C# programs use the same Linux/macOS, tmux and .NET prerequisites as the
F# setup. They contain explicit `using` directives and a top-level async entry
point. Each file rejects Windows before calling the Unix tmux APIs.

| Program | Task |
| --- | --- |
| [ServerConstruction.cs](../LibTmux.Examples/Programs/ServerConstruction.cs) | Open an endpoint and discover its live server |
| [ServerListings.cs](../LibTmux.Examples/Programs/ServerListings.cs) | Read sessions, windows, panes and clients |
| [CreateWindowPane.cs](../LibTmux.Examples/Programs/CreateWindowPane.cs) | Create a session and window, then split a pane |
| [ServerQueries.cs](../LibTmux.Examples/Programs/ServerQueries.cs) | Compare LINQ, portable predicates and captured hierarchy queries |
| [EntityLookups.cs](../LibTmux.Examples/Programs/EntityLookups.cs) | Distinguish nullable absence, required lookup, cancellation and failed reads |
| [SnapshotAccess.cs](../LibTmux.Examples/Programs/SnapshotAccess.cs) | Distinguish uncaptured, captured and empty state |
| [InputCapture.cs](../LibTmux.Examples/Programs/InputCapture.cs) | Send literal text and Enter, then capture signalled output |

From a checkout containing these files, create an external consumer:

```console
$ consumer=$(mktemp -d)
$ git clone --no-hardlinks . "$consumer/libtmux-source"
$ git -C "$consumer/libtmux-source" checkout --detach "$(git rev-parse HEAD)"
$ cd "$consumer"
$ cp libtmux-source/global.json .
$ cp libtmux-source/examples/api/NuGet.config .
$ cp libtmux-source/examples/api/csharp/Example.csproj .
$ cp libtmux-source/examples/LibTmux.Examples/Programs/InputCapture.cs Program.cs
$ export NUGET_PACKAGES="$consumer/packages"
$ dotnet restore libtmux-source/src/LibTmux/LibTmux.csproj --locked-mode
$ dotnet pack libtmux-source/src/LibTmux/LibTmux.csproj \
    --configuration Release --no-restore -p:ContinuousIntegrationBuild=true \
    --output "$consumer/libtmux-source/artifacts/api-example-packages"
$ dotnet restore Example.csproj --configfile NuGet.config
$ dotnet build Example.csproj --configuration Release --no-restore --warnaserror
$ export LIBTMUX_TMUX="$(command -v tmux)"
$ export TMUX_TMPDIR=/tmp/libtmux-dotnet-dev
$ mkdir -p "$TMUX_TMPDIR"
$ unset TMUX TMUX_PANE
$ dotnet bin/Release/net8.0/Example.dll
api capture ready
$ dotnet bin/Release/net10.0/Example.dll
api capture ready
```

Copy another complete program to `Program.cs` and rebuild to run it with the
same setup. This project compiles only that file, disables implicit imports,
and references only the source-built `LibTmux` package. API pages must provide
an HTTPS clone and exact checkout revision in place of the local clone above.

`await using` disposes ownership scopes after success, cancellation or an
exception. The directly created entities in `CreateWindowPane.cs` belong to
its outer private server scope; disposing it stops the daemon and those
entities. The wait channel in `InputCapture.cs` is also asynchronously
disposed. Its whole-line assertion and signal use the same semantics as the
F# capture program.

`ServerQueries.cs` runs predicates on already read rows. Translating an
expression does no tmux I/O. Predicates that traverse children need an
explicitly captured hierarchy. Portable query evaluation uses reflection and
is not presented as a trimmed or Native AOT example; the repository's separate
AOT programs cover that deployment mode.

## Attachment and execution checks

[manifest.json](manifest.json) binds each complete program to exact public C# and F#
compiler IDs. The verifier rejects unknown targets, repeated targets within
one example, omitted programs, paths outside the repository, hidden project
imports and changed package mappings. Descriptions and expected output belong
to this source manifest, alongside the files they describe.

After the ordinary build, compiler inventory and package gates, validate it:

```console
$ python3 eng/docs/verify_api_examples.py
```

The packed-example workflow uses the same verifier to copy every displayed
file to an external consumer directory, restore the native packages, compile
both target frameworks, and compare exact stdout. It also checks that each
program leaves no live owned server. A failed program still produces a
receipt; emergency cleanup addresses only its newly allocated socket root.
The existing tmux version matrix compiles and runs every complete program too.

```console
$ python3 eng/docs/verify_api_examples.py \
    --packages artifacts/packages \
    --output /tmp/libtmux-dotnet-api-consumers
```

Choose an output directory that does not exist. `receipt.json` records source
revision and dirty state, whole-file hashes, package hashes, commands, output
and cleanup. The consumer gate uses already packed artifacts; it does not
claim to test a public clone. Public revision checks run the displayed clone
and source-pack setup separately before the API site pins that revision.
