# LibTmux approved public API

> This is a reviewed contract. Production implementation and passing
> evidence are intentionally absent at this boundary.

The API targets `net8.0` and `net10.0`. Stable tmux releases from `3.2a` onward are supported.
The required compatibility matrix covers
3.2a, 3.3a, 3.4, 3.5, 3.6, 3.7a, 3.7b, 3.7c; tmux master is advisory and `unknown`.
Native Windows tmux execution is unsupported. IDs, snapshots, local query
evaluation, JSON, and pure test helpers remain portable.

Member IDs use the project-stable `libtmux-csharp-contract-v1` grammar;
they are not compiler XML documentation IDs. The grammar retains C# source
aliases, nullable markers, dotted explicit-interface names, and `()` for
zero-argument methods.

## TmuxVersion semantic contract

Minimum support is `3.2a` inclusive; `3.7c` is informational, not a support ceiling.
Stable releases use named capability intervals.
The detection line starts with the exact lowercase prefix `tmux `.
The complete parsing, ordering, detection, and support contract follows.

```json
{
  "grammar": [
    "version = next / release / micro / prerelease",
    "next = \"next-\" core",
    "release = core [patch] [\"-openbsd\"]",
    "micro = core \".\" uint",
    "prerelease = core (\"-rc\" posint / \"-dev\" [\".\" uint])",
    "core = uint \".\" uint",
    "patch = 1*LOWER",
    "uint = \"0\" / (NZDIGIT *DIGIT)",
    "posint = NZDIGIT *DIGIT"
  ],
  "projection": {
    "raw": "the entire canonical token exactly",
    "majorMinor": "the two invariant-culture decimal core components",
    "suffixExamples": {
      "3.7": null,
      "3.3.7": "7",
      "3.7b": "b",
      "3.7c": "c",
      "3.0-rc3": "rc3",
      "3.3a-openbsd": "a-openbsd",
      "next-3.8": "next"
    },
    "toString": "Raw"
  },
  "parsing": {
    "acceptedInput": "the whole canonical token; no whitespace trimming or case folding",
    "constructorNull": "throws ArgumentNullException",
    "constructorInvalid": "throws FormatException",
    "parseNull": "throws ArgumentNullException",
    "parseInvalid": "throws FormatException",
    "tryParseFailure": "returns false and assigns default for null or invalid input",
    "rejectedExamples": [
      "",
      " 3.7",
      "3.7 ",
      "tmux 3.7",
      "master",
      "03.7",
      "3.07",
      "3.7B",
      "3.7.01",
      "3.7-",
      "+3.7",
      "integer component overflow"
    ]
  },
  "ordering": {
    "core": "major then minor, numerically ascending",
    "sameCore": "next < dev < rcN < final < vendor final < numeric micro < letter patch",
    "development": "a missing dev number precedes numeric dev numbers",
    "releaseCandidate": "N compares numerically",
    "micro": "N compares numerically",
    "patch": "bijective base-26 lowercase ordinal: a=1, z=26, aa=27",
    "vendor": "-openbsd immediately follows its corresponding final or patch release",
    "exactIdentity": "CompareTo returns zero if and only if equality is true",
    "examples": [
      "next-3.7 < 3.7-dev < 3.7-dev.0 < 3.7-rc1 < 3.7-rc2",
      "3.7-rc2 < 3.7 < 3.7-openbsd < 3.7a < 3.7a-openbsd < 3.7b < 3.7c",
      "3.3 < 3.3.1 < 3.3.10 < 3.3a",
      "3.7c < next-3.8 < 3.8"
    ],
    "invalidOperands": "CompareTo, <, <=, >, >=, IsAtLeast, and EnsureAtLeast throw InvalidOperationException if either operand is invalid",
    "ensureAtLeastFailure": "a valid value below a valid minimum throws TmuxVersionTooLowException"
  },
  "detection": {
    "command": "tmuxBinaryPath -V",
    "output": "a successful process with exactly one stdout line",
    "line": "the exact lowercase prefix \"tmux \" followed by one version token",
    "lineEnding": "remove only the single trailing line terminator",
    "token": "parse without whitespace trimming or case folding",
    "detectStringAsync": "returns the exact validated canonical token",
    "detectAsync": "returns Parse of that token",
    "invalidOutput": "throws FormatException",
    "failureMapping": {
      "nonzeroExit": "TmuxCommandException carrying Result",
      "nonemptyStderr": "TmuxCommandException carrying Result",
      "missingExecutable": "TmuxCommandNotFoundException",
      "otherLaunchOrReadFailure": "TmuxTransportException",
      "preStartCallerCancellation": "OperationCanceledException",
      "postStartCallerCancellation": "TmuxOperationCanceledException",
      "cleanupFailure": "TmuxCleanupException",
      "passthrough": "do not wrap TmuxCommandException, TmuxCommandNotFoundException, TmuxTransportException, OperationCanceledException, TmuxOperationCanceledException, TmuxCleanupException, or FormatException"
    },
    "advisoryMaster": "master is a matrix lane label, not a token; source must report next-X.Y"
  },
  "support": {
    "minimum": "3.2a",
    "minimumInclusive": true,
    "maximumTested": "3.7c",
    "maximumTestedSemantics": "informational; not a support ceiling",
    "minimumChecks": "enforce only the minimum; newer untested versions may satisfy them",
    "exactVersionIdentity": "3.7, 3.7a, 3.7b, and 3.7c are distinct",
    "capabilitySelection": "named support intervals apply to every stable release at or above the minimum; capabilities without a recorded end remain supported on later stable releases",
    "unknownCapabilityVersion": "invalid, below-minimum, development, release-candidate, and next versions have unknown capability state"
  }
}
```

## Packages

| Package | Dependency | Responsibility |
| --- | --- | --- |
| `LibTmux` | Microsoft.Extensions.Logging.Abstractions (centrally-managed) | hierarchy, values, query AST, local evaluator |
| `LibTmux.Query.Json` | LibTmux (same) | System.Text.Json converters and source-generated context |
| `LibTmux.Testing` | LibTmux (same) | scoped tmux servers, sessions and windows for tests |

## Conventions

| Contract | Approved behavior |
| --- | --- |
| `nullable` | enabled |
| `io` | async-only |
| `cancellation` | final optional CancellationToken |
| `entityMutation` | returns immutable replacement |
| `listedEntityDisposal` | none |
| `ownedScopeDisposal` | IAsyncDisposable with bounded observable cleanup |
| `nativeWindowsTmux` | unsupported |
| `rawCommand` | public result; internal transport |
| `listFailurePolicy` | strict |
| `requestCollections` | defensive immutable copies |

## Consumer-first examples

### Connect and own a session

Owned scopes make cleanup explicit; listed handles remain borrowed.

```csharp
using System;
using System.Threading.Tasks;
using LibTmux;

internal static class Program
{
    public static async Task Main()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("tmux process execution is unavailable on Windows.");
        }

        await using OwnedServerScope ownedServer = await Server.CreateOwnedAsync();
        await using OwnedSessionScope ownedSession =
            await ownedServer.Value.CreateOwnedSessionAsync(
                new NewSessionRequest { Name = "work" });
        Console.WriteLine(ownedSession.Value.Name);
    }
}
```

### Keep immutable replacements

Mutations return fresh state and leave the receiver unchanged.

```csharp
using System;
using System.Threading.Tasks;
using LibTmux;

internal static class Program
{
    public static async Task Main()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("tmux process execution is unavailable on Windows.");
        }

        await using OwnedServerScope ownedServer = await Server.CreateOwnedAsync();
        await using OwnedSessionScope ownedSession =
            await ownedServer.Value.CreateOwnedSessionAsync();
        Session original = ownedSession.Value;
        Session renamed = await original.RenameAsync("review");
        Console.WriteLine($"{original.Name} -> {renamed.Name}");
    }
}
```

### Capture, query, and round-trip JSON

Snapshot properties are local; one canonical AST drives matching and JSON.

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LibTmux;
using LibTmux.Query;
using LibTmux.Query.Json;
using LibTmux.Testing;

internal static class Program
{
    public static async Task Main()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("tmux process execution is unavailable on Windows.");
        }

        var factory = new TmuxTestFactory();
        await using TemporaryHierarchyScope hierarchy =
            await factory.CreateHierarchyAsync();
        Server snapshot =
            await hierarchy.Server.CaptureSnapshotAsync(SnapshotDepth.Windows);
        IReadOnlyList<Session> sessions = [.. snapshot.Sessions];
        QueryDocument document =
            QueryExtensions.Translate<Session>(session => session.Attached);
        IReadOnlyList<Session> attached =
            QueryExtensions.Matching(sessions, document);
        string json = QueryJson.Serialize(document);
        QueryDocument roundTripped = QueryJson.Deserialize(json);
        if (roundTripped != document)
        {
            throw new InvalidOperationException("Query JSON changed meaning.");
        }

        Console.WriteLine(attached.Count);
    }
}
```

### Use the test-framework-independent real-tmux kit

The public test scope and bounded poller work without an xUnit dependency.

```csharp
using System;
using System.Linq;
using System.Threading.Tasks;
using LibTmux;
using LibTmux.Testing;

internal static class Program
{
    public static async Task Main()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("tmux process execution is unavailable on Windows.");
        }

        var factory = new TmuxTestFactory();
        await using TemporaryHierarchyScope hierarchy =
            await factory.CreateHierarchyAsync();
        await hierarchy.Pane.SendKeysAsync(
            new SendKeysRequest { Text = "echo libtmux-$(printf %s ready)" });
        await TmuxWait.UntilAsync(
            token => hierarchy.Pane.CaptureAsync(cancellationToken: token),
            lines => lines.Any(
                line => string.Equals(
                    line,
                    "libtmux-ready",
                    StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(25));
    }
}
```

## Public types

| Type | Kind | Modifiers | Interfaces | Base | Ownership | Contract | Package |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `T:LibTmux.AttachSessionRequest` | record | `public, sealed` | `LibTmux.ITmuxRequest<LibTmux.Session>` | `object` | value | Parameters for AttachSession. | `LibTmux` |
| `T:LibTmux.BindKeyRequest` | record | `public, sealed` | `LibTmux.ITmuxRequest<LibTmux.Server>` | `object` | value | Parameters for BindKey. | `LibTmux` |
| `T:LibTmux.CapturePanePosition` | readonly record struct | `public, readonly` | None | `ValueType` | value | A numeric capture line or the tmux hyphen boundary sentinel. | `LibTmux` |
| `T:LibTmux.CapturePaneRequest` | record | `public, sealed` | `LibTmux.ITmuxRequest<LibTmux.Pane>` | `object` | value | Parameters for CapturePane. | `LibTmux` |
| ``T:LibTmux.CapturedRelation`1`` | class | `public, sealed` | `IReadOnlyList<T>` | `object` | value | A copy-backed relation that distinguishes uncaptured from captured-empty. | `LibTmux` |
| ``T:LibTmux.CapturedValue`1`` | class | `public, sealed` | None | `object` | value | A relation holding at most one child that distinguishes uncaptured from absent. | `LibTmux` |
| `T:LibTmux.ChooseTreeRequest` | record | `public, sealed` | `LibTmux.ITmuxRequest<LibTmux.Pane>` | `object` | value | Parameters for ChooseTree. | `LibTmux` |
| `T:LibTmux.ChooseTreeSort` | enum | `public` | None | `Enum` | value | Defines ChooseTreeSort values. | `LibTmux` |
| `T:LibTmux.Client` | class | `public, sealed` | `IEquatable<Client>` | `object` | borrowed | An immutable client handle and snapshot. Equality: ServerGeneration and Name; Tty excluded. | `LibTmux` |
| `T:LibTmux.ClientAttachment` | record | `public, sealed` | None | `object` | value | A fresh client attachment resolution. | `LibTmux` |
| `T:LibTmux.CommandPromptRequest` | record | `public, sealed` | `LibTmux.ITmuxRequest<LibTmux.Server>` | `object` | value | Parameters for CommandPrompt. | `LibTmux` |
| `T:LibTmux.ConfirmBeforeRequest` | record | `public, sealed` | `LibTmux.ITmuxRequest<LibTmux.Server>` | `object` | value | Parameters for ConfirmBefore. | `LibTmux` |
| `T:LibTmux.CopyModeRequest` | record | `public, sealed` | `LibTmux.ITmuxRequest<LibTmux.Pane>` | `object` | value | Parameters for CopyMode. | `LibTmux` |
| `T:LibTmux.DisplayMenuRequest` | record | `public, sealed` | `LibTmux.ITmuxRequest<LibTmux.Server>` | `object` | value | Parameters for DisplayMenu. | `LibTmux` |
| `T:LibTmux.DisplayMessageRequest` | record | `public, sealed` | `LibTmux.ITmuxRequest<LibTmux.Server>` | `object` | value | Parameters for DisplayMessage. Validation: UpdatePane is valid only for pane-scoped execution. | `LibTmux` |
| `T:LibTmux.DisplayPopupRequest` | record | `public, sealed` | `LibTmux.ITmuxRequest<LibTmux.Pane>` | `object` | value | Parameters for DisplayPopup. | `LibTmux` |
| `T:LibTmux.FindWindowRequest` | record | `public, sealed` | `LibTmux.ITmuxRequest<LibTmux.Pane>` | `object` | value | Parameters for FindWindow. | `LibTmux` |
| `T:LibTmux.GetOptionRequest` | record | `public, sealed` | `LibTmux.ITmuxRequest<LibTmux.TmuxOptions>` | `object` | value | Parameters for GetOption. | `LibTmux` |
| `T:LibTmux.GetOptionsRequest` | record | `public, sealed` | `LibTmux.ITmuxRequest<LibTmux.TmuxOptions>` | `object` | value | Parameters for GetOptions. | `LibTmux` |
| `T:LibTmux.HookRequest` | record | `public, sealed` | None | `object` | value | Parameters for Hook. | `LibTmux` |
| `T:LibTmux.IfShellRequest` | record | `public, sealed` | `LibTmux.ITmuxRequest<LibTmux.Server>` | `object` | value | Parameters for IfShell. | `LibTmux` |
| `T:LibTmux.IncompleteSnapshotException` | class | `public, sealed` | None | `InvalidOperationException` | value | Reports IncompleteSnapshot failure. State: RelationName. | `LibTmux` |
| `T:LibTmux.LibTmuxException` | class | `public` | None | `Exception` | value | Reports LibTmux failure. | `LibTmux` |
| `T:LibTmux.LibTmuxInfo` | static class | `public, static` | None | `object` | value | Reports package identity and supported tmux range. | `LibTmux` |
| `T:LibTmux.LinkWindowRequest` | record | `public, sealed` | `LibTmux.ITmuxRequest<LibTmux.Window>` | `object` | value | Parameters for LinkWindow. | `LibTmux` |
| `T:LibTmux.ListBuffersRequest` | record | `public, sealed` | `LibTmux.ITmuxRequest<LibTmux.Server>` | `object` | value | Parameters for ListBuffers. | `LibTmux` |
| `T:LibTmux.ListHooksRequest` | record | `public, sealed` | `LibTmux.ITmuxRequest<LibTmux.TmuxHooks>` | `object` | value | Parameters for ListHooks. | `LibTmux` |
| `T:LibTmux.MovePaneRequest` | record | `public, sealed` | `LibTmux.ITmuxRequest<LibTmux.Pane>` | `object` | value | Parameters for MovePane. | `LibTmux` |
| `T:LibTmux.MoveWindowRequest` | record | `public, sealed` | `LibTmux.ITmuxRequest<LibTmux.Window>` | `object` | value | Parameters for MoveWindow. | `LibTmux` |
| `T:LibTmux.NewPaneRequest` | record | `public, sealed` | `LibTmux.ITmuxRequest<LibTmux.Pane>` | `object` | value | Parameters for NewPane. | `LibTmux` |
| `T:LibTmux.NewSessionRequest` | record | `public, sealed` | `LibTmux.ITmuxRequest<LibTmux.Server>` | `object` | value | Parameters for NewSession. | `LibTmux` |
| `T:LibTmux.NewWindowRequest` | record | `public, sealed` | `LibTmux.ITmuxRequest<LibTmux.Session>` | `object` | value | Parameters for NewWindow. Validation: Index and TargetWindow are mutually exclusive; refused at dispatch. | `LibTmux` |
| `T:LibTmux.OptionScope` | enum | `public` | None | `Enum` | value | Defines OptionScope values. | `LibTmux` |
| `T:LibTmux.OwnedServerScope` | class | `public, sealed` | `IAsyncDisposable` | `object` | owned | Owns a temporary server resource and bounded cleanup. | `LibTmux` |
| `T:LibTmux.OwnedSessionScope` | class | `public, sealed` | `IAsyncDisposable` | `object` | owned | Owns a temporary session resource and bounded cleanup. | `LibTmux` |
| `T:LibTmux.OwnedWindowScope` | class | `public, sealed` | `IAsyncDisposable` | `object` | owned | Owns a temporary window resource and bounded cleanup. | `LibTmux` |
| `T:LibTmux.Pane` | class | `public, sealed` | `IEquatable<Pane>` | `object` | borrowed | An immutable pane handle and snapshot. Equality: ServerGeneration and PaneId. | `LibTmux` |
| `T:LibTmux.PaneDirection` | enum | `public` | None | `Enum` | value | Defines PaneDirection values. | `LibTmux` |
| `T:LibTmux.PaneId` | record struct | `public, readonly` | `IComparable<PaneId>`, `IParsable<PaneId>`, `ISpanParsable<PaneId>` | `ValueType` | value | A generation-independent tmux pane identifier. Identity: {"defaultIsValid":true,"minimum":0,"parseRejects":["null","malformed","negative","wrongPrefix"],"prefix":"%","tryParseFailure":"returns false and assigns default","valueType":"int"}. | `LibTmux` |
| `T:LibTmux.PaneInputMode` | enum | `public` | None | `Enum` | value | Defines PaneInputMode values. | `LibTmux` |
| `T:LibTmux.PaneSelectDirection` | enum | `public` | None | `Enum` | value | Defines PaneSelectDirection values. | `LibTmux` |
| `T:LibTmux.PaneSwapDirection` | enum | `public` | None | `Enum` | value | Defines PaneSwapDirection values. | `LibTmux` |
| `T:LibTmux.PasteBufferRequest` | record | `public, sealed` | `LibTmux.ITmuxRequest<LibTmux.Pane>` | `object` | value | Parameters for PasteBuffer. | `LibTmux` |
| `T:LibTmux.PipePaneRequest` | record | `public, sealed` | `LibTmux.ITmuxRequest<LibTmux.Pane>` | `object` | value | Parameters for PipePane. | `LibTmux` |
| `T:LibTmux.PopupCloseMode` | enum | `public` | None | `Enum` | value | Defines PopupCloseMode values. | `LibTmux` |
| `T:LibTmux.PromptType` | enum | `public` | None | `Enum` | value | Defines PromptType values. | `LibTmux` |
| `T:LibTmux.PsmuxCaptureOptions` | class | `public, sealed` | None | `object` | value | Typed capture choices audited for the psmux query preview. | `LibTmux` |
| `T:LibTmux.PsmuxConnectionOptions` | class | `public, sealed` | None | `object` | value | A pinned psmux client file and one isolated namespace. | `LibTmux` |
| `T:LibTmux.PsmuxPane` | class | `public, sealed` | None | `object` | value | An immutable pane observation from the psmux query preview. | `LibTmux` |
| `T:LibTmux.PsmuxServer` | class | `public, sealed` | None | `object` | reference | A query-only connection to one isolated psmux namespace. | `LibTmux` |
| `T:LibTmux.PsmuxSession` | class | `public, sealed` | None | `object` | value | An immutable observation of the sole psmux session. | `LibTmux` |
| `T:LibTmux.PsmuxWindow` | class | `public, sealed` | None | `object` | value | An immutable window observation from the psmux query preview. | `LibTmux` |
| `T:LibTmux.Query.Json.QueryJson` | static class | `public, static` | None | `object` | value | Serializes and parses v1 query documents. | `LibTmux.Query.Json` |
| `T:LibTmux.Query.Json.QueryJsonLimits` | record | `public, sealed` | None | `object` | value | Tightens the fixed v1 JSON resource ceilings. | `LibTmux.Query.Json` |
| `T:LibTmux.Query.QueryDocument` | record | `public, sealed` | None | `object` | value | A versioned canonical query document. | `LibTmux` |
| `T:LibTmux.Query.QueryEdgeParser` | static class | `public, static` | None | `object` | value | Parses the one supported Python-style edge lookup. | `LibTmux` |
| `T:LibTmux.Query.QueryExtensions` | static class | `public, static` | None | `object` | value | Translates and evaluates closed snapshot queries. | `LibTmux` |
| `T:LibTmux.Query.QueryTarget` | enum | `public` | None | `Enum` | value | Defines QueryTarget values. | `LibTmux` |
| `T:LibTmux.ResizeDirection` | enum | `public` | None | `Enum` | value | Defines ResizeDirection values. | `LibTmux` |
| `T:LibTmux.ResizePaneRequest` | record | `public, sealed` | `LibTmux.ITmuxRequest<LibTmux.Pane>` | `object` | value | Parameters for ResizePane. Validation: exactly one primary resize mode; Direction and Adjustment go together; refused at dispatch. | `LibTmux` |
| `T:LibTmux.ResizeWindowRequest` | record | `public, sealed` | `LibTmux.ITmuxRequest<LibTmux.Window>` | `object` | value | Parameters for ResizeWindow. Validation: at most one primary resize mode; Direction and Adjustment go together; refused at dispatch. | `LibTmux` |
| `T:LibTmux.RespawnRequest` | record | `public, sealed` | `LibTmux.ITmuxRequest<LibTmux.Pane>` | `object` | value | Parameters for Respawn. | `LibTmux` |
| `T:LibTmux.RunShellRequest` | record | `public, sealed` | `LibTmux.ITmuxRequest<LibTmux.Server>` | `object` | value | Parameters for RunShell. | `LibTmux` |
| `T:LibTmux.SelectLayoutMode` | enum | `public` | None | `Enum` | value | Defines SelectLayoutMode values. | `LibTmux` |
| `T:LibTmux.SelectLayoutRequest` | record | `public, sealed` | `LibTmux.ITmuxRequest<LibTmux.Window>` | `object` | value | Parameters for SelectLayout. | `LibTmux` |
| `T:LibTmux.SelectPaneRequest` | record | `public, sealed` | `LibTmux.ITmuxRequest<LibTmux.Pane>` | `object` | value | Parameters for SelectPane. Validation: nullable Mark and InputEnabled preserve paired positive and negative flags. | `LibTmux` |
| `T:LibTmux.SendKeysRequest` | record | `public, sealed` | `LibTmux.ITmuxRequest<LibTmux.Pane>` | `object` | value | Parameters for SendKeys. | `LibTmux` |
| `T:LibTmux.Server` | class | `public, sealed` | `IEquatable<Server>` | `object` | borrowed | An immutable server handle and snapshot. Equality: normalized connection endpoint. | `LibTmux` |
| `T:LibTmux.ServerAccessRequest` | record | `public, sealed` | `LibTmux.ITmuxRequest<LibTmux.Server>` | `object` | value | Parameters for ServerAccess. Validation: AllowUser and DenyUser are mutually exclusive, as are ReadOnly and ReadWrite; refused at dispatch. | `LibTmux` |
| `T:LibTmux.ServerConnectionOptions` | record | `public, sealed` | None | `object` | value | Configures a tmux server connection without mutating process-wide state. Endpoint precedence: SocketPath, SocketName, SocketNameFactory. | `LibTmux` |
| `T:LibTmux.ServerGeneration` | readonly record struct | `public, readonly` | None | `ValueType` | value | Identifies one tmux daemon generation. Validation: ProcessId and StartTime must both be positive; default is invalid. | `LibTmux` |
| `T:LibTmux.Session` | class | `public, sealed` | `IEquatable<Session>` | `object` | borrowed | An immutable session handle and snapshot. Equality: ServerGeneration and SessionId. | `LibTmux` |
| `T:LibTmux.SessionId` | record struct | `public, readonly` | `IComparable<SessionId>`, `IParsable<SessionId>`, `ISpanParsable<SessionId>` | `ValueType` | value | A generation-independent tmux session identifier. Identity: {"defaultIsValid":true,"minimum":0,"parseRejects":["null","malformed","negative","wrongPrefix"],"prefix":"$","tryParseFailure":"returns false and assigns default","valueType":"int"}. | `LibTmux` |
| `T:LibTmux.SessionWindowEdge` | record | `public, sealed` | None | `ValueType` | value | Identifies one session-to-window snapshot path. | `LibTmux` |
| `T:LibTmux.SetHookRequest` | record | `public, sealed` | `LibTmux.ITmuxRequest<LibTmux.TmuxHooks>` | `object` | value | Parameters for SetHook. | `LibTmux` |
| `T:LibTmux.SetHooksRequest` | record | `public, sealed` | None | `object` | value | Parameters for SetHooks. Validation: sparse hook indices are nonnegative and preserved. | `LibTmux` |
| `T:LibTmux.SetOptionRequest` | record | `public, sealed` | `LibTmux.ITmuxRequest<LibTmux.TmuxOptions>` | `object` | value | Parameters for SetOption. | `LibTmux` |
| `T:LibTmux.ShowMessagesMode` | enum | `public` | None | `Enum` | value | Defines ShowMessagesMode values. | `LibTmux` |
| `T:LibTmux.SnapshotDepth` | enum | `public` | None | `Enum` | value | Defines SnapshotDepth values. | `LibTmux` |
| `T:LibTmux.SplitPaneRequest` | record | `public, sealed` | `LibTmux.ITmuxRequest<LibTmux.Pane>` | `object` | value | Parameters for SplitPane. Validation: Size and Percentage are mutually exclusive; refused at dispatch. | `LibTmux` |
| `T:LibTmux.StaleServerGenerationException` | class | `public, sealed` | None | `InvalidOperationException` | value | Reports StaleServerGeneration failure. State: Expected, Actual. | `LibTmux` |
| `T:LibTmux.SwapPaneRequest` | record | `public, sealed` | `LibTmux.ITmuxRequest<LibTmux.Pane>` | `object` | value | Parameters for SwapPane. Validation: exactly one of Target or Direction; refused at dispatch. | `LibTmux` |
| `T:LibTmux.Testing.TemporaryHierarchyScope` | class | `public, sealed` | `IAsyncDisposable` | `object` | owned | Provides TemporaryHierarchyScope testing support. | `LibTmux.Testing` |
| `T:LibTmux.Testing.TemporaryServerScope` | class | `public, sealed` | `IAsyncDisposable` | `object` | owned | Provides TemporaryServerScope testing support. | `LibTmux.Testing` |
| `T:LibTmux.Testing.TemporarySessionScope` | class | `public, sealed` | `IAsyncDisposable` | `object` | owned | Provides TemporarySessionScope testing support. | `LibTmux.Testing` |
| `T:LibTmux.Testing.TemporaryWindowScope` | class | `public, sealed` | `IAsyncDisposable` | `object` | owned | Provides TemporaryWindowScope testing support. | `LibTmux.Testing` |
| `T:LibTmux.Testing.TestEnvironment` | record | `public, sealed` | None | `object` | value | Provides TestEnvironment testing support. | `LibTmux.Testing` |
| `T:LibTmux.Testing.TmuxNameGenerator` | class | `public, sealed` | None | `object` | value | Provides TmuxNameGenerator testing support. | `LibTmux.Testing` |
| `T:LibTmux.Testing.TmuxTestContext` | class | `public, sealed` | `IAsyncDisposable` | `object` | owned | Provides TmuxTestContext testing support. | `LibTmux.Testing` |
| `T:LibTmux.Testing.TmuxTestFactory` | class | `public, sealed` | None | `object` | value | Provides TmuxTestFactory testing support. | `LibTmux.Testing` |
| `T:LibTmux.Testing.TmuxTestOptions` | record | `public, sealed` | None | `object` | value | Provides TmuxTestOptions testing support. | `LibTmux.Testing` |
| `T:LibTmux.TmuxWait` | static class | `public, static` | None | `object` | value | Provides TmuxWait testing support. | `LibTmux` |
| `T:LibTmux.TmuxBuffer` | record | `public, sealed` | None | `object` | value | One tmux paste buffer snapshot. | `LibTmux` |
| `T:LibTmux.TmuxCleanupException` | class | `public, sealed` | None | `LibTmuxException` | value | Reports TmuxCleanup failure. State: OriginalCancellation, ClientProcessId, CleanupFailure. | `LibTmux` |
| `T:LibTmux.TmuxColorMode` | enum | `public` | None | `Enum` | value | Defines valid tmux color modes. Numeric value 1 is reserved; ServerConnectionOptions rejects undefined values with ArgumentOutOfRangeException. | `LibTmux` |
| `T:LibTmux.TmuxCommandException` | class | `public, sealed` | None | `LibTmuxException` | value | Reports TmuxCommand failure. State: Result. | `LibTmux` |
| `T:LibTmux.TmuxCommandNotFoundException` | class | `public, sealed` | None | `LibTmuxException` | value | Reports TmuxCommandNotFound failure. State: TmuxBinaryPath. | `LibTmux` |
| `T:LibTmux.TmuxCommandResult` | record | `public, sealed` | None | `object` | value | The complete inspectable result of one raw tmux command. | `LibTmux` |
| `T:LibTmux.TmuxDispatchState` | enum | `public` | None | `Enum` | value | Says whether a failed command reached tmux, which is what decides if retrying is safe. | `LibTmux` |
| `T:LibTmux.TmuxDiagnostics` | static class | `public, static` | None | `object` | value | Names the diagnostic sources this library publishes. | `LibTmux` |
| `T:LibTmux.TmuxEnvironment` | class | `public, sealed` | None | `object` | borrowed | Scoped environment operations. | `LibTmux` |
| `T:LibTmux.TmuxEnvironmentEntry` | record | `public, sealed` | None | `object` | value | One tmux environment entry, including removal markers. | `LibTmux` |
| `T:LibTmux.TmuxHook` | record | `public, sealed` | None | `object` | value | One tmux hook and its sparse commands. | `LibTmux` |
| `T:LibTmux.TmuxHookEntry` | record | `public, sealed` | None | `object` | value | One sparse tmux hook command. | `LibTmux` |
| `T:LibTmux.TmuxHooks` | class | `public, sealed` | None | `object` | borrowed | Scoped hooks operations. | `LibTmux` |
| `T:LibTmux.TmuxMenuItem` | record | `public, sealed` | None | `object` | value | One display-menu entry. | `LibTmux` |
| `T:LibTmux.TmuxObjectNotFoundException` | class | `public, sealed` | None | `LibTmuxException` | value | Reports TmuxObjectNotFound failure. State: Target. | `LibTmux` |
| `T:LibTmux.TmuxOperationCanceledException` | class | `public, sealed` | None | `OperationCanceledException` | value | Reports TmuxOperationCanceled failure. State: CommandMayHaveExecuted, ClientProcessId. | `LibTmux` |
| `T:LibTmux.TmuxOption` | record | `public, sealed` | None | `object` | value | One scalar or sparse-array tmux option entry. | `LibTmux` |
| `T:LibTmux.TmuxOptionException` | class | `public, sealed` | None | `LibTmuxException` | value | Reports TmuxOption failure. State: OptionName. | `LibTmux` |
| `T:LibTmux.TmuxOptionState` | enum | `public` | None | `Enum` | value | Defines TmuxOptionState values. | `LibTmux` |
| `T:LibTmux.TmuxOptionValue` | record | `public, sealed` | None | `object` | value | A lossless tmux option value with typed convenience projections. | `LibTmux` |
| `T:LibTmux.TmuxOptions` | class | `public, sealed` | None | `object` | borrowed | Scoped options operations. | `LibTmux` |
| `T:LibTmux.TmuxPaneException` | class | `public, sealed` | None | `LibTmuxException` | value | Reports TmuxPane failure. State: PaneId. | `LibTmux` |
| `T:LibTmux.TmuxSessionExistsException` | class | `public, sealed` | None | `LibTmuxException` | value | Reports TmuxSessionExists failure. State: SessionName. | `LibTmux` |
| `T:LibTmux.TmuxTransportException` | class | `public, sealed` | None | `LibTmuxException` | value | Reports TmuxTransport failure. State: Arguments. | `LibTmux` |
| `T:LibTmux.TmuxProtocolException` | class | `public, sealed` | None | `LibTmuxException` | value | Reports an answer from tmux this library could not read. State: Arguments. | `LibTmux` |
| `T:LibTmux.TmuxVersion` | record struct | `public, readonly` | `IComparable<TmuxVersion>` | `ValueType` | value | A lossless parsed tmux version with stable ordering semantics. Default value: {"comparison":"equality is valid; ordered comparison throws InvalidOperationException","isValid":false,"major":0,"minor":0,"raw":"","suffix":null,"toString":""}. | `LibTmux` |
| `T:LibTmux.TmuxVersionTooLowException` | class | `public, sealed` | None | `LibTmuxException` | value | Reports TmuxVersionTooLow failure. State: RequiredVersion, ActualVersion. | `LibTmux` |
| `T:LibTmux.TmuxWaitMode` | enum | `public` | None | `Enum` | value | Selects wait-for behavior. | `LibTmux` |
| `T:LibTmux.TmuxWaitTimeoutException` | class | `public, sealed` | None | `TimeoutException` | value | Reports TmuxWaitTimeout failure. State: Timeout. | `LibTmux` |
| `T:LibTmux.TmuxWindowException` | class | `public, sealed` | None | `LibTmuxException` | value | Reports TmuxWindow failure. State: WindowId. | `LibTmux` |
| `T:LibTmux.UnbindKeyRequest` | record | `public, sealed` | `LibTmux.ITmuxRequest<LibTmux.Server>` | `object` | value | Parameters for UnbindKey. Validation: Key is required unless All is true; refused at dispatch. | `LibTmux` |
| `T:LibTmux.UnsafeTmuxFilter` | record | `public, sealed` | None | `object` | value | An explicitly unsafe native tmux filter with tmux-native semantics. | `LibTmux` |
| `T:LibTmux.UnsetOptionRequest` | record | `public, sealed` | `LibTmux.ITmuxRequest<LibTmux.TmuxOptions>` | `object` | value | Parameters for UnsetOption. | `LibTmux` |
| `T:LibTmux.UnsupportedQueryExpressionException` | class | `public, sealed` | None | `NotSupportedException` | value | Reports UnsupportedQueryExpression failure. State: Expression. | `LibTmux` |
| `T:LibTmux.WaitForRequest` | record | `public, sealed` | `LibTmux.ITmuxRequest<LibTmux.Server>` | `object` | value | Parameters for WaitFor. | `LibTmux` |
| `T:LibTmux.Window` | class | `public, sealed` | `IEquatable<Window>` | `object` | borrowed | An immutable window handle and snapshot. Equality: ServerGeneration and WindowId; relation edge excluded. | `LibTmux` |
| `T:LibTmux.WindowDirection` | enum | `public` | None | `Enum` | value | Defines WindowDirection values. | `LibTmux` |
| `T:LibTmux.WindowEntityKey` | readonly record struct | `public, readonly` | None | `ValueType` | value | Defines equality for linked window views. | `LibTmux` |
| `T:LibTmux.WindowId` | record struct | `public, readonly` | `IComparable<WindowId>`, `IParsable<WindowId>`, `ISpanParsable<WindowId>` | `ValueType` | value | A generation-independent tmux window identifier. Identity: {"defaultIsValid":true,"minimum":0,"parseRejects":["null","malformed","negative","wrongPrefix"],"prefix":"@","tryParseFailure":"returns false and assigns default","valueType":"int"}. | `LibTmux` |
| `T:LibTmux.WindowResizeMode` | enum | `public` | None | `Enum` | value | Defines WindowResizeMode values. | `LibTmux` |
| `T:LibTmux.WindowRotationDirection` | enum | `public` | None | `Enum` | value | Defines WindowRotationDirection values. | `LibTmux` |
| `T:LibTmux.IControlModeSession` | interface | `public` | `System.IAsyncDisposable` | `None` | reference | A live tmux control client reporting what tmux does until disposed. | `LibTmux` |
| `T:LibTmux.PaneObservation` | static class | `public, static` | None | `object` | value | Narrows a control client's event stream to one pane, and ends it cleanly. | `LibTmux` |
| `T:LibTmux.ControlModeSubscriptions` | static class | `public, static` | None | `object` | value | Subscribes a control client to a format changing. | `LibTmux` |
| `T:LibTmux.ControlModeCommandException` | class | `public, sealed` | None | `LibTmuxException` | reference | Reports a command rejected by a live tmux control client. State: Command, OutputLines, ErrorLines. | `LibTmux` |
| `T:LibTmux.TmuxEvent` | record | `public, abstract` | None | `object` | value | One thing a tmux control client reported without being asked. | `LibTmux` |
| `T:LibTmux.TmuxEventsDroppedEvent` | record | `public, sealed` | None | `LibTmux.TmuxEvent` | value | A loss marker emitted when the bounded control-event buffer overflows. | `LibTmux` |
| `T:LibTmux.TmuxOutputEvent` | record | `public, sealed` | None | `LibTmux.TmuxEvent` | value | Bytes a pane wrote, with tmux's escaping decoded. | `LibTmux` |
| `T:LibTmux.TmuxPaneGoneEvent` | record | `public, sealed` | None | `LibTmux.TmuxEvent` | value | The pane a PaneObservation.WatchAsync stream was watching left its window's arrangement. | `LibTmux` |
| `T:LibTmux.TmuxNotificationEvent` | record | `public, sealed` | None | `LibTmux.TmuxEvent` | value | A tmux notification carried by name with its words unparsed. | `LibTmux` |
| `T:LibTmux.TmuxExitEvent` | record | `public, sealed` | None | `LibTmux.TmuxEvent` | value | The control client ended; always the last event in the stream. | `LibTmux` |
| `T:LibTmux.TmuxCommand` | record | `public, sealed` | None | `object` | value | One tmux command and the arguments it carries. | `LibTmux` |
| `T:LibTmux.TmuxChain` | class | `public, sealed` | None | `object` | reference | Commands tmux runs together, in one process. | `LibTmux` |
| `T:LibTmux.TmuxChaining` | class | `public, static` | None | `object` | value | Runs a request on its own, as a chain of one command. | `LibTmux` |
| `T:LibTmux.TmuxWaitChannel` | class | `public, sealed` | `IAsyncDisposable` | `object` | owned | Holds a tmux wait-for registration across timed attempts. | `LibTmux` |
| ``T:LibTmux.ITmuxRequest`1`` | interface | `public` | None | `None` | value | A request that becomes one tmux command against a target. | `LibTmux` |

