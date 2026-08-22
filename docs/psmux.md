# psmux query preview on Windows

LibTmux contains an experimental facade for querying one isolated
[psmux](https://github.com/psmux/psmux) namespace from native Windows .NET or
from a WSL .NET process launching a Windows executable. The release workflow
requires both paths to pass on net8.0 and net10.0; this is a deliberately narrow
preview, not general Windows or tmux parity.

The public preview is a separate, analyzer-clean API. It exposes only the
behavior audited for the pinned psmux build:

| Surface | Preview state |
| --- | --- |
| Connect and read the sole session | `PsmuxServer`, `PsmuxSession` |
| Enumerate windows and panes | `PsmuxWindow`, `PsmuxPane` |
| Capture pane text | `PsmuxPane.CaptureAsync(PsmuxCaptureOptions)` |
| Accepted client artifact | One exact Windows x64 executable; release requires a published URL with pinned source and license provenance |
| Native Windows .NET | Required by the release harness on net8.0 and net10.0 |
| WSL-to-Windows process interop | Required by the release harness on net8.0 and net10.0 |
| Session/server lifecycle or mutations | Rejected |
| Raw commands, chaining, control mode, waits, streaming, MCP | Rejected or unavailable |
| Multiple sessions, default namespace, socket paths | Rejected |

The ordinary `Server`, `Session`, `Window`, and `Pane` surface keeps its Windows
unsupported annotations because its lifecycle, mutation, grouping, control-mode,
and atomic stale-handle contracts still require real tmux. Preview callers do
not suppress `CA1416`; they use the `Psmux*` types instead.

## Pinned build and trust boundary

The only accepted client is the Windows x64 `psmux.exe` published in the psmux
`v3.3.8` release, built from commit
`66cf61354c473b35d4f0c06c57384fc46d61ffdb`, with the exact banner
`psmux 3.3.8 (66cf613 2026-08-18)` and executable SHA-256
`54e5c54db259218348f966b5d0d0b5153fdef6350074855ea9ce627d20537b0d`.
`PsmuxConnectionOptions` requires:

- an absolute `.exe` path on a fixed local Windows drive rather than `PATH` or
  a network share;
- that exact SHA-256, also exposed as `PsmuxServer.SupportedBinarySha256`;
- an absolute local-drive `PSMUX_DATA_DIR` dedicated to this integration; and
- an explicit, non-default, 16–64 character namespace containing one session.

The namespace uses lowercase ASCII letters, digits, `-`, and `_`; `__` is
rejected. It should be high entropy because psmux discovers `-L name`
registries with a prefix scan. Session names may also use uppercase ASCII.
The data-directory drive letter is uppercased and its segments lowercased
before endpoint identity is constructed. Native Windows rejects mapped,
removable, and network drives. WSL cannot query the Windows drive type, so WSL
callers must ensure both paths are backed by a fixed local Windows drive.
Network shares, filesystem roots, and reserved Windows segments are rejected
because psmux treats this directory as machine-local registry and
process-ownership state. Case-sensitive Windows directories are outside this
preview.

LibTmux hashes the client and checks its audited build markers before every
launch, then requires the exact two-line version banner. Those checks attest
the client file only. They do not attest the already-running server executable,
its configuration, or replacement of the path between verification and
`Process.Start`. Provision the session with the same verified clean binary at a
caller-controlled, immutable, non-symlink path and an alias-free configuration
with warm helpers disabled.

The psmux 3.3.7 release build at `05cc5d4` is unsafe for this integration. Its
startup reaper can terminate psmux, tmux, or pmux listeners owned by another
data directory or Windows profile, and that reaper runs before `-V` is parsed.
Never point LibTmux or the smoke harness at that installed build; rejecting its
banner would already be too late.

### Artifact availability

The accepted client is a published psmux release asset, so anyone can obtain
the exact bytes this preview accepts:

```console
$ curl --fail --location --proto '=https' --tlsv1.2 \
    --output psmux-v3.3.8-windows-x64.zip \
    https://github.com/psmux/psmux/releases/download/v3.3.8/psmux-v3.3.8-windows-x64.zip
```

The archive is SHA-256
`1ad127ba937194a890b933a73d9b023e297bd73dc742abd841bf159984c2effe`, and the
`psmux.exe` inside it is the pinned client hash above. It also carries psmux's
MIT `LICENSE`, so nothing has to redistribute a copy. Verify both hashes before
use: a matching source banner is not a substitute for the pinned SHA-256.

Rebuilding this source does not reproduce these bytes. The upstream release
workflow builds with the moving `windows-latest` image and a `stable` Rust
toolchain, and psmux pins no toolchain channel of its own. Download the
published asset and check its hash; do not build psmux and assume the result is
accepted.

Commit `aa26cd3` — an untagged build that earlier releases of this preview
pinned — is contained in `v3.3.8`. The startup reaper that makes an unaudited
client dangerous is byte-identical between the two, so the audited behaviour is
what the published release ships.

What a release needs on top of this contract — the WSL repository variables and
the self-hosted runner — is in
[`CONTRIBUTING.md`](../.github/CONTRIBUTING.md#the-psmux-preview-gates).

## Query from C#

The example below is compiled as part of the examples project and published
from that source. It takes all endpoint trust values explicitly and cannot
reach mutations, lifecycle, raw commands, chains, or control mode.

<!-- snippet: QueryPsmux usings: LibTmux -->
```csharp
using LibTmux;

using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
CancellationToken cancellationToken = cancellation.Token;

string executable = Environment.GetEnvironmentVariable("LIBTMUX_PSMUX_BINARY")
    ?? throw new InvalidOperationException("LIBTMUX_PSMUX_BINARY is required.");
string dataDirectory = Environment.GetEnvironmentVariable("PSMUX_DATA_DIR")
    ?? throw new InvalidOperationException("PSMUX_DATA_DIR is required.");
string namespaceName = Environment.GetEnvironmentVariable("LIBTMUX_PSMUX_NAMESPACE")
    ?? throw new InvalidOperationException("LIBTMUX_PSMUX_NAMESPACE is required.");

PsmuxServer server = await PsmuxServer.ConnectAsync(
    new PsmuxConnectionOptions(
        executablePath: executable,
        expectedBinarySha256: PsmuxServer.SupportedBinarySha256,
        dataDirectory: dataDirectory,
        namespaceName: namespaceName),
    cancellationToken);
PsmuxSession session = await server.GetSessionAsync(cancellationToken);

Console.WriteLine($"{session.Id} {session.Name}");
foreach (PsmuxWindow window in await session.GetWindowsAsync(cancellationToken))
{
    Console.WriteLine($"  {window.Id} {window.Index}: {window.Name}");
    foreach (PsmuxPane pane in await window.GetPanesAsync(cancellationToken))
    {
        IReadOnlyList<string> lines = await pane.CaptureAsync(
            new PsmuxCaptureOptions(joinWrappedLines: true),
            cancellationToken);
        Console.WriteLine($"    {pane.Id} {pane.Width}x{pane.Height}");
        foreach (string line in lines)
        {
            Console.WriteLine($"      {line}");
        }
    }
}
```
<!-- endsnippet -->

`PsmuxSession`, `PsmuxWindow`, and `PsmuxPane` are immutable observations, not
tmux-style stable handles. Call the query methods again for a fresh observation.
Cancellation covers the streamed prelaunch hash and every client process; the
facade introduces no synchronous file read or process wait.

Each `PsmuxServer` is bound to the session generation seen at connection time.
A later query throws `InvalidOperationException` if no live session remains and
`StaleServerGenerationException` if the sole session was replaced; the latter
derives from the former. Call `RefreshAsync` to obtain a replacement server
observation. More than one visible session throws `NotSupportedException`, and
a vanished window or pane can throw `TmuxObjectNotFoundException` during target
preflight.

## Native Windows and WSL smoke

Build the complete solution from WSL first. This produces both target
frameworks for every packable project and for the checked-in example:

```console
$ mise exec -- dotnet build \
    LibTmux.slnx \
    --configuration Release \
    --warnaserror
```

Pack the final tree, then restore and build the downstream package consumer
through a newly allocated package cache. A fresh cache prevents an older
package with the same prerelease version from satisfying the test:

```console
$ mise exec -- dotnet pack \
    LibTmux.slnx \
    --configuration Release \
    --no-build \
    --output artifacts/packages
```

```console
$ package_cache="$(mktemp -d /tmp/libtmux-dotnet-psmux-nuget.XXXXXX)" && \
    NUGET_PACKAGES="$package_cache" mise exec -- dotnet restore \
        LibTmux.slnx \
        --locked-mode && \
    NUGET_PACKAGES="$package_cache" mise exec -- dotnet restore \
        tests/LibTmux.PackageConsumer/LibTmux.PackageConsumer.csproj && \
    NUGET_PACKAGES="$package_cache" mise exec -- dotnet build \
        tests/LibTmux.PackageConsumer/LibTmux.PackageConsumer.csproj \
        --configuration Release \
        --framework net10.0 \
        --no-restore \
        --warnaserror
```

Then run the checked-in harness from native Windows PowerShell. Replace the
example paths with paths on the test machine, but retain the exact SHA shown
below: the harness rejects every other artifact. `DataDirectory` must be a
fresh, nonexistent, high-entropy directory for each run; the harness refuses an
existing directory rather than overwriting anything in it. It disables psmux's
warm helper, removes only the exact session identity it created, and deletes the
owned directory only after the server process and every live registry entry are
gone. The optional WSL arguments make the same native PowerShell process keep
the server alive while both native .NET and WSL .NET query it:

```console
$ & '\\wsl.localhost\<distribution>\home\<user>\libtmux-dotnet\eng\psmux\Invoke-PsmuxSmoke.ps1' `
    -PsmuxPath 'C:\Tools\psmux-v3.3.8\psmux.exe' `
    -ExpectedSha256 '54e5c54db259218348f966b5d0d0b5153fdef6350074855ea9ce627d20537b0d' `
    -DataDirectory 'C:\Users\me\AppData\Local\Temp\libtmux-psmux-smoke-01a00fd3' `
    -NamespaceName 'libtmux_smoke_01a00fd3' `
    -DotnetPath 'C:\Program Files\dotnet\dotnet.exe' `
    -TestAssembly '\\wsl.localhost\<distribution>\home\<user>\libtmux-dotnet\tests\LibTmux.UnitTests\bin\Release\net10.0\LibTmux.UnitTests.dll' `
    -ExampleAssembly '\\wsl.localhost\<distribution>\home\<user>\libtmux-dotnet\examples\LibTmux.Examples\bin\Release\net10.0\LibTmux.Examples.dll' `
    -PackageConsumerAssembly '\\wsl.localhost\<distribution>\home\<user>\libtmux-dotnet\tests\LibTmux.PackageConsumer\bin\Release\net10.0\LibTmux.PackageConsumer.dll' `
    -TargetFramework 'net10.0' `
    -RunWslSmoke `
    -WslDistribution '<distribution>' `
    -WslRepository '/home/<user>/libtmux-dotnet' `
    -WslDotnetPath '/home/<user>/.config/mise/dotnet-root/dotnet'
```

Before its first psmux launch, the harness verifies the SHA and embedded audited
build markers, creates the isolated directory, and sets `PSMUX_DATA_DIR`. It
then requires the exact clean banner, creates an alias-free configuration with
warm helpers disabled, starts
exactly one `powershell.exe` session, writes `héllo-雪-😀`, and waits for that
text to be capturable. Each native and optional WSL leg must pass the focused
public-facade test, run the checked-in example, and query through the packed
NuGet consumer. Repeat the build and harness with `net8.0` paths to cover both
target frameworks. Finally, the harness re-resolves the session ID and
generation it recorded after creation, refuses to kill a changed or unknown
identity, removes only its fresh owned directory, and restores the process
environment and console encoding. It never calls `kill-server` or touches the
default namespace. A failing cleanup is a failing harness run.

The WSL leg uses the `/mnt/c/...` executable path while retaining a
Windows-absolute `PSMUX_DATA_DIR`. LibTmux owns and canonicalizes `WSLENV`,
removes inherited tmux routing and every `PSMUX_*` entry case-insensitively,
and forwards only `PSMUX_DATA_DIR/w` without path translation. WSL is a client
in this workflow; native PowerShell owns the psmux server lifecycle. A bounded
translation accepts either a Linux or Windows-absolute `WslRepository`, and a
bounded preflight canonicalizes `WslDotnetPath` inside the selected
distribution, requires the result to be an executable regular file, and
confirms that it supplies the `Microsoft.NETCore.App` runtime matching the
selected target framework. Every WSL leg then invokes that exact path; none
depends on a login profile or the non-login `wsl.exe --exec` search path.

## Exact compatibility limits

- Exactly one pre-existing session must be visible. psmux window and pane IDs
  repeat between session processes, and pid/start generation is per session.
- The client-side allowlist admits only the precise list/display/capture command
  shapes emitted by the typed facade. It rejects empty arguments, NUL, CR, LF,
  quotes, unsafe backslashes, every semicolon, multiple target options, compact
  or unsupported flags, `#(` shell formats, recursive `#{E...}`/`#{T...}`
  formats, noncanonical capture ranges, and relative or symbolic targets.
- This allowlist prevents accidental unsupported use; it is not a security
  boundary. psmux expands configured canonical command aliases server-side,
  and every client invocation performs owned registry maintenance. A trusted,
  alias-free server configuration is required.
- Registry enumeration can omit entries after timeout or authentication
  failures. LibTmux exact-targets and validates each visible row, but cannot
  prove enumeration completeness or eliminate namespace-prefix collisions.
- Session, generation, and object checks are separate client processes. An
  external process can mutate the namespace between a preflight and query.
  psmux may then return active-object data or an `ERROR:` line with exit code
  zero. Target and result parity under external mutation is not claimed.
- Grouped commands and chains are rejected because psmux can silently execute
  only the first argv-level command. Control mode is rejected because its `-C`
  readiness framing is not tmux-compatible.
- Socket paths, default namespaces, forced color modes, per-client config
  files, session creation, lifecycle, mutations, environment discovery, and
  raw commands are absent from the public preview.
- `Server.FromEnvironment()` rejects psmux markers and fake psmux `TMUX` paths;
  it never falls back to an installed `psmux.exe` or fake tmux `-S` routing.
- Numeric version `3.3.8` has no tmux capability profile. Optional tmux flags
  remain disabled rather than being inferred from a nearby release.

This is a core one-shot query preview. `LibTmux.Workspace` and the creation
helpers in `LibTmux.Testing` are unavailable. `LibTmux.Mcp` is also unavailable
for psmux because its writes and waits depend on mutation and control mode.
`LibTmux.Query.Json` remains portable but does not expand this command surface.