## Public members

### `T:LibTmux.AttachSessionRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.AttachSessionRequest.ToCommand(LibTmux.Session)` | `TmuxCommand LibTmux.AttachSessionRequest.ToCommand(Session session)` | Public | No | Portable | Returns a attach request as one tmux command. |
| `P:LibTmux.AttachSessionRequest.ClientFlags` | `IReadOnlyList<string>? LibTmux.AttachSessionRequest.ClientFlags { get; init; }` | Public | No | Portable | Gets ClientFlags. |
| `P:LibTmux.AttachSessionRequest.DetachOthers` | `bool LibTmux.AttachSessionRequest.DetachOthers { get; init; }` | Public | No | Portable | Gets DetachOthers. |
| `P:LibTmux.AttachSessionRequest.ExitOnDetach` | `bool LibTmux.AttachSessionRequest.ExitOnDetach { get; init; }` | Public | No | Portable | Gets ExitOnDetach. |
| `P:LibTmux.AttachSessionRequest.ReadOnly` | `bool LibTmux.AttachSessionRequest.ReadOnly { get; init; }` | Public | No | Portable | Gets ReadOnly. |
| `P:LibTmux.AttachSessionRequest.Target` | `string? LibTmux.AttachSessionRequest.Target { get; init; }` | Public | No | Portable | Gets Target. |

### `T:LibTmux.BindKeyRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.BindKeyRequest.#ctor(string,IReadOnlyList<string>)` | `BindKeyRequest(string key, IReadOnlyList<string> command)` | Public | No | Portable | Creates BindKeyRequest. |
| `M:LibTmux.BindKeyRequest.LibTmux.ITmuxRequest<LibTmux.Server>.ToCommand(LibTmux.Server)` | `TmuxCommand LibTmux.ITmuxRequest<LibTmux.Server>.ToCommand(Server target)` | Explicit interface | No | Portable | Returns this request as one tmux command; the server is not needed to build it. |
| `M:LibTmux.BindKeyRequest.ToCommand()` | `TmuxCommand LibTmux.BindKeyRequest.ToCommand()` | Public | No | Portable | Returns a key-binding request as one tmux command. |
| `P:LibTmux.BindKeyRequest.Command` | `IReadOnlyList<string> LibTmux.BindKeyRequest.Command { get; }` | Public | No | Portable | Gets Command. |
| `P:LibTmux.BindKeyRequest.Key` | `string LibTmux.BindKeyRequest.Key { get; }` | Public | No | Portable | Gets Key. |
| `P:LibTmux.BindKeyRequest.KeyTable` | `string? LibTmux.BindKeyRequest.KeyTable { get; init; }` | Public | No | Portable | Gets KeyTable. |
| `P:LibTmux.BindKeyRequest.Note` | `string? LibTmux.BindKeyRequest.Note { get; init; }` | Public | No | Portable | Gets Note. |
| `P:LibTmux.BindKeyRequest.Repeat` | `bool LibTmux.BindKeyRequest.Repeat { get; init; }` | Public | No | Portable | Gets Repeat. |

### `T:LibTmux.CapturePanePosition`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.CapturePanePosition.#ctor(int)` | `CapturePanePosition(int lineNumber)` | Public | No | Portable | Creates a numeric capture boundary. |
| `P:LibTmux.CapturePanePosition.BeginningOfHistory` | `static CapturePanePosition LibTmux.CapturePanePosition.BeginningOfHistory { get; }` | Public | Yes | Portable | Gets the tmux hyphen boundary for a capture start. |
| `P:LibTmux.CapturePanePosition.EndOfVisiblePane` | `static CapturePanePosition LibTmux.CapturePanePosition.EndOfVisiblePane { get; }` | Public | Yes | Portable | Gets the tmux hyphen boundary for a capture end. |
| `P:LibTmux.CapturePanePosition.LineNumber` | `int? LibTmux.CapturePanePosition.LineNumber { get; }` | Public | No | Portable | Gets the numeric line, or null for the tmux boundary sentinel. |

### `T:LibTmux.CapturePaneRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.CapturePaneRequest.ToCommand(LibTmux.Pane)` | `TmuxCommand LibTmux.CapturePaneRequest.ToCommand(Pane pane)` | Public | No | Portable | Returns a capture request as one tmux command. |
| `P:LibTmux.CapturePaneRequest.AlternateScreen` | `bool LibTmux.CapturePaneRequest.AlternateScreen { get; init; }` | Public | No | Portable | Gets AlternateScreen. |
| `P:LibTmux.CapturePaneRequest.EndLine` | `CapturePanePosition? LibTmux.CapturePaneRequest.EndLine { get; init; }` | Public | No | Portable | Gets EndLine. |
| `P:LibTmux.CapturePaneRequest.EscapeNonPrintable` | `bool LibTmux.CapturePaneRequest.EscapeNonPrintable { get; init; }` | Public | No | Portable | Gets EscapeNonPrintable. |
| `P:LibTmux.CapturePaneRequest.EscapeSequences` | `bool LibTmux.CapturePaneRequest.EscapeSequences { get; init; }` | Public | No | Portable | Gets EscapeSequences. |
| `P:LibTmux.CapturePaneRequest.Hyperlinks` | `bool LibTmux.CapturePaneRequest.Hyperlinks { get; init; }` | Public | No | Portable | Gets Hyperlinks. |
| `P:LibTmux.CapturePaneRequest.JoinWrappedLines` | `bool LibTmux.CapturePaneRequest.JoinWrappedLines { get; init; }` | Public | No | Portable | Gets JoinWrappedLines. |
| `P:LibTmux.CapturePaneRequest.LineFlags` | `bool LibTmux.CapturePaneRequest.LineFlags { get; init; }` | Public | No | Portable | Gets LineFlags. |
| `P:LibTmux.CapturePaneRequest.LineNumbers` | `bool LibTmux.CapturePaneRequest.LineNumbers { get; init; }` | Public | No | Portable | Gets LineNumbers. |
| `P:LibTmux.CapturePaneRequest.ModeScreen` | `bool LibTmux.CapturePaneRequest.ModeScreen { get; init; }` | Public | No | Portable | Gets ModeScreen. |
| `P:LibTmux.CapturePaneRequest.Pending` | `bool LibTmux.CapturePaneRequest.Pending { get; init; }` | Public | No | Portable | Gets Pending. |
| `P:LibTmux.CapturePaneRequest.PreserveTrailingSpaces` | `bool LibTmux.CapturePaneRequest.PreserveTrailingSpaces { get; init; }` | Public | No | Portable | Gets PreserveTrailingSpaces. |
| `P:LibTmux.CapturePaneRequest.Quiet` | `bool LibTmux.CapturePaneRequest.Quiet { get; init; }` | Public | No | Portable | Gets Quiet. |
| `P:LibTmux.CapturePaneRequest.StartLine` | `CapturePanePosition? LibTmux.CapturePaneRequest.StartLine { get; init; }` | Public | No | Portable | Gets StartLine. |
| `P:LibTmux.CapturePaneRequest.TrimTrailingSpaces` | `bool LibTmux.CapturePaneRequest.TrimTrailingSpaces { get; init; }` | Public | No | Portable | Gets TrimTrailingSpaces. |

### ``T:LibTmux.CapturedRelation`1``

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| ``M:LibTmux.CapturedRelation`1.GetEnumerator()`` | `IEnumerator<T> LibTmux.CapturedRelation<T>.GetEnumerator()` | Public | No | Portable | Enumerates captured items or throws when uncaptured. |
| ``M:LibTmux.CapturedRelation`1.OrEmpty()`` | `IReadOnlyList<T> LibTmux.CapturedRelation<T>.OrEmpty()` | Public | No | Portable | Returns the captured items, or none when nothing was captured. |
| ``M:LibTmux.CapturedRelation`1.System.Collections.IEnumerable.GetEnumerator()`` | `IEnumerator System.Collections.IEnumerable.GetEnumerator()` | Explicit interface | No | Portable | Enumerates captured items through the non-generic interface. |
| ``P:LibTmux.CapturedRelation`1.Count`` | `int LibTmux.CapturedRelation<T>.Count { get; }` | Public | No | Portable | Gets the captured item count or throws when uncaptured. |
| ``P:LibTmux.CapturedRelation`1.IsCaptured`` | `bool LibTmux.CapturedRelation<T>.IsCaptured { get; }` | Public | No | Portable | Gets whether the relation was captured. |
| ``P:LibTmux.CapturedRelation`1.Item(int)`` | `T LibTmux.CapturedRelation<T>.this[int index] { get; }` | Public | No | Portable | Gets a captured item or throws when uncaptured. |

### ``T:LibTmux.CapturedValue`1``

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| ``M:LibTmux.CapturedValue`1.OrNull()`` | `T? LibTmux.CapturedValue<T>.OrNull()` | Public | No | Portable | The captured child, or null when the snapshot never read it. |
| ``M:LibTmux.CapturedValue`1.TryGetValue(T?)`` | `bool LibTmux.CapturedValue<T>.TryGetValue(out T? value)` | Public | No | Portable | Tries to read the captured child. |
| ``P:LibTmux.CapturedValue`1.CapturedDepth`` | `SnapshotDepth LibTmux.CapturedValue<T>.CapturedDepth { get; }` | Public | No | Portable | The depth the owning snapshot reached. |
| ``P:LibTmux.CapturedValue`1.IsCaptured`` | `bool LibTmux.CapturedValue<T>.IsCaptured { get; }` | Public | No | Portable | Whether the snapshot read this relation. |
| ``P:LibTmux.CapturedValue`1.Relation`` | `string LibTmux.CapturedValue<T>.Relation { get; }` | Public | No | Portable | The relation name this instance carries. |
| ``P:LibTmux.CapturedValue`1.Value`` | `T LibTmux.CapturedValue<T>.Value { get; }` | Public | No | Portable | The captured child, throwing when the snapshot never read it. |

### `T:LibTmux.ChooseTreeRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.ChooseTreeRequest.ToCommand(LibTmux.Pane)` | `TmuxCommand LibTmux.ChooseTreeRequest.ToCommand(Pane pane)` | Public | No | Portable | Returns a chooser request as one tmux command. |
| `P:LibTmux.ChooseTreeRequest.Format` | `string? LibTmux.ChooseTreeRequest.Format { get; init; }` | Public | No | Portable | Gets Format. |
| `P:LibTmux.ChooseTreeRequest.NativeFilter` | `UnsafeTmuxFilter? LibTmux.ChooseTreeRequest.NativeFilter { get; init; }` | Public | No | Portable | Gets NativeFilter. |
| `P:LibTmux.ChooseTreeRequest.Reverse` | `bool LibTmux.ChooseTreeRequest.Reverse { get; init; }` | Public | No | Portable | Gets Reverse. |
| `P:LibTmux.ChooseTreeRequest.SessionsCollapsed` | `bool LibTmux.ChooseTreeRequest.SessionsCollapsed { get; init; }` | Public | No | Portable | Gets SessionsCollapsed. |
| `P:LibTmux.ChooseTreeRequest.Sort` | `ChooseTreeSort? LibTmux.ChooseTreeRequest.Sort { get; init; }` | Public | No | Portable | Gets Sort. |
| `P:LibTmux.ChooseTreeRequest.WindowsCollapsed` | `bool LibTmux.ChooseTreeRequest.WindowsCollapsed { get; init; }` | Public | No | Portable | Gets WindowsCollapsed. |
| `P:LibTmux.ChooseTreeRequest.Zoom` | `bool LibTmux.ChooseTreeRequest.Zoom { get; init; }` | Public | No | Portable | Gets Zoom. |

### `T:LibTmux.ChooseTreeSort`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `F:LibTmux.ChooseTreeSort.Index` | `Index = 0` | Public | Implicit | Portable | The Index value. Value: `0`. |
| `F:LibTmux.ChooseTreeSort.Name` | `Name = 1` | Public | Implicit | Portable | The Name value. Value: `1`. |
| `F:LibTmux.ChooseTreeSort.Size` | `Size = 3` | Public | Implicit | Portable | The Size value. Value: `3`. |
| `F:LibTmux.ChooseTreeSort.Time` | `Time = 2` | Public | Implicit | Portable | The Time value. Value: `2`. |

### `T:LibTmux.Client`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.Client.GetAsync(Server,string,CancellationToken)` | `static Task<Client> LibTmux.Client.GetAsync(Server server, string name, CancellationToken cancellationToken = default)` | Public | Yes | `UnsupportedOSPlatform("windows")` | Performs Get. Missing behavior: throws TmuxObjectNotFoundException. |
| `M:LibTmux.Client.GetAttachedPaneAsync(CancellationToken)` | `Task<Pane?> LibTmux.Client.GetAttachedPaneAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Refreshes the client and resolves its attached pane. |
| `M:LibTmux.Client.GetAttachedSessionAsync(CancellationToken)` | `Task<Session?> LibTmux.Client.GetAttachedSessionAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Refreshes the client and resolves its attached session. |
| `M:LibTmux.Client.GetAttachedWindowAsync(CancellationToken)` | `Task<Window?> LibTmux.Client.GetAttachedWindowAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Refreshes the client and resolves its attached window. |
| `M:LibTmux.Client.RefreshAsync(CancellationToken)` | `Task<Client> LibTmux.Client.RefreshAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs Refresh. |
| `M:LibTmux.Client.ResolveAttachmentAsync(CancellationToken)` | `Task<ClientAttachment?> LibTmux.Client.ResolveAttachmentAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Resolves session, window, and pane from one fresh client read. |
| `M:LibTmux.Client.op_Equality(Client?,Client?)` | `static bool operator ==(Client? left, Client? right)` | Public | Yes | Portable | Reports whether two handles name the same client. |
| `M:LibTmux.Client.op_Inequality(Client?,Client?)` | `static bool operator !=(Client? left, Client? right)` | Public | Yes | Portable | Reports whether two handles name different clients. |
| `P:LibTmux.Client.AttachedSessionId` | `SessionId? LibTmux.Client.AttachedSessionId { get; }` | Public | No | Portable | Gets the captured AttachedSessionId value. |
| `P:LibTmux.Client.Generation` | `ServerGeneration LibTmux.Client.Generation { get; }` | Public | No | Portable | Gets the captured Generation value. |
| `P:LibTmux.Client.IsControlClient` | `bool LibTmux.Client.IsControlClient { get; }` | Public | No | Portable | Gets the captured IsControlClient value. |
| `P:LibTmux.Client.Name` | `string LibTmux.Client.Name { get; }` | Public | No | Portable | Gets the captured Name value. |
| `P:LibTmux.Client.RawFormatFields` | `IReadOnlyDictionary<string,string?> LibTmux.Client.RawFormatFields { get; }` | Public | No | Portable | Gets copied raw tmux format tokens captured for this snapshot. |
| `P:LibTmux.Client.Server` | `Server LibTmux.Client.Server { get; }` | Public | No | Portable | Gets the captured Server value. |
| `P:LibTmux.Client.Tty` | `string? LibTmux.Client.Tty { get; }` | Public | No | Portable | Gets the captured Tty value. |

### `T:LibTmux.ClientAttachment`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.ClientAttachment.#ctor(Session?,Window?,Pane?)` | `ClientAttachment(Session? session, Window? window, Pane? pane)` | Public | No | Portable | Creates ClientAttachment. |
| `P:LibTmux.ClientAttachment.Pane` | `Pane? LibTmux.ClientAttachment.Pane { get; init; }` | Public | No | Portable | Gets Pane. |
| `P:LibTmux.ClientAttachment.Session` | `Session? LibTmux.ClientAttachment.Session { get; init; }` | Public | No | Portable | Gets Session. |
| `P:LibTmux.ClientAttachment.Window` | `Window? LibTmux.ClientAttachment.Window { get; init; }` | Public | No | Portable | Gets Window. |

### `T:LibTmux.CommandPromptRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.CommandPromptRequest.#ctor(string)` | `CommandPromptRequest(string template)` | Public | No | Portable | Creates CommandPromptRequest. |
| `M:LibTmux.CommandPromptRequest.ToCommand(LibTmux.Server)` | `TmuxCommand LibTmux.CommandPromptRequest.ToCommand(Server server)` | Public | No | `UnsupportedOSPlatform("windows")` | Returns a prompt request as one tmux command. |
| `P:LibTmux.CommandPromptRequest.BackspaceExits` | `bool LibTmux.CommandPromptRequest.BackspaceExits { get; init; }` | Public | No | Portable | Gets BackspaceExits. |
| `P:LibTmux.CommandPromptRequest.ExpandFormat` | `bool LibTmux.CommandPromptRequest.ExpandFormat { get; init; }` | Public | No | Portable | Gets ExpandFormat. |
| `P:LibTmux.CommandPromptRequest.Inputs` | `string? LibTmux.CommandPromptRequest.Inputs { get; init; }` | Public | No | Portable | Gets Inputs. |
| `P:LibTmux.CommandPromptRequest.KeyOnly` | `bool LibTmux.CommandPromptRequest.KeyOnly { get; init; }` | Public | No | Portable | Gets KeyOnly. |
| `P:LibTmux.CommandPromptRequest.Literal` | `bool LibTmux.CommandPromptRequest.Literal { get; init; }` | Public | No | Portable | Gets Literal. |
| `P:LibTmux.CommandPromptRequest.NoFreeze` | `bool LibTmux.CommandPromptRequest.NoFreeze { get; init; }` | Public | No | Portable | Gets NoFreeze. |
| `P:LibTmux.CommandPromptRequest.Numeric` | `bool LibTmux.CommandPromptRequest.Numeric { get; init; }` | Public | No | Portable | Gets Numeric. |
| `P:LibTmux.CommandPromptRequest.OnInputChange` | `bool LibTmux.CommandPromptRequest.OnInputChange { get; init; }` | Public | No | Portable | Gets OnInputChange. |
| `P:LibTmux.CommandPromptRequest.OneKey` | `bool LibTmux.CommandPromptRequest.OneKey { get; init; }` | Public | No | Portable | Gets OneKey. |
| `P:LibTmux.CommandPromptRequest.Prompt` | `string? LibTmux.CommandPromptRequest.Prompt { get; init; }` | Public | No | Portable | Gets Prompt. |
| `P:LibTmux.CommandPromptRequest.TargetClient` | `string? LibTmux.CommandPromptRequest.TargetClient { get; init; }` | Public | No | Portable | Gets TargetClient. |
| `P:LibTmux.CommandPromptRequest.Template` | `string LibTmux.CommandPromptRequest.Template { get; }` | Public | No | Portable | Gets Template. |
| `P:LibTmux.CommandPromptRequest.Type` | `PromptType? LibTmux.CommandPromptRequest.Type { get; init; }` | Public | No | Portable | Gets Type. |

### `T:LibTmux.ConfirmBeforeRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.ConfirmBeforeRequest.#ctor(IReadOnlyList<string>)` | `ConfirmBeforeRequest(IReadOnlyList<string> command)` | Public | No | Portable | Creates ConfirmBeforeRequest. |
| `M:LibTmux.ConfirmBeforeRequest.ToCommand(LibTmux.Server)` | `TmuxCommand LibTmux.ConfirmBeforeRequest.ToCommand(Server server)` | Public | No | `UnsupportedOSPlatform("windows")` | Returns a confirmation request as one tmux command. |
| `P:LibTmux.ConfirmBeforeRequest.Command` | `IReadOnlyList<string> LibTmux.ConfirmBeforeRequest.Command { get; }` | Public | No | Portable | Gets Command. |
| `P:LibTmux.ConfirmBeforeRequest.ConfirmKey` | `string? LibTmux.ConfirmBeforeRequest.ConfirmKey { get; init; }` | Public | No | Portable | Gets ConfirmKey. |
| `P:LibTmux.ConfirmBeforeRequest.DefaultYes` | `bool LibTmux.ConfirmBeforeRequest.DefaultYes { get; init; }` | Public | No | Portable | Gets DefaultYes. |
| `P:LibTmux.ConfirmBeforeRequest.Prompt` | `string? LibTmux.ConfirmBeforeRequest.Prompt { get; init; }` | Public | No | Portable | Gets Prompt. |
| `P:LibTmux.ConfirmBeforeRequest.TargetClient` | `string? LibTmux.ConfirmBeforeRequest.TargetClient { get; init; }` | Public | No | Portable | Gets TargetClient. |

### `T:LibTmux.ControlModeCommandException`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.ControlModeCommandException.#ctor(string,TmuxCommand,System.Collections.Generic.IReadOnlyList{string},System.Collections.Generic.IReadOnlyList{string},Exception?)` | `ControlModeCommandException(string message, TmuxCommand command, IReadOnlyList<string> outputLines, IReadOnlyList<string> errorLines, Exception? innerException = null)` | Public | No | Portable | Initializes a control-mode command exception. |
| `P:LibTmux.ControlModeCommandException.Command` | `TmuxCommand LibTmux.ControlModeCommandException.Command { get; }` | Public | No | Portable | Gets the command tmux rejected. |
| `P:LibTmux.ControlModeCommandException.ErrorLines` | `IReadOnlyList<string> LibTmux.ControlModeCommandException.ErrorLines { get; }` | Public | No | Portable | Gets the error lines tmux reported. |
| `P:LibTmux.ControlModeCommandException.OutputLines` | `IReadOnlyList<string> LibTmux.ControlModeCommandException.OutputLines { get; }` | Public | No | Portable | Gets output produced before tmux rejected the command. |

### `T:LibTmux.ControlModeSubscriptions`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.ControlModeSubscriptions.SubscribeSessionAsync(IControlModeSession,string,string,System.Threading.CancellationToken)` | `static Task<IReadOnlyList<string>> LibTmux.ControlModeSubscriptions.SubscribeSessionAsync(this IControlModeSession session, string name, string format, CancellationToken cancellationToken = default)` | Public | Yes | `UnsupportedOSPlatform("windows")` | Subscribes to a session-scoped format changing. |

### `T:LibTmux.CopyModeRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.CopyModeRequest.ToCommand(LibTmux.Pane)` | `TmuxCommand LibTmux.CopyModeRequest.ToCommand(Pane pane)` | Public | No | Portable | Returns a copy-mode request as one tmux command. |
| `P:LibTmux.CopyModeRequest.Cancel` | `bool LibTmux.CopyModeRequest.Cancel { get; init; }` | Public | No | Portable | Gets Cancel. |
| `P:LibTmux.CopyModeRequest.ExitOnBottom` | `bool LibTmux.CopyModeRequest.ExitOnBottom { get; init; }` | Public | No | Portable | Gets ExitOnBottom. |
| `P:LibTmux.CopyModeRequest.MouseDrag` | `bool LibTmux.CopyModeRequest.MouseDrag { get; init; }` | Public | No | Portable | Gets MouseDrag. |
| `P:LibTmux.CopyModeRequest.PageDown` | `bool LibTmux.CopyModeRequest.PageDown { get; init; }` | Public | No | Portable | Gets PageDown. |
| `P:LibTmux.CopyModeRequest.ScrollUp` | `bool LibTmux.CopyModeRequest.ScrollUp { get; init; }` | Public | No | Portable | Gets ScrollUp. |
| `P:LibTmux.CopyModeRequest.SourcePane` | `string? LibTmux.CopyModeRequest.SourcePane { get; init; }` | Public | No | Portable | Gets SourcePane. |

### `T:LibTmux.DisplayMenuRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.DisplayMenuRequest.#ctor(IReadOnlyList<TmuxMenuItem>)` | `DisplayMenuRequest(IReadOnlyList<TmuxMenuItem> items)` | Public | No | Portable | Creates DisplayMenuRequest. |
| `M:LibTmux.DisplayMenuRequest.ToCommand(LibTmux.Server)` | `TmuxCommand LibTmux.DisplayMenuRequest.ToCommand(Server server)` | Public | No | Portable | Returns a menu request as one tmux command. |
| `P:LibTmux.DisplayMenuRequest.BorderLines` | `string? LibTmux.DisplayMenuRequest.BorderLines { get; init; }` | Public | No | Portable | Gets BorderLines. |
| `P:LibTmux.DisplayMenuRequest.BorderStyle` | `string? LibTmux.DisplayMenuRequest.BorderStyle { get; init; }` | Public | No | Portable | Gets BorderStyle. |
| `P:LibTmux.DisplayMenuRequest.Items` | `IReadOnlyList<TmuxMenuItem> LibTmux.DisplayMenuRequest.Items { get; }` | Public | No | Portable | Gets Items. |
| `P:LibTmux.DisplayMenuRequest.Mouse` | `bool LibTmux.DisplayMenuRequest.Mouse { get; init; }` | Public | No | Portable | Gets Mouse. |
| `P:LibTmux.DisplayMenuRequest.SelectedStyle` | `string? LibTmux.DisplayMenuRequest.SelectedStyle { get; init; }` | Public | No | Portable | Gets SelectedStyle. |
| `P:LibTmux.DisplayMenuRequest.StartingChoice` | `string? LibTmux.DisplayMenuRequest.StartingChoice { get; init; }` | Public | No | Portable | Gets StartingChoice. |
| `P:LibTmux.DisplayMenuRequest.StayOpen` | `bool LibTmux.DisplayMenuRequest.StayOpen { get; init; }` | Public | No | Portable | Gets StayOpen. |
| `P:LibTmux.DisplayMenuRequest.Style` | `string? LibTmux.DisplayMenuRequest.Style { get; init; }` | Public | No | Portable | Gets Style. |
| `P:LibTmux.DisplayMenuRequest.TargetClient` | `string? LibTmux.DisplayMenuRequest.TargetClient { get; init; }` | Public | No | Portable | Gets TargetClient. |
| `P:LibTmux.DisplayMenuRequest.TargetPane` | `string? LibTmux.DisplayMenuRequest.TargetPane { get; init; }` | Public | No | Portable | Gets TargetPane. |
| `P:LibTmux.DisplayMenuRequest.Title` | `string? LibTmux.DisplayMenuRequest.Title { get; init; }` | Public | No | Portable | Gets Title. |
| `P:LibTmux.DisplayMenuRequest.X` | `string? LibTmux.DisplayMenuRequest.X { get; init; }` | Public | No | Portable | Gets X. |
| `P:LibTmux.DisplayMenuRequest.Y` | `string? LibTmux.DisplayMenuRequest.Y { get; init; }` | Public | No | Portable | Gets Y. |

### `T:LibTmux.DisplayMessageRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.DisplayMessageRequest.ToCommand(LibTmux.Server)` | `TmuxCommand LibTmux.DisplayMessageRequest.ToCommand(Server server)` | Public | No | Portable | Returns a message request as one tmux command. |
| `P:LibTmux.DisplayMessageRequest.AllFormats` | `bool LibTmux.DisplayMessageRequest.AllFormats { get; init; }` | Public | No | Portable | Gets AllFormats. |
| `P:LibTmux.DisplayMessageRequest.Delay` | `TimeSpan? LibTmux.DisplayMessageRequest.Delay { get; init; }` | Public | No | Portable | Gets Delay. |
| `P:LibTmux.DisplayMessageRequest.Format` | `string? LibTmux.DisplayMessageRequest.Format { get; init; }` | Public | No | Portable | Gets Format. |
| `P:LibTmux.DisplayMessageRequest.Message` | `string LibTmux.DisplayMessageRequest.Message { get; init; }` | Public | No | Portable | Gets Message. |
| `P:LibTmux.DisplayMessageRequest.NoExpand` | `bool LibTmux.DisplayMessageRequest.NoExpand { get; init; }` | Public | No | Portable | Gets NoExpand. |
| `P:LibTmux.DisplayMessageRequest.Notify` | `bool LibTmux.DisplayMessageRequest.Notify { get; init; }` | Public | No | Portable | Gets Notify. |
| `P:LibTmux.DisplayMessageRequest.ReturnText` | `bool LibTmux.DisplayMessageRequest.ReturnText { get; init; }` | Public | No | Portable | Gets ReturnText. |
| `P:LibTmux.DisplayMessageRequest.TargetClient` | `string? LibTmux.DisplayMessageRequest.TargetClient { get; init; }` | Public | No | Portable | Gets TargetClient. |
| `P:LibTmux.DisplayMessageRequest.UpdatePane` | `bool LibTmux.DisplayMessageRequest.UpdatePane { get; init; }` | Public | No | Portable | Gets UpdatePane. |
| `P:LibTmux.DisplayMessageRequest.Verbose` | `bool LibTmux.DisplayMessageRequest.Verbose { get; init; }` | Public | No | Portable | Gets Verbose. |

### `T:LibTmux.DisplayPopupRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.DisplayPopupRequest.ToCommand(LibTmux.Pane)` | `TmuxCommand LibTmux.DisplayPopupRequest.ToCommand(Pane pane)` | Public | No | Portable | Returns a popup request as one tmux command. |
| `P:LibTmux.DisplayPopupRequest.BorderLines` | `string? LibTmux.DisplayPopupRequest.BorderLines { get; init; }` | Public | No | Portable | Gets BorderLines. |
| `P:LibTmux.DisplayPopupRequest.BorderStyle` | `string? LibTmux.DisplayPopupRequest.BorderStyle { get; init; }` | Public | No | Portable | Gets BorderStyle. |
| `P:LibTmux.DisplayPopupRequest.CloseExisting` | `bool LibTmux.DisplayPopupRequest.CloseExisting { get; init; }` | Public | No | Portable | Gets CloseExisting. |
| `P:LibTmux.DisplayPopupRequest.CloseMode` | `PopupCloseMode? LibTmux.DisplayPopupRequest.CloseMode { get; init; }` | Public | No | Portable | Gets CloseMode. |
| `P:LibTmux.DisplayPopupRequest.CloseOnAnyKey` | `bool LibTmux.DisplayPopupRequest.CloseOnAnyKey { get; init; }` | Public | No | Portable | Gets CloseOnAnyKey. |
| `P:LibTmux.DisplayPopupRequest.Command` | `string? LibTmux.DisplayPopupRequest.Command { get; init; }` | Public | No | Portable | Gets Command. |
| `P:LibTmux.DisplayPopupRequest.Environment` | `IReadOnlyDictionary<string,string>? LibTmux.DisplayPopupRequest.Environment { get; init; }` | Public | No | Portable | Gets Environment. |
| `P:LibTmux.DisplayPopupRequest.Height` | `string? LibTmux.DisplayPopupRequest.Height { get; init; }` | Public | No | Portable | Gets Height. |
| `P:LibTmux.DisplayPopupRequest.NoBorder` | `bool LibTmux.DisplayPopupRequest.NoBorder { get; init; }` | Public | No | Portable | Gets NoBorder. |
| `P:LibTmux.DisplayPopupRequest.NoKeys` | `bool LibTmux.DisplayPopupRequest.NoKeys { get; init; }` | Public | No | Portable | Gets NoKeys. |
| `P:LibTmux.DisplayPopupRequest.StartDirectory` | `string? LibTmux.DisplayPopupRequest.StartDirectory { get; init; }` | Public | No | Portable | Gets StartDirectory. |
| `P:LibTmux.DisplayPopupRequest.Style` | `string? LibTmux.DisplayPopupRequest.Style { get; init; }` | Public | No | Portable | Gets Style. |
| `P:LibTmux.DisplayPopupRequest.TargetClient` | `string? LibTmux.DisplayPopupRequest.TargetClient { get; init; }` | Public | No | Portable | Gets TargetClient. |
| `P:LibTmux.DisplayPopupRequest.Title` | `string? LibTmux.DisplayPopupRequest.Title { get; init; }` | Public | No | Portable | Gets Title. |
| `P:LibTmux.DisplayPopupRequest.Width` | `string? LibTmux.DisplayPopupRequest.Width { get; init; }` | Public | No | Portable | Gets Width. |
| `P:LibTmux.DisplayPopupRequest.X` | `string? LibTmux.DisplayPopupRequest.X { get; init; }` | Public | No | Portable | Gets X. |
| `P:LibTmux.DisplayPopupRequest.Y` | `string? LibTmux.DisplayPopupRequest.Y { get; init; }` | Public | No | Portable | Gets Y. |

### `T:LibTmux.FindWindowRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.FindWindowRequest.#ctor(string)` | `FindWindowRequest(string pattern)` | Public | No | Portable | Creates FindWindowRequest. |
| `M:LibTmux.FindWindowRequest.ToCommand(LibTmux.Pane)` | `TmuxCommand LibTmux.FindWindowRequest.ToCommand(Pane pane)` | Public | No | Portable | Returns a window-search request as one tmux command. |
| `P:LibTmux.FindWindowRequest.IgnoreCase` | `bool LibTmux.FindWindowRequest.IgnoreCase { get; init; }` | Public | No | Portable | Gets IgnoreCase. |
| `P:LibTmux.FindWindowRequest.MatchContent` | `bool LibTmux.FindWindowRequest.MatchContent { get; init; }` | Public | No | Portable | Gets MatchContent. |
| `P:LibTmux.FindWindowRequest.MatchName` | `bool LibTmux.FindWindowRequest.MatchName { get; init; }` | Public | No | Portable | Gets MatchName. |
| `P:LibTmux.FindWindowRequest.MatchTitle` | `bool LibTmux.FindWindowRequest.MatchTitle { get; init; }` | Public | No | Portable | Gets MatchTitle. |
| `P:LibTmux.FindWindowRequest.Pattern` | `string LibTmux.FindWindowRequest.Pattern { get; }` | Public | No | Portable | Gets Pattern. |
| `P:LibTmux.FindWindowRequest.Regex` | `bool LibTmux.FindWindowRequest.Regex { get; init; }` | Public | No | Portable | Gets Regex. |

### `T:LibTmux.GetOptionRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.GetOptionRequest.#ctor(string)` | `GetOptionRequest(string name)` | Public | No | Portable | Creates GetOptionRequest. |
| `M:LibTmux.GetOptionRequest.ToCommand(LibTmux.TmuxOptions)` | `TmuxCommand LibTmux.GetOptionRequest.ToCommand(TmuxOptions options)` | Public | No | `UnsupportedOSPlatform("windows")` | Returns a named option read as one tmux command. |
| `P:LibTmux.GetOptionRequest.Global` | `bool LibTmux.GetOptionRequest.Global { get; init; }` | Public | No | Portable | Gets Global. |
| `P:LibTmux.GetOptionRequest.IncludeHooks` | `bool LibTmux.GetOptionRequest.IncludeHooks { get; init; }` | Public | No | Portable | Gets IncludeHooks. |
| `P:LibTmux.GetOptionRequest.IncludeInherited` | `bool LibTmux.GetOptionRequest.IncludeInherited { get; init; }` | Public | No | Portable | Gets IncludeInherited. |
| `P:LibTmux.GetOptionRequest.Name` | `string LibTmux.GetOptionRequest.Name { get; }` | Public | No | Portable | Gets Name. |
| `P:LibTmux.GetOptionRequest.Quiet` | `bool LibTmux.GetOptionRequest.Quiet { get; init; }` | Public | No | Portable | Gets Quiet. |
| `P:LibTmux.GetOptionRequest.Scope` | `OptionScope? LibTmux.GetOptionRequest.Scope { get; init; }` | Public | No | Portable | Gets Scope. |

### `T:LibTmux.GetOptionsRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.GetOptionsRequest.ToCommand(LibTmux.TmuxOptions)` | `TmuxCommand LibTmux.GetOptionsRequest.ToCommand(TmuxOptions options)` | Public | No | `UnsupportedOSPlatform("windows")` | Returns a whole-scope option read as one tmux command. |
| `P:LibTmux.GetOptionsRequest.Global` | `bool LibTmux.GetOptionsRequest.Global { get; init; }` | Public | No | Portable | Gets Global. |
| `P:LibTmux.GetOptionsRequest.IncludeHooks` | `bool LibTmux.GetOptionsRequest.IncludeHooks { get; init; }` | Public | No | Portable | Gets IncludeHooks. |
| `P:LibTmux.GetOptionsRequest.IncludeInherited` | `bool LibTmux.GetOptionsRequest.IncludeInherited { get; init; }` | Public | No | Portable | Gets IncludeInherited. |
| `P:LibTmux.GetOptionsRequest.Quiet` | `bool LibTmux.GetOptionsRequest.Quiet { get; init; }` | Public | No | Portable | Gets Quiet. |
| `P:LibTmux.GetOptionsRequest.Scope` | `OptionScope? LibTmux.GetOptionsRequest.Scope { get; init; }` | Public | No | Portable | Gets Scope. |

### `T:LibTmux.HookRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.HookRequest.#ctor(string)` | `HookRequest(string name)` | Public | No | Portable | Creates HookRequest. |
| `P:LibTmux.HookRequest.Global` | `bool LibTmux.HookRequest.Global { get; init; }` | Public | No | Portable | Gets Global. |
| `P:LibTmux.HookRequest.Name` | `string LibTmux.HookRequest.Name { get; }` | Public | No | Portable | Gets Name. |
| `P:LibTmux.HookRequest.Scope` | `OptionScope? LibTmux.HookRequest.Scope { get; init; }` | Public | No | Portable | Gets Scope. |

### `T:LibTmux.IControlModeSession`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.IControlModeSession.SendAsync(LibTmux.TmuxCommand,System.Threading.CancellationToken)` | `Task<IReadOnlyList<string>> SendAsync(TmuxCommand command, CancellationToken cancellationToken = default)` | Public | No | Portable | Runs one command on this client and reads what it answered. |
| `P:LibTmux.IControlModeSession.Events` | `IAsyncEnumerable<TmuxEvent> LibTmux.IControlModeSession.Events { get; }` | Public | No | Portable | Reads what tmux reports for as long as the client runs. |
| `P:LibTmux.IControlModeSession.IsRunning` | `bool LibTmux.IControlModeSession.IsRunning { get; }` | Public | No | Portable | Gets whether the client is still running. |

### ``T:LibTmux.ITmuxRequest`1``

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| ``M:LibTmux.ITmuxRequest`1.ToCommand(`0)`` | `TmuxCommand LibTmux.ITmuxRequest<TTarget>.ToCommand(TTarget target)` | Public | No | Portable | Returns this request as one tmux command. |

### `T:LibTmux.IfShellRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.IfShellRequest.#ctor(string,IReadOnlyList<string>)` | `IfShellRequest(string shellCommand, IReadOnlyList<string> thenCommand)` | Public | No | Portable | Creates IfShellRequest. |
| `M:LibTmux.IfShellRequest.LibTmux.ITmuxRequest<LibTmux.Server>.ToCommand(LibTmux.Server)` | `TmuxCommand LibTmux.ITmuxRequest<LibTmux.Server>.ToCommand(Server target)` | Explicit interface | No | Portable | Returns this request as one tmux command; the server is not needed to build it. |
| `M:LibTmux.IfShellRequest.ToCommand()` | `TmuxCommand LibTmux.IfShellRequest.ToCommand()` | Public | No | Portable | Returns a conditional request as one tmux command. |
| `P:LibTmux.IfShellRequest.Background` | `bool LibTmux.IfShellRequest.Background { get; init; }` | Public | No | Portable | Gets Background. |
| `P:LibTmux.IfShellRequest.ElseCommand` | `IReadOnlyList<string>? LibTmux.IfShellRequest.ElseCommand { get; init; }` | Public | No | Portable | Gets ElseCommand. |
| `P:LibTmux.IfShellRequest.ShellCommand` | `string LibTmux.IfShellRequest.ShellCommand { get; }` | Public | No | Portable | Gets ShellCommand. |
| `P:LibTmux.IfShellRequest.TargetPane` | `string? LibTmux.IfShellRequest.TargetPane { get; init; }` | Public | No | Portable | Gets TargetPane. |
| `P:LibTmux.IfShellRequest.ThenCommand` | `IReadOnlyList<string> LibTmux.IfShellRequest.ThenCommand { get; }` | Public | No | Portable | Gets ThenCommand. |

### `T:LibTmux.IncompleteSnapshotException`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.IncompleteSnapshotException.#ctor(string,SnapshotDepth)` | `IncompleteSnapshotException(string relation, SnapshotDepth capturedDepth)` | Public | No | Portable | Creates IncompleteSnapshotException. |
| `P:LibTmux.IncompleteSnapshotException.CapturedDepth` | `SnapshotDepth LibTmux.IncompleteSnapshotException.CapturedDepth { get; }` | Public | No | Portable | Gets how far down the capture that missed it reached. |
| `P:LibTmux.IncompleteSnapshotException.Relation` | `string LibTmux.IncompleteSnapshotException.Relation { get; }` | Public | No | Portable | Gets the relation that was not captured. |

### `T:LibTmux.LibTmuxException`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.LibTmuxException.#ctor(string,Exception?)` | `LibTmuxException(string message, Exception? innerException = null)` | Public | No | Portable | Creates LibTmuxException. |
| `M:LibTmux.LibTmuxException.#ctor(string,TmuxDispatchState,Exception?)` | `LibTmuxException(string message, TmuxDispatchState dispatch, Exception? innerException = null)` | Public | No | Portable | Creates LibTmuxException with a known dispatch state. |
| `P:LibTmux.LibTmuxException.Dispatch` | `TmuxDispatchState LibTmux.LibTmuxException.Dispatch { get; }` | Public | No | Portable | Gets whether the command reached tmux, and so whether a retry is safe. |

### `T:LibTmux.LibTmuxInfo`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `P:LibTmux.LibTmuxInfo.MaximumTestedTmuxVersion` | `static TmuxVersion LibTmux.LibTmuxInfo.MaximumTestedTmuxVersion { get; }` | Public | Yes | Portable | Gets the highest required tested tmux version. |
| `P:LibTmux.LibTmuxInfo.MinimumTmuxVersion` | `static TmuxVersion LibTmux.LibTmuxInfo.MinimumTmuxVersion { get; }` | Public | Yes | Portable | Gets the minimum supported tmux version. |
| `P:LibTmux.LibTmuxInfo.Version` | `static Version LibTmux.LibTmuxInfo.Version { get; }` | Public | Yes | Portable | Gets the library assembly version. |

### `T:LibTmux.LinkWindowRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.LinkWindowRequest.#ctor(string)` | `LinkWindowRequest(string targetSession)` | Public | No | Portable | Creates LinkWindowRequest. |
| `M:LibTmux.LinkWindowRequest.ToCommand(LibTmux.Window)` | `TmuxCommand LibTmux.LinkWindowRequest.ToCommand(Window window)` | Public | No | Portable | Returns a link request as one tmux command. |
| `P:LibTmux.LinkWindowRequest.Detach` | `bool LibTmux.LinkWindowRequest.Detach { get; init; }` | Public | No | Portable | Gets Detach. |
| `P:LibTmux.LinkWindowRequest.Direction` | `WindowDirection? LibTmux.LinkWindowRequest.Direction { get; init; }` | Public | No | Portable | Gets Direction. |
| `P:LibTmux.LinkWindowRequest.ReplaceExisting` | `bool LibTmux.LinkWindowRequest.ReplaceExisting { get; init; }` | Public | No | Portable | Gets ReplaceExisting. |
| `P:LibTmux.LinkWindowRequest.TargetIndex` | `string? LibTmux.LinkWindowRequest.TargetIndex { get; init; }` | Public | No | Portable | Gets TargetIndex. |
| `P:LibTmux.LinkWindowRequest.TargetSession` | `string LibTmux.LinkWindowRequest.TargetSession { get; }` | Public | No | Portable | Gets TargetSession. |

### `T:LibTmux.ListBuffersRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.ListBuffersRequest.LibTmux.ITmuxRequest<LibTmux.Server>.ToCommand(LibTmux.Server)` | `TmuxCommand LibTmux.ITmuxRequest<LibTmux.Server>.ToCommand(Server target)` | Explicit interface | No | Portable | Returns this request as one tmux command; the server is not needed to build it. |
| `M:LibTmux.ListBuffersRequest.ToCommand()` | `TmuxCommand LibTmux.ListBuffersRequest.ToCommand()` | Public | No | Portable | Returns a buffer-listing request as one tmux command. |
| `P:LibTmux.ListBuffersRequest.Filter` | `UnsafeTmuxFilter? LibTmux.ListBuffersRequest.Filter { get; init; }` | Public | No | Portable | Gets Filter. |
| `P:LibTmux.ListBuffersRequest.Format` | `string? LibTmux.ListBuffersRequest.Format { get; init; }` | Public | No | Portable | Gets Format. |

### `T:LibTmux.ListHooksRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.ListHooksRequest.ToCommand(LibTmux.TmuxHooks)` | `TmuxCommand LibTmux.ListHooksRequest.ToCommand(TmuxHooks hooks)` | Public | No | `UnsupportedOSPlatform("windows")` | Returns a hook listing as one tmux command. |
| `P:LibTmux.ListHooksRequest.Global` | `bool LibTmux.ListHooksRequest.Global { get; init; }` | Public | No | Portable | Gets Global. |
| `P:LibTmux.ListHooksRequest.Scope` | `OptionScope? LibTmux.ListHooksRequest.Scope { get; init; }` | Public | No | Portable | Gets Scope. |

### `T:LibTmux.MovePaneRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.MovePaneRequest.#ctor(string)` | `MovePaneRequest(string target)` | Public | No | Portable | Creates MovePaneRequest. |
| `M:LibTmux.MovePaneRequest.ToCommand(LibTmux.Pane)` | `TmuxCommand LibTmux.MovePaneRequest.ToCommand(Pane pane)` | Public | No | Portable | Returns a pane-move request as one tmux command. |
| `P:LibTmux.MovePaneRequest.Before` | `bool LibTmux.MovePaneRequest.Before { get; init; }` | Public | No | Portable | Gets Before. |
| `P:LibTmux.MovePaneRequest.Detach` | `bool LibTmux.MovePaneRequest.Detach { get; init; }` | Public | No | Portable | Gets Detach. |
| `P:LibTmux.MovePaneRequest.Direction` | `PaneDirection LibTmux.MovePaneRequest.Direction { get; init; }` | Public | No | Portable | Gets Direction. |
| `P:LibTmux.MovePaneRequest.FullWindow` | `bool LibTmux.MovePaneRequest.FullWindow { get; init; }` | Public | No | Portable | Gets FullWindow. |
| `P:LibTmux.MovePaneRequest.Size` | `string? LibTmux.MovePaneRequest.Size { get; init; }` | Public | No | Portable | Gets Size. |
| `P:LibTmux.MovePaneRequest.Target` | `string LibTmux.MovePaneRequest.Target { get; }` | Public | No | Portable | Gets Target. |

### `T:LibTmux.MoveWindowRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.MoveWindowRequest.ToCommand(LibTmux.Window)` | `TmuxCommand LibTmux.MoveWindowRequest.ToCommand(Window window)` | Public | No | Portable | Returns a window-move request as one tmux command. |
| `P:LibTmux.MoveWindowRequest.Destination` | `string LibTmux.MoveWindowRequest.Destination { get; init; }` | Public | No | Portable | Gets Destination. |
| `P:LibTmux.MoveWindowRequest.Direction` | `WindowDirection? LibTmux.MoveWindowRequest.Direction { get; init; }` | Public | No | Portable | Gets Direction. |
| `P:LibTmux.MoveWindowRequest.NoSelect` | `bool LibTmux.MoveWindowRequest.NoSelect { get; init; }` | Public | No | Portable | Gets NoSelect. |
| `P:LibTmux.MoveWindowRequest.Renumber` | `bool LibTmux.MoveWindowRequest.Renumber { get; init; }` | Public | No | Portable | Gets Renumber. |
| `P:LibTmux.MoveWindowRequest.ReplaceExisting` | `bool LibTmux.MoveWindowRequest.ReplaceExisting { get; init; }` | Public | No | Portable | Gets ReplaceExisting. |
| `P:LibTmux.MoveWindowRequest.Session` | `string? LibTmux.MoveWindowRequest.Session { get; init; }` | Public | No | Portable | Gets Session. |

### `T:LibTmux.NewPaneRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.NewPaneRequest.ToCommand(LibTmux.Pane)` | `TmuxCommand LibTmux.NewPaneRequest.ToCommand(Pane pane)` | Public | No | Portable | Returns a floating-pane request as one tmux command. |
| `P:LibTmux.NewPaneRequest.ActiveBorderStyle` | `string? LibTmux.NewPaneRequest.ActiveBorderStyle { get; init; }` | Public | No | Portable | Gets ActiveBorderStyle. |
| `P:LibTmux.NewPaneRequest.Attach` | `bool LibTmux.NewPaneRequest.Attach { get; init; }` | Public | No | Portable | Gets Attach. |
| `P:LibTmux.NewPaneRequest.Command` | `string? LibTmux.NewPaneRequest.Command { get; init; }` | Public | No | Portable | Gets Command. |
| `P:LibTmux.NewPaneRequest.Empty` | `bool LibTmux.NewPaneRequest.Empty { get; init; }` | Public | No | Portable | Gets Empty. |
| `P:LibTmux.NewPaneRequest.Environment` | `IReadOnlyDictionary<string,string>? LibTmux.NewPaneRequest.Environment { get; init; }` | Public | No | Portable | Gets Environment. |
| `P:LibTmux.NewPaneRequest.Height` | `int? LibTmux.NewPaneRequest.Height { get; init; }` | Public | No | Portable | Gets Height. |
| `P:LibTmux.NewPaneRequest.InactiveBorderStyle` | `string? LibTmux.NewPaneRequest.InactiveBorderStyle { get; init; }` | Public | No | Portable | Gets InactiveBorderStyle. |
| `P:LibTmux.NewPaneRequest.KeepOpen` | `bool LibTmux.NewPaneRequest.KeepOpen { get; init; }` | Public | No | Portable | Gets KeepOpen. |
| `P:LibTmux.NewPaneRequest.Message` | `string? LibTmux.NewPaneRequest.Message { get; init; }` | Public | No | Portable | Gets Message. |
| `P:LibTmux.NewPaneRequest.StartDirectory` | `string? LibTmux.NewPaneRequest.StartDirectory { get; init; }` | Public | No | Portable | Gets StartDirectory. |
| `P:LibTmux.NewPaneRequest.Style` | `string? LibTmux.NewPaneRequest.Style { get; init; }` | Public | No | Portable | Gets Style. |
| `P:LibTmux.NewPaneRequest.Target` | `string? LibTmux.NewPaneRequest.Target { get; init; }` | Public | No | Portable | Gets Target. |
| `P:LibTmux.NewPaneRequest.Width` | `int? LibTmux.NewPaneRequest.Width { get; init; }` | Public | No | Portable | Gets Width. |
| `P:LibTmux.NewPaneRequest.X` | `int? LibTmux.NewPaneRequest.X { get; init; }` | Public | No | Portable | Gets X. |
| `P:LibTmux.NewPaneRequest.Y` | `int? LibTmux.NewPaneRequest.Y { get; init; }` | Public | No | Portable | Gets Y. |
| `P:LibTmux.NewPaneRequest.Zoom` | `bool LibTmux.NewPaneRequest.Zoom { get; init; }` | Public | No | Portable | Gets Zoom. |

### `T:LibTmux.NewSessionRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.NewSessionRequest.LibTmux.ITmuxRequest<LibTmux.Server>.ToCommand(LibTmux.Server)` | `TmuxCommand LibTmux.ITmuxRequest<LibTmux.Server>.ToCommand(Server target)` | Explicit interface | No | Portable | Returns this request as one tmux command; the server is not needed to build it. |
| `M:LibTmux.NewSessionRequest.ToCommand()` | `TmuxCommand LibTmux.NewSessionRequest.ToCommand()` | Public | No | Portable | Returns a session request as one tmux command. |
| `P:LibTmux.NewSessionRequest.Attach` | `bool LibTmux.NewSessionRequest.Attach { get; init; }` | Public | No | Portable | Gets Attach. |
| `P:LibTmux.NewSessionRequest.ClientFlags` | `string? LibTmux.NewSessionRequest.ClientFlags { get; init; }` | Public | No | Portable | Gets ClientFlags. |
| `P:LibTmux.NewSessionRequest.Command` | `string? LibTmux.NewSessionRequest.Command { get; init; }` | Public | No | Portable | Gets Command. |
| `P:LibTmux.NewSessionRequest.DetachOthers` | `bool LibTmux.NewSessionRequest.DetachOthers { get; init; }` | Public | No | Portable | Gets DetachOthers. |
| `P:LibTmux.NewSessionRequest.Environment` | `IReadOnlyDictionary<string,string>? LibTmux.NewSessionRequest.Environment { get; init; }` | Public | No | Portable | Gets Environment. |
| `P:LibTmux.NewSessionRequest.Height` | `string? LibTmux.NewSessionRequest.Height { get; init; }` | Public | No | Portable | Gets Height. |
| `P:LibTmux.NewSessionRequest.Name` | `string? LibTmux.NewSessionRequest.Name { get; init; }` | Public | No | Portable | Gets Name. |
| `P:LibTmux.NewSessionRequest.NoSize` | `bool LibTmux.NewSessionRequest.NoSize { get; init; }` | Public | No | Portable | Gets NoSize. |
| `P:LibTmux.NewSessionRequest.ReplaceExisting` | `bool LibTmux.NewSessionRequest.ReplaceExisting { get; init; }` | Public | No | Portable | Gets ReplaceExisting. |
| `P:LibTmux.NewSessionRequest.StartDirectory` | `string? LibTmux.NewSessionRequest.StartDirectory { get; init; }` | Public | No | Portable | Gets StartDirectory. |
| `P:LibTmux.NewSessionRequest.Width` | `string? LibTmux.NewSessionRequest.Width { get; init; }` | Public | No | Portable | Gets Width. |
| `P:LibTmux.NewSessionRequest.WindowName` | `string? LibTmux.NewSessionRequest.WindowName { get; init; }` | Public | No | Portable | Gets WindowName. |

### `T:LibTmux.NewWindowRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.NewWindowRequest.ToCommand(LibTmux.Session)` | `TmuxCommand LibTmux.NewWindowRequest.ToCommand(Session session)` | Public | No | Portable | Returns a window request as one tmux command. |
| `P:LibTmux.NewWindowRequest.Attach` | `bool LibTmux.NewWindowRequest.Attach { get; init; }` | Public | No | Portable | Gets Attach. |
| `P:LibTmux.NewWindowRequest.Command` | `string? LibTmux.NewWindowRequest.Command { get; init; }` | Public | No | Portable | Gets Command. |
| `P:LibTmux.NewWindowRequest.Direction` | `WindowDirection? LibTmux.NewWindowRequest.Direction { get; init; }` | Public | No | Portable | Gets Direction. |
| `P:LibTmux.NewWindowRequest.Environment` | `IReadOnlyDictionary<string,string>? LibTmux.NewWindowRequest.Environment { get; init; }` | Public | No | Portable | Gets Environment. |
| `P:LibTmux.NewWindowRequest.Index` | `string? LibTmux.NewWindowRequest.Index { get; init; }` | Public | No | Portable | Gets Index. |
| `P:LibTmux.NewWindowRequest.KillExisting` | `bool LibTmux.NewWindowRequest.KillExisting { get; init; }` | Public | No | Portable | Gets KillExisting. |
| `P:LibTmux.NewWindowRequest.Name` | `string? LibTmux.NewWindowRequest.Name { get; init; }` | Public | No | Portable | Gets Name. |
| `P:LibTmux.NewWindowRequest.SelectExisting` | `bool LibTmux.NewWindowRequest.SelectExisting { get; init; }` | Public | No | Portable | Gets SelectExisting. |
| `P:LibTmux.NewWindowRequest.StartDirectory` | `string? LibTmux.NewWindowRequest.StartDirectory { get; init; }` | Public | No | Portable | Gets StartDirectory. |
| `P:LibTmux.NewWindowRequest.TargetWindow` | `string? LibTmux.NewWindowRequest.TargetWindow { get; init; }` | Public | No | Portable | Gets TargetWindow. |

### `T:LibTmux.OptionScope`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `F:LibTmux.OptionScope.Pane` | `Pane = 3` | Public | Implicit | Portable | The Pane value. Value: `3`. |
| `F:LibTmux.OptionScope.Server` | `Server = 0` | Public | Implicit | Portable | The Server value. Value: `0`. |
| `F:LibTmux.OptionScope.Session` | `Session = 1` | Public | Implicit | Portable | The Session value. Value: `1`. |
| `F:LibTmux.OptionScope.Window` | `Window = 2` | Public | Implicit | Portable | The Window value. Value: `2`. |

### `T:LibTmux.OwnedServerScope`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.OwnedServerScope.DisposeAsync()` | `ValueTask LibTmux.OwnedServerScope.DisposeAsync()` | Public | No | `UnsupportedOSPlatform("windows")` | Performs idempotent bounded cleanup with observable failures. |
| `P:LibTmux.OwnedServerScope.Value` | `Server LibTmux.OwnedServerScope.Value { get; }` | Public | No | Portable | Gets the owned server handle. |

### `T:LibTmux.OwnedSessionScope`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.OwnedSessionScope.DisposeAsync()` | `ValueTask LibTmux.OwnedSessionScope.DisposeAsync()` | Public | No | `UnsupportedOSPlatform("windows")` | Performs idempotent bounded cleanup with observable failures. |
| `P:LibTmux.OwnedSessionScope.Value` | `Session LibTmux.OwnedSessionScope.Value { get; }` | Public | No | Portable | Gets the owned session handle. |

### `T:LibTmux.OwnedWindowScope`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.OwnedWindowScope.DisposeAsync()` | `ValueTask LibTmux.OwnedWindowScope.DisposeAsync()` | Public | No | `UnsupportedOSPlatform("windows")` | Performs idempotent bounded cleanup with observable failures. |
| `P:LibTmux.OwnedWindowScope.Value` | `Window LibTmux.OwnedWindowScope.Value { get; }` | Public | No | Portable | Gets the owned window handle. |

### `T:LibTmux.Pane`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.Pane.BreakAsync(string?,bool,CancellationToken)` | `Task<Window> LibTmux.Pane.BreakAsync(string? windowName = null, bool detach = true, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs Break. |
| `M:LibTmux.Pane.CaptureAsync(CapturePaneRequest?,CancellationToken)` | `Task<IReadOnlyList<string>> LibTmux.Pane.CaptureAsync(CapturePaneRequest? request = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs Capture. |
| `M:LibTmux.Pane.CaptureToBufferAsync(string,CapturePaneRequest?,CancellationToken)` | `Task LibTmux.Pane.CaptureToBufferAsync(string bufferName, CapturePaneRequest? request = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Captures pane content directly into a tmux buffer. |
| `M:LibTmux.Pane.ChooseBufferAsync(CancellationToken)` | `Task LibTmux.Pane.ChooseBufferAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs ChooseBuffer. |
| `M:LibTmux.Pane.ChooseClientAsync(CancellationToken)` | `Task LibTmux.Pane.ChooseClientAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs ChooseClient. |
| `M:LibTmux.Pane.ChooseTreeAsync(ChooseTreeRequest?,CancellationToken)` | `Task LibTmux.Pane.ChooseTreeAsync(ChooseTreeRequest? request = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs ChooseTree. |
| `M:LibTmux.Pane.ClearAsync(CancellationToken)` | `Task<Pane> LibTmux.Pane.ClearAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs Clear. |
| `M:LibTmux.Pane.ClearHistoryAsync(bool,CancellationToken)` | `Task LibTmux.Pane.ClearHistoryAsync(bool resetHyperlinks = false, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs ClearHistory. |
| `M:LibTmux.Pane.CreatePaneAsync(NewPaneRequest?,CancellationToken)` | `Task<Pane> LibTmux.Pane.CreatePaneAsync(NewPaneRequest? request = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs CreatePane. |
| `M:LibTmux.Pane.DisplayMessageAsync(DisplayMessageRequest,CancellationToken)` | `Task<IReadOnlyList<string>?> LibTmux.Pane.DisplayMessageAsync(DisplayMessageRequest request, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs DisplayMessage. |
| `M:LibTmux.Pane.DisplayPaneNumbersAsync(TimeSpan?,bool,CancellationToken)` | `Task LibTmux.Pane.DisplayPaneNumbersAsync(TimeSpan? duration = null, bool noSelect = false, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs DisplayPaneNumbers. |
| `M:LibTmux.Pane.DisplayPopupAsync(DisplayPopupRequest?,CancellationToken)` | `Task LibTmux.Pane.DisplayPopupAsync(DisplayPopupRequest? request = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs DisplayPopup. |
| `M:LibTmux.Pane.EnterAsync(CancellationToken)` | `Task<Pane> LibTmux.Pane.EnterAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs Enter. |
| `M:LibTmux.Pane.EnterClockModeAsync(CancellationToken)` | `Task LibTmux.Pane.EnterClockModeAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs EnterClockMode. |
| `M:LibTmux.Pane.EnterCopyModeAsync(CopyModeRequest?,CancellationToken)` | `Task LibTmux.Pane.EnterCopyModeAsync(CopyModeRequest? request = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs EnterCopyMode. |
| `M:LibTmux.Pane.EnterCustomizeModeAsync(CancellationToken)` | `Task LibTmux.Pane.EnterCustomizeModeAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs EnterCustomizeMode. |
| `M:LibTmux.Pane.ExecuteCommandAsync(IReadOnlyList<string>,string?,CancellationToken)` | `Task<TmuxCommandResult> LibTmux.Pane.ExecuteCommandAsync(IReadOnlyList<string> arguments, string? targetOverride = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Executes a raw command with stable target injection for the entity handle. |
| `M:LibTmux.Pane.FindWindowAsync(FindWindowRequest,CancellationToken)` | `Task LibTmux.Pane.FindWindowAsync(FindWindowRequest request, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs FindWindow. |
| `M:LibTmux.Pane.FromEnvironmentAsync(IReadOnlyDictionary<string,string>?,CancellationToken)` | `static Task<Pane> LibTmux.Pane.FromEnvironmentAsync(IReadOnlyDictionary<string,string>? environment = null, CancellationToken cancellationToken = default)` | Public | Yes | `UnsupportedOSPlatform("windows")` | Performs FromEnvironment. |
| `M:LibTmux.Pane.JoinAsync(MovePaneRequest,CancellationToken)` | `Task LibTmux.Pane.JoinAsync(MovePaneRequest request, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs Join. |
| `M:LibTmux.Pane.KillAsync(bool,CancellationToken)` | `Task LibTmux.Pane.KillAsync(bool allExcept = false, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs Kill. |
| `M:LibTmux.Pane.MoveAsync(MovePaneRequest,CancellationToken)` | `Task LibTmux.Pane.MoveAsync(MovePaneRequest request, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs Move. |
| `M:LibTmux.Pane.PasteBufferAsync(PasteBufferRequest?,CancellationToken)` | `Task LibTmux.Pane.PasteBufferAsync(PasteBufferRequest? request = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs PasteBuffer. |
| `M:LibTmux.Pane.PipeAsync(PipePaneRequest?,CancellationToken)` | `Task LibTmux.Pane.PipeAsync(PipePaneRequest? request = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs Pipe. |
| `M:LibTmux.Pane.RefreshAsync(CancellationToken)` | `Task<Pane> LibTmux.Pane.RefreshAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs Refresh. |
| `M:LibTmux.Pane.ResetAsync(CancellationToken)` | `Task<Pane> LibTmux.Pane.ResetAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs Reset. |
| `M:LibTmux.Pane.ResizeAsync(ResizePaneRequest,CancellationToken)` | `Task<Pane> LibTmux.Pane.ResizeAsync(ResizePaneRequest request, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs Resize. |
| `M:LibTmux.Pane.RespawnAsync(RespawnRequest?,CancellationToken)` | `Task LibTmux.Pane.RespawnAsync(RespawnRequest? request = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs Respawn. |
| `M:LibTmux.Pane.SelectAsync(SelectPaneRequest?,CancellationToken)` | `Task<Pane> LibTmux.Pane.SelectAsync(SelectPaneRequest? request = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs Select. |
| `M:LibTmux.Pane.SendKeysAsync(SendKeysRequest,CancellationToken)` | `Task LibTmux.Pane.SendKeysAsync(SendKeysRequest request, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs SendKeys. |
| `M:LibTmux.Pane.SendPrefixAsync(bool,CancellationToken)` | `Task LibTmux.Pane.SendPrefixAsync(bool secondary = false, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs SendPrefix. |
| `M:LibTmux.Pane.SendTextAsync(string,bool,CancellationToken)` | `Task LibTmux.Pane.SendTextAsync(string text, bool enter = true, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Sends text and optionally Enter. |
| `M:LibTmux.Pane.SetHeightAsync(int,CancellationToken)` | `Task<Pane> LibTmux.Pane.SetHeightAsync(int height, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs SetHeight. |
| `M:LibTmux.Pane.SetTitleAsync(string,CancellationToken)` | `Task<Pane> LibTmux.Pane.SetTitleAsync(string title, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs SetTitle. |
| `M:LibTmux.Pane.SetWidthAsync(int,CancellationToken)` | `Task<Pane> LibTmux.Pane.SetWidthAsync(int width, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs SetWidth. |
| `M:LibTmux.Pane.SplitAsync(SplitPaneRequest?,CancellationToken)` | `Task<Pane> LibTmux.Pane.SplitAsync(SplitPaneRequest? request = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs Split. |
| `M:LibTmux.Pane.SwapAsync(SwapPaneRequest,CancellationToken)` | `Task LibTmux.Pane.SwapAsync(SwapPaneRequest request, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs Swap. |
| `M:LibTmux.Pane.op_Equality(Pane?,Pane?)` | `static bool operator ==(Pane? left, Pane? right)` | Public | Yes | Portable | Reports whether two handles name the same pane. |
| `M:LibTmux.Pane.op_Inequality(Pane?,Pane?)` | `static bool operator !=(Pane? left, Pane? right)` | Public | Yes | Portable | Reports whether two handles name different panes. |
| `P:LibTmux.Pane.AtBottom` | `bool LibTmux.Pane.AtBottom { get; }` | Public | No | Portable | Gets the captured AtBottom value. |
| `P:LibTmux.Pane.AtLeft` | `bool LibTmux.Pane.AtLeft { get; }` | Public | No | Portable | Gets the captured AtLeft value. |
| `P:LibTmux.Pane.AtRight` | `bool LibTmux.Pane.AtRight { get; }` | Public | No | Portable | Gets the captured AtRight value. |
| `P:LibTmux.Pane.AtTop` | `bool LibTmux.Pane.AtTop { get; }` | Public | No | Portable | Gets the captured AtTop value. |
| `P:LibTmux.Pane.Generation` | `ServerGeneration LibTmux.Pane.Generation { get; }` | Public | No | Portable | Gets the captured Generation value. |
| `P:LibTmux.Pane.Height` | `int LibTmux.Pane.Height { get; }` | Public | No | Portable | Gets the captured Height value. |
| `P:LibTmux.Pane.Hooks` | `TmuxHooks LibTmux.Pane.Hooks { get; }` | Public | No | Portable | Gets the captured Hooks value. |
| `P:LibTmux.Pane.Id` | `PaneId LibTmux.Pane.Id { get; }` | Public | No | Portable | Gets the captured Id value. |
| `P:LibTmux.Pane.Index` | `int LibTmux.Pane.Index { get; }` | Public | No | Portable | Gets the captured Index value. |
| `P:LibTmux.Pane.Left` | `int LibTmux.Pane.Left { get; }` | Public | No | Portable | Gets the captured Left value. |
| `P:LibTmux.Pane.Options` | `TmuxOptions LibTmux.Pane.Options { get; }` | Public | No | Portable | Gets the captured Options value. |
| `P:LibTmux.Pane.RawFormatFields` | `IReadOnlyDictionary<string,string?> LibTmux.Pane.RawFormatFields { get; }` | Public | No | Portable | Gets copied raw tmux format tokens captured for this snapshot. |
| `P:LibTmux.Pane.Server` | `Server LibTmux.Pane.Server { get; }` | Public | No | Portable | Gets the captured Server value. |
| `P:LibTmux.Pane.Session` | `Session LibTmux.Pane.Session { get; }` | Public | No | Portable | Gets the captured Session value. |
| `P:LibTmux.Pane.Title` | `string? LibTmux.Pane.Title { get; }` | Public | No | Portable | Gets the captured Title value. |
| `P:LibTmux.Pane.Top` | `int LibTmux.Pane.Top { get; }` | Public | No | Portable | Gets the captured Top value. |
| `P:LibTmux.Pane.Width` | `int LibTmux.Pane.Width { get; }` | Public | No | Portable | Gets the captured Width value. |
| `P:LibTmux.Pane.Window` | `Window LibTmux.Pane.Window { get; }` | Public | No | Portable | Gets the captured Window value. |

### `T:LibTmux.PaneDirection`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `F:LibTmux.PaneDirection.Above` | `Above = 0` | Public | Implicit | Portable | The Above value. Value: `0`. |
| `F:LibTmux.PaneDirection.Below` | `Below = 1` | Public | Implicit | Portable | The Below value. Value: `1`. |
| `F:LibTmux.PaneDirection.Left` | `Left = 2` | Public | Implicit | Portable | The Left value. Value: `2`. |
| `F:LibTmux.PaneDirection.Right` | `Right = 3` | Public | Implicit | Portable | The Right value. Value: `3`. |

### `T:LibTmux.PaneId`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.PaneId.#ctor(int)` | `PaneId(int value)` | Public | No | Portable | Creates a validated identifier. |
| `M:LibTmux.PaneId.CompareTo(PaneId)` | `int LibTmux.PaneId.CompareTo(PaneId other)` | Public | No | Portable | Orders this identifier against another numerically. |
| `M:LibTmux.PaneId.Parse(ReadOnlySpan<char>)` | `static static PaneId LibTmux.PaneId.Parse(ReadOnlySpan<char> text)` | Public | Yes | Portable | Parses a prefixed pane identifier from a span. |
| `M:LibTmux.PaneId.Parse(string)` | `static PaneId LibTmux.PaneId.Parse(string text)` | Public | Yes | Portable | Parses a prefixed identifier. |
| `M:LibTmux.PaneId.ToString()` | `string LibTmux.PaneId.ToString()` | Public | No | Portable | Returns the canonical prefixed identifier. |
| `M:LibTmux.PaneId.TryParse(ReadOnlySpan<char>,PaneId)` | `static static bool LibTmux.PaneId.TryParse(ReadOnlySpan<char> text, out PaneId result)` | Public | Yes | Portable | Tries to parse a prefixed pane identifier from a span. |
| `M:LibTmux.PaneId.TryParse(string?,PaneId)` | `static bool LibTmux.PaneId.TryParse(string? text, out PaneId result)` | Public | Yes | Portable | Tries to parse a prefixed identifier without throwing. |
| `M:LibTmux.PaneId.op_GreaterThan(PaneId,PaneId)` | `static bool operator >(PaneId left, PaneId right)` | Public | Yes | Portable | Compares the order two pane identifiers were handed out in. |
| `M:LibTmux.PaneId.op_GreaterThanOrEqual(PaneId,PaneId)` | `static bool operator >=(PaneId left, PaneId right)` | Public | Yes | Portable | Compares the order two pane identifiers were handed out in. |
| `M:LibTmux.PaneId.op_LessThan(PaneId,PaneId)` | `static bool operator <(PaneId left, PaneId right)` | Public | Yes | Portable | Compares the order two pane identifiers were handed out in. |
| `M:LibTmux.PaneId.op_LessThanOrEqual(PaneId,PaneId)` | `static bool operator <=(PaneId left, PaneId right)` | Public | Yes | Portable | Compares the order two pane identifiers were handed out in. |
| `P:LibTmux.PaneId.Value` | `int LibTmux.PaneId.Value { get; }` | Public | No | Portable | Gets the nonnegative numeric value. |

### `T:LibTmux.PaneInputMode`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `F:LibTmux.PaneInputMode.Disable` | `Disable = 1` | Public | Implicit | Portable | The Disable value. Value: `1`. |
| `F:LibTmux.PaneInputMode.Enable` | `Enable = 0` | Public | Implicit | Portable | The Enable value. Value: `0`. |

### `T:LibTmux.PaneObservation`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.PaneObservation.WatchAsync(IControlModeSession,Pane,System.Threading.CancellationToken)` | `static IAsyncEnumerable<TmuxEvent> LibTmux.PaneObservation.WatchAsync(this IControlModeSession session, Pane pane, CancellationToken cancellationToken = default)` | Public | Yes | `UnsupportedOSPlatform("windows")` | Watches one pane's output until it ends. |

### `T:LibTmux.PaneSelectDirection`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `F:LibTmux.PaneSelectDirection.Down` | `Down = 1` | Public | Implicit | Portable | The Down value. Value: `1`. |
| `F:LibTmux.PaneSelectDirection.Last` | `Last = 4` | Public | Implicit | Portable | The Last value. Value: `4`. |
| `F:LibTmux.PaneSelectDirection.Left` | `Left = 2` | Public | Implicit | Portable | The Left value. Value: `2`. |
| `F:LibTmux.PaneSelectDirection.Right` | `Right = 3` | Public | Implicit | Portable | The Right value. Value: `3`. |
| `F:LibTmux.PaneSelectDirection.Up` | `Up = 0` | Public | Implicit | Portable | The Up value. Value: `0`. |

### `T:LibTmux.PaneSwapDirection`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `F:LibTmux.PaneSwapDirection.Down` | `Down = 1` | Public | Implicit | Portable | The Down value. Value: `1`. |
| `F:LibTmux.PaneSwapDirection.Up` | `Up = 0` | Public | Implicit | Portable | The Up value. Value: `0`. |

### `T:LibTmux.PasteBufferRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.PasteBufferRequest.ToCommand(LibTmux.Pane)` | `TmuxCommand LibTmux.PasteBufferRequest.ToCommand(Pane pane)` | Public | No | Portable | Returns a paste request as one tmux command. |
| `P:LibTmux.PasteBufferRequest.Bracketed` | `bool LibTmux.PasteBufferRequest.Bracketed { get; init; }` | Public | No | Portable | Gets Bracketed. |
| `P:LibTmux.PasteBufferRequest.DeleteAfter` | `bool LibTmux.PasteBufferRequest.DeleteAfter { get; init; }` | Public | No | Portable | Gets DeleteAfter. |
| `P:LibTmux.PasteBufferRequest.Name` | `string? LibTmux.PasteBufferRequest.Name { get; init; }` | Public | No | Portable | Gets Name. |
| `P:LibTmux.PasteBufferRequest.RawBytes` | `bool LibTmux.PasteBufferRequest.RawBytes { get; init; }` | Public | No | Portable | Gets RawBytes. |
| `P:LibTmux.PasteBufferRequest.Separator` | `string? LibTmux.PasteBufferRequest.Separator { get; init; }` | Public | No | Portable | Gets Separator. |
| `P:LibTmux.PasteBufferRequest.UseLineFeedSeparator` | `bool LibTmux.PasteBufferRequest.UseLineFeedSeparator { get; init; }` | Public | No | Portable | Gets UseLineFeedSeparator. |

### `T:LibTmux.PipePaneRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.PipePaneRequest.ToCommand(LibTmux.Pane)` | `TmuxCommand LibTmux.PipePaneRequest.ToCommand(Pane pane)` | Public | No | Portable | Returns a pane-piping request as one tmux command. |
| `P:LibTmux.PipePaneRequest.Command` | `string? LibTmux.PipePaneRequest.Command { get; init; }` | Public | No | Portable | Gets Command. |
| `P:LibTmux.PipePaneRequest.InputOnly` | `bool LibTmux.PipePaneRequest.InputOnly { get; init; }` | Public | No | Portable | Gets InputOnly. |
| `P:LibTmux.PipePaneRequest.OutputOnly` | `bool LibTmux.PipePaneRequest.OutputOnly { get; init; }` | Public | No | Portable | Gets OutputOnly. |
| `P:LibTmux.PipePaneRequest.Toggle` | `bool LibTmux.PipePaneRequest.Toggle { get; init; }` | Public | No | Portable | Gets Toggle. |

### `T:LibTmux.PopupCloseMode`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `F:LibTmux.PopupCloseMode.AnyExit` | `AnyExit = 0` | Public | Implicit | Portable | The AnyExit value. Value: `0`. |
| `F:LibTmux.PopupCloseMode.SuccessfulExit` | `SuccessfulExit = 1` | Public | Implicit | Portable | The SuccessfulExit value. Value: `1`. |

### `T:LibTmux.PromptType`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `F:LibTmux.PromptType.Command` | `Command = 0` | Public | Implicit | Portable | The Command value. Value: `0`. |
| `F:LibTmux.PromptType.Search` | `Search = 1` | Public | Implicit | Portable | The Search value. Value: `1`. |
| `F:LibTmux.PromptType.Target` | `Target = 2` | Public | Implicit | Portable | The Target value. Value: `2`. |
| `F:LibTmux.PromptType.WindowTarget` | `WindowTarget = 3` | Public | Implicit | Portable | The WindowTarget value. Value: `3`. |

### `T:LibTmux.PsmuxCaptureOptions`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.PsmuxCaptureOptions.#ctor(CapturePanePosition?,CapturePanePosition?,bool,bool)` | `PsmuxCaptureOptions(CapturePanePosition? startLine = null, CapturePanePosition? endLine = null, bool escapeSequences = false, bool joinWrappedLines = false)` | Public | No | Portable | Creates an audited psmux capture request. |
| `P:LibTmux.PsmuxCaptureOptions.EndLine` | `CapturePanePosition? LibTmux.PsmuxCaptureOptions.EndLine { get; }` | Public | No | Portable | Gets the last capture line. |
| `P:LibTmux.PsmuxCaptureOptions.EscapeSequences` | `bool LibTmux.PsmuxCaptureOptions.EscapeSequences { get; }` | Public | No | Portable | Gets whether terminal escape sequences are preserved. |
| `P:LibTmux.PsmuxCaptureOptions.JoinWrappedLines` | `bool LibTmux.PsmuxCaptureOptions.JoinWrappedLines { get; }` | Public | No | Portable | Gets whether wrapped screen rows are joined. |
| `P:LibTmux.PsmuxCaptureOptions.StartLine` | `CapturePanePosition? LibTmux.PsmuxCaptureOptions.StartLine { get; }` | Public | No | Portable | Gets the first capture line. |

### `T:LibTmux.PsmuxConnectionOptions`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.PsmuxConnectionOptions.#ctor(string,string,string,string,ILogger?)` | `PsmuxConnectionOptions(string executablePath, string expectedBinarySha256, string dataDirectory, string namespaceName, ILogger? logger = null)` | Public | No | Portable | Creates one pinned client and isolated psmux endpoint. |
| `P:LibTmux.PsmuxConnectionOptions.DataDirectory` | `string LibTmux.PsmuxConnectionOptions.DataDirectory { get; }` | Public | No | Portable | Gets the canonical isolated Windows data directory. |
| `P:LibTmux.PsmuxConnectionOptions.ExecutablePath` | `string LibTmux.PsmuxConnectionOptions.ExecutablePath { get; }` | Public | No | Portable | Gets the absolute psmux client executable path. |
| `P:LibTmux.PsmuxConnectionOptions.ExpectedBinarySha256` | `string LibTmux.PsmuxConnectionOptions.ExpectedBinarySha256 { get; }` | Public | No | Portable | Gets the expected executable SHA-256. |
| `P:LibTmux.PsmuxConnectionOptions.Logger` | `ILogger? LibTmux.PsmuxConnectionOptions.Logger { get; }` | Public | No | Portable | Gets the optional connection logger. |
| `P:LibTmux.PsmuxConnectionOptions.NamespaceName` | `string LibTmux.PsmuxConnectionOptions.NamespaceName { get; }` | Public | No | Portable | Gets the explicit non-default psmux namespace. |

### `T:LibTmux.PsmuxPane`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.PsmuxPane.CaptureAsync(PsmuxCaptureOptions?,CancellationToken)` | `Task<IReadOnlyList<string>> LibTmux.PsmuxPane.CaptureAsync(PsmuxCaptureOptions? options = null, CancellationToken cancellationToken = default)` | Public | No | Portable | Captures pane text with best-effort target consistency. |
| `P:LibTmux.PsmuxPane.Height` | `int LibTmux.PsmuxPane.Height { get; }` | Public | No | Portable | Gets the captured pane height. |
| `P:LibTmux.PsmuxPane.Id` | `PaneId LibTmux.PsmuxPane.Id { get; }` | Public | No | Portable | Gets the captured pane identifier. |
| `P:LibTmux.PsmuxPane.Index` | `int LibTmux.PsmuxPane.Index { get; }` | Public | No | Portable | Gets the captured pane index. |
| `P:LibTmux.PsmuxPane.Server` | `PsmuxServer LibTmux.PsmuxPane.Server { get; }` | Public | No | Portable | Gets the psmux endpoint that produced the observation. |
| `P:LibTmux.PsmuxPane.SessionId` | `SessionId LibTmux.PsmuxPane.SessionId { get; }` | Public | No | Portable | Gets the captured parent session identifier. |
| `P:LibTmux.PsmuxPane.Title` | `string? LibTmux.PsmuxPane.Title { get; }` | Public | No | Portable | Gets the captured pane title. |
| `P:LibTmux.PsmuxPane.Width` | `int LibTmux.PsmuxPane.Width { get; }` | Public | No | Portable | Gets the captured pane width. |
| `P:LibTmux.PsmuxPane.WindowId` | `WindowId LibTmux.PsmuxPane.WindowId { get; }` | Public | No | Portable | Gets the captured parent window identifier. |

### `T:LibTmux.PsmuxServer`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `F:LibTmux.PsmuxServer.SupportedBinarySha256` | `static const string LibTmux.PsmuxServer.SupportedBinarySha256` | Public | Yes | Portable | The exact psmux client executable SHA-256 accepted by this preview. Value: `54e5c54db259218348f966b5d0d0b5153fdef6350074855ea9ce627d20537b0d`. |
| `F:LibTmux.PsmuxServer.SupportedCommit` | `static const string LibTmux.PsmuxServer.SupportedCommit` | Public | Yes | Portable | The exact psmux source commit accepted by this preview. Value: `66cf61354c473b35d4f0c06c57384fc46d61ffdb`. |
| `F:LibTmux.PsmuxServer.SupportedImplementationBanner` | `static const string LibTmux.PsmuxServer.SupportedImplementationBanner` | Public | Yes | Portable | The exact clean implementation banner accepted by this preview. Value: `psmux 3.3.8 (66cf613 2026-08-18)`. |
| `M:LibTmux.PsmuxServer.ConnectAsync(PsmuxConnectionOptions,CancellationToken)` | `static Task<PsmuxServer> LibTmux.PsmuxServer.ConnectAsync(PsmuxConnectionOptions options, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Connects through the pinned client and validates one session. |
| `M:LibTmux.PsmuxServer.GetPanesAsync(CancellationToken)` | `Task<IReadOnlyList<PsmuxPane>> LibTmux.PsmuxServer.GetPanesAsync(CancellationToken cancellationToken = default)` | Public | No | Portable | Reads all current panes in the sole visible session. |
| `M:LibTmux.PsmuxServer.GetSessionAsync(CancellationToken)` | `Task<PsmuxSession> LibTmux.PsmuxServer.GetSessionAsync(CancellationToken cancellationToken = default)` | Public | No | Portable | Reads the sole visible session or fails closed. |
| `M:LibTmux.PsmuxServer.GetWindowsAsync(CancellationToken)` | `Task<IReadOnlyList<PsmuxWindow>> LibTmux.PsmuxServer.GetWindowsAsync(CancellationToken cancellationToken = default)` | Public | No | Portable | Reads all current windows in the sole visible session. |
| `M:LibTmux.PsmuxServer.RefreshAsync(CancellationToken)` | `Task<PsmuxServer> LibTmux.PsmuxServer.RefreshAsync(CancellationToken cancellationToken = default)` | Public | No | Portable | Reconnects and returns a fresh endpoint observation. |
| `P:LibTmux.PsmuxServer.ConnectionOptions` | `PsmuxConnectionOptions LibTmux.PsmuxServer.ConnectionOptions { get; }` | Public | No | Portable | Gets the endpoint trust and routing settings. |
| `P:LibTmux.PsmuxServer.Version` | `TmuxVersion LibTmux.PsmuxServer.Version { get; }` | Public | No | Portable | Gets the psmux compatibility version. |

### `T:LibTmux.PsmuxSession`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.PsmuxSession.GetPanesAsync(CancellationToken)` | `Task<IReadOnlyList<PsmuxPane>> LibTmux.PsmuxSession.GetPanesAsync(CancellationToken cancellationToken = default)` | Public | No | Portable | Reads the session's current panes. |
| `M:LibTmux.PsmuxSession.GetWindowsAsync(CancellationToken)` | `Task<IReadOnlyList<PsmuxWindow>> LibTmux.PsmuxSession.GetWindowsAsync(CancellationToken cancellationToken = default)` | Public | No | Portable | Reads the session's current windows. |
| `P:LibTmux.PsmuxSession.Attached` | `bool LibTmux.PsmuxSession.Attached { get; }` | Public | No | Portable | Gets whether a client was attached when observed. |
| `P:LibTmux.PsmuxSession.Id` | `SessionId LibTmux.PsmuxSession.Id { get; }` | Public | No | Portable | Gets the captured session identifier. |
| `P:LibTmux.PsmuxSession.Name` | `string LibTmux.PsmuxSession.Name { get; }` | Public | No | Portable | Gets the captured session name. |
| `P:LibTmux.PsmuxSession.Server` | `PsmuxServer LibTmux.PsmuxSession.Server { get; }` | Public | No | Portable | Gets the psmux endpoint that produced the observation. |

### `T:LibTmux.PsmuxWindow`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.PsmuxWindow.GetPanesAsync(CancellationToken)` | `Task<IReadOnlyList<PsmuxPane>> LibTmux.PsmuxWindow.GetPanesAsync(CancellationToken cancellationToken = default)` | Public | No | Portable | Reads the window's current panes. |
| `P:LibTmux.PsmuxWindow.Height` | `int LibTmux.PsmuxWindow.Height { get; }` | Public | No | Portable | Gets the captured window height. |
| `P:LibTmux.PsmuxWindow.Id` | `WindowId LibTmux.PsmuxWindow.Id { get; }` | Public | No | Portable | Gets the captured window identifier. |
| `P:LibTmux.PsmuxWindow.Index` | `int LibTmux.PsmuxWindow.Index { get; }` | Public | No | Portable | Gets the captured window index. |
| `P:LibTmux.PsmuxWindow.Name` | `string LibTmux.PsmuxWindow.Name { get; }` | Public | No | Portable | Gets the captured window name. |
| `P:LibTmux.PsmuxWindow.Server` | `PsmuxServer LibTmux.PsmuxWindow.Server { get; }` | Public | No | Portable | Gets the psmux endpoint that produced the observation. |
| `P:LibTmux.PsmuxWindow.SessionId` | `SessionId LibTmux.PsmuxWindow.SessionId { get; }` | Public | No | Portable | Gets the captured parent session identifier. |
| `P:LibTmux.PsmuxWindow.Width` | `int LibTmux.PsmuxWindow.Width { get; }` | Public | No | Portable | Gets the captured window width. |

### `T:LibTmux.Query.Json.QueryJson`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.Query.Json.QueryJson.Deserialize(string,QueryJsonLimits?)` | `static QueryDocument LibTmux.Query.Json.QueryJson.Deserialize(string json, QueryJsonLimits? limits = null)` | Public | Yes | Portable | Parses canonical v1 JSON with bounded resources. |
| `M:LibTmux.Query.Json.QueryJson.Serialize(QueryDocument)` | `static string LibTmux.Query.Json.QueryJson.Serialize(QueryDocument document)` | Public | Yes | Portable | Serializes canonical v1 JSON. |

### `T:LibTmux.Query.Json.QueryJsonLimits`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.Query.Json.QueryJsonLimits.#ctor(int,int,int,int,int)` | `QueryJsonLimits(int maximumDepth, int maximumNodes, int maximumStringLength, int maximumPatternLength, int maximumUtf8Bytes)` | Public | No | Portable | Creates QueryJsonLimits. |
| `P:LibTmux.Query.Json.QueryJsonLimits.MaximumDepth` | `int LibTmux.Query.Json.QueryJsonLimits.MaximumDepth { get; }` | Public | No | Portable | Gets MaximumDepth. |
| `P:LibTmux.Query.Json.QueryJsonLimits.MaximumNodes` | `int LibTmux.Query.Json.QueryJsonLimits.MaximumNodes { get; }` | Public | No | Portable | Gets MaximumNodes. |
| `P:LibTmux.Query.Json.QueryJsonLimits.MaximumPatternLength` | `int LibTmux.Query.Json.QueryJsonLimits.MaximumPatternLength { get; }` | Public | No | Portable | Gets MaximumPatternLength. |
| `P:LibTmux.Query.Json.QueryJsonLimits.MaximumStringLength` | `int LibTmux.Query.Json.QueryJsonLimits.MaximumStringLength { get; }` | Public | No | Portable | Gets MaximumStringLength. |
| `P:LibTmux.Query.Json.QueryJsonLimits.MaximumUtf8Bytes` | `int LibTmux.Query.Json.QueryJsonLimits.MaximumUtf8Bytes { get; }` | Public | No | Portable | Gets MaximumUtf8Bytes. |

### `T:LibTmux.Query.QueryDocument`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `P:LibTmux.Query.QueryDocument.RequiredSnapshotDepth` | `SnapshotDepth LibTmux.Query.QueryDocument.RequiredSnapshotDepth { get; }` | Public | No | Portable | Gets the minimum relation depth needed for complete local evaluation. |
| `P:LibTmux.Query.QueryDocument.Schema` | `string LibTmux.Query.QueryDocument.Schema { get; }` | Public | No | Portable | Gets Schema. |
| `P:LibTmux.Query.QueryDocument.Target` | `QueryTarget LibTmux.Query.QueryDocument.Target { get; }` | Public | No | Portable | Gets Target. |
| `P:LibTmux.Query.QueryDocument.Version` | `int LibTmux.Query.QueryDocument.Version { get; }` | Public | No | Portable | Gets Version. |

### `T:LibTmux.Query.QueryEdgeParser`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.Query.QueryEdgeParser.ParseNameContains(QueryTarget,string)` | `static QueryDocument LibTmux.Query.QueryEdgeParser.ParseNameContains(QueryTarget target, string value)` | Public | Yes | Portable | Parses name__contains into the canonical AST. |

### `T:LibTmux.Query.QueryExtensions`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| ``M:LibTmux.Query.QueryExtensions.Compile``1(QueryDocument)`` | `static Func<T,bool> LibTmux.Query.QueryExtensions.Compile<T>(this QueryDocument document)` | Public | Yes | Portable | Compiles the canonical direct interpreter for a query document. |
| ``M:LibTmux.Query.QueryExtensions.Matching``1(IEnumerable<T>,Expression<Func<T,bool>>)`` | `static IReadOnlyList<T> LibTmux.Query.QueryExtensions.Matching<T>(this IEnumerable<T> source, Expression<Func<T,bool>> predicate)` | Public | Yes | Portable | Translates and evaluates a supported predicate against an explicit snapshot. |
| ``M:LibTmux.Query.QueryExtensions.Matching``1(IEnumerable<T>,QueryDocument)`` | `static IReadOnlyList<T> LibTmux.Query.QueryExtensions.Matching<T>(this IEnumerable<T> source, QueryDocument document)` | Public | Yes | Portable | Evaluates one canonical query document against an explicit snapshot. |
| ``M:LibTmux.Query.QueryExtensions.Matching``1(IEnumerable<T>,QueryDocument,CancellationToken)`` | `static IReadOnlyList<T> LibTmux.Query.QueryExtensions.Matching<T>(this IEnumerable<T> source, QueryDocument document, CancellationToken cancellationToken)` | Public | Yes | Portable | Evaluates one canonical query document with cooperative cancellation. |
| ``M:LibTmux.Query.QueryExtensions.Translate``1(Expression<Func<T,bool>>)`` | `static QueryDocument LibTmux.Query.QueryExtensions.Translate<T>(Expression<Func<T,bool>> predicate)` | Public | Yes | Portable | Translates a supported expression into the canonical query document. |

### `T:LibTmux.Query.QueryTarget`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `F:LibTmux.Query.QueryTarget.Client` | `Client = 3` | Public | Implicit | Portable | The Client value. Value: `3`. |
| `F:LibTmux.Query.QueryTarget.Pane` | `Pane = 2` | Public | Implicit | Portable | The Pane value. Value: `2`. |
| `F:LibTmux.Query.QueryTarget.Session` | `Session = 0` | Public | Implicit | Portable | The Session value. Value: `0`. |
| `F:LibTmux.Query.QueryTarget.Window` | `Window = 1` | Public | Implicit | Portable | The Window value. Value: `1`. |

### `T:LibTmux.ResizeDirection`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `F:LibTmux.ResizeDirection.Down` | `Down = 1` | Public | Implicit | Portable | The Down value. Value: `1`. |
| `F:LibTmux.ResizeDirection.Left` | `Left = 2` | Public | Implicit | Portable | The Left value. Value: `2`. |
| `F:LibTmux.ResizeDirection.Right` | `Right = 3` | Public | Implicit | Portable | The Right value. Value: `3`. |
| `F:LibTmux.ResizeDirection.Up` | `Up = 0` | Public | Implicit | Portable | The Up value. Value: `0`. |

### `T:LibTmux.ResizePaneRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.ResizePaneRequest.ToCommand(LibTmux.Pane)` | `TmuxCommand LibTmux.ResizePaneRequest.ToCommand(Pane pane)` | Public | No | Portable | Returns a pane-resize request as one tmux command. |
| `P:LibTmux.ResizePaneRequest.Adjustment` | `int? LibTmux.ResizePaneRequest.Adjustment { get; init; }` | Public | No | Portable | Gets Adjustment. |
| `P:LibTmux.ResizePaneRequest.Direction` | `ResizeDirection? LibTmux.ResizePaneRequest.Direction { get; init; }` | Public | No | Portable | Gets Direction. |
| `P:LibTmux.ResizePaneRequest.Height` | `string? LibTmux.ResizePaneRequest.Height { get; init; }` | Public | No | Portable | Gets Height. |
| `P:LibTmux.ResizePaneRequest.Mouse` | `bool LibTmux.ResizePaneRequest.Mouse { get; init; }` | Public | No | Portable | Gets Mouse. |
| `P:LibTmux.ResizePaneRequest.TrimBelow` | `bool LibTmux.ResizePaneRequest.TrimBelow { get; init; }` | Public | No | Portable | Gets TrimBelow. |
| `P:LibTmux.ResizePaneRequest.Width` | `string? LibTmux.ResizePaneRequest.Width { get; init; }` | Public | No | Portable | Gets Width. |
| `P:LibTmux.ResizePaneRequest.Zoom` | `bool LibTmux.ResizePaneRequest.Zoom { get; init; }` | Public | No | Portable | Gets Zoom. |

### `T:LibTmux.ResizeWindowRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.ResizeWindowRequest.ToCommand(LibTmux.Window)` | `TmuxCommand LibTmux.ResizeWindowRequest.ToCommand(Window window)` | Public | No | Portable | Returns a window-resize request as one tmux command. |
| `P:LibTmux.ResizeWindowRequest.Adjustment` | `int? LibTmux.ResizeWindowRequest.Adjustment { get; init; }` | Public | No | Portable | Gets Adjustment. |
| `P:LibTmux.ResizeWindowRequest.Direction` | `ResizeDirection? LibTmux.ResizeWindowRequest.Direction { get; init; }` | Public | No | Portable | Gets Direction. |
| `P:LibTmux.ResizeWindowRequest.Height` | `int? LibTmux.ResizeWindowRequest.Height { get; init; }` | Public | No | Portable | Gets Height. |
| `P:LibTmux.ResizeWindowRequest.Mode` | `WindowResizeMode? LibTmux.ResizeWindowRequest.Mode { get; init; }` | Public | No | Portable | Gets Mode. |
| `P:LibTmux.ResizeWindowRequest.Width` | `int? LibTmux.ResizeWindowRequest.Width { get; init; }` | Public | No | Portable | Gets Width. |

### `T:LibTmux.RespawnRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.RespawnRequest.ToCommand(LibTmux.Pane)` | `TmuxCommand LibTmux.RespawnRequest.ToCommand(Pane pane)` | Public | No | Portable | Returns a respawn request as one tmux command. |
| `P:LibTmux.RespawnRequest.Command` | `string? LibTmux.RespawnRequest.Command { get; init; }` | Public | No | Portable | Gets Command. |
| `P:LibTmux.RespawnRequest.Environment` | `IReadOnlyDictionary<string,string>? LibTmux.RespawnRequest.Environment { get; init; }` | Public | No | Portable | Gets Environment. |
| `P:LibTmux.RespawnRequest.KillExistingProcess` | `bool LibTmux.RespawnRequest.KillExistingProcess { get; init; }` | Public | No | Portable | Gets KillExistingProcess. |
| `P:LibTmux.RespawnRequest.StartDirectory` | `string? LibTmux.RespawnRequest.StartDirectory { get; init; }` | Public | No | Portable | Gets StartDirectory. |

### `T:LibTmux.RunShellRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.RunShellRequest.#ctor(string)` | `RunShellRequest(string command)` | Public | No | Portable | Creates RunShellRequest. |
| `M:LibTmux.RunShellRequest.ToCommand(LibTmux.Server)` | `TmuxCommand LibTmux.RunShellRequest.ToCommand(Server server)` | Public | No | Portable | Returns a shell request as one tmux command. |
| `P:LibTmux.RunShellRequest.Arguments` | `IReadOnlyList<string>? LibTmux.RunShellRequest.Arguments { get; init; }` | Public | No | Portable | Gets Arguments. |
| `P:LibTmux.RunShellRequest.AsTmuxCommand` | `bool LibTmux.RunShellRequest.AsTmuxCommand { get; init; }` | Public | No | Portable | Gets AsTmuxCommand. |
| `P:LibTmux.RunShellRequest.Background` | `bool LibTmux.RunShellRequest.Background { get; init; }` | Public | No | Portable | Gets Background. |
| `P:LibTmux.RunShellRequest.Command` | `string LibTmux.RunShellRequest.Command { get; }` | Public | No | Portable | Gets Command. |
| `P:LibTmux.RunShellRequest.Delay` | `TimeSpan? LibTmux.RunShellRequest.Delay { get; init; }` | Public | No | Portable | Gets Delay. |
| `P:LibTmux.RunShellRequest.ShowStandardError` | `bool LibTmux.RunShellRequest.ShowStandardError { get; init; }` | Public | No | Portable | Gets ShowStandardError. |
| `P:LibTmux.RunShellRequest.TargetPane` | `string? LibTmux.RunShellRequest.TargetPane { get; init; }` | Public | No | Portable | Gets TargetPane. |
| `P:LibTmux.RunShellRequest.WorkingDirectory` | `string? LibTmux.RunShellRequest.WorkingDirectory { get; init; }` | Public | No | Portable | Gets WorkingDirectory. |

### `T:LibTmux.SelectLayoutMode`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `F:LibTmux.SelectLayoutMode.Next` | `Next = 1` | Public | Implicit | Portable | The Next value. Value: `1`. |
| `F:LibTmux.SelectLayoutMode.Previous` | `Previous = 2` | Public | Implicit | Portable | The Previous value. Value: `2`. |
| `F:LibTmux.SelectLayoutMode.Spread` | `Spread = 0` | Public | Implicit | Portable | The Spread value. Value: `0`. |

### `T:LibTmux.SelectLayoutRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.SelectLayoutRequest.ToCommand(LibTmux.Window)` | `TmuxCommand LibTmux.SelectLayoutRequest.ToCommand(Window window)` | Public | No | Portable | Returns a layout request as one tmux command for a window. |
| `P:LibTmux.SelectLayoutRequest.Layout` | `string? LibTmux.SelectLayoutRequest.Layout { get; init; }` | Public | No | Portable | Gets Layout. |
| `P:LibTmux.SelectLayoutRequest.Mode` | `SelectLayoutMode? LibTmux.SelectLayoutRequest.Mode { get; init; }` | Public | No | Portable | Gets Mode. |

### `T:LibTmux.SelectPaneRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.SelectPaneRequest.ToCommand(LibTmux.Pane)` | `TmuxCommand LibTmux.SelectPaneRequest.ToCommand(Pane pane)` | Public | No | Portable | Returns a pane-selection request as one tmux command. |
| `P:LibTmux.SelectPaneRequest.Direction` | `PaneSelectDirection? LibTmux.SelectPaneRequest.Direction { get; init; }` | Public | No | Portable | Gets Direction. |
| `P:LibTmux.SelectPaneRequest.InputEnabled` | `bool? LibTmux.SelectPaneRequest.InputEnabled { get; init; }` | Public | No | Portable | Gets InputEnabled. |
| `P:LibTmux.SelectPaneRequest.KeepZoom` | `bool LibTmux.SelectPaneRequest.KeepZoom { get; init; }` | Public | No | Portable | Gets KeepZoom. |
| `P:LibTmux.SelectPaneRequest.Last` | `bool LibTmux.SelectPaneRequest.Last { get; init; }` | Public | No | Portable | Gets Last. |
| `P:LibTmux.SelectPaneRequest.Mark` | `bool? LibTmux.SelectPaneRequest.Mark { get; init; }` | Public | No | Portable | Gets Mark. |

### `T:LibTmux.SendKeysRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.SendKeysRequest.ToCommand(LibTmux.Pane)` | `TmuxCommand LibTmux.SendKeysRequest.ToCommand(Pane pane)` | Public | No | Portable | Returns a key request as one tmux command for a pane. |
| `P:LibTmux.SendKeysRequest.CopyModeCommand` | `string? LibTmux.SendKeysRequest.CopyModeCommand { get; init; }` | Public | No | Portable | Gets CopyModeCommand. |
| `P:LibTmux.SendKeysRequest.Enter` | `bool LibTmux.SendKeysRequest.Enter { get; init; }` | Public | No | Portable | Gets Enter. |
| `P:LibTmux.SendKeysRequest.ExpandFormats` | `bool LibTmux.SendKeysRequest.ExpandFormats { get; init; }` | Public | No | Portable | Gets ExpandFormats. |
| `P:LibTmux.SendKeysRequest.HexKeys` | `bool LibTmux.SendKeysRequest.HexKeys { get; init; }` | Public | No | Portable | Gets HexKeys. |
| `P:LibTmux.SendKeysRequest.KeyName` | `bool LibTmux.SendKeysRequest.KeyName { get; init; }` | Public | No | Portable | Gets KeyName. |
| `P:LibTmux.SendKeysRequest.Literal` | `bool LibTmux.SendKeysRequest.Literal { get; init; }` | Public | No | Portable | Gets Literal. |
| `P:LibTmux.SendKeysRequest.Repeat` | `int? LibTmux.SendKeysRequest.Repeat { get; init; }` | Public | No | Portable | Gets Repeat. |
| `P:LibTmux.SendKeysRequest.Reset` | `bool LibTmux.SendKeysRequest.Reset { get; init; }` | Public | No | Portable | Gets Reset. |
| `P:LibTmux.SendKeysRequest.SuppressHistory` | `bool LibTmux.SendKeysRequest.SuppressHistory { get; init; }` | Public | No | Portable | Gets SuppressHistory. |
| `P:LibTmux.SendKeysRequest.TargetClient` | `string? LibTmux.SendKeysRequest.TargetClient { get; init; }` | Public | No | Portable | Gets TargetClient. |
| `P:LibTmux.SendKeysRequest.Text` | `string? LibTmux.SendKeysRequest.Text { get; init; }` | Public | No | Portable | Gets Text. |

### `T:LibTmux.Server`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.Server.AttachSessionAsync(AttachSessionRequest,CancellationToken)` | `Task LibTmux.Server.AttachSessionAsync(AttachSessionRequest request, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs AttachSession. |
| `M:LibTmux.Server.BindKeyAsync(BindKeyRequest,CancellationToken)` | `Task LibTmux.Server.BindKeyAsync(BindKeyRequest request, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs BindKey. |
| `M:LibTmux.Server.CaptureSnapshotAsync(SnapshotDepth,CancellationToken)` | `Task<Server> LibTmux.Server.CaptureSnapshotAsync(SnapshotDepth depth, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Captures an immutable hierarchy to the requested depth. |
| `M:LibTmux.Server.Chain` | `TmuxChain Chain()` | Public | No | Portable | Begins a chain that runs its commands in one tmux invocation. |
| `M:LibTmux.Server.ClearPromptHistoryAsync(PromptType?,CancellationToken)` | `Task LibTmux.Server.ClearPromptHistoryAsync(PromptType? type = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs ClearPromptHistory. |
| `M:LibTmux.Server.ConfigureAccessAsync(ServerAccessRequest,CancellationToken)` | `Task<IReadOnlyList<string>?> LibTmux.Server.ConfigureAccessAsync(ServerAccessRequest request, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs ConfigureAccess. |
| `M:LibTmux.Server.ConfirmBeforeAsync(ConfirmBeforeRequest,CancellationToken)` | `Task LibTmux.Server.ConfirmBeforeAsync(ConfirmBeforeRequest request, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs ConfirmBefore. |
| `M:LibTmux.Server.ConnectAsync(CancellationToken)` | `Task<Server> LibTmux.Server.ConnectAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Materializes this connection handle and returns its immutable replacement. |
| `M:LibTmux.Server.ConnectAsync(ServerConnectionOptions?,CancellationToken)` | `static Task<Server> LibTmux.Server.ConnectAsync(ServerConnectionOptions? options = null, CancellationToken cancellationToken = default)` | Public | Yes | `UnsupportedOSPlatform("windows")` | Connects to an existing or configured tmux endpoint without taking cleanup ownership. |
| `M:LibTmux.Server.CreateOwnedAsync(ServerConnectionOptions?,CancellationToken)` | `static Task<OwnedServerScope> LibTmux.Server.CreateOwnedAsync(ServerConnectionOptions? options = null, CancellationToken cancellationToken = default)` | Public | Yes | `UnsupportedOSPlatform("windows")` | Creates and owns an isolated tmux server. |
| `M:LibTmux.Server.CreateOwnedSessionAsync(NewSessionRequest?,CancellationToken)` | `Task<OwnedSessionScope> LibTmux.Server.CreateOwnedSessionAsync(NewSessionRequest? request = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Creates a session and returns an explicitly owned cleanup scope. |
| `M:LibTmux.Server.CreateSessionAsync(NewSessionRequest?,CancellationToken)` | `Task<Session> LibTmux.Server.CreateSessionAsync(NewSessionRequest? request = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs CreateSession. |
| `M:LibTmux.Server.DeleteBufferAsync(string?,CancellationToken)` | `Task LibTmux.Server.DeleteBufferAsync(string? name = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs DeleteBuffer. |
| `M:LibTmux.Server.DetachAllClientsAsync(string?,string?,CancellationToken)` | `Task LibTmux.Server.DetachAllClientsAsync(string? keepClient = null, string? shellCommand = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs DetachAllClients. |
| `M:LibTmux.Server.DetachClientAsync(string?,string?,CancellationToken)` | `Task LibTmux.Server.DetachClientAsync(string? targetClient = null, string? shellCommand = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs DetachClient. |
| `M:LibTmux.Server.DisplayMessageAsync(DisplayMessageRequest,CancellationToken)` | `Task<IReadOnlyList<string>?> LibTmux.Server.DisplayMessageAsync(DisplayMessageRequest request, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs DisplayMessage. |
| `M:LibTmux.Server.EnterControlModeAsync(string?,System.Threading.CancellationToken)` | `Task<IControlModeSession> EnterControlModeAsync(string? target = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Starts a tmux control client and keeps it running. |
| `M:LibTmux.Server.ExecuteCommandAsync(IReadOnlyList<string>,CancellationToken)` | `Task<TmuxCommandResult> LibTmux.Server.ExecuteCommandAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Executes one raw tmux command and returns both byte streams. |
| `M:LibTmux.Server.FindPaneAsync(PaneId,CancellationToken)` | `Task<Pane?> LibTmux.Server.FindPaneAsync(PaneId id, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Reads materialized scalar state, returning null only when a successful lookup finds no match. |
| `M:LibTmux.Server.FindSessionAsync(SessionId,CancellationToken)` | `Task<Session?> LibTmux.Server.FindSessionAsync(SessionId id, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Reads materialized scalar state, returning null only when a successful lookup finds no match. |
| `M:LibTmux.Server.FindWindowAsync(WindowId,CancellationToken)` | `Task<Window?> LibTmux.Server.FindWindowAsync(WindowId id, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Reads materialized scalar state, returning null only when a successful lookup finds no match. |
| `M:LibTmux.Server.FromEnvironment(IReadOnlyDictionary<string,string>?)` | `static Server LibTmux.Server.FromEnvironment(IReadOnlyDictionary<string,string>? environment = null)` | Public | Yes | Portable | Parses a tmux endpoint from an environment snapshot without starting a process. |
| `M:LibTmux.Server.GetAttachedSessionsAsync(CancellationToken)` | `Task<IReadOnlyList<Session>> LibTmux.Server.GetAttachedSessionsAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Reads the live collection and preserves failures, including an absent daemon. List error policy: loud. |
| `M:LibTmux.Server.GetBufferAsync(string?,CancellationToken)` | `Task<string> LibTmux.Server.GetBufferAsync(string? name = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs GetBuffer. |
| `M:LibTmux.Server.GetBufferLinesAsync(ListBuffersRequest?,CancellationToken)` | `Task<IReadOnlyList<string>> LibTmux.Server.GetBufferLinesAsync(ListBuffersRequest? request = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs GetBufferLines. |
| `M:LibTmux.Server.GetBuffersAsync(CancellationToken)` | `Task<IReadOnlyList<TmuxBuffer>> LibTmux.Server.GetBuffersAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Gets typed paste-buffer snapshots using the canonical projection. |
| `M:LibTmux.Server.GetClientsAsync(CancellationToken)` | `Task<IReadOnlyList<Client>> LibTmux.Server.GetClientsAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Reads the live collection and preserves failures, including an absent daemon. List error policy: loud. |
| `M:LibTmux.Server.GetCommandsAsync(string?,CancellationToken)` | `Task<IReadOnlyList<string>> LibTmux.Server.GetCommandsAsync(string? name = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs GetCommands. |
| `M:LibTmux.Server.GetKeysAsync(string?,string?,CancellationToken)` | `Task<IReadOnlyList<string>> LibTmux.Server.GetKeysAsync(string? keyTable = null, string? format = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs GetKeys. |
| `M:LibTmux.Server.GetMessagesAsync(string?,ShowMessagesMode,CancellationToken)` | `Task<IReadOnlyList<string>> LibTmux.Server.GetMessagesAsync(string? targetClient = null, ShowMessagesMode mode = ShowMessagesMode.Messages, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs GetMessages. |
| `M:LibTmux.Server.GetPaneAsync(PaneId,CancellationToken)` | `Task<Pane> LibTmux.Server.GetPaneAsync(PaneId id, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Reads materialized scalar state, throwing TmuxObjectNotFoundException when absent. |
| `M:LibTmux.Server.GetPanesAsync(CancellationToken)` | `Task<IReadOnlyList<Pane>> LibTmux.Server.GetPanesAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Reads the live collection and preserves failures, including an absent daemon. List error policy: loud. |
| `M:LibTmux.Server.GetPromptHistoryAsync(PromptType?,CancellationToken)` | `Task<IReadOnlyList<string>> LibTmux.Server.GetPromptHistoryAsync(PromptType? type = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs GetPromptHistory. |
| `M:LibTmux.Server.GetSessionAsync(SessionId,CancellationToken)` | `Task<Session> LibTmux.Server.GetSessionAsync(SessionId id, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Reads materialized scalar state, throwing TmuxObjectNotFoundException when absent. |
| `M:LibTmux.Server.GetSessionsAsync(CancellationToken)` | `Task<IReadOnlyList<Session>> LibTmux.Server.GetSessionsAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Reads the live collection and preserves failures, including an absent daemon. List error policy: loud. |
| `M:LibTmux.Server.GetWindowAsync(WindowId,CancellationToken)` | `Task<Window> LibTmux.Server.GetWindowAsync(WindowId id, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Reads materialized scalar state, throwing TmuxObjectNotFoundException when absent. |
| `M:LibTmux.Server.GetWindowsAsync(CancellationToken)` | `Task<IReadOnlyList<Window>> LibTmux.Server.GetWindowsAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Reads the live collection and preserves failures, including an absent daemon. List error policy: loud. |
| `M:LibTmux.Server.HasSessionAsync(string,bool,CancellationToken)` | `Task<bool> LibTmux.Server.HasSessionAsync(string target, bool exact = true, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs HasSession. |
| `M:LibTmux.Server.IfShellAsync(IfShellRequest,CancellationToken)` | `Task LibTmux.Server.IfShellAsync(IfShellRequest request, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs IfShell. |
| `M:LibTmux.Server.IsAliveAsync(CancellationToken)` | `Task<bool> LibTmux.Server.IsAliveAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs IsAlive. |
| `M:LibTmux.Server.KillAsync(CancellationToken)` | `Task LibTmux.Server.KillAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs Kill. |
| `M:LibTmux.Server.KillSessionAsync(string,CancellationToken)` | `Task LibTmux.Server.KillSessionAsync(string target, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs KillSession. |
| `M:LibTmux.Server.LoadBufferAsync(string,string?,CancellationToken)` | `Task LibTmux.Server.LoadBufferAsync(string path, string? name = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs LoadBuffer. |
| `M:LibTmux.Server.LockAsync(CancellationToken)` | `Task LibTmux.Server.LockAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs Lock. |
| `M:LibTmux.Server.LockClientAsync(string?,CancellationToken)` | `Task LibTmux.Server.LockClientAsync(string? targetClient = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs LockClient. |
| `M:LibTmux.Server.Open(ServerConnectionOptions?)` | `static Server LibTmux.Server.Open(ServerConnectionOptions? options = null)` | Public | Yes | Portable | Opens an unmaterialized connection handle without starting a process. |
| `M:LibTmux.Server.OpenWaitChannel(String)` | `TmuxWaitChannel LibTmux.Server.OpenWaitChannel(string channel)` | Public | No | `UnsupportedOSPlatform("windows")` | Opens a wait that survives a timed attempt. |
| `M:LibTmux.Server.RefreshClientAsync(string?,bool,CancellationToken)` | `Task LibTmux.Server.RefreshClientAsync(string? targetClient = null, bool requestClipboard = false, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs RefreshClient. |
| `M:LibTmux.Server.RunShellAsync(RunShellRequest,CancellationToken)` | `Task<IReadOnlyList<string>?> LibTmux.Server.RunShellAsync(RunShellRequest request, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs RunShell. |
| `M:LibTmux.Server.SaveBufferAsync(string,string?,bool,CancellationToken)` | `Task LibTmux.Server.SaveBufferAsync(string path, string? name = null, bool append = false, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs SaveBuffer. |
| `M:LibTmux.Server.SearchPanesAsync(UnsafeTmuxFilter,CancellationToken)` | `Task<IReadOnlyList<Pane>> LibTmux.Server.SearchPanesAsync(UnsafeTmuxFilter filter, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs a loud native panes search. |
| `M:LibTmux.Server.SearchSessionsAsync(UnsafeTmuxFilter,CancellationToken)` | `Task<IReadOnlyList<Session>> LibTmux.Server.SearchSessionsAsync(UnsafeTmuxFilter filter, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs a loud native sessions search. |
| `M:LibTmux.Server.SearchWindowsAsync(UnsafeTmuxFilter,CancellationToken)` | `Task<IReadOnlyList<Window>> LibTmux.Server.SearchWindowsAsync(UnsafeTmuxFilter filter, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs a loud native windows search. |
| `M:LibTmux.Server.SetBufferAsync(string,string?,bool,CancellationToken)` | `Task LibTmux.Server.SetBufferAsync(string data, string? name = null, bool append = false, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs SetBuffer. |
| `M:LibTmux.Server.ShowCommandPromptAsync(CommandPromptRequest,CancellationToken)` | `Task LibTmux.Server.ShowCommandPromptAsync(CommandPromptRequest request, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs ShowCommandPrompt. |
| `M:LibTmux.Server.ShowMenuAsync(DisplayMenuRequest,CancellationToken)` | `Task LibTmux.Server.ShowMenuAsync(DisplayMenuRequest request, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs ShowMenu. |
| `M:LibTmux.Server.SourceFileAsync(string,bool,bool,bool,CancellationToken)` | `Task LibTmux.Server.SourceFileAsync(string path, bool quiet = false, bool parseOnly = false, bool verbose = false, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs SourceFile. |
| `M:LibTmux.Server.StartServerAsync(CancellationToken)` | `Task LibTmux.Server.StartServerAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs StartServer. |
| `M:LibTmux.Server.SuspendClientAsync(string?,CancellationToken)` | `Task LibTmux.Server.SuspendClientAsync(string? targetClient = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs SuspendClient. |
| `M:LibTmux.Server.SwitchClientAsync(string,CancellationToken)` | `Task LibTmux.Server.SwitchClientAsync(string targetSession, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs SwitchClient. |
| `M:LibTmux.Server.ThrowIfDeadAsync(CancellationToken)` | `Task LibTmux.Server.ThrowIfDeadAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Throws unless a tmux server is answering. |
| `M:LibTmux.Server.UnbindKeyAsync(UnbindKeyRequest,CancellationToken)` | `Task LibTmux.Server.UnbindKeyAsync(UnbindKeyRequest request, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs UnbindKey. |
| `M:LibTmux.Server.WaitForAsync(WaitForRequest,CancellationToken)` | `Task LibTmux.Server.WaitForAsync(WaitForRequest request, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs WaitFor. |
| `M:LibTmux.Server.op_Equality(Server?,Server?)` | `static bool operator ==(Server? left, Server? right)` | Public | Yes | Portable | Reports whether two handles reach the same server endpoint. |
| `M:LibTmux.Server.op_Inequality(Server?,Server?)` | `static bool operator !=(Server? left, Server? right)` | Public | Yes | Portable | Reports whether two handles reach different server endpoints. |
| `P:LibTmux.Server.Clients` | `CapturedRelation<Client> LibTmux.Server.Clients { get; }` | Public | No | Portable | Gets the captured Clients value. |
| `P:LibTmux.Server.ConnectionOptions` | `ServerConnectionOptions LibTmux.Server.ConnectionOptions { get; }` | Public | No | Portable | Gets the captured ConnectionOptions value. |
| `P:LibTmux.Server.Environment` | `TmuxEnvironment LibTmux.Server.Environment { get; }` | Public | No | Portable | Gets the captured Environment value. |
| `P:LibTmux.Server.Generation` | `ServerGeneration? LibTmux.Server.Generation { get; }` | Public | No | Portable | Gets the captured Generation value. |
| `P:LibTmux.Server.Hooks` | `TmuxHooks LibTmux.Server.Hooks { get; }` | Public | No | Portable | Gets the captured Hooks value. |
| `P:LibTmux.Server.IsMaterialized` | `bool LibTmux.Server.IsMaterialized { get; }` | Public | No | Portable | Gets the captured IsMaterialized value. |
| `P:LibTmux.Server.Options` | `TmuxOptions LibTmux.Server.Options { get; }` | Public | No | Portable | Gets the captured Options value. |
| `P:LibTmux.Server.Panes` | `CapturedRelation<Pane> LibTmux.Server.Panes { get; }` | Public | No | Portable | Gets the captured Panes value. |
| `P:LibTmux.Server.Sessions` | `CapturedRelation<Session> LibTmux.Server.Sessions { get; }` | Public | No | Portable | Gets the captured Sessions value. |
| `P:LibTmux.Server.Version` | `TmuxVersion? LibTmux.Server.Version { get; }` | Public | No | Portable | Gets the captured Version value. |
| `P:LibTmux.Server.Windows` | `CapturedRelation<Window> LibTmux.Server.Windows { get; }` | Public | No | Portable | Gets the captured Windows value. |

### `T:LibTmux.ServerAccessRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.ServerAccessRequest.ToCommand(LibTmux.Server)` | `TmuxCommand LibTmux.ServerAccessRequest.ToCommand(Server server)` | Public | No | `UnsupportedOSPlatform("windows")` | Returns an access request as one tmux command. |
| `P:LibTmux.ServerAccessRequest.AllowUser` | `string? LibTmux.ServerAccessRequest.AllowUser { get; init; }` | Public | No | Portable | Gets AllowUser. |
| `P:LibTmux.ServerAccessRequest.DenyUser` | `string? LibTmux.ServerAccessRequest.DenyUser { get; init; }` | Public | No | Portable | Gets DenyUser. |
| `P:LibTmux.ServerAccessRequest.List` | `bool LibTmux.ServerAccessRequest.List { get; init; }` | Public | No | Portable | Gets List. |
| `P:LibTmux.ServerAccessRequest.ReadOnly` | `bool LibTmux.ServerAccessRequest.ReadOnly { get; init; }` | Public | No | Portable | Gets ReadOnly. |
| `P:LibTmux.ServerAccessRequest.ReadWrite` | `bool LibTmux.ServerAccessRequest.ReadWrite { get; init; }` | Public | No | Portable | Gets ReadWrite. |

### `T:LibTmux.ServerConnectionOptions`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `P:LibTmux.ServerConnectionOptions.ChildEnvironment` | `IReadOnlyDictionary<string,string?>? LibTmux.ServerConnectionOptions.ChildEnvironment { get; init; }` | Public | No | Portable | Gets ChildEnvironment. |
| `P:LibTmux.ServerConnectionOptions.ColorMode` | `TmuxColorMode LibTmux.ServerConnectionOptions.ColorMode { get; init; }` | Public | No | Portable | Gets ColorMode. |
| `P:LibTmux.ServerConnectionOptions.CommandTimeout` | `TimeSpan? LibTmux.ServerConnectionOptions.CommandTimeout { get; init; }` | Public | No | Portable | How long one tmux command may run, or null to wait indefinitely. |
| `P:LibTmux.ServerConnectionOptions.ConfigurationFile` | `string? LibTmux.ServerConnectionOptions.ConfigurationFile { get; init; }` | Public | No | Portable | Gets ConfigurationFile. |
| `P:LibTmux.ServerConnectionOptions.ControlModeEventBufferCapacity` | `int? LibTmux.ServerConnectionOptions.ControlModeEventBufferCapacity { get; init; }` | Public | No | Portable | How many control-mode events are buffered before the oldest are dropped. |
| `P:LibTmux.ServerConnectionOptions.Default` | `static ServerConnectionOptions LibTmux.ServerConnectionOptions.Default { get; }` | Public | Yes | Portable | Gets conventional connection defaults using the tmux executable on PATH. |
| `P:LibTmux.ServerConnectionOptions.InitializeAsync` | `Func<Server,CancellationToken,ValueTask>? LibTmux.ServerConnectionOptions.InitializeAsync { get; init; }` | Public | No | Portable | Gets InitializeAsync. |
| `P:LibTmux.ServerConnectionOptions.Logger` | `ILogger? LibTmux.ServerConnectionOptions.Logger { get; init; }` | Public | No | Portable | Gets Logger. |
| `P:LibTmux.ServerConnectionOptions.MaxCapturedBytesPerStream` | `int? LibTmux.ServerConnectionOptions.MaxCapturedBytesPerStream { get; init; }` | Public | No | Portable | The largest output one command may capture, in bytes. |
| `P:LibTmux.ServerConnectionOptions.SocketName` | `string? LibTmux.ServerConnectionOptions.SocketName { get; init; }` | Public | No | Portable | Gets SocketName. |
| `P:LibTmux.ServerConnectionOptions.SocketNameFactory` | `Func<string>? LibTmux.ServerConnectionOptions.SocketNameFactory { get; init; }` | Public | No | Portable | Gets SocketNameFactory. |
| `P:LibTmux.ServerConnectionOptions.SocketPath` | `string? LibTmux.ServerConnectionOptions.SocketPath { get; init; }` | Public | No | Portable | Gets SocketPath. |
| `P:LibTmux.ServerConnectionOptions.TmuxBinaryPath` | `string LibTmux.ServerConnectionOptions.TmuxBinaryPath { get; init; }` | Public | No | Portable | Gets TmuxBinaryPath. |

### `T:LibTmux.ServerGeneration`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.ServerGeneration.#ctor(int,long)` | `ServerGeneration(int processId, long startTime)` | Public | No | Portable | Creates ServerGeneration. |
| `P:LibTmux.ServerGeneration.ProcessId` | `int LibTmux.ServerGeneration.ProcessId { get; }` | Public | No | Portable | Gets ProcessId. |
| `P:LibTmux.ServerGeneration.StartTime` | `long LibTmux.ServerGeneration.StartTime { get; }` | Public | No | Portable | Gets StartTime. |

### `T:LibTmux.Session`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.Session.AttachAsync(AttachSessionRequest?,CancellationToken)` | `Task<Session> LibTmux.Session.AttachAsync(AttachSessionRequest? request = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs Attach. |
| `M:LibTmux.Session.CreateOwnedWindowAsync(NewWindowRequest?,CancellationToken)` | `Task<OwnedWindowScope> LibTmux.Session.CreateOwnedWindowAsync(NewWindowRequest? request = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Creates a window with explicit cleanup ownership. |
| `M:LibTmux.Session.CreateWindowAsync(NewWindowRequest?,CancellationToken)` | `Task<Window> LibTmux.Session.CreateWindowAsync(NewWindowRequest? request = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs CreateWindow. |
| `M:LibTmux.Session.DetachClientAsync(string?,CancellationToken)` | `Task LibTmux.Session.DetachClientAsync(string? shellCommand = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs DetachClient. |
| `M:LibTmux.Session.ExecuteCommandAsync(IReadOnlyList<string>,string?,CancellationToken)` | `Task<TmuxCommandResult> LibTmux.Session.ExecuteCommandAsync(IReadOnlyList<string> arguments, string? targetOverride = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Executes a raw command with stable target injection for the entity handle. |
| `M:LibTmux.Session.FindWindowAsync(WindowId,CancellationToken)` | `Task<Window?> LibTmux.Session.FindWindowAsync(WindowId id, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Reads one window in this session, returning null only when a successful lookup finds no match. |
| `M:LibTmux.Session.FindWindowAsync(string,CancellationToken)` | `Task<Window?> LibTmux.Session.FindWindowAsync(string target, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Reads one window in this session, returning null only when a successful lookup finds no match. |
| `M:LibTmux.Session.FromEnvironmentAsync(IReadOnlyDictionary<string,string>?,CancellationToken)` | `static Task<Session> LibTmux.Session.FromEnvironmentAsync(IReadOnlyDictionary<string,string>? environment = null, CancellationToken cancellationToken = default)` | Public | Yes | `UnsupportedOSPlatform("windows")` | Performs FromEnvironment. |
| `M:LibTmux.Session.GetPanesAsync(CancellationToken)` | `Task<IReadOnlyList<Pane>> LibTmux.Session.GetPanesAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs loud child pane traversal. List error policy: loud. |
| `M:LibTmux.Session.GetWindowAsync(WindowId,CancellationToken)` | `Task<Window> LibTmux.Session.GetWindowAsync(WindowId id, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Reads one window in this session, throwing TmuxObjectNotFoundException when absent. |
| `M:LibTmux.Session.GetWindowAsync(string,CancellationToken)` | `Task<Window> LibTmux.Session.GetWindowAsync(string target, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Reads one window in this session, throwing TmuxObjectNotFoundException when absent. |
| `M:LibTmux.Session.GetWindowsAsync(CancellationToken)` | `Task<IReadOnlyList<Window>> LibTmux.Session.GetWindowsAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs loud child window traversal. List error policy: loud. |
| `M:LibTmux.Session.KillAsync(bool,bool,bool,CancellationToken)` | `Task LibTmux.Session.KillAsync(bool allExcept = false, bool clearAlerts = false, bool group = false, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs Kill. |
| `M:LibTmux.Session.KillWindowAsync(string?,CancellationToken)` | `Task LibTmux.Session.KillWindowAsync(string? target = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs KillWindow. |
| `M:LibTmux.Session.LockAsync(CancellationToken)` | `Task LibTmux.Session.LockAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs Lock. |
| `M:LibTmux.Session.RefreshAsync(CancellationToken)` | `Task<Session> LibTmux.Session.RefreshAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs Refresh. |
| `M:LibTmux.Session.RenameAsync(string,CancellationToken)` | `Task<Session> LibTmux.Session.RenameAsync(string name, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs Rename. |
| `M:LibTmux.Session.SearchPanesAsync(UnsafeTmuxFilter,CancellationToken)` | `Task<IReadOnlyList<Pane>> LibTmux.Session.SearchPanesAsync(UnsafeTmuxFilter filter, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs a loud native pane search. List error policy: loud. |
| `M:LibTmux.Session.SearchWindowsAsync(UnsafeTmuxFilter,CancellationToken)` | `Task<IReadOnlyList<Window>> LibTmux.Session.SearchWindowsAsync(UnsafeTmuxFilter filter, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs a loud native window search. List error policy: loud. |
| `M:LibTmux.Session.SelectLastWindowAsync(CancellationToken)` | `Task<Window> LibTmux.Session.SelectLastWindowAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs SelectLastWindow. |
| `M:LibTmux.Session.SelectNextWindowAsync(CancellationToken)` | `Task<Window> LibTmux.Session.SelectNextWindowAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs SelectNextWindow. |
| `M:LibTmux.Session.SelectPreviousWindowAsync(CancellationToken)` | `Task<Window> LibTmux.Session.SelectPreviousWindowAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs SelectPreviousWindow. |
| `M:LibTmux.Session.SelectWindowAsync(string,CancellationToken)` | `Task<Window> LibTmux.Session.SelectWindowAsync(string target, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs SelectWindow. |
| `M:LibTmux.Session.SwitchClientAsync(CancellationToken)` | `Task<Session> LibTmux.Session.SwitchClientAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs SwitchClient. |
| `M:LibTmux.Session.op_Equality(Session?,Session?)` | `static bool operator ==(Session? left, Session? right)` | Public | Yes | Portable | Reports whether two handles name the same session. |
| `M:LibTmux.Session.op_Inequality(Session?,Session?)` | `static bool operator !=(Session? left, Session? right)` | Public | Yes | Portable | Reports whether two handles name different sessions. |
| `P:LibTmux.Session.ActivePane` | `CapturedValue<Pane> LibTmux.Session.ActivePane { get; }` | Public | No | `UnsupportedOSPlatform("windows")` | Gets the captured active child, or an uncaptured relation. |
| `P:LibTmux.Session.ActiveWindow` | `CapturedValue<Window> LibTmux.Session.ActiveWindow { get; }` | Public | No | `UnsupportedOSPlatform("windows")` | Gets the captured active child, or an uncaptured relation. |
| `P:LibTmux.Session.Attached` | `bool LibTmux.Session.Attached { get; }` | Public | No | Portable | Gets the captured Attached value. |
| `P:LibTmux.Session.Environment` | `TmuxEnvironment LibTmux.Session.Environment { get; }` | Public | No | Portable | Gets the captured Environment value. |
| `P:LibTmux.Session.Generation` | `ServerGeneration LibTmux.Session.Generation { get; }` | Public | No | Portable | Gets the captured Generation value. |
| `P:LibTmux.Session.Hooks` | `TmuxHooks LibTmux.Session.Hooks { get; }` | Public | No | Portable | Gets the captured Hooks value. |
| `P:LibTmux.Session.Id` | `SessionId LibTmux.Session.Id { get; }` | Public | No | Portable | Gets the captured Id value. |
| `P:LibTmux.Session.Name` | `string LibTmux.Session.Name { get; }` | Public | No | Portable | Gets the captured Name value. |
| `P:LibTmux.Session.Options` | `TmuxOptions LibTmux.Session.Options { get; }` | Public | No | Portable | Gets the captured Options value. |
| `P:LibTmux.Session.Panes` | `CapturedRelation<Pane> LibTmux.Session.Panes { get; }` | Public | No | Portable | Gets the captured Panes value. |
| `P:LibTmux.Session.RawFormatFields` | `IReadOnlyDictionary<string,string?> LibTmux.Session.RawFormatFields { get; }` | Public | No | Portable | Gets copied raw tmux format tokens captured for this snapshot. |
| `P:LibTmux.Session.Server` | `Server LibTmux.Session.Server { get; }` | Public | No | Portable | Gets the captured Server value. |
| `P:LibTmux.Session.Windows` | `CapturedRelation<Window> LibTmux.Session.Windows { get; }` | Public | No | Portable | Gets the captured Windows value. |

### `T:LibTmux.SessionId`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.SessionId.#ctor(int)` | `SessionId(int value)` | Public | No | Portable | Creates a validated identifier. |
| `M:LibTmux.SessionId.CompareTo(SessionId)` | `int LibTmux.SessionId.CompareTo(SessionId other)` | Public | No | Portable | Orders this identifier against another numerically. |
| `M:LibTmux.SessionId.Parse(ReadOnlySpan<char>)` | `static static SessionId LibTmux.SessionId.Parse(ReadOnlySpan<char> text)` | Public | Yes | Portable | Parses a prefixed session identifier from a span. |
| `M:LibTmux.SessionId.Parse(string)` | `static SessionId LibTmux.SessionId.Parse(string text)` | Public | Yes | Portable | Parses a prefixed identifier. |
| `M:LibTmux.SessionId.ToString()` | `string LibTmux.SessionId.ToString()` | Public | No | Portable | Returns the canonical prefixed identifier. |
| `M:LibTmux.SessionId.TryParse(ReadOnlySpan<char>,SessionId)` | `static static bool LibTmux.SessionId.TryParse(ReadOnlySpan<char> text, out SessionId result)` | Public | Yes | Portable | Tries to parse a prefixed session identifier from a span. |
| `M:LibTmux.SessionId.TryParse(string?,SessionId)` | `static bool LibTmux.SessionId.TryParse(string? text, out SessionId result)` | Public | Yes | Portable | Tries to parse a prefixed identifier without throwing. |
| `M:LibTmux.SessionId.op_GreaterThan(SessionId,SessionId)` | `static bool operator >(SessionId left, SessionId right)` | Public | Yes | Portable | Compares the order two session identifiers were handed out in. |
| `M:LibTmux.SessionId.op_GreaterThanOrEqual(SessionId,SessionId)` | `static bool operator >=(SessionId left, SessionId right)` | Public | Yes | Portable | Compares the order two session identifiers were handed out in. |
| `M:LibTmux.SessionId.op_LessThan(SessionId,SessionId)` | `static bool operator <(SessionId left, SessionId right)` | Public | Yes | Portable | Compares the order two session identifiers were handed out in. |
| `M:LibTmux.SessionId.op_LessThanOrEqual(SessionId,SessionId)` | `static bool operator <=(SessionId left, SessionId right)` | Public | Yes | Portable | Compares the order two session identifiers were handed out in. |
| `P:LibTmux.SessionId.Value` | `int LibTmux.SessionId.Value { get; }` | Public | No | Portable | Gets the nonnegative numeric value. |

### `T:LibTmux.SessionWindowEdge`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `P:LibTmux.SessionWindowEdge.Key` | `WindowEntityKey LibTmux.SessionWindowEdge.Key { get; }` | Public | No | Portable | Gets the session and window this edge joins. |
| `P:LibTmux.SessionWindowEdge.Ordinal` | `int? LibTmux.SessionWindowEdge.Ordinal { get; init; }` | Public | No | Portable | Gets the edge's position in the session's window order. |
| `P:LibTmux.SessionWindowEdge.SessionId` | `SessionId LibTmux.SessionWindowEdge.SessionId { get; init; }` | Public | No | Portable | Gets SessionId. |
| `P:LibTmux.SessionWindowEdge.WindowId` | `WindowId LibTmux.SessionWindowEdge.WindowId { get; init; }` | Public | No | Portable | Gets WindowId. |
| `P:LibTmux.SessionWindowEdge.WindowIndex` | `int LibTmux.SessionWindowEdge.WindowIndex { get; init; }` | Public | No | Portable | Gets the tmux window index inside the session. |

### `T:LibTmux.SetHookRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.SetHookRequest.#ctor(string,string)` | `SetHookRequest(string name, string value)` | Public | No | Portable | Creates SetHookRequest. |
| `M:LibTmux.SetHookRequest.ToCommand(LibTmux.TmuxHooks)` | `TmuxCommand LibTmux.SetHookRequest.ToCommand(TmuxHooks hooks)` | Public | No | `UnsupportedOSPlatform("windows")` | Returns a hook request as one tmux command. |
| `P:LibTmux.SetHookRequest.Append` | `bool LibTmux.SetHookRequest.Append { get; init; }` | Public | No | Portable | Gets Append. |
| `P:LibTmux.SetHookRequest.Global` | `bool LibTmux.SetHookRequest.Global { get; init; }` | Public | No | Portable | Gets Global. |
| `P:LibTmux.SetHookRequest.Name` | `string LibTmux.SetHookRequest.Name { get; }` | Public | No | Portable | Gets Name. |
| `P:LibTmux.SetHookRequest.RunImmediately` | `bool LibTmux.SetHookRequest.RunImmediately { get; init; }` | Public | No | Portable | Gets RunImmediately. |
| `P:LibTmux.SetHookRequest.Scope` | `OptionScope? LibTmux.SetHookRequest.Scope { get; init; }` | Public | No | Portable | Gets Scope. |
| `P:LibTmux.SetHookRequest.Unset` | `bool LibTmux.SetHookRequest.Unset { get; init; }` | Public | No | Portable | Gets Unset. |
| `P:LibTmux.SetHookRequest.Value` | `string LibTmux.SetHookRequest.Value { get; }` | Public | No | Portable | Gets Value. |

### `T:LibTmux.SetHooksRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.SetHooksRequest.#ctor(string,IReadOnlyDictionary<int,string>)` | `SetHooksRequest(string name, IReadOnlyDictionary<int,string> values)` | Public | No | Portable | Creates SetHooksRequest. |
| `P:LibTmux.SetHooksRequest.ClearExisting` | `bool LibTmux.SetHooksRequest.ClearExisting { get; init; }` | Public | No | Portable | Gets ClearExisting. |
| `P:LibTmux.SetHooksRequest.Global` | `bool LibTmux.SetHooksRequest.Global { get; init; }` | Public | No | Portable | Gets Global. |
| `P:LibTmux.SetHooksRequest.Name` | `string LibTmux.SetHooksRequest.Name { get; }` | Public | No | Portable | Gets Name. |
| `P:LibTmux.SetHooksRequest.Scope` | `OptionScope? LibTmux.SetHooksRequest.Scope { get; init; }` | Public | No | Portable | Gets Scope. |
| `P:LibTmux.SetHooksRequest.Values` | `IReadOnlyDictionary<int,string> LibTmux.SetHooksRequest.Values { get; }` | Public | No | Portable | Gets Values. |

### `T:LibTmux.SetOptionRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.SetOptionRequest.#ctor(string,string)` | `SetOptionRequest(string name, string value)` | Public | No | Portable | Creates SetOptionRequest. |
| `M:LibTmux.SetOptionRequest.ToCommand(LibTmux.TmuxOptions)` | `TmuxCommand LibTmux.SetOptionRequest.ToCommand(TmuxOptions options)` | Public | No | `UnsupportedOSPlatform("windows")` | Returns an option request as one tmux command. |
| `P:LibTmux.SetOptionRequest.Append` | `bool LibTmux.SetOptionRequest.Append { get; init; }` | Public | No | Portable | Gets Append. |
| `P:LibTmux.SetOptionRequest.ExpandFormat` | `bool LibTmux.SetOptionRequest.ExpandFormat { get; init; }` | Public | No | Portable | Gets ExpandFormat. |
| `P:LibTmux.SetOptionRequest.Global` | `bool LibTmux.SetOptionRequest.Global { get; init; }` | Public | No | Portable | Gets Global. |
| `P:LibTmux.SetOptionRequest.Name` | `string LibTmux.SetOptionRequest.Name { get; }` | Public | No | Portable | Gets Name. |
| `P:LibTmux.SetOptionRequest.PreventOverwrite` | `bool LibTmux.SetOptionRequest.PreventOverwrite { get; init; }` | Public | No | Portable | Gets PreventOverwrite. |
| `P:LibTmux.SetOptionRequest.Quiet` | `bool LibTmux.SetOptionRequest.Quiet { get; init; }` | Public | No | Portable | Gets Quiet. |
| `P:LibTmux.SetOptionRequest.Scope` | `OptionScope? LibTmux.SetOptionRequest.Scope { get; init; }` | Public | No | Portable | Gets Scope. |
| `P:LibTmux.SetOptionRequest.Value` | `string LibTmux.SetOptionRequest.Value { get; }` | Public | No | Portable | Gets Value. |

### `T:LibTmux.ShowMessagesMode`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `F:LibTmux.ShowMessagesMode.Jobs` | `Jobs = 2` | Public | Implicit | Portable | The Jobs value. Value: `2`. |
| `F:LibTmux.ShowMessagesMode.Messages` | `Messages = 0` | Public | Implicit | Portable | The Messages value. Value: `0`. |
| `F:LibTmux.ShowMessagesMode.Terminals` | `Terminals = 1` | Public | Implicit | Portable | The Terminals value. Value: `1`. |

### `T:LibTmux.SnapshotDepth`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `F:LibTmux.SnapshotDepth.Panes` | `Panes = 3` | Public | Implicit | Portable | Sessions, windows, and their panes were captured. Value: `3`. |
| `F:LibTmux.SnapshotDepth.Server` | `Server = 0` | Public | Implicit | Portable | Only the server itself was captured. Value: `0`. |
| `F:LibTmux.SnapshotDepth.Sessions` | `Sessions = 1` | Public | Implicit | Portable | Sessions were captured, but not their windows. Value: `1`. |
| `F:LibTmux.SnapshotDepth.Windows` | `Windows = 2` | Public | Implicit | Portable | Sessions and their windows were captured. Value: `2`. |

### `T:LibTmux.SplitPaneRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.SplitPaneRequest.ToCommand(LibTmux.Pane)` | `TmuxCommand LibTmux.SplitPaneRequest.ToCommand(Pane pane)` | Public | No | Portable | Returns a split request as one tmux command. |
| `P:LibTmux.SplitPaneRequest.ActiveBorderStyle` | `string? LibTmux.SplitPaneRequest.ActiveBorderStyle { get; init; }` | Public | No | Portable | Gets ActiveBorderStyle. |
| `P:LibTmux.SplitPaneRequest.Attach` | `bool LibTmux.SplitPaneRequest.Attach { get; init; }` | Public | No | Portable | Gets Attach. |
| `P:LibTmux.SplitPaneRequest.Command` | `string? LibTmux.SplitPaneRequest.Command { get; init; }` | Public | No | Portable | Gets Command. |
| `P:LibTmux.SplitPaneRequest.Direction` | `PaneDirection? LibTmux.SplitPaneRequest.Direction { get; init; }` | Public | No | Portable | Gets Direction. |
| `P:LibTmux.SplitPaneRequest.Empty` | `bool LibTmux.SplitPaneRequest.Empty { get; init; }` | Public | No | Portable | Gets Empty. |
| `P:LibTmux.SplitPaneRequest.Environment` | `IReadOnlyDictionary<string,string>? LibTmux.SplitPaneRequest.Environment { get; init; }` | Public | No | Portable | Gets Environment. |
| `P:LibTmux.SplitPaneRequest.FullWindow` | `bool LibTmux.SplitPaneRequest.FullWindow { get; init; }` | Public | No | Portable | Gets FullWindow. |
| `P:LibTmux.SplitPaneRequest.InactiveBorderStyle` | `string? LibTmux.SplitPaneRequest.InactiveBorderStyle { get; init; }` | Public | No | Portable | Gets InactiveBorderStyle. |
| `P:LibTmux.SplitPaneRequest.KeepOpen` | `bool LibTmux.SplitPaneRequest.KeepOpen { get; init; }` | Public | No | Portable | Gets KeepOpen. |
| `P:LibTmux.SplitPaneRequest.Message` | `string? LibTmux.SplitPaneRequest.Message { get; init; }` | Public | No | Portable | Gets Message. |
| `P:LibTmux.SplitPaneRequest.Percentage` | `int? LibTmux.SplitPaneRequest.Percentage { get; init; }` | Public | No | Portable | Gets Percentage. |
| `P:LibTmux.SplitPaneRequest.Size` | `string? LibTmux.SplitPaneRequest.Size { get; init; }` | Public | No | Portable | Gets Size. |
| `P:LibTmux.SplitPaneRequest.StartDirectory` | `string? LibTmux.SplitPaneRequest.StartDirectory { get; init; }` | Public | No | Portable | Gets StartDirectory. |
| `P:LibTmux.SplitPaneRequest.Style` | `string? LibTmux.SplitPaneRequest.Style { get; init; }` | Public | No | Portable | Gets Style. |
| `P:LibTmux.SplitPaneRequest.Target` | `string? LibTmux.SplitPaneRequest.Target { get; init; }` | Public | No | Portable | Gets Target. |
| `P:LibTmux.SplitPaneRequest.Zoom` | `bool LibTmux.SplitPaneRequest.Zoom { get; init; }` | Public | No | Portable | Gets Zoom. |

### `T:LibTmux.StaleServerGenerationException`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.StaleServerGenerationException.#ctor(string,ServerGeneration,Exception?)` | `StaleServerGenerationException(string message, ServerGeneration expected, Exception? innerException = null)` | Public | No | Portable | Creates StaleServerGenerationException without a known replacement generation. |
| `M:LibTmux.StaleServerGenerationException.#ctor(string,ServerGeneration,ServerGeneration,Exception?)` | `StaleServerGenerationException(string message, ServerGeneration expected, ServerGeneration actual, Exception? innerException = null)` | Public | No | Portable | Creates StaleServerGenerationException. |
| `P:LibTmux.StaleServerGenerationException.Actual` | `ServerGeneration? LibTmux.StaleServerGenerationException.Actual { get; }` | Public | No | Portable | Gets Actual, or null when it could not be observed. |
| `P:LibTmux.StaleServerGenerationException.Expected` | `ServerGeneration LibTmux.StaleServerGenerationException.Expected { get; }` | Public | No | Portable | Gets Expected. |

### `T:LibTmux.SwapPaneRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.SwapPaneRequest.ToCommand(LibTmux.Pane)` | `TmuxCommand LibTmux.SwapPaneRequest.ToCommand(Pane pane)` | Public | No | Portable | Returns a pane-swap request as one tmux command. |
| `P:LibTmux.SwapPaneRequest.Detach` | `bool LibTmux.SwapPaneRequest.Detach { get; init; }` | Public | No | Portable | Gets Detach. |
| `P:LibTmux.SwapPaneRequest.Direction` | `PaneSwapDirection? LibTmux.SwapPaneRequest.Direction { get; init; }` | Public | No | Portable | Gets Direction. |
| `P:LibTmux.SwapPaneRequest.KeepZoom` | `bool LibTmux.SwapPaneRequest.KeepZoom { get; init; }` | Public | No | Portable | Gets KeepZoom. |
| `P:LibTmux.SwapPaneRequest.Target` | `string? LibTmux.SwapPaneRequest.Target { get; init; }` | Public | No | Portable | Gets Target. |

### `T:LibTmux.Testing.TemporaryHierarchyScope`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.Testing.TemporaryHierarchyScope.DisposeAsync()` | `ValueTask LibTmux.Testing.TemporaryHierarchyScope.DisposeAsync()` | Public | No | `UnsupportedOSPlatform("windows")` | Performs bounded isolated cleanup. |
| `P:LibTmux.Testing.TemporaryHierarchyScope.Pane` | `Pane LibTmux.Testing.TemporaryHierarchyScope.Pane { get; }` | Public | No | Portable | Gets the temporary pane. |
| `P:LibTmux.Testing.TemporaryHierarchyScope.Server` | `Server LibTmux.Testing.TemporaryHierarchyScope.Server { get; }` | Public | No | Portable | Gets the temporary server. |
| `P:LibTmux.Testing.TemporaryHierarchyScope.Session` | `Session LibTmux.Testing.TemporaryHierarchyScope.Session { get; }` | Public | No | Portable | Gets the temporary session. |
| `P:LibTmux.Testing.TemporaryHierarchyScope.Window` | `Window LibTmux.Testing.TemporaryHierarchyScope.Window { get; }` | Public | No | Portable | Gets the temporary window. |

### `T:LibTmux.Testing.TemporaryServerScope`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.Testing.TemporaryServerScope.DisposeAsync()` | `ValueTask LibTmux.Testing.TemporaryServerScope.DisposeAsync()` | Public | No | `UnsupportedOSPlatform("windows")` | Performs bounded isolated cleanup. |
| `P:LibTmux.Testing.TemporaryServerScope.Server` | `Server LibTmux.Testing.TemporaryServerScope.Server { get; }` | Public | No | Portable | Gets the temporary server. |

### `T:LibTmux.Testing.TemporarySessionScope`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.Testing.TemporarySessionScope.DisposeAsync()` | `ValueTask LibTmux.Testing.TemporarySessionScope.DisposeAsync()` | Public | No | `UnsupportedOSPlatform("windows")` | Performs bounded isolated cleanup. |
| `P:LibTmux.Testing.TemporarySessionScope.Session` | `Session LibTmux.Testing.TemporarySessionScope.Session { get; }` | Public | No | Portable | Gets the temporary session. |

### `T:LibTmux.Testing.TemporaryWindowScope`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.Testing.TemporaryWindowScope.DisposeAsync()` | `ValueTask LibTmux.Testing.TemporaryWindowScope.DisposeAsync()` | Public | No | `UnsupportedOSPlatform("windows")` | Performs bounded isolated cleanup. |
| `P:LibTmux.Testing.TemporaryWindowScope.Window` | `Window LibTmux.Testing.TemporaryWindowScope.Window { get; }` | Public | No | Portable | Gets the temporary window. |

### `T:LibTmux.Testing.TestEnvironment`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.Testing.TestEnvironment.#ctor(string,IReadOnlyDictionary<string,string?>)` | `TestEnvironment(string workingDirectory, IReadOnlyDictionary<string,string?> variables)` | Public | No | Portable | Creates immutable child-process test environment state. |
| `M:LibTmux.Testing.TestEnvironment.WithVariable(string,string)` | `TestEnvironment LibTmux.Testing.TestEnvironment.WithVariable(string name, string value)` | Public | No | Portable | Returns a copy with one child-process variable set. |
| `M:LibTmux.Testing.TestEnvironment.WithoutVariable(string)` | `TestEnvironment LibTmux.Testing.TestEnvironment.WithoutVariable(string name)` | Public | No | Portable | Returns a copy without one child-process variable. |
| `P:LibTmux.Testing.TestEnvironment.Variables` | `IReadOnlyDictionary<string,string?> LibTmux.Testing.TestEnvironment.Variables { get; }` | Public | No | Portable | Gets the isolated child environment. |
| `P:LibTmux.Testing.TestEnvironment.WorkingDirectory` | `string LibTmux.Testing.TestEnvironment.WorkingDirectory { get; }` | Public | No | Portable | Gets the isolated working directory. |

### `T:LibTmux.Testing.TmuxNameGenerator`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.Testing.TmuxNameGenerator.#ctor(string)` | `TmuxNameGenerator(string prefix = "lt")` | Public | No | Portable | Creates a unique-name generator. |
| `M:LibTmux.Testing.TmuxNameGenerator.CreateAvailableSessionNameAsync(Server,string?,CancellationToken)` | `Task<string> LibTmux.Testing.TmuxNameGenerator.CreateAvailableSessionNameAsync(Server server, string? prefix = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Creates a unique session name absent from the supplied server. |
| `M:LibTmux.Testing.TmuxNameGenerator.CreateAvailableWindowNameAsync(Session,string?,CancellationToken)` | `Task<string> LibTmux.Testing.TmuxNameGenerator.CreateAvailableWindowNameAsync(Session session, string? prefix = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Creates a unique window name absent from the supplied session. |
| `M:LibTmux.Testing.TmuxNameGenerator.CreateSessionName()` | `string LibTmux.Testing.TmuxNameGenerator.CreateSessionName()` | Public | No | Portable | Creates a unique tmux-safe session name. |
| `M:LibTmux.Testing.TmuxNameGenerator.CreateWindowName()` | `string LibTmux.Testing.TmuxNameGenerator.CreateWindowName()` | Public | No | Portable | Creates a unique tmux-safe window name. |

### `T:LibTmux.Testing.TmuxTestContext`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.Testing.TmuxTestContext.DisposeAsync()` | `ValueTask LibTmux.Testing.TmuxTestContext.DisposeAsync()` | Public | No | `UnsupportedOSPlatform("windows")` | Performs bounded isolated cleanup. |
| `P:LibTmux.Testing.TmuxTestContext.Environment` | `TestEnvironment LibTmux.Testing.TmuxTestContext.Environment { get; }` | Public | No | Portable | Gets the isolated test environment. |
| `P:LibTmux.Testing.TmuxTestContext.Server` | `Server LibTmux.Testing.TmuxTestContext.Server { get; }` | Public | No | Portable | Gets the isolated server for one test context. |

### `T:LibTmux.Testing.TmuxTestFactory`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.Testing.TmuxTestFactory.#ctor()` | `TmuxTestFactory()` | Public | No | Portable | Creates an xUnit-independent real-tmux test factory. |
| `M:LibTmux.Testing.TmuxTestFactory.CreateContextAsync(TmuxTestOptions?,CancellationToken)` | `Task<TmuxTestContext> LibTmux.Testing.TmuxTestFactory.CreateContextAsync(TmuxTestOptions? options = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Creates an isolated real-tmux context and child environment. |
| `M:LibTmux.Testing.TmuxTestFactory.CreateHierarchyAsync(TmuxTestOptions?,CancellationToken)` | `Task<TemporaryHierarchyScope> LibTmux.Testing.TmuxTestFactory.CreateHierarchyAsync(TmuxTestOptions? options = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Creates an isolated temporary hierarchy. |
| `M:LibTmux.Testing.TmuxTestFactory.CreateServerAsync(TmuxTestOptions?,CancellationToken)` | `Task<TemporaryServerScope> LibTmux.Testing.TmuxTestFactory.CreateServerAsync(TmuxTestOptions? options = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Creates an isolated temporary server. |
| `M:LibTmux.Testing.TmuxTestFactory.CreateSessionAsync(Server,TmuxTestOptions?,CancellationToken)` | `Task<TemporarySessionScope> LibTmux.Testing.TmuxTestFactory.CreateSessionAsync(Server server, TmuxTestOptions? options = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Creates a temporary session within a caller-supplied server. |
| `M:LibTmux.Testing.TmuxTestFactory.CreateSessionAsync(TmuxTestOptions?,CancellationToken)` | `Task<TemporarySessionScope> LibTmux.Testing.TmuxTestFactory.CreateSessionAsync(TmuxTestOptions? options = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Creates an isolated temporary session. |
| `M:LibTmux.Testing.TmuxTestFactory.CreateWindowAsync(Session,TmuxTestOptions?,CancellationToken)` | `Task<TemporaryWindowScope> LibTmux.Testing.TmuxTestFactory.CreateWindowAsync(Session session, TmuxTestOptions? options = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Creates a temporary window within a caller-supplied session. |
| `M:LibTmux.Testing.TmuxTestFactory.CreateWindowAsync(TmuxTestOptions?,CancellationToken)` | `Task<TemporaryWindowScope> LibTmux.Testing.TmuxTestFactory.CreateWindowAsync(TmuxTestOptions? options = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Creates an isolated temporary window. |

### `T:LibTmux.Testing.TmuxTestOptions`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.Testing.TmuxTestOptions.#ctor(ServerConnectionOptions?,TimeSpan?,TimeSpan?,string)` | `TmuxTestOptions(ServerConnectionOptions? connectionOptions = null, TimeSpan? timeout = null, TimeSpan? pollInterval = null, string sessionNamePrefix = "lt")` | Public | No | Portable | Creates immutable real-tmux test options. |
| `P:LibTmux.Testing.TmuxTestOptions.ConnectionOptions` | `ServerConnectionOptions LibTmux.Testing.TmuxTestOptions.ConnectionOptions { get; }` | Public | No | Portable | Gets isolated connection options. |
| `P:LibTmux.Testing.TmuxTestOptions.Default` | `static TmuxTestOptions LibTmux.Testing.TmuxTestOptions.Default { get; }` | Public | Yes | Portable | Gets safe isolated test defaults. |
| `P:LibTmux.Testing.TmuxTestOptions.PollInterval` | `TimeSpan LibTmux.Testing.TmuxTestOptions.PollInterval { get; }` | Public | No | Portable | Gets the bounded polling interval. |
| `P:LibTmux.Testing.TmuxTestOptions.SessionNamePrefix` | `string LibTmux.Testing.TmuxTestOptions.SessionNamePrefix { get; }` | Public | No | Portable | Gets the tmux-safe session name prefix. |
| `P:LibTmux.Testing.TmuxTestOptions.Timeout` | `TimeSpan LibTmux.Testing.TmuxTestOptions.Timeout { get; }` | Public | No | Portable | Gets the operation deadline. |

### `T:LibTmux.TmuxBuffer`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.TmuxBuffer.#ctor(string,long,string?)` | `TmuxBuffer(string name, long size, string? sample)` | Public | No | Portable | Creates TmuxBuffer. |
| `P:LibTmux.TmuxBuffer.Name` | `string LibTmux.TmuxBuffer.Name { get; }` | Public | No | Portable | Gets Name. |
| `P:LibTmux.TmuxBuffer.Sample` | `string? LibTmux.TmuxBuffer.Sample { get; }` | Public | No | Portable | Gets Sample. |
| `P:LibTmux.TmuxBuffer.Size` | `long LibTmux.TmuxBuffer.Size { get; }` | Public | No | Portable | Gets Size. |

### `T:LibTmux.TmuxChain`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.TmuxChain.ExecuteAsync(System.Threading.CancellationToken)` | `Task<TmuxCommandResult> ExecuteAsync(CancellationToken cancellationToken = default)` | Public | No | Portable | Runs every command in one tmux invocation. |
| `M:LibTmux.TmuxChain.Then(LibTmux.TmuxCommand)` | `TmuxChain Then(TmuxCommand command)` | Public | No | Portable | Adds one command and returns the longer chain. |
| `M:LibTmux.TmuxChain.Then(System.Collections.Generic.IEnumerable{LibTmux.TmuxCommand})` | `TmuxChain Then(IEnumerable<TmuxCommand> commands)` | Public | No | Portable | Adds every command in order and returns the longer chain. |
| `M:LibTmux.TmuxChain.Then(string,string[])` | `TmuxChain Then(string name, params string[] arguments)` | Public | No | Portable | Adds one command by name and returns the longer chain. |
| `P:LibTmux.TmuxChain.Commands` | `IReadOnlyList<TmuxCommand> LibTmux.TmuxChain.Commands { get; }` | Public | No | Portable | Gets the commands this chain will run, in order. |

### `T:LibTmux.TmuxChaining`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.HookRequest,LibTmux.TmuxHooks,LibTmux.Server,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this HookRequest request, TmuxHooks hooks, Server server, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Runs a named hook on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.ITmuxRequest<LibTmux.Pane>,LibTmux.Pane,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this ITmuxRequest<Pane> request, Pane pane, CancellationToken cancellationToken = default)` | Public | Yes | `UnsupportedOSPlatform("windows")` | Runs a pane request on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.ITmuxRequest<LibTmux.Server>,LibTmux.Server,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this ITmuxRequest<Server> request, Server server, CancellationToken cancellationToken = default)` | Public | Yes | `UnsupportedOSPlatform("windows")` | Runs a server request on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.ITmuxRequest<LibTmux.Session>,LibTmux.Session,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this ITmuxRequest<Session> request, Session session, CancellationToken cancellationToken = default)` | Public | Yes | `UnsupportedOSPlatform("windows")` | Runs a session request on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.ITmuxRequest<LibTmux.TmuxHooks>,LibTmux.TmuxHooks,LibTmux.Server,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this ITmuxRequest<TmuxHooks> request, TmuxHooks hooks, Server server, CancellationToken cancellationToken = default)` | Public | Yes | `UnsupportedOSPlatform("windows")` | Runs a hook request on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.ITmuxRequest<LibTmux.TmuxOptions>,LibTmux.TmuxOptions,LibTmux.Server,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this ITmuxRequest<TmuxOptions> request, TmuxOptions options, Server server, CancellationToken cancellationToken = default)` | Public | Yes | `UnsupportedOSPlatform("windows")` | Runs an option request on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.ITmuxRequest<LibTmux.Window>,LibTmux.Window,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this ITmuxRequest<Window> request, Window window, CancellationToken cancellationToken = default)` | Public | Yes | `UnsupportedOSPlatform("windows")` | Runs a window request on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.SetHooksRequest,LibTmux.TmuxHooks,LibTmux.Server,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this SetHooksRequest request, TmuxHooks hooks, Server server, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Runs a multi-entry hook request in one invocation. |
| `M:LibTmux.TmuxChaining.ToCommands(LibTmux.SetHooksRequest,LibTmux.TmuxHooks)` | `static static IReadOnlyList<TmuxCommand> ToCommands(this SetHooksRequest request, TmuxHooks hooks)` | Public | Yes | Portable | Returns every command a multi-entry hook request sends. |
| `M:LibTmux.TmuxChaining.ToRunCommand(LibTmux.HookRequest,LibTmux.TmuxHooks)` | `static static TmuxCommand ToRunCommand(this HookRequest request, TmuxHooks hooks)` | Public | Yes | Portable | Returns running a named hook as one tmux command. |
| `M:LibTmux.TmuxChaining.ToUnsetCommand(LibTmux.HookRequest,LibTmux.TmuxHooks)` | `static static TmuxCommand ToUnsetCommand(this HookRequest request, TmuxHooks hooks)` | Public | Yes | Portable | Returns removing a named hook as one tmux command. |

### `T:LibTmux.TmuxCleanupException`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.TmuxCleanupException.#ctor(string,OperationCanceledException,int,Exception)` | `TmuxCleanupException(string message, OperationCanceledException originalCancellation, int clientProcessId, Exception cleanupFailure)` | Public | No | Portable | Creates TmuxCleanupException. |
| `P:LibTmux.TmuxCleanupException.CleanupFailure` | `Exception LibTmux.TmuxCleanupException.CleanupFailure { get; }` | Public | No | Portable | Gets CleanupFailure. |
| `P:LibTmux.TmuxCleanupException.ClientProcessId` | `int LibTmux.TmuxCleanupException.ClientProcessId { get; }` | Public | No | Portable | Gets ClientProcessId. |
| `P:LibTmux.TmuxCleanupException.OriginalCancellation` | `OperationCanceledException LibTmux.TmuxCleanupException.OriginalCancellation { get; }` | Public | No | Portable | Gets OriginalCancellation. |

### `T:LibTmux.TmuxColorMode`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `F:LibTmux.TmuxColorMode.Colors256` | `Colors256 = 2` | Public | Implicit | Portable | Requests 256-color mode. Value: `2`. |
| `F:LibTmux.TmuxColorMode.Default` | `Default = 0` | Public | Implicit | Portable | Uses tmux default color capabilities. Value: `0`. |
| `F:LibTmux.TmuxColorMode.TrueColor` | `TrueColor = 3` | Public | Implicit | Portable | Requests RGB true-color mode. Value: `3`. |

### `T:LibTmux.TmuxCommand`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.TmuxCommand.#ctor(string,System.Collections.Generic.IReadOnlyList{string})` | `TmuxCommand(string Name, IReadOnlyList<string> Arguments)` | Public | No | Portable | Creates TmuxCommand. |
| `M:LibTmux.TmuxCommand.Create(string,string[])` | `static TmuxCommand Create(string name, params string[] arguments)` | Public | No | Portable | Creates a command from its name and arguments. |
| `M:LibTmux.TmuxCommand.ToArguments` | `IReadOnlyList<string> ToArguments()` | Public | No | Portable | Returns this command the way tmux receives it. |
| `P:LibTmux.TmuxCommand.Arguments` | `IReadOnlyList<string> LibTmux.TmuxCommand.Arguments { get; init; }` | Public | No | Portable | Gets Arguments. |
| `P:LibTmux.TmuxCommand.Name` | `string LibTmux.TmuxCommand.Name { get; init; }` | Public | No | Portable | Gets Name. |

### `T:LibTmux.TmuxCommandException`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.TmuxCommandException.#ctor(string,TmuxCommandResult,Exception?)` | `TmuxCommandException(string message, TmuxCommandResult result, Exception? innerException = null)` | Public | No | Portable | Creates TmuxCommandException. |
| `P:LibTmux.TmuxCommandException.Result` | `TmuxCommandResult LibTmux.TmuxCommandException.Result { get; }` | Public | No | Portable | Gets Result. |

### `T:LibTmux.TmuxCommandNotFoundException`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.TmuxCommandNotFoundException.#ctor(string,string,Exception?)` | `TmuxCommandNotFoundException(string message, string tmuxBinaryPath, Exception? innerException = null)` | Public | No | Portable | Creates TmuxCommandNotFoundException. |
| `P:LibTmux.TmuxCommandNotFoundException.TmuxBinaryPath` | `string LibTmux.TmuxCommandNotFoundException.TmuxBinaryPath { get; }` | Public | No | Portable | Gets TmuxBinaryPath. |

### `T:LibTmux.TmuxCommandResult`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.TmuxCommandResult.#ctor(IReadOnlyList<string>,int,ReadOnlyMemory<byte>,ReadOnlyMemory<byte>,IReadOnlyList<string>,IReadOnlyList<string>)` | `TmuxCommandResult(IReadOnlyList<string> arguments, int exitCode, ReadOnlyMemory<byte> standardOutput, ReadOnlyMemory<byte> standardError, IReadOnlyList<string> standardOutputLines, IReadOnlyList<string> standardErrorLines)` | Public | No | Portable | Creates TmuxCommandResult. |
| `P:LibTmux.TmuxCommandResult.Arguments` | `IReadOnlyList<string> LibTmux.TmuxCommandResult.Arguments { get; }` | Public | No | Portable | Gets Arguments. |
| `P:LibTmux.TmuxCommandResult.ExitCode` | `int LibTmux.TmuxCommandResult.ExitCode { get; }` | Public | No | Portable | Gets ExitCode. |
| `P:LibTmux.TmuxCommandResult.StandardError` | `ReadOnlyMemory<byte> LibTmux.TmuxCommandResult.StandardError { get; }` | Public | No | Portable | Gets StandardError. |
| `P:LibTmux.TmuxCommandResult.StandardErrorLines` | `IReadOnlyList<string> LibTmux.TmuxCommandResult.StandardErrorLines { get; }` | Public | No | Portable | Gets StandardErrorLines. |
| `P:LibTmux.TmuxCommandResult.StandardOutput` | `ReadOnlyMemory<byte> LibTmux.TmuxCommandResult.StandardOutput { get; }` | Public | No | Portable | Gets StandardOutput. |
| `P:LibTmux.TmuxCommandResult.StandardOutputLines` | `IReadOnlyList<string> LibTmux.TmuxCommandResult.StandardOutputLines { get; }` | Public | No | Portable | Gets StandardOutputLines. |

### `T:LibTmux.TmuxDiagnostics`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `F:LibTmux.TmuxDiagnostics.ActivitySourceName` | `static const string LibTmux.TmuxDiagnostics.ActivitySourceName` | Public | Yes | Portable | The activity source name every tmux command is traced under. Value: `LibTmux`. |
| `F:LibTmux.TmuxDiagnostics.CommandDurationInstrumentName` | `static const string LibTmux.TmuxDiagnostics.CommandDurationInstrumentName` | Public | Yes | Portable | The histogram recording how long each tmux command took, in seconds. Value: `libtmux.command.duration`. |
| `F:LibTmux.TmuxDiagnostics.MeterName` | `static const string LibTmux.TmuxDiagnostics.MeterName` | Public | Yes | Portable | The meter name every tmux command is measured under. Value: `LibTmux`. |

### `T:LibTmux.TmuxDispatchState`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `F:LibTmux.TmuxDispatchState.Dispatched` | `Dispatched = 2` | Public | Implicit | Portable | tmux ran the command and answered, so any side effect has already happened. Value: `2`. |
| `F:LibTmux.TmuxDispatchState.NotDispatched` | `NotDispatched = 1` | Public | Implicit | Portable | The command never reached tmux, so a retry repeats nothing. Value: `1`. |
| `F:LibTmux.TmuxDispatchState.Unknown` | `Unknown = 0` | Public | Implicit | Portable | Whether tmux acted on the command cannot be determined; treat a retry as able to repeat it. Value: `0`. |

### `T:LibTmux.TmuxEnvironment`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.TmuxEnvironment.GetAllAsync(CancellationToken)` | `Task<IReadOnlyList<TmuxEnvironmentEntry>> LibTmux.TmuxEnvironment.GetAllAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Gets every environment entry. |
| `M:LibTmux.TmuxEnvironment.GetAsync(string,CancellationToken)` | `Task<TmuxEnvironmentEntry?> LibTmux.TmuxEnvironment.GetAsync(string name, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Gets one environment entry. |
| `M:LibTmux.TmuxEnvironment.RemoveAsync(string,CancellationToken)` | `Task LibTmux.TmuxEnvironment.RemoveAsync(string name, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Removes one environment entry. |
| `M:LibTmux.TmuxEnvironment.SetAsync(string,string,bool,bool,CancellationToken)` | `Task<TmuxEnvironmentEntry> LibTmux.TmuxEnvironment.SetAsync(string name, string value, bool expandFormats = false, bool hidden = false, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Sets one tmux environment variable. |
| `M:LibTmux.TmuxEnvironment.UnsetAsync(string,CancellationToken)` | `Task LibTmux.TmuxEnvironment.UnsetAsync(string name, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Marks one variable unset. |

### `T:LibTmux.TmuxEnvironmentEntry`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.TmuxEnvironmentEntry.#ctor(string,string?,bool)` | `TmuxEnvironmentEntry(string name, string? value, bool isRemoved)` | Public | No | Portable | Creates TmuxEnvironmentEntry. |
| `P:LibTmux.TmuxEnvironmentEntry.IsRemoved` | `bool LibTmux.TmuxEnvironmentEntry.IsRemoved { get; }` | Public | No | Portable | Gets IsRemoved. |
| `P:LibTmux.TmuxEnvironmentEntry.Name` | `string LibTmux.TmuxEnvironmentEntry.Name { get; }` | Public | No | Portable | Gets Name. |
| `P:LibTmux.TmuxEnvironmentEntry.Value` | `string? LibTmux.TmuxEnvironmentEntry.Value { get; }` | Public | No | Portable | Gets Value. |

### `T:LibTmux.TmuxEventsDroppedEvent`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.TmuxEventsDroppedEvent.#ctor(long,long)` | `TmuxEventsDroppedEvent(long Count, long TotalDropped)` | Public | No | Portable | Creates a bounded-event-buffer loss marker. |
| `P:LibTmux.TmuxEventsDroppedEvent.Count` | `long LibTmux.TmuxEventsDroppedEvent.Count { get; init; }` | Public | No | Portable | Gets the events discarded since the previous loss report. |
| `P:LibTmux.TmuxEventsDroppedEvent.TotalDropped` | `long LibTmux.TmuxEventsDroppedEvent.TotalDropped { get; init; }` | Public | No | Portable | Gets the events discarded over this control client's lifetime. |

### `T:LibTmux.TmuxExitEvent`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.TmuxExitEvent.#ctor(string?)` | `TmuxExitEvent(string? Reason)` | Public | No | Portable | Creates TmuxExitEvent. |
| `P:LibTmux.TmuxExitEvent.Reason` | `string? LibTmux.TmuxExitEvent.Reason { get; init; }` | Public | No | Portable | Gets Reason. |

### `T:LibTmux.TmuxHook`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.TmuxHook.#ctor(string,IReadOnlyList<TmuxHookEntry>)` | `TmuxHook(string name, IReadOnlyList<TmuxHookEntry> values)` | Public | No | Portable | Creates TmuxHook. |
| `P:LibTmux.TmuxHook.Name` | `string LibTmux.TmuxHook.Name { get; }` | Public | No | Portable | Gets Name. |
| `P:LibTmux.TmuxHook.Values` | `IReadOnlyList<TmuxHookEntry> LibTmux.TmuxHook.Values { get; }` | Public | No | Portable | Gets Values. |

### `T:LibTmux.TmuxHookEntry`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.TmuxHookEntry.#ctor(int,string)` | `TmuxHookEntry(int index, string command)` | Public | No | Portable | Creates TmuxHookEntry. |
| `P:LibTmux.TmuxHookEntry.Command` | `string LibTmux.TmuxHookEntry.Command { get; }` | Public | No | Portable | Gets Command. |
| `P:LibTmux.TmuxHookEntry.Index` | `int LibTmux.TmuxHookEntry.Index { get; }` | Public | No | Portable | Gets Index. |

### `T:LibTmux.TmuxHooks`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.TmuxHooks.GetAllAsync(ListHooksRequest?,CancellationToken)` | `Task<IReadOnlyList<TmuxHook>> LibTmux.TmuxHooks.GetAllAsync(ListHooksRequest? request = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Gets hooks for one scope. |
| `M:LibTmux.TmuxHooks.GetAsync(HookRequest,CancellationToken)` | `Task<TmuxHook?> LibTmux.TmuxHooks.GetAsync(HookRequest request, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Gets one hook. |
| `M:LibTmux.TmuxHooks.RunAsync(HookRequest,CancellationToken)` | `Task LibTmux.TmuxHooks.RunAsync(HookRequest request, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Runs one hook. |
| `M:LibTmux.TmuxHooks.SetAsync(SetHookRequest,CancellationToken)` | `Task<TmuxHook> LibTmux.TmuxHooks.SetAsync(SetHookRequest request, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Sets one hook. |
| `M:LibTmux.TmuxHooks.SetAsync(SetHooksRequest,CancellationToken)` | `Task<TmuxHook> LibTmux.TmuxHooks.SetAsync(SetHooksRequest request, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Sets sparse commands for one hook. |
| `M:LibTmux.TmuxHooks.UnsetAsync(HookRequest,CancellationToken)` | `Task LibTmux.TmuxHooks.UnsetAsync(HookRequest request, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Unsets one hook. |
| `P:LibTmux.TmuxHooks.Scope` | `OptionScope LibTmux.TmuxHooks.Scope { get; }` | Public | No | Portable | Gets the scope bound to this entity service. |

### `T:LibTmux.TmuxMenuItem`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.TmuxMenuItem.#ctor(string,string,string)` | `TmuxMenuItem(string name, string key, string command)` | Public | No | Portable | Creates TmuxMenuItem. |
| `P:LibTmux.TmuxMenuItem.Command` | `string LibTmux.TmuxMenuItem.Command { get; }` | Public | No | Portable | Gets Command. |
| `P:LibTmux.TmuxMenuItem.Key` | `string LibTmux.TmuxMenuItem.Key { get; }` | Public | No | Portable | Gets Key. |
| `P:LibTmux.TmuxMenuItem.Name` | `string LibTmux.TmuxMenuItem.Name { get; }` | Public | No | Portable | Gets Name. |

### `T:LibTmux.TmuxNotificationEvent`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.TmuxNotificationEvent.#ctor(string,System.Collections.Generic.IReadOnlyList{string})` | `TmuxNotificationEvent(string Name, IReadOnlyList<string> Arguments)` | Public | No | Portable | Creates TmuxNotificationEvent. |
| `P:LibTmux.TmuxNotificationEvent.Arguments` | `IReadOnlyList<string> LibTmux.TmuxNotificationEvent.Arguments { get; init; }` | Public | No | Portable | Gets Arguments. |
| `P:LibTmux.TmuxNotificationEvent.Name` | `string LibTmux.TmuxNotificationEvent.Name { get; init; }` | Public | No | Portable | Gets Name. |

### `T:LibTmux.TmuxObjectNotFoundException`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.TmuxObjectNotFoundException.#ctor(string,string,Exception?)` | `TmuxObjectNotFoundException(string message, string target, Exception? innerException = null)` | Public | No | Portable | Creates TmuxObjectNotFoundException. |
| `P:LibTmux.TmuxObjectNotFoundException.Target` | `string LibTmux.TmuxObjectNotFoundException.Target { get; }` | Public | No | Portable | Gets Target. |

### `T:LibTmux.TmuxOperationCanceledException`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.TmuxOperationCanceledException.#ctor(string,CancellationToken,bool,int,Exception?)` | `TmuxOperationCanceledException(string message, CancellationToken cancellationToken, bool commandMayHaveExecuted, int clientProcessId, Exception? innerException = null)` | Public | No | Portable | Creates TmuxOperationCanceledException. |
| `P:LibTmux.TmuxOperationCanceledException.ClientProcessId` | `int LibTmux.TmuxOperationCanceledException.ClientProcessId { get; }` | Public | No | Portable | Gets ClientProcessId. |
| `P:LibTmux.TmuxOperationCanceledException.CommandMayHaveExecuted` | `bool LibTmux.TmuxOperationCanceledException.CommandMayHaveExecuted { get; }` | Public | No | Portable | Gets CommandMayHaveExecuted. |

### `T:LibTmux.TmuxOption`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.TmuxOption.#ctor(string,TmuxOptionValue,int?,bool)` | `TmuxOption(string name, TmuxOptionValue value, int? index, bool inherited = false)` | Public | No | Portable | Creates TmuxOption. |
| `P:LibTmux.TmuxOption.Index` | `int? LibTmux.TmuxOption.Index { get; }` | Public | No | Portable | Gets Index. |
| `P:LibTmux.TmuxOption.Inherited` | `bool LibTmux.TmuxOption.Inherited { get; }` | Public | No | Portable | Gets Inherited. |
| `P:LibTmux.TmuxOption.Name` | `string LibTmux.TmuxOption.Name { get; }` | Public | No | Portable | Gets Name. |
| `P:LibTmux.TmuxOption.Value` | `TmuxOptionValue LibTmux.TmuxOption.Value { get; }` | Public | No | Portable | Gets Value. |

### `T:LibTmux.TmuxOptionException`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.TmuxOptionException.#ctor(string,string,Exception?)` | `TmuxOptionException(string message, string optionName, Exception? innerException = null)` | Public | No | Portable | Creates TmuxOptionException. |
| `P:LibTmux.TmuxOptionException.OptionName` | `string LibTmux.TmuxOptionException.OptionName { get; }` | Public | No | Portable | Gets OptionName. |

### `T:LibTmux.TmuxOptionState`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `F:LibTmux.TmuxOptionState.Absent` | `Absent = 0` | Public | Implicit | Portable | The Absent value. Value: `0`. |
| `F:LibTmux.TmuxOptionState.Off` | `Off = 1` | Public | Implicit | Portable | The Off value. Value: `1`. |
| `F:LibTmux.TmuxOptionState.On` | `On = 2` | Public | Implicit | Portable | The On value. Value: `2`. |
| `F:LibTmux.TmuxOptionState.Value` | `Value = 3` | Public | Implicit | Portable | The Value value. Value: `3`. |

### `T:LibTmux.TmuxOptionValue`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.TmuxOptionValue.#ctor(string?,TmuxOptionState,bool?,long?)` | `TmuxOptionValue(string? raw, TmuxOptionState state, bool? boolean, long? integer)` | Public | No | Portable | Creates TmuxOptionValue. |
| `P:LibTmux.TmuxOptionValue.Boolean` | `bool? LibTmux.TmuxOptionValue.Boolean { get; }` | Public | No | Portable | Gets Boolean. |
| `P:LibTmux.TmuxOptionValue.Integer` | `long? LibTmux.TmuxOptionValue.Integer { get; }` | Public | No | Portable | Gets Integer. |
| `P:LibTmux.TmuxOptionValue.Raw` | `string? LibTmux.TmuxOptionValue.Raw { get; }` | Public | No | Portable | Gets Raw. |
| `P:LibTmux.TmuxOptionValue.State` | `TmuxOptionState LibTmux.TmuxOptionValue.State { get; }` | Public | No | Portable | Gets State. |

### `T:LibTmux.TmuxOptions`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.TmuxOptions.GetAllAsync(GetOptionsRequest?,CancellationToken)` | `Task<IReadOnlyList<TmuxOption>> LibTmux.TmuxOptions.GetAllAsync(GetOptionsRequest? request = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Gets scalar and sparse-array options. |
| `M:LibTmux.TmuxOptions.GetAsync(GetOptionRequest,CancellationToken)` | `Task<IReadOnlyList<TmuxOption>> LibTmux.TmuxOptions.GetAsync(GetOptionRequest request, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Gets zero, one scalar, or multiple sparse values for one option name. Option cardinality: empty, scalar-one, or sparse-many. |
| `M:LibTmux.TmuxOptions.SetAsync(SetOptionRequest,CancellationToken)` | `Task<TmuxOptionValue> LibTmux.TmuxOptions.SetAsync(SetOptionRequest request, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Sets one option and returns its canonical value. |
| `M:LibTmux.TmuxOptions.UnsetAsync(UnsetOptionRequest,CancellationToken)` | `Task LibTmux.TmuxOptions.UnsetAsync(UnsetOptionRequest request, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Unsets one option. |
| `P:LibTmux.TmuxOptions.Scope` | `OptionScope LibTmux.TmuxOptions.Scope { get; }` | Public | No | Portable | Gets the scope bound to this entity service. |

### `T:LibTmux.TmuxOutputEvent`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.TmuxOutputEvent.#ctor(PaneId,string)` | `TmuxOutputEvent(PaneId PaneId, string Data)` | Public | No | Portable | Creates TmuxOutputEvent. |
| `P:LibTmux.TmuxOutputEvent.Data` | `string LibTmux.TmuxOutputEvent.Data { get; init; }` | Public | No | Portable | Gets Data. |
| `P:LibTmux.TmuxOutputEvent.PaneId` | `PaneId LibTmux.TmuxOutputEvent.PaneId { get; init; }` | Public | No | Portable | Gets PaneId. |

### `T:LibTmux.TmuxPaneException`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.TmuxPaneException.#ctor(string,PaneId,Exception?)` | `TmuxPaneException(string message, PaneId paneId, Exception? innerException = null)` | Public | No | Portable | Creates TmuxPaneException. |
| `P:LibTmux.TmuxPaneException.PaneId` | `PaneId LibTmux.TmuxPaneException.PaneId { get; }` | Public | No | Portable | Gets PaneId. |

### `T:LibTmux.TmuxPaneGoneEvent`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.TmuxPaneGoneEvent.#ctor(PaneId)` | `TmuxPaneGoneEvent(PaneId PaneId)` | Public | No | Portable | Creates TmuxPaneGoneEvent. |
| `P:LibTmux.TmuxPaneGoneEvent.PaneId` | `PaneId LibTmux.TmuxPaneGoneEvent.PaneId { get; init; }` | Public | No | Portable | Gets the pane that is gone. |

### `T:LibTmux.TmuxProtocolException`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.TmuxProtocolException.#ctor(string,TmuxDispatchState,Exception?)` | `TmuxProtocolException(string message, TmuxDispatchState dispatch, Exception? innerException = null)` | Public | No | Portable | Initializes the exception for an unreadable answer. |
| `M:LibTmux.TmuxProtocolException.#ctor(string,string,TmuxDispatchState,Exception?)` | `TmuxProtocolException(string message, string payload, TmuxDispatchState dispatch, Exception? innerException = null)` | Public | No | Portable | Initializes the exception naming what tmux sent. |
| `P:LibTmux.TmuxProtocolException.Payload` | `string LibTmux.TmuxProtocolException.Payload { get; }` | Public | No | Portable | What tmux sent that could not be read. |

### `T:LibTmux.TmuxSessionExistsException`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.TmuxSessionExistsException.#ctor(string,string,Exception?)` | `TmuxSessionExistsException(string message, string sessionName, Exception? innerException = null)` | Public | No | Portable | Creates TmuxSessionExistsException. |
| `P:LibTmux.TmuxSessionExistsException.SessionName` | `string LibTmux.TmuxSessionExistsException.SessionName { get; }` | Public | No | Portable | Gets SessionName. |

### `T:LibTmux.TmuxTransportException`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.TmuxTransportException.#ctor(string,IReadOnlyList<string>,Exception?)` | `TmuxTransportException(string message, IReadOnlyList<string> arguments, Exception? innerException = null)` | Public | No | Portable | Creates TmuxTransportException. |
| `M:LibTmux.TmuxTransportException.#ctor(string,IReadOnlyList<string>,TmuxDispatchState,Exception?)` | `TmuxTransportException(string message, IReadOnlyList<string> arguments, TmuxDispatchState dispatch, Exception? innerException = null)` | Public | No | Portable | Creates TmuxTransportException with a known dispatch state. |
| `P:LibTmux.TmuxTransportException.Arguments` | `IReadOnlyList<string> LibTmux.TmuxTransportException.Arguments { get; }` | Public | No | Portable | Gets Arguments. |

### `T:LibTmux.TmuxVersion`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.TmuxVersion.#ctor(string)` | `TmuxVersion(string raw)` | Public | No | Portable | Parses and preserves one tmux version string. |
| `M:LibTmux.TmuxVersion.CheckMinimumSupportedVersionAsync(bool,string,CancellationToken)` | `static Task<bool> LibTmux.TmuxVersion.CheckMinimumSupportedVersionAsync(bool throwIfUnsupported = true, string tmuxBinaryPath = "tmux", CancellationToken cancellationToken = default)` | Public | Yes | `UnsupportedOSPlatform("windows")` | Checks the package minimum and optionally throws TmuxVersionTooLowException. |
| `M:LibTmux.TmuxVersion.CompareTo(TmuxVersion)` | `int LibTmux.TmuxVersion.CompareTo(TmuxVersion other)` | Public | No | Portable | Compares parsed tmux versions. |
| `M:LibTmux.TmuxVersion.DetectAsync(string,CancellationToken)` | `static Task<TmuxVersion> LibTmux.TmuxVersion.DetectAsync(string tmuxBinaryPath = "tmux", CancellationToken cancellationToken = default)` | Public | Yes | `UnsupportedOSPlatform("windows")` | Detects the selected tmux executable version. |
| `M:LibTmux.TmuxVersion.DetectStringAsync(string,CancellationToken)` | `static Task<string> LibTmux.TmuxVersion.DetectStringAsync(string tmuxBinaryPath = "tmux", CancellationToken cancellationToken = default)` | Public | Yes | `UnsupportedOSPlatform("windows")` | Detects the selected tmux executable version string. |
| `M:LibTmux.TmuxVersion.EnsureAtLeast(TmuxVersion)` | `void LibTmux.TmuxVersion.EnsureAtLeast(TmuxVersion minimum)` | Public | No | Portable | Throws TmuxVersionTooLowException when this version is too old. |
| `M:LibTmux.TmuxVersion.EnsureMinimumSupportedVersionAsync(string,CancellationToken)` | `static Task LibTmux.TmuxVersion.EnsureMinimumSupportedVersionAsync(string tmuxBinaryPath = "tmux", CancellationToken cancellationToken = default)` | Public | Yes | `UnsupportedOSPlatform("windows")` | Throws TmuxVersionTooLowException when the selected tmux executable is too old. |
| `M:LibTmux.TmuxVersion.IsAtLeast(TmuxVersion)` | `bool LibTmux.TmuxVersion.IsAtLeast(TmuxVersion minimum)` | Public | No | Portable | Reports whether this version meets a minimum. |
| `M:LibTmux.TmuxVersion.IsInstalledAtLeastAsync(TmuxVersion,string,CancellationToken)` | `static Task<bool> LibTmux.TmuxVersion.IsInstalledAtLeastAsync(TmuxVersion version, string tmuxBinaryPath = "tmux", CancellationToken cancellationToken = default)` | Public | Yes | `UnsupportedOSPlatform("windows")` | Checks whether installed tmux meets a minimum. |
| `M:LibTmux.TmuxVersion.IsInstalledAtMostAsync(TmuxVersion,string,CancellationToken)` | `static Task<bool> LibTmux.TmuxVersion.IsInstalledAtMostAsync(TmuxVersion version, string tmuxBinaryPath = "tmux", CancellationToken cancellationToken = default)` | Public | Yes | `UnsupportedOSPlatform("windows")` | Checks whether installed tmux is at most a maximum. |
| `M:LibTmux.TmuxVersion.IsInstalledNewerThanAsync(TmuxVersion,string,CancellationToken)` | `static Task<bool> LibTmux.TmuxVersion.IsInstalledNewerThanAsync(TmuxVersion version, string tmuxBinaryPath = "tmux", CancellationToken cancellationToken = default)` | Public | Yes | `UnsupportedOSPlatform("windows")` | Checks whether installed tmux is newer. |
| `M:LibTmux.TmuxVersion.IsInstalledOlderThanAsync(TmuxVersion,string,CancellationToken)` | `static Task<bool> LibTmux.TmuxVersion.IsInstalledOlderThanAsync(TmuxVersion version, string tmuxBinaryPath = "tmux", CancellationToken cancellationToken = default)` | Public | Yes | `UnsupportedOSPlatform("windows")` | Checks whether installed tmux is older. |
| `M:LibTmux.TmuxVersion.IsInstalledVersionAsync(TmuxVersion,string,CancellationToken)` | `static Task<bool> LibTmux.TmuxVersion.IsInstalledVersionAsync(TmuxVersion version, string tmuxBinaryPath = "tmux", CancellationToken cancellationToken = default)` | Public | Yes | `UnsupportedOSPlatform("windows")` | Checks exact installed version equality. |
| `M:LibTmux.TmuxVersion.IsMinimumSupportedVersionInstalledAsync(string,CancellationToken)` | `static Task<bool> LibTmux.TmuxVersion.IsMinimumSupportedVersionInstalledAsync(string tmuxBinaryPath = "tmux", CancellationToken cancellationToken = default)` | Public | Yes | `UnsupportedOSPlatform("windows")` | Reports whether the selected tmux executable meets the package minimum. |
| `M:LibTmux.TmuxVersion.Parse(string)` | `static TmuxVersion LibTmux.TmuxVersion.Parse(string text)` | Public | Yes | Portable | Parses a tmux version string. |
| `M:LibTmux.TmuxVersion.ToString()` | `string LibTmux.TmuxVersion.ToString()` | Public | No | Portable | Returns the canonical tmux version string. |
| `M:LibTmux.TmuxVersion.TryParse(string?,TmuxVersion)` | `static bool LibTmux.TmuxVersion.TryParse(string? text, out TmuxVersion result)` | Public | Yes | Portable | Tries to parse a tmux version string. |
| `M:LibTmux.TmuxVersion.op_Equality(TmuxVersion,TmuxVersion)` | `static bool operator ==(TmuxVersion left, TmuxVersion right)` | Public | Yes | Portable | Implements the == version operator. Compiler-generated by the record struct. |
| `M:LibTmux.TmuxVersion.op_GreaterThan(TmuxVersion,TmuxVersion)` | `static bool operator >(TmuxVersion left, TmuxVersion right)` | Public | Yes | Portable | Implements the > version operator. |
| `M:LibTmux.TmuxVersion.op_GreaterThanOrEqual(TmuxVersion,TmuxVersion)` | `static bool operator >=(TmuxVersion left, TmuxVersion right)` | Public | Yes | Portable | Implements the >= version operator. |
| `M:LibTmux.TmuxVersion.op_Inequality(TmuxVersion,TmuxVersion)` | `static bool operator !=(TmuxVersion left, TmuxVersion right)` | Public | Yes | Portable | Implements the != version operator. Compiler-generated by the record struct. |
| `M:LibTmux.TmuxVersion.op_LessThan(TmuxVersion,TmuxVersion)` | `static bool operator <(TmuxVersion left, TmuxVersion right)` | Public | Yes | Portable | Implements the < version operator. |
| `M:LibTmux.TmuxVersion.op_LessThanOrEqual(TmuxVersion,TmuxVersion)` | `static bool operator <=(TmuxVersion left, TmuxVersion right)` | Public | Yes | Portable | Implements the <= version operator. |
| `P:LibTmux.TmuxVersion.IsValid` | `bool LibTmux.TmuxVersion.IsValid { get; }` | Public | No | Portable | Gets whether this value contains a parsed tmux version. |
| `P:LibTmux.TmuxVersion.Major` | `int LibTmux.TmuxVersion.Major { get; }` | Public | No | Portable | Gets the parsed major version. |
| `P:LibTmux.TmuxVersion.Minor` | `int LibTmux.TmuxVersion.Minor { get; }` | Public | No | Portable | Gets the parsed minor version. |
| `P:LibTmux.TmuxVersion.Raw` | `string LibTmux.TmuxVersion.Raw { get; }` | Public | No | Portable | Gets the exact normalized tmux version text. |
| `P:LibTmux.TmuxVersion.Suffix` | `string? LibTmux.TmuxVersion.Suffix { get; }` | Public | No | Portable | Gets the exact preserved patch, prerelease, development, vendor, or next suffix projection. |

### `T:LibTmux.TmuxVersionTooLowException`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.TmuxVersionTooLowException.#ctor(string,TmuxVersion,TmuxVersion,Exception?)` | `TmuxVersionTooLowException(string message, TmuxVersion requiredVersion, TmuxVersion actualVersion, Exception? innerException = null)` | Public | No | Portable | Creates TmuxVersionTooLowException. |
| `P:LibTmux.TmuxVersionTooLowException.ActualVersion` | `TmuxVersion LibTmux.TmuxVersionTooLowException.ActualVersion { get; }` | Public | No | Portable | Gets ActualVersion. |
| `P:LibTmux.TmuxVersionTooLowException.RequiredVersion` | `TmuxVersion LibTmux.TmuxVersionTooLowException.RequiredVersion { get; }` | Public | No | Portable | Gets RequiredVersion. |

### `T:LibTmux.TmuxWait`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.TmuxWait.UntilAsync(Func<CancellationToken,Task<bool>>,TimeSpan,TimeSpan,bool,CancellationToken)` | `static Task<bool> LibTmux.TmuxWait.UntilAsync(Func<CancellationToken,Task<bool>> probe, TimeSpan timeout, TimeSpan interval, bool throwOnTimeout = true, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Polls a Boolean probe and optionally returns false on timeout. |
| ``M:LibTmux.TmuxWait.UntilAsync``1(Func<CancellationToken,Task<T>>,Func<T,bool>,TimeSpan,TimeSpan,CancellationToken)`` | `static Task<T> LibTmux.TmuxWait.UntilAsync<T>(Func<CancellationToken,Task<T>> probe, Func<T,bool> predicate, TimeSpan timeout, TimeSpan interval, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Polls with a deadline and caller cancellation. |

### `T:LibTmux.TmuxWaitChannel`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.TmuxWaitChannel.DisposeAsync()` | `ValueTask LibTmux.TmuxWaitChannel.DisposeAsync()` | Public | No | `UnsupportedOSPlatform("windows")` | Withdraws the waiter from tmux. |
| `M:LibTmux.TmuxWaitChannel.WaitAsync(TimeSpan,CancellationToken)` | `Task<bool> LibTmux.TmuxWaitChannel.WaitAsync(TimeSpan budget, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Waits for the signal, giving this attempt a budget. |
| `P:LibTmux.TmuxWaitChannel.Channel` | `string LibTmux.TmuxWaitChannel.Channel { get; }` | Public | No | Portable | Gets the channel being waited on. |
| `P:LibTmux.TmuxWaitChannel.Signalled` | `bool LibTmux.TmuxWaitChannel.Signalled { get; }` | Public | No | Portable | Gets whether the wait completed before withdrawal began. |

### `T:LibTmux.TmuxWaitMode`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `F:LibTmux.TmuxWaitMode.Lock` | `Lock = 1` | Public | Implicit | Portable | The Lock mode. Value: `1`. |
| `F:LibTmux.TmuxWaitMode.Signal` | `Signal = 3` | Public | Implicit | Portable | The Signal mode. Value: `3`. |
| `F:LibTmux.TmuxWaitMode.Unlock` | `Unlock = 2` | Public | Implicit | Portable | The Unlock mode. Value: `2`. |
| `F:LibTmux.TmuxWaitMode.Wait` | `Wait = 0` | Public | Implicit | Portable | The Wait mode. Value: `0`. |

### `T:LibTmux.TmuxWaitTimeoutException`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.TmuxWaitTimeoutException.#ctor(string,TimeSpan,Exception?)` | `TmuxWaitTimeoutException(string message, TimeSpan timeout, Exception? innerException = null)` | Public | No | Portable | Creates TmuxWaitTimeoutException. |
| `P:LibTmux.TmuxWaitTimeoutException.Timeout` | `TimeSpan LibTmux.TmuxWaitTimeoutException.Timeout { get; }` | Public | No | Portable | Gets Timeout. |

### `T:LibTmux.TmuxWindowException`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.TmuxWindowException.#ctor(string,WindowId,Exception?)` | `TmuxWindowException(string message, WindowId windowId, Exception? innerException = null)` | Public | No | Portable | Creates TmuxWindowException. |
| `M:LibTmux.TmuxWindowException.#ctor(string,WindowId,TmuxDispatchState,Exception?)` | `TmuxWindowException(string message, WindowId windowId, TmuxDispatchState dispatch, Exception? innerException = null)` | Public | No | Portable | Creates TmuxWindowException. |
| `P:LibTmux.TmuxWindowException.WindowId` | `WindowId LibTmux.TmuxWindowException.WindowId { get; }` | Public | No | Portable | Gets WindowId. |

### `T:LibTmux.UnbindKeyRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.UnbindKeyRequest.LibTmux.ITmuxRequest<LibTmux.Server>.ToCommand(LibTmux.Server)` | `TmuxCommand LibTmux.ITmuxRequest<LibTmux.Server>.ToCommand(Server target)` | Explicit interface | No | Portable | Returns this request as one tmux command; the server is not needed to build it. |
| `M:LibTmux.UnbindKeyRequest.ToCommand()` | `TmuxCommand LibTmux.UnbindKeyRequest.ToCommand()` | Public | No | Portable | Returns a key-unbinding request as one tmux command. |
| `P:LibTmux.UnbindKeyRequest.All` | `bool LibTmux.UnbindKeyRequest.All { get; init; }` | Public | No | Portable | Gets All. |
| `P:LibTmux.UnbindKeyRequest.Key` | `string? LibTmux.UnbindKeyRequest.Key { get; init; }` | Public | No | Portable | Gets Key. |
| `P:LibTmux.UnbindKeyRequest.KeyTable` | `string? LibTmux.UnbindKeyRequest.KeyTable { get; init; }` | Public | No | Portable | Gets KeyTable. |
| `P:LibTmux.UnbindKeyRequest.Quiet` | `bool LibTmux.UnbindKeyRequest.Quiet { get; init; }` | Public | No | Portable | Gets Quiet. |

### `T:LibTmux.UnsafeTmuxFilter`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.UnsafeTmuxFilter.#ctor(string)` | `UnsafeTmuxFilter(string value)` | Public | No | Portable | Creates UnsafeTmuxFilter. |
| `P:LibTmux.UnsafeTmuxFilter.Value` | `string LibTmux.UnsafeTmuxFilter.Value { get; init; }` | Public | No | Portable | Gets Value. |

### `T:LibTmux.UnsetOptionRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.UnsetOptionRequest.#ctor(string)` | `UnsetOptionRequest(string name)` | Public | No | Portable | Creates UnsetOptionRequest. |
| `M:LibTmux.UnsetOptionRequest.ToCommand(LibTmux.TmuxOptions)` | `TmuxCommand LibTmux.UnsetOptionRequest.ToCommand(TmuxOptions options)` | Public | No | `UnsupportedOSPlatform("windows")` | Returns an unset request as one tmux command. |
| `P:LibTmux.UnsetOptionRequest.Global` | `bool LibTmux.UnsetOptionRequest.Global { get; init; }` | Public | No | Portable | Gets Global. |
| `P:LibTmux.UnsetOptionRequest.Name` | `string LibTmux.UnsetOptionRequest.Name { get; }` | Public | No | Portable | Gets Name. |
| `P:LibTmux.UnsetOptionRequest.Quiet` | `bool LibTmux.UnsetOptionRequest.Quiet { get; init; }` | Public | No | Portable | Gets Quiet. |
| `P:LibTmux.UnsetOptionRequest.Scope` | `OptionScope? LibTmux.UnsetOptionRequest.Scope { get; init; }` | Public | No | Portable | Gets Scope. |
| `P:LibTmux.UnsetOptionRequest.UnsetPaneOverrides` | `bool LibTmux.UnsetOptionRequest.UnsetPaneOverrides { get; init; }` | Public | No | Portable | Gets UnsetPaneOverrides. |

### `T:LibTmux.UnsupportedQueryExpressionException`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.UnsupportedQueryExpressionException.#ctor(string)` | `UnsupportedQueryExpressionException(string message)` | Public | No | Portable | Initializes the exception for one untranslatable expression. |
| `M:LibTmux.UnsupportedQueryExpressionException.#ctor(string,string,Exception?)` | `UnsupportedQueryExpressionException(string message, string expression, Exception? innerException = null)` | Public | No | Portable | Creates UnsupportedQueryExpressionException. |
| `P:LibTmux.UnsupportedQueryExpressionException.Expression` | `string LibTmux.UnsupportedQueryExpressionException.Expression { get; }` | Public | No | Portable | Gets Expression. |

### `T:LibTmux.WaitForRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.WaitForRequest.#ctor(string,TmuxWaitMode)` | `WaitForRequest(string channel, TmuxWaitMode mode)` | Public | No | Portable | Creates WaitForRequest. |
| `M:LibTmux.WaitForRequest.LibTmux.ITmuxRequest<LibTmux.Server>.ToCommand(LibTmux.Server)` | `TmuxCommand LibTmux.ITmuxRequest<LibTmux.Server>.ToCommand(Server target)` | Explicit interface | No | Portable | Returns this request as one tmux command; the server is not needed to build it. |
| `M:LibTmux.WaitForRequest.ToCommand()` | `TmuxCommand LibTmux.WaitForRequest.ToCommand()` | Public | No | Portable | Returns a channel request as one tmux command. |
| `P:LibTmux.WaitForRequest.Channel` | `string LibTmux.WaitForRequest.Channel { get; }` | Public | No | Portable | Gets Channel. |
| `P:LibTmux.WaitForRequest.Mode` | `TmuxWaitMode LibTmux.WaitForRequest.Mode { get; }` | Public | No | Portable | Gets Mode. |

### `T:LibTmux.Window`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.Window.CreatePaneAsync(NewPaneRequest?,CancellationToken)` | `Task<Pane> LibTmux.Window.CreatePaneAsync(NewPaneRequest? request = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs CreatePane. |
| `M:LibTmux.Window.CreateWindowAsync(NewWindowRequest?,CancellationToken)` | `Task<Window> LibTmux.Window.CreateWindowAsync(NewWindowRequest? request = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs CreateWindow. |
| `M:LibTmux.Window.DisplayMessageAsync(DisplayMessageRequest,CancellationToken)` | `Task<IReadOnlyList<string>?> LibTmux.Window.DisplayMessageAsync(DisplayMessageRequest request, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs DisplayMessage. |
| `M:LibTmux.Window.ExecuteCommandAsync(IReadOnlyList<string>,string?,CancellationToken)` | `Task<TmuxCommandResult> LibTmux.Window.ExecuteCommandAsync(IReadOnlyList<string> arguments, string? targetOverride = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Executes a raw command with stable target injection for the entity handle. |
| `M:LibTmux.Window.FindPaneAsync(string,CancellationToken)` | `Task<Pane?> LibTmux.Window.FindPaneAsync(string target, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Reads one pane in this window, returning null only when a successful lookup finds no match. |
| `M:LibTmux.Window.FromEnvironmentAsync(IReadOnlyDictionary<string,string>?,CancellationToken)` | `static Task<Window> LibTmux.Window.FromEnvironmentAsync(IReadOnlyDictionary<string,string>? environment = null, CancellationToken cancellationToken = default)` | Public | Yes | `UnsupportedOSPlatform("windows")` | Performs FromEnvironment. |
| `M:LibTmux.Window.GetLinkedSessionsAsync(CancellationToken)` | `Task<IReadOnlyList<Session>> LibTmux.Window.GetLinkedSessionsAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Reads the live collection and preserves failures, including an absent daemon. List error policy: loud. |
| `M:LibTmux.Window.GetPaneAsync(string,CancellationToken)` | `Task<Pane> LibTmux.Window.GetPaneAsync(string target, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Reads one pane in this window, throwing TmuxObjectNotFoundException when absent. |
| `M:LibTmux.Window.GetPanesAsync(CancellationToken)` | `Task<IReadOnlyList<Pane>> LibTmux.Window.GetPanesAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs loud child pane traversal. List error policy: loud. |
| `M:LibTmux.Window.KillAsync(bool,CancellationToken)` | `Task LibTmux.Window.KillAsync(bool allExcept = false, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs Kill. |
| `M:LibTmux.Window.LinkAsync(LinkWindowRequest,CancellationToken)` | `Task LibTmux.Window.LinkAsync(LinkWindowRequest request, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs Link. |
| `M:LibTmux.Window.MoveAsync(MoveWindowRequest,CancellationToken)` | `Task<Window> LibTmux.Window.MoveAsync(MoveWindowRequest request, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs Move. |
| `M:LibTmux.Window.RefreshAsync(CancellationToken)` | `Task<Window> LibTmux.Window.RefreshAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs Refresh. |
| `M:LibTmux.Window.RenameAsync(string,CancellationToken)` | `Task<Window> LibTmux.Window.RenameAsync(string name, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs Rename. |
| `M:LibTmux.Window.ResizeAsync(ResizeWindowRequest,CancellationToken)` | `Task<Window> LibTmux.Window.ResizeAsync(ResizeWindowRequest request, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs Resize. |
| `M:LibTmux.Window.RespawnAsync(RespawnRequest?,CancellationToken)` | `Task LibTmux.Window.RespawnAsync(RespawnRequest? request = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs Respawn. |
| `M:LibTmux.Window.RotateAsync(WindowRotationDirection?,bool,CancellationToken)` | `Task<Window> LibTmux.Window.RotateAsync(WindowRotationDirection? direction = null, bool keepZoom = false, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs Rotate. |
| `M:LibTmux.Window.SearchPanesAsync(UnsafeTmuxFilter,CancellationToken)` | `Task<IReadOnlyList<Pane>> LibTmux.Window.SearchPanesAsync(UnsafeTmuxFilter filter, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs a loud native pane search. List error policy: loud. |
| `M:LibTmux.Window.SelectAsync(CancellationToken)` | `Task<Window> LibTmux.Window.SelectAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs Select. |
| `M:LibTmux.Window.SelectLastPaneAsync(PaneInputMode?,bool,CancellationToken)` | `Task<Pane?> LibTmux.Window.SelectLastPaneAsync(PaneInputMode? inputMode = null, bool keepZoom = false, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Selects the previously active pane with input and zoom controls. |
| `M:LibTmux.Window.SelectLayoutAsync(SelectLayoutRequest?,CancellationToken)` | `Task<Window> LibTmux.Window.SelectLayoutAsync(SelectLayoutRequest? request = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs SelectLayout. |
| `M:LibTmux.Window.SelectNextLayoutAsync(CancellationToken)` | `Task<Window> LibTmux.Window.SelectNextLayoutAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs SelectNextLayout. |
| `M:LibTmux.Window.SelectPaneAsync(string,CancellationToken)` | `Task<Pane?> LibTmux.Window.SelectPaneAsync(string target, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs SelectPane. |
| `M:LibTmux.Window.SelectPreviousLayoutAsync(CancellationToken)` | `Task<Window> LibTmux.Window.SelectPreviousLayoutAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs SelectPreviousLayout. |
| `M:LibTmux.Window.SplitPaneAsync(SplitPaneRequest?,CancellationToken)` | `Task<Pane> LibTmux.Window.SplitPaneAsync(SplitPaneRequest? request = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs SplitPane. |
| `M:LibTmux.Window.SwapAsync(WindowId,bool,CancellationToken)` | `Task LibTmux.Window.SwapAsync(WindowId target, bool detach = false, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs Swap. |
| `M:LibTmux.Window.UnlinkAsync(bool,CancellationToken)` | `Task LibTmux.Window.UnlinkAsync(bool killIfLast = false, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs Unlink. |
| `M:LibTmux.Window.op_Equality(Window?,Window?)` | `static bool operator ==(Window? left, Window? right)` | Public | Yes | Portable | Reports whether two handles name the same window. |
| `M:LibTmux.Window.op_Inequality(Window?,Window?)` | `static bool operator !=(Window? left, Window? right)` | Public | Yes | Portable | Reports whether two handles name different windows. |
| `P:LibTmux.Window.ActivePane` | `CapturedValue<Pane> LibTmux.Window.ActivePane { get; }` | Public | No | `UnsupportedOSPlatform("windows")` | Gets the captured active child, or an uncaptured relation. |
| `P:LibTmux.Window.Edge` | `SessionWindowEdge LibTmux.Window.Edge { get; }` | Public | No | Portable | Gets the captured Edge value. |
| `P:LibTmux.Window.EntityKey` | `WindowEntityKey LibTmux.Window.EntityKey { get; }` | Public | No | Portable | Gets the captured EntityKey value. |
| `P:LibTmux.Window.Generation` | `ServerGeneration LibTmux.Window.Generation { get; }` | Public | No | Portable | Gets the captured Generation value. |
| `P:LibTmux.Window.Height` | `int LibTmux.Window.Height { get; }` | Public | No | Portable | Gets the captured Height value. |
| `P:LibTmux.Window.Hooks` | `TmuxHooks LibTmux.Window.Hooks { get; }` | Public | No | Portable | Gets the captured Hooks value. |
| `P:LibTmux.Window.Id` | `WindowId LibTmux.Window.Id { get; }` | Public | No | Portable | Gets the captured Id value. |
| `P:LibTmux.Window.Index` | `int LibTmux.Window.Index { get; }` | Public | No | Portable | Gets the captured Index value. |
| `P:LibTmux.Window.Layout` | `string LibTmux.Window.Layout { get; }` | Public | No | Portable | Gets the captured Layout value. |
| `P:LibTmux.Window.LinkedSessions` | `CapturedRelation<Session> LibTmux.Window.LinkedSessions { get; }` | Public | No | Portable | Gets the captured LinkedSessions value. |
| `P:LibTmux.Window.Name` | `string LibTmux.Window.Name { get; }` | Public | No | Portable | Gets the captured Name value. |
| `P:LibTmux.Window.Options` | `TmuxOptions LibTmux.Window.Options { get; }` | Public | No | Portable | Gets the captured Options value. |
| `P:LibTmux.Window.Panes` | `CapturedRelation<Pane> LibTmux.Window.Panes { get; }` | Public | No | Portable | Gets the captured Panes value. |
| `P:LibTmux.Window.RawFormatFields` | `IReadOnlyDictionary<string,string?> LibTmux.Window.RawFormatFields { get; }` | Public | No | Portable | Gets copied raw tmux format tokens captured for this snapshot. |
| `P:LibTmux.Window.Server` | `Server LibTmux.Window.Server { get; }` | Public | No | Portable | Gets the captured Server value. |
| `P:LibTmux.Window.Session` | `Session LibTmux.Window.Session { get; }` | Public | No | Portable | Gets the captured Session value. |
| `P:LibTmux.Window.Width` | `int LibTmux.Window.Width { get; }` | Public | No | Portable | Gets the captured Width value. |

### `T:LibTmux.WindowDirection`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `F:LibTmux.WindowDirection.After` | `After = 1` | Public | Implicit | Portable | The After value. Value: `1`. |
| `F:LibTmux.WindowDirection.Before` | `Before = 0` | Public | Implicit | Portable | The Before value. Value: `0`. |

### `T:LibTmux.WindowEntityKey`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.WindowEntityKey.#ctor(SessionId,WindowId)` | `WindowEntityKey(SessionId SessionId, WindowId WindowId)` | Public | No | Portable | Creates WindowEntityKey. |
| `P:LibTmux.WindowEntityKey.SessionId` | `SessionId LibTmux.WindowEntityKey.SessionId { get; init; }` | Public | No | Portable | Gets the session the window is linked into. |
| `P:LibTmux.WindowEntityKey.WindowId` | `WindowId LibTmux.WindowEntityKey.WindowId { get; init; }` | Public | No | Portable | Gets WindowId. |

### `T:LibTmux.WindowId`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.WindowId.#ctor(int)` | `WindowId(int value)` | Public | No | Portable | Creates a validated identifier. |
| `M:LibTmux.WindowId.CompareTo(WindowId)` | `int LibTmux.WindowId.CompareTo(WindowId other)` | Public | No | Portable | Orders this identifier against another numerically. |
| `M:LibTmux.WindowId.Parse(ReadOnlySpan<char>)` | `static static WindowId LibTmux.WindowId.Parse(ReadOnlySpan<char> text)` | Public | Yes | Portable | Parses a prefixed window identifier from a span. |
| `M:LibTmux.WindowId.Parse(string)` | `static WindowId LibTmux.WindowId.Parse(string text)` | Public | Yes | Portable | Parses a prefixed identifier. |
| `M:LibTmux.WindowId.ToString()` | `string LibTmux.WindowId.ToString()` | Public | No | Portable | Returns the canonical prefixed identifier. |
| `M:LibTmux.WindowId.TryParse(ReadOnlySpan<char>,WindowId)` | `static static bool LibTmux.WindowId.TryParse(ReadOnlySpan<char> text, out WindowId result)` | Public | Yes | Portable | Tries to parse a prefixed window identifier from a span. |
| `M:LibTmux.WindowId.TryParse(string?,WindowId)` | `static bool LibTmux.WindowId.TryParse(string? text, out WindowId result)` | Public | Yes | Portable | Tries to parse a prefixed identifier without throwing. |
| `M:LibTmux.WindowId.op_GreaterThan(WindowId,WindowId)` | `static bool operator >(WindowId left, WindowId right)` | Public | Yes | Portable | Compares the order two window identifiers were handed out in. |
| `M:LibTmux.WindowId.op_GreaterThanOrEqual(WindowId,WindowId)` | `static bool operator >=(WindowId left, WindowId right)` | Public | Yes | Portable | Compares the order two window identifiers were handed out in. |
| `M:LibTmux.WindowId.op_LessThan(WindowId,WindowId)` | `static bool operator <(WindowId left, WindowId right)` | Public | Yes | Portable | Compares the order two window identifiers were handed out in. |
| `M:LibTmux.WindowId.op_LessThanOrEqual(WindowId,WindowId)` | `static bool operator <=(WindowId left, WindowId right)` | Public | Yes | Portable | Compares the order two window identifiers were handed out in. |
| `P:LibTmux.WindowId.Value` | `int LibTmux.WindowId.Value { get; }` | Public | No | Portable | Gets the nonnegative numeric value. |

### `T:LibTmux.WindowResizeMode`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `F:LibTmux.WindowResizeMode.Expand` | `Expand = 0` | Public | Implicit | Portable | The Expand value. Value: `0`. |
| `F:LibTmux.WindowResizeMode.Shrink` | `Shrink = 1` | Public | Implicit | Portable | The Shrink value. Value: `1`. |

### `T:LibTmux.WindowRotationDirection`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `F:LibTmux.WindowRotationDirection.Down` | `Down = 1` | Public | Implicit | Portable | The Down value. Value: `1`. |
| `F:LibTmux.WindowRotationDirection.Up` | `Up = 0` | Public | Implicit | Portable | The Up value. Value: `0`. |

## Query boundary

`Matching()` consumes `IEnumerable<T>` and produces `IReadOnlyList<T>`.
The only Python-style edge lookup is `name__contains`. Cardinality uses
the BCL names `First`, `FirstOrDefault`, `Single`, `SingleOrDefault`,
`Any`, and `Count`. Safe planning and physical tmux mappings stay internal;
`UnsafeTmuxFilter` is the explicit native-semantics escape hatch.

## Ownership boundary

`Server`, `Session`, `Window`, `Pane`, and `Client` are borrowed immutable
handles and never perform destructive disposal. Only clearly named owned
or temporary scopes implement `IAsyncDisposable`. Cleanup is bounded,
idempotent, and its failures remain observable.

## Approved internal implementation contract

These internal types and members are frozen because parity rows and
component ownership depend on their exact typed boundaries.

| Type | Kind | Contract |
| --- | --- | --- |
| `T:LibTmux.Internal.CommandFlagCatalog` | static class | Maps closed option and direction values to tmux command arguments. |
| `T:LibTmux.Internal.FormatCatalog` | static class | Contains generated format, scope, and version metadata. |
| `T:LibTmux.Internal.FormatFieldDescriptor` | record | Describes one generated format field without runtime reflection. |
| `T:LibTmux.Internal.FormatProjection` | record | Defines one version-gated length-prefixed tmux projection. Behavior: {"emittedFieldCounts":{"list-clients":{"3.2a":146,"3.3a-3.6":150,"3.7a+":161},"list-panes":{"3.2a":123,"3.3a-3.6":125,"3.7a+":136},"list-sessions":{"3.2a":123,"3.3a-3.6":125,"3.7a+":136},"list-windows":{"3.2a":123,"3.3a-3.6":125,"3.7a+":136}},"framedFieldCount":"Fields.Count * 2"}. |
| `T:LibTmux.Internal.SeparatedRowFramer` | static class | Decodes separator-framed tmux fields without delimiter ambiguity. Validation: row := value{projection.Fields.Count}, each value terminated by FormatProjection.RowSeparator; wire names are not sent and values are read positionally from the same projection both ends build; every field is expanded exactly once, because a byte-count prefix would expand it a second time and a field that moved in between would desynchronise the payload; the separator is randomised per process so a caller-controlled name can neither contain nor predict it, and carries no '#' for tmux to expand; tmux LF separates rows and CRLF is accepted; a complete final row may end at EOF; embedded CR and LF remain value data; an empty value maps to null after Utf8BackslashDecoder, with its key present; maxFramedFieldBytes bounds one value; a row that ends before every field is read, a value that never closes, an oversized value, and a row not terminated by a newline each throw InvalidDataException; returned memories are copied. |
| `T:LibTmux.Internal.MaterializationContext` | class | Carries the owning server while generated rows are materialized. |
| `T:LibTmux.Internal.MaterializationQuery` | sealed class | Reads version-gated framed rows for materialization. Behavior: {"liveAcquisition":"reject unmaterialized MaterializationContext.Server.Generation","mismatch":"StaleServerGenerationException","requiredUniversalFields":["pid","start_time"],"rowValidation":"parse pid/start_time as ServerGeneration and require equality with MaterializationContext.Server.Generation"}. State: MaterializationContext. |
| `T:LibTmux.Internal.Materializer` | static class | Materializes generated format projections. Behavior: {"mismatch":"StaleServerGenerationException","requiredUniversalFields":["pid","start_time"],"rowValidation":"parse pid/start_time as ServerGeneration and require equality with MaterializationContext.Server.Generation"}. |
| `T:LibTmux.Internal.OptionFailure` | static class | Classifies option-command failures. |
| `T:LibTmux.Internal.OptionParser` | static class | Parses lossless scalar, sparse, and complex option values. |
| `T:LibTmux.Internal.ServerProjection` | static class | Defines the server-to-session materialization projection. |
| `T:LibTmux.Internal.ServerProjectionDescriptor` | record | Describes the typed server child identifier and format prefix. |
| `T:LibTmux.Internal.SessionName` | static class | Validates tmux session names. |
| `T:LibTmux.Internal.TmuxCommandContext` | class | Carries stable structured command context. |
| `T:LibTmux.Internal.TmuxCommandDispatcher` | class | Dispatches logical tmux commands through the internal transport. |
| `T:LibTmux.Internal.TmuxCommandFailure` | static class | Classifies typed tmux command failures. |
| `T:LibTmux.Internal.TmuxProcessTransport` | class | Runs one tmux client process per request. |

| Member ID | Declaration | Notes |
| --- | --- | --- |
| `M:LibTmux.Internal.CommandFlagCatalog.GetHookScopeFlag(OptionScope)` | `static string LibTmux.Internal.CommandFlagCatalog.GetHookScopeFlag(OptionScope scope)` | Gets the tmux hook-scope flag. |
| `M:LibTmux.Internal.CommandFlagCatalog.GetOptionScopeFlag(OptionScope)` | `static string LibTmux.Internal.CommandFlagCatalog.GetOptionScopeFlag(OptionScope scope)` | Gets the tmux option-scope flag. |
| `M:LibTmux.Internal.CommandFlagCatalog.GetPaneDirectionFlags(PaneDirection)` | `static IReadOnlyList<string> LibTmux.Internal.CommandFlagCatalog.GetPaneDirectionFlags(PaneDirection direction)` | Gets the tmux pane-direction flags. |
| `M:LibTmux.Internal.CommandFlagCatalog.GetResizeDirectionFlag(ResizeDirection)` | `static string LibTmux.Internal.CommandFlagCatalog.GetResizeDirectionFlag(ResizeDirection direction)` | Gets the tmux resize-direction flag. |
| `M:LibTmux.Internal.CommandFlagCatalog.GetWindowDirectionFlag(WindowDirection)` | `static string LibTmux.Internal.CommandFlagCatalog.GetWindowDirectionFlag(WindowDirection direction)` | Gets the tmux window-direction flag. |
| `M:LibTmux.Internal.FormatCatalog.GetMinimumTmuxVersion(string)` | `static TmuxVersion LibTmux.Internal.FormatCatalog.GetMinimumTmuxVersion(string wireName)` | Gets the first tmux version that defines one field. |
| `M:LibTmux.Internal.FormatCatalog.GetScopesForListCommand(string)` | `static IReadOnlySet<string> LibTmux.Internal.FormatCatalog.GetScopesForListCommand(string listCommand)` | Gets the format scopes available to one list command. |
| `M:LibTmux.Internal.FormatCatalog.Resolve(string)` | `static FormatFieldDescriptor LibTmux.Internal.FormatCatalog.Resolve(string wireName)` | Resolves one closed generated format field. |
| `M:LibTmux.Internal.FormatFieldDescriptor.#ctor(string,string,TmuxVersion,IReadOnlySet<string>)` | `FormatFieldDescriptor(string wireName, string clrMemberName, TmuxVersion minimumTmuxVersion, IReadOnlySet<string> scopes)` | Creates one generated format-field descriptor. |
| `M:LibTmux.Internal.FormatProjection.Create(string,TmuxVersion)` | `static FormatProjection LibTmux.Internal.FormatProjection.Create(string listCommand, TmuxVersion tmuxVersion)` | Creates the ordered supported projection for one list command and exact tmux version. |
| `M:LibTmux.Internal.SeparatedRowFramer.Decode(ReadOnlySpan<byte>)` | `static IReadOnlyList<ReadOnlyMemory<byte>> LibTmux.Internal.SeparatedRowFramer.Decode(ReadOnlySpan<byte> payload)` | Decodes one separator-framed row. |
| `M:LibTmux.Internal.SeparatedRowFramer.DecodeRows(ReadOnlySpan<byte>,int,int)` | `static IReadOnlyList<IReadOnlyDictionary<string,ReadOnlyMemory<byte>>> LibTmux.Internal.SeparatedRowFramer.DecodeRows(ReadOnlySpan<byte> payload, int expectedFieldCount, int maxFramedFieldBytes)` | Decodes copied raw field values from one or more complete separator-framed rows. Validation: row := value{projection.Fields.Count}, each value terminated by FormatProjection.RowSeparator; wire names are not sent and values are read positionally from the same projection both ends build; every field is expanded exactly once, because a byte-count prefix would expand it a second time and a field that moved in between would desynchronise the payload; the separator is randomised per process so a caller-controlled name can neither contain nor predict it, and carries no '#' for tmux to expand; tmux LF separates rows and CRLF is accepted; a complete final row may end at EOF; embedded CR and LF remain value data; an empty value maps to null after Utf8BackslashDecoder, with its key present; maxFramedFieldBytes bounds one value; a row that ends before every field is read, a value that never closes, an oversized value, and a row not terminated by a newline each throw InvalidDataException; returned memories are copied. |
| `M:LibTmux.Internal.MaterializationContext.#ctor(Server)` | `MaterializationContext(Server server)` | Creates materialization context for one owning server. |
| `M:LibTmux.Internal.MaterializationQuery.FetchAsync(string,IEnumerable<string>?,CancellationToken)` | `Task<IReadOnlyList<IReadOnlyDictionary<string,string?>>> LibTmux.Internal.MaterializationQuery.FetchAsync(string listCommand, IEnumerable<string>? extraArguments = null, CancellationToken cancellationToken = default)` | Acquires and decodes every version-gated row for one logical tmux list command. Behavior: {"projection":"FormatProjection.Create(listCommand, context.TmuxVersion)","rawValues":"Utf8BackslashDecoder after byte framing","result":"all decoded rows as copied dictionaries"}. Failure mapping: {"framing":"TmuxTransportException carrying logical tmux arguments","lowLevel":"InvalidDataException"}. |
| `M:LibTmux.Internal.MaterializationQuery.FetchOneAsync(string,string,string,TmuxTarget?,CancellationToken)` | `Task<IReadOnlyDictionary<string,string?>?> LibTmux.Internal.MaterializationQuery.FetchOneAsync(string listCommand, string idWireName, string identifier, TmuxTarget? inSession = null, CancellationToken cancellationToken = default)` | Reads one tmux entity without listing the server. Behavior: {"missingTarget":"a row that does not carry the identifier back returns null","read":"display-message -p -t target rendering the list command's projection","scoping":"a session-scoped target is read before the bare identifier","unreachableServer":"tmux or transport failure propagates distinctly"}. Failure mapping: {"framing":"TmuxTransportException carrying logical tmux arguments","lowLevel":"InvalidDataException"}. |
| `M:LibTmux.Internal.Materializer.MaterializeFormatFields(MaterializationContext,ReadOnlySpan<byte>)` | `static IReadOnlyDictionary<string,string?> LibTmux.Internal.Materializer.MaterializeFormatFields(MaterializationContext context, ReadOnlySpan<byte> payload)` | Materializes lossless format fields with explicit owner context. |
| `M:LibTmux.Internal.Materializer.MaterializePane(MaterializationContext,IReadOnlyDictionary<string,string?>)` | `static Pane LibTmux.Internal.Materializer.MaterializePane(MaterializationContext context, IReadOnlyDictionary<string,string?> fields)` | Materializes one pane projection dictionary with explicit owner context. |
| `M:LibTmux.Internal.Materializer.MaterializePane(MaterializationContext,ReadOnlySpan<byte>)` | `static Pane LibTmux.Internal.Materializer.MaterializePane(MaterializationContext context, ReadOnlySpan<byte> payload)` | Materializes one pane projection with explicit owner context. |
| `M:LibTmux.Internal.Materializer.MaterializeSession(MaterializationContext,IReadOnlyDictionary<string,string?>)` | `static Session LibTmux.Internal.Materializer.MaterializeSession(MaterializationContext context, IReadOnlyDictionary<string,string?> fields)` | Materializes one session projection dictionary with explicit owner context. |
| `M:LibTmux.Internal.Materializer.MaterializeSession(MaterializationContext,ReadOnlySpan<byte>)` | `static Session LibTmux.Internal.Materializer.MaterializeSession(MaterializationContext context, ReadOnlySpan<byte> payload)` | Materializes one session projection with explicit owner context. |
| `M:LibTmux.Internal.Materializer.MaterializeWindow(MaterializationContext,IReadOnlyDictionary<string,string?>)` | `static Window LibTmux.Internal.Materializer.MaterializeWindow(MaterializationContext context, IReadOnlyDictionary<string,string?> fields)` | Materializes one session-scoped window projection dictionary with explicit owner context. |
| `M:LibTmux.Internal.Materializer.MaterializeWindow(MaterializationContext,ReadOnlySpan<byte>)` | `static Window LibTmux.Internal.Materializer.MaterializeWindow(MaterializationContext context, ReadOnlySpan<byte> payload)` | Materializes one session-scoped window projection with explicit owner context. |
| `M:LibTmux.Internal.ServerProjectionDescriptor.#ctor(string,string)` | `ServerProjectionDescriptor(string childIdAttribute, string formatterPrefix)` | Creates the server child projection descriptor. |
| `M:LibTmux.Internal.OptionFailure.ThrowIfFailed(TmuxCommandResult,string)` | `static void LibTmux.Internal.OptionFailure.ThrowIfFailed(TmuxCommandResult result, string optionName)` | Throws a typed option exception for a failed result. |
| `M:LibTmux.Internal.OptionParser.ParseComplex(IReadOnlyList<TmuxOption>)` | `static IReadOnlyDictionary<string,object?> LibTmux.Internal.OptionParser.ParseComplex(IReadOnlyList<TmuxOption> options)` | Builds the typed compatibility view for complex options. |
| `M:LibTmux.Internal.OptionParser.ParseRows(IReadOnlyList<string>)` | `static IReadOnlyList<TmuxOption> LibTmux.Internal.OptionParser.ParseRows(IReadOnlyList<string> lines)` | Parses raw option rows. |
| `M:LibTmux.Internal.OptionParser.ParseSparse(IReadOnlyList<string>)` | `static IReadOnlyList<TmuxOption> LibTmux.Internal.OptionParser.ParseSparse(IReadOnlyList<string> rows)` | Parses sparse indexed option rows. |
| `M:LibTmux.Internal.OptionParser.ParseValue(string?)` | `static TmuxOptionValue LibTmux.Internal.OptionParser.ParseValue(string? value)` | Parses one lossless option value. |
| `M:LibTmux.Internal.OptionParser.ParseValues(IReadOnlyList<string?>)` | `static IReadOnlyList<TmuxOptionValue> LibTmux.Internal.OptionParser.ParseValues(IReadOnlyList<string?> values)` | Parses multiple lossless option values. |
| `M:LibTmux.Internal.SessionName.Validate(string?)` | `static string LibTmux.Internal.SessionName.Validate(string? name)` | Validates and returns one tmux session name. |
| `M:LibTmux.Internal.TmuxCommandDispatcher.ExecuteAsync(IReadOnlyList<string>,CancellationToken)` | `Task<TmuxCommandResult> LibTmux.Internal.TmuxCommandDispatcher.ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)` | Dispatches one logical tmux command. |
| `M:LibTmux.Internal.TmuxCommandFailure.ThrowIfFailed(TmuxCommandResult,string)` | `static void LibTmux.Internal.TmuxCommandFailure.ThrowIfFailed(TmuxCommandResult result, string operation)` | Throws a command-specific exception for a failed result. |
| `M:LibTmux.Internal.TmuxProcessTransport.ExecuteAsync(IReadOnlyList<string>,CancellationToken)` | `Task<TmuxCommandResult> LibTmux.Internal.TmuxProcessTransport.ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)` | Executes one raw-byte tmux request. |
| `P:LibTmux.Internal.CommandFlagCatalog.DefaultOptionScope` | `static OptionScope? LibTmux.Internal.CommandFlagCatalog.DefaultOptionScope { get; }` | Gets the null sentinel that selects an entity-bound default option scope. |
| `P:LibTmux.Internal.FormatCatalog.ClientProjection` | `static IReadOnlyList<FormatFieldDescriptor> LibTmux.Internal.FormatCatalog.ClientProjection { get; }` | Gets the ordered generated client field projection. |
| `P:LibTmux.Internal.FormatCatalog.ObjProjection` | `static IReadOnlyList<FormatFieldDescriptor> LibTmux.Internal.FormatCatalog.ObjProjection { get; }` | Gets the complete generated Python Obj field projection. Behavior: {"addedFieldCount":106,"combinedCatalogCount":188,"existingCatalogOverlapCount":72,"existingCatalogUnionCount":82,"minimumTmuxVersions":{"3.3":["client_uid","client_user","pane_dead_signal","pane_dead_time"],"3.7":["pane_flags","pane_floating_flag","pane_x","pane_y","pane_z","pane_zoomed_flag","pane_pb_progress","pane_pb_state","pane_pipe_pid","bracket_paste_flag","synchronized_output_flag"],"default":"3.2a"},"objFieldCount":178,"scopeCounts":{"buffer":3,"client":25,"context":5,"event":9,"pane":70,"session":23,"universal":9,"window":34}}. |
| `P:LibTmux.Internal.FormatCatalog.PaneProjection` | `static IReadOnlyList<FormatFieldDescriptor> LibTmux.Internal.FormatCatalog.PaneProjection { get; }` | Gets the ordered generated pane field projection. |
| `P:LibTmux.Internal.FormatCatalog.SessionProjection` | `static IReadOnlyList<FormatFieldDescriptor> LibTmux.Internal.FormatCatalog.SessionProjection { get; }` | Gets the ordered generated session field projection. |
| `P:LibTmux.Internal.FormatCatalog.WindowProjection` | `static IReadOnlyList<FormatFieldDescriptor> LibTmux.Internal.FormatCatalog.WindowProjection { get; }` | Gets the ordered generated window field projection. |
| `P:LibTmux.Internal.FormatFieldDescriptor.ClrMemberName` | `string LibTmux.Internal.FormatFieldDescriptor.ClrMemberName { get; }` | Gets the generated destination member name. |
| `P:LibTmux.Internal.FormatFieldDescriptor.MinimumTmuxVersion` | `TmuxVersion LibTmux.Internal.FormatFieldDescriptor.MinimumTmuxVersion { get; }` | Gets the minimum tmux version that defines the token. |
| `P:LibTmux.Internal.FormatFieldDescriptor.Scopes` | `IReadOnlySet<string> LibTmux.Internal.FormatFieldDescriptor.Scopes { get; }` | Gets the list-command scopes that can resolve the token. |
| `P:LibTmux.Internal.FormatFieldDescriptor.WireName` | `string LibTmux.Internal.FormatFieldDescriptor.WireName { get; }` | Gets the tmux format token. |
| `P:LibTmux.Internal.FormatProjection.Fields` | `IReadOnlyList<FormatFieldDescriptor> LibTmux.Internal.FormatProjection.Fields { get; }` | Gets the ordered fields supported by the selected tmux version. |
| `P:LibTmux.Internal.FormatProjection.FramedFieldCount` | `int LibTmux.Internal.FormatProjection.FramedFieldCount { get; }` | Gets twice Fields.Count for framed wire-name and value scalars. |
| `P:LibTmux.Internal.FormatProjection.TmuxFormat` | `string LibTmux.Internal.FormatProjection.TmuxFormat { get; }` | Gets the byte-length-framed tmux format expression. |
| `P:LibTmux.Internal.MaterializationContext.Server` | `Server LibTmux.Internal.MaterializationContext.Server { get; }` | Gets the server that owns a materialized handle. |
| `P:LibTmux.Internal.ServerProjection.Descriptor` | `static ServerProjectionDescriptor LibTmux.Internal.ServerProjection.Descriptor { get; }` | Gets the typed server child projection descriptor. |
| `P:LibTmux.Internal.ServerProjectionDescriptor.ChildIdAttribute` | `string LibTmux.Internal.ServerProjectionDescriptor.ChildIdAttribute { get; }` | Gets the typed child identifier attribute. |
| `P:LibTmux.Internal.ServerProjectionDescriptor.FormatterPrefix` | `string LibTmux.Internal.ServerProjectionDescriptor.FormatterPrefix { get; }` | Gets the format-prefix used for server children. |
| `P:LibTmux.Internal.TmuxCommandContext.Logger` | `ILogger LibTmux.Internal.TmuxCommandContext.Logger { get; }` | Gets the structured logger for one command context. |
