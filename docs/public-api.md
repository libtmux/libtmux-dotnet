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
| `LibTmux` | Microsoft.Extensions.Logging.Abstractions (centrally-managed) | hierarchy, values, query AST, local evaluator, testing |
| `LibTmux.Query.Json` | LibTmux (same) | System.Text.Json converters and source-generated context |

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
| `listFailurePolicy` | member-specific |
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
                new NewSessionRequest(name: "work"));
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
            new SendKeysRequest(text: "echo libtmux-$(printf %s ready)"));
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
| `T:LibTmux.AttachSessionRequest` | record | `public, sealed` | None | `object` | value | Parameters for AttachSession. | `LibTmux` |
| `T:LibTmux.BindKeyRequest` | record | `public, sealed` | None | `object` | value | Parameters for BindKey. | `LibTmux` |
| `T:LibTmux.CapturePanePosition` | readonly record struct | `public, readonly` | None | `ValueType` | value | A numeric capture line or the tmux hyphen boundary sentinel. | `LibTmux` |
| `T:LibTmux.CapturePaneRequest` | record | `public, sealed` | None | `object` | value | Parameters for CapturePane. | `LibTmux` |
| ``T:LibTmux.CapturedRelation`1`` | class | `public, sealed` | `IReadOnlyList<T>` | `object` | value | A copy-backed relation that distinguishes uncaptured from captured-empty. | `LibTmux` |
| `T:LibTmux.ChooseTreeRequest` | record | `public, sealed` | None | `object` | value | Parameters for ChooseTree. | `LibTmux` |
| `T:LibTmux.ChooseTreeSort` | enum | `public` | None | `Enum` | value | Defines ChooseTreeSort values. | `LibTmux` |
| `T:LibTmux.Client` | class | `public, sealed` | None | `object` | borrowed | An immutable client handle and snapshot. Equality: ServerGeneration and Name; Tty excluded. | `LibTmux` |
| `T:LibTmux.ClientAttachment` | record | `public, sealed` | None | `object` | value | A fresh client attachment resolution. | `LibTmux` |
| `T:LibTmux.CommandPromptRequest` | record | `public, sealed` | None | `object` | value | Parameters for CommandPrompt. | `LibTmux` |
| `T:LibTmux.ConfirmBeforeRequest` | record | `public, sealed` | None | `object` | value | Parameters for ConfirmBefore. | `LibTmux` |
| `T:LibTmux.CopyModeRequest` | record | `public, sealed` | None | `object` | value | Parameters for CopyMode. | `LibTmux` |
| `T:LibTmux.DisplayMenuRequest` | record | `public, sealed` | None | `object` | value | Parameters for DisplayMenu. | `LibTmux` |
| `T:LibTmux.DisplayMessageRequest` | record | `public, sealed` | None | `object` | value | Parameters for DisplayMessage. Validation: UpdatePane is valid only for pane-scoped execution. | `LibTmux` |
| `T:LibTmux.DisplayPopupRequest` | record | `public, sealed` | None | `object` | value | Parameters for DisplayPopup. | `LibTmux` |
| `T:LibTmux.FindWindowRequest` | record | `public, sealed` | None | `object` | value | Parameters for FindWindow. | `LibTmux` |
| `T:LibTmux.GetOptionRequest` | record | `public, sealed` | None | `object` | value | Parameters for GetOption. | `LibTmux` |
| `T:LibTmux.GetOptionsRequest` | record | `public, sealed` | None | `object` | value | Parameters for GetOptions. | `LibTmux` |
| `T:LibTmux.HookRequest` | record | `public, sealed` | None | `object` | value | Parameters for Hook. | `LibTmux` |
| `T:LibTmux.IfShellRequest` | record | `public, sealed` | None | `object` | value | Parameters for IfShell. | `LibTmux` |
| `T:LibTmux.IncompleteSnapshotException` | class | `public, sealed` | None | `InvalidOperationException` | value | Reports IncompleteSnapshot failure. State: RelationName. | `LibTmux` |
| `T:LibTmux.LibTmuxException` | class | `public` | None | `Exception` | value | Reports LibTmux failure. | `LibTmux` |
| `T:LibTmux.LibTmuxInfo` | static class | `public, static` | None | `object` | value | Reports package identity and supported tmux range. | `LibTmux` |
| `T:LibTmux.LinkWindowRequest` | record | `public, sealed` | None | `object` | value | Parameters for LinkWindow. | `LibTmux` |
| `T:LibTmux.ListBuffersRequest` | record | `public, sealed` | None | `object` | value | Parameters for ListBuffers. | `LibTmux` |
| `T:LibTmux.ListHooksRequest` | record | `public, sealed` | None | `object` | value | Parameters for ListHooks. | `LibTmux` |
| `T:LibTmux.MovePaneRequest` | record | `public, sealed` | None | `object` | value | Parameters for MovePane. | `LibTmux` |
| `T:LibTmux.MoveWindowRequest` | record | `public, sealed` | None | `object` | value | Parameters for MoveWindow. | `LibTmux` |
| `T:LibTmux.NewPaneRequest` | record | `public, sealed` | None | `object` | value | Parameters for NewPane. | `LibTmux` |
| `T:LibTmux.NewSessionRequest` | record | `public, sealed` | None | `object` | value | Parameters for NewSession. | `LibTmux` |
| `T:LibTmux.NewWindowRequest` | record | `public, sealed` | None | `object` | value | Parameters for NewWindow. | `LibTmux` |
| `T:LibTmux.OptionScope` | enum | `public` | None | `Enum` | value | Defines OptionScope values. | `LibTmux` |
| `T:LibTmux.OwnedServerScope` | class | `public, sealed` | `IAsyncDisposable` | `object` | owned | Owns a temporary server resource and bounded cleanup. | `LibTmux` |
| `T:LibTmux.OwnedSessionScope` | class | `public, sealed` | `IAsyncDisposable` | `object` | owned | Owns a temporary session resource and bounded cleanup. | `LibTmux` |
| `T:LibTmux.OwnedWindowScope` | class | `public, sealed` | `IAsyncDisposable` | `object` | owned | Owns a temporary window resource and bounded cleanup. | `LibTmux` |
| `T:LibTmux.Pane` | class | `public, sealed` | None | `object` | borrowed | An immutable pane handle and snapshot. Equality: ServerGeneration and PaneId. | `LibTmux` |
| `T:LibTmux.PaneDirection` | enum | `public` | None | `Enum` | value | Defines PaneDirection values. | `LibTmux` |
| `T:LibTmux.PaneId` | record struct | `public, readonly` | None | `ValueType` | value | A generation-independent tmux pane identifier. Identity: {"defaultIsValid":true,"minimum":0,"parseRejects":["null","malformed","negative","wrongPrefix"],"prefix":"%","tryParseFailure":"returns false and assigns default","valueType":"int"}. | `LibTmux` |
| `T:LibTmux.PaneInputMode` | enum | `public` | None | `Enum` | value | Defines PaneInputMode values. | `LibTmux` |
| `T:LibTmux.PaneSelectDirection` | enum | `public` | None | `Enum` | value | Defines PaneSelectDirection values. | `LibTmux` |
| `T:LibTmux.PaneSwapDirection` | enum | `public` | None | `Enum` | value | Defines PaneSwapDirection values. | `LibTmux` |
| `T:LibTmux.PasteBufferRequest` | record | `public, sealed` | None | `object` | value | Parameters for PasteBuffer. | `LibTmux` |
| `T:LibTmux.PipePaneRequest` | record | `public, sealed` | None | `object` | value | Parameters for PipePane. | `LibTmux` |
| `T:LibTmux.PopupCloseMode` | enum | `public` | None | `Enum` | value | Defines PopupCloseMode values. | `LibTmux` |
| `T:LibTmux.PromptType` | enum | `public` | None | `Enum` | value | Defines PromptType values. | `LibTmux` |
| `T:LibTmux.PsmuxCaptureOptions` | class | `public, sealed` | None | `object` | value | Typed capture choices audited for the psmux query preview. | `LibTmux` |
| `T:LibTmux.PsmuxConnectionOptions` | class | `public, sealed` | None | `object` | value | A pinned psmux client file and one isolated namespace. | `LibTmux` |
| `T:LibTmux.PsmuxPane` | class | `public, sealed` | None | `object` | value | An immutable pane observation from the psmux query preview. | `LibTmux` |
| `T:LibTmux.PsmuxServer` | class | `public, sealed` | None | `object` | reference | A query-only connection to one isolated psmux namespace. | `LibTmux` |
| `T:LibTmux.PsmuxSession` | class | `public, sealed` | None | `object` | value | An immutable observation of the sole psmux session. | `LibTmux` |
| `T:LibTmux.PsmuxWindow` | class | `public, sealed` | None | `object` | value | An immutable window observation from the psmux query preview. | `LibTmux` |
| `T:LibTmux.Query.AndNode` | record | `public, sealed` | None | `QueryNode` | value | A canonical and query node. Equality: structural ordered operand equality and hashing. | `LibTmux` |
| `T:LibTmux.Query.BooleanConstant` | record | `public, sealed` | None | `QueryConstant` | value | A canonical boolean constant. | `LibTmux` |
| `T:LibTmux.Query.ComparisonNode` | record | `public, sealed` | None | `QueryNode` | value | A canonical comparison query node. | `LibTmux` |
| `T:LibTmux.Query.ConstantNode` | record | `public, sealed` | None | `QueryNode` | value | A canonical constant query node. | `LibTmux` |
| `T:LibTmux.Query.FieldNode` | record | `public, sealed` | None | `QueryNode` | value | A canonical field query node. | `LibTmux` |
| `T:LibTmux.Query.Int64Constant` | record | `public, sealed` | None | `QueryConstant` | value | A canonical int64 constant. | `LibTmux` |
| `T:LibTmux.Query.Json.QueryJson` | static class | `public, static` | None | `object` | value | Serializes and parses v1 query documents. | `LibTmux.Query.Json` |
| `T:LibTmux.Query.Json.QueryJsonLimits` | record | `public, sealed` | None | `object` | value | Tightens the fixed v1 JSON resource ceilings. | `LibTmux.Query.Json` |
| `T:LibTmux.Query.NotNode` | record | `public, sealed` | None | `QueryNode` | value | A canonical not query node. | `LibTmux` |
| `T:LibTmux.Query.NullConstant` | record | `public, sealed` | None | `QueryConstant` | value | A canonical null constant. | `LibTmux` |
| `T:LibTmux.Query.OrNode` | record | `public, sealed` | None | `QueryNode` | value | A canonical or query node. Equality: structural ordered operand equality and hashing. | `LibTmux` |
| `T:LibTmux.Query.QuantifierNode` | record | `public, sealed` | None | `QueryNode` | value | A canonical quantifier query node. | `LibTmux` |
| `T:LibTmux.Query.QueryComparison` | enum | `public` | None | `Enum` | value | Defines QueryComparison values. | `LibTmux` |
| `T:LibTmux.Query.QueryConstant` | abstract record | `public, abstract` | None | `object` | value | The closed base type for query constants. | `LibTmux` |
| `T:LibTmux.Query.QueryDocument` | record | `public, sealed` | None | `object` | value | A versioned canonical query document. | `LibTmux` |
| `T:LibTmux.Query.QueryEdgeParser` | static class | `public, static` | None | `object` | value | Parses the one supported Python-style edge lookup. | `LibTmux` |
| `T:LibTmux.Query.QueryExtensions` | static class | `public, static` | None | `object` | value | Translates and evaluates closed snapshot queries. | `LibTmux` |
| `T:LibTmux.Query.QueryNode` | abstract record | `public, abstract` | None | `object` | value | The closed base type for canonical query nodes. | `LibTmux` |
| `T:LibTmux.Query.QueryQuantifier` | enum | `public` | None | `Enum` | value | Defines QueryQuantifier values. | `LibTmux` |
| `T:LibTmux.Query.QueryStringOperation` | enum | `public` | None | `Enum` | value | Defines QueryStringOperation values. | `LibTmux` |
| `T:LibTmux.Query.QueryTarget` | enum | `public` | None | `Enum` | value | Defines QueryTarget values. | `LibTmux` |
| `T:LibTmux.Query.RegexNode` | record | `public, sealed` | None | `QueryNode` | value | A canonical regex query node. | `LibTmux` |
| `T:LibTmux.Query.StringConstant` | record | `public, sealed` | None | `QueryConstant` | value | A canonical string constant. | `LibTmux` |
| `T:LibTmux.Query.StringNode` | record | `public, sealed` | None | `QueryNode` | value | A canonical string query node. | `LibTmux` |
| `T:LibTmux.Query.TypedIdConstant` | record | `public, sealed` | None | `QueryConstant` | value | A canonical typedid constant. | `LibTmux` |
| `T:LibTmux.ResizeDirection` | enum | `public` | None | `Enum` | value | Defines ResizeDirection values. | `LibTmux` |
| `T:LibTmux.ResizePaneRequest` | record | `public, sealed` | None | `object` | value | Parameters for ResizePane. Validation: exactly one primary resize mode; Direction requires Adjustment. | `LibTmux` |
| `T:LibTmux.ResizeWindowRequest` | record | `public, sealed` | None | `object` | value | Parameters for ResizeWindow. Validation: exactly one primary resize mode; Direction requires Adjustment. | `LibTmux` |
| `T:LibTmux.RespawnRequest` | record | `public, sealed` | None | `object` | value | Parameters for Respawn. | `LibTmux` |
| `T:LibTmux.RunShellRequest` | record | `public, sealed` | None | `object` | value | Parameters for RunShell. | `LibTmux` |
| `T:LibTmux.SelectLayoutMode` | enum | `public` | None | `Enum` | value | Defines SelectLayoutMode values. | `LibTmux` |
| `T:LibTmux.SelectLayoutRequest` | record | `public, sealed` | None | `object` | value | Parameters for SelectLayout. | `LibTmux` |
| `T:LibTmux.SelectPaneRequest` | record | `public, sealed` | None | `object` | value | Parameters for SelectPane. Validation: nullable Mark and InputEnabled preserve paired positive and negative flags. | `LibTmux` |
| `T:LibTmux.SendKeysRequest` | record | `public, sealed` | None | `object` | value | Parameters for SendKeys. | `LibTmux` |
| `T:LibTmux.Server` | class | `public, sealed` | None | `object` | borrowed | An immutable server handle and snapshot. Equality: normalized connection endpoint. | `LibTmux` |
| `T:LibTmux.ServerAccessRequest` | record | `public, sealed` | None | `object` | value | Parameters for ServerAccess. Validation: ReadOnly and ReadWrite are mutually exclusive. | `LibTmux` |
| `T:LibTmux.ServerConnectionOptions` | record | `public, sealed` | None | `object` | value | Configures a tmux server connection without mutating process-wide state. Endpoint precedence: SocketPath, SocketName, SocketNameFactory. | `LibTmux` |
| `T:LibTmux.ServerGeneration` | readonly record struct | `public, readonly` | None | `ValueType` | value | Identifies one tmux daemon generation. Validation: ProcessId and StartTime must both be positive; default is invalid. | `LibTmux` |
| `T:LibTmux.Session` | class | `public, sealed` | None | `object` | borrowed | An immutable session handle and snapshot. Equality: ServerGeneration and SessionId. | `LibTmux` |
| `T:LibTmux.SessionId` | record struct | `public, readonly` | None | `ValueType` | value | A generation-independent tmux session identifier. Identity: {"defaultIsValid":true,"minimum":0,"parseRejects":["null","malformed","negative","wrongPrefix"],"prefix":"$","tryParseFailure":"returns false and assigns default","valueType":"int"}. | `LibTmux` |
| `T:LibTmux.SessionWindowEdge` | record | `public, sealed` | None | `ValueType` | value | Identifies one session-to-window snapshot path. | `LibTmux` |
| `T:LibTmux.SetHookRequest` | record | `public, sealed` | None | `object` | value | Parameters for SetHook. | `LibTmux` |
| `T:LibTmux.SetHooksRequest` | record | `public, sealed` | None | `object` | value | Parameters for SetHooks. Validation: sparse hook indices are nonnegative and preserved. | `LibTmux` |
| `T:LibTmux.SetOptionRequest` | record | `public, sealed` | None | `object` | value | Parameters for SetOption. | `LibTmux` |
| `T:LibTmux.ShowMessagesMode` | enum | `public` | None | `Enum` | value | Defines ShowMessagesMode values. | `LibTmux` |
| `T:LibTmux.SnapshotDepth` | enum | `public` | None | `Enum` | value | Defines SnapshotDepth values. | `LibTmux` |
| `T:LibTmux.SplitPaneRequest` | record | `public, sealed` | None | `object` | value | Parameters for SplitPane. Validation: Size and Percentage are mutually exclusive. | `LibTmux` |
| `T:LibTmux.StaleServerGenerationException` | class | `public, sealed` | None | `InvalidOperationException` | value | Reports StaleServerGeneration failure. State: Expected, Actual. | `LibTmux` |
| `T:LibTmux.SwapPaneRequest` | record | `public, sealed` | None | `object` | value | Parameters for SwapPane. Validation: exactly one of Target or Direction. | `LibTmux` |
| `T:LibTmux.Testing.TemporaryHierarchyScope` | class | `public, sealed` | `IAsyncDisposable` | `object` | owned | Provides TemporaryHierarchyScope testing support. | `LibTmux` |
| `T:LibTmux.Testing.TemporaryServerScope` | class | `public, sealed` | `IAsyncDisposable` | `object` | owned | Provides TemporaryServerScope testing support. | `LibTmux` |
| `T:LibTmux.Testing.TemporarySessionScope` | class | `public, sealed` | `IAsyncDisposable` | `object` | owned | Provides TemporarySessionScope testing support. | `LibTmux` |
| `T:LibTmux.Testing.TemporaryWindowScope` | class | `public, sealed` | `IAsyncDisposable` | `object` | owned | Provides TemporaryWindowScope testing support. | `LibTmux` |
| `T:LibTmux.Testing.TestEnvironment` | record | `public, sealed` | None | `object` | value | Provides TestEnvironment testing support. | `LibTmux` |
| `T:LibTmux.Testing.TmuxNameGenerator` | class | `public, sealed` | None | `object` | value | Provides TmuxNameGenerator testing support. | `LibTmux` |
| `T:LibTmux.Testing.TmuxTestContext` | class | `public, sealed` | `IAsyncDisposable` | `object` | owned | Provides TmuxTestContext testing support. | `LibTmux` |
| `T:LibTmux.Testing.TmuxTestFactory` | class | `public, sealed` | None | `object` | value | Provides TmuxTestFactory testing support. | `LibTmux` |
| `T:LibTmux.Testing.TmuxTestOptions` | record | `public, sealed` | None | `object` | value | Provides TmuxTestOptions testing support. | `LibTmux` |
| `T:LibTmux.Testing.TmuxWait` | static class | `public, static` | None | `object` | value | Provides TmuxWait testing support. | `LibTmux` |
| `T:LibTmux.TmuxBuffer` | record | `public, sealed` | None | `object` | value | One tmux paste buffer snapshot. | `LibTmux` |
| `T:LibTmux.TmuxCleanupException` | class | `public, sealed` | None | `LibTmuxException` | value | Reports TmuxCleanup failure. State: OriginalCancellation, ClientProcessId, CleanupFailure. | `LibTmux` |
| `T:LibTmux.TmuxColorMode` | enum | `public` | None | `Enum` | value | Defines valid tmux color modes. Numeric value 1 is reserved; ServerConnectionOptions rejects undefined values with ArgumentOutOfRangeException. | `LibTmux` |
| `T:LibTmux.TmuxCommandException` | class | `public, sealed` | None | `LibTmuxException` | value | Reports TmuxCommand failure. State: Result. | `LibTmux` |
| `T:LibTmux.TmuxCommandNotFoundException` | class | `public, sealed` | None | `LibTmuxException` | value | Reports TmuxCommandNotFound failure. State: TmuxBinaryPath. | `LibTmux` |
| `T:LibTmux.TmuxCommandResult` | record | `public, sealed` | None | `object` | value | The complete inspectable result of one raw tmux command. | `LibTmux` |
| `T:LibTmux.TmuxDispatchState` | enum | `public` | None | `Enum` | value | Says whether a failed command reached tmux, which is what decides if retrying is safe. | `LibTmux` |
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
| `T:LibTmux.TmuxVersion` | record struct | `public, readonly` | `IComparable<TmuxVersion>` | `ValueType` | value | A lossless parsed tmux version with stable ordering semantics. Default value: {"comparison":"equality is valid; ordered comparison throws InvalidOperationException","isValid":false,"major":0,"minor":0,"raw":"","suffix":null,"toString":""}. | `LibTmux` |
| `T:LibTmux.TmuxVersionTooLowException` | class | `public, sealed` | None | `LibTmuxException` | value | Reports TmuxVersionTooLow failure. State: RequiredVersion, ActualVersion. | `LibTmux` |
| `T:LibTmux.TmuxWaitMode` | enum | `public` | None | `Enum` | value | Selects wait-for behavior. | `LibTmux` |
| `T:LibTmux.TmuxWaitTimeoutException` | class | `public, sealed` | None | `TimeoutException` | value | Reports TmuxWaitTimeout failure. State: Timeout. | `LibTmux` |
| `T:LibTmux.TmuxWindowException` | class | `public, sealed` | None | `LibTmuxException` | value | Reports TmuxWindow failure. State: WindowId. | `LibTmux` |
| `T:LibTmux.UnbindKeyRequest` | record | `public, sealed` | None | `object` | value | Parameters for UnbindKey. Validation: Key is required unless All is true. | `LibTmux` |
| `T:LibTmux.UnsafeTmuxFilter` | record | `public, sealed` | None | `object` | value | An explicitly unsafe native tmux filter with tmux-native semantics. | `LibTmux` |
| `T:LibTmux.UnsetOptionRequest` | record | `public, sealed` | None | `object` | value | Parameters for UnsetOption. | `LibTmux` |
| `T:LibTmux.UnsupportedQueryExpressionException` | class | `public, sealed` | None | `NotSupportedException` | value | Reports UnsupportedQueryExpression failure. State: Expression. | `LibTmux` |
| `T:LibTmux.WaitForRequest` | record | `public, sealed` | None | `object` | value | Parameters for WaitFor. | `LibTmux` |
| `T:LibTmux.Window` | class | `public, sealed` | None | `object` | borrowed | An immutable window handle and snapshot. Equality: ServerGeneration and WindowId; relation edge excluded. | `LibTmux` |
| `T:LibTmux.WindowDirection` | enum | `public` | None | `Enum` | value | Defines WindowDirection values. | `LibTmux` |
| `T:LibTmux.WindowEntityKey` | readonly record struct | `public, readonly` | None | `ValueType` | value | Defines equality for linked window views. | `LibTmux` |
| `T:LibTmux.WindowId` | record struct | `public, readonly` | None | `ValueType` | value | A generation-independent tmux window identifier. Identity: {"defaultIsValid":true,"minimum":0,"parseRejects":["null","malformed","negative","wrongPrefix"],"prefix":"@","tryParseFailure":"returns false and assigns default","valueType":"int"}. | `LibTmux` |
| `T:LibTmux.WindowResizeMode` | enum | `public` | None | `Enum` | value | Defines WindowResizeMode values. | `LibTmux` |
| `T:LibTmux.WindowRotationDirection` | enum | `public` | None | `Enum` | value | Defines WindowRotationDirection values. | `LibTmux` |
| `T:LibTmux.IControlModeSession` | interface | `public` | `System.IAsyncDisposable` | `None` | reference | A live tmux control client reporting what tmux does until disposed. | `LibTmux` |
| `T:LibTmux.ControlModeCommandException` | class | `public, sealed` | None | `LibTmuxException` | reference | Reports a command rejected by a live tmux control client. State: Command, OutputLines, ErrorLines. | `LibTmux` |
| `T:LibTmux.TmuxEvent` | record | `public, abstract` | None | `object` | value | One thing a tmux control client reported without being asked. | `LibTmux` |
| `T:LibTmux.TmuxEventsDroppedEvent` | record | `public, sealed` | None | `LibTmux.TmuxEvent` | value | A loss marker emitted when the bounded control-event buffer overflows. | `LibTmux` |
| `T:LibTmux.TmuxOutputEvent` | record | `public, sealed` | None | `LibTmux.TmuxEvent` | value | Bytes a pane wrote, with tmux's escaping decoded. | `LibTmux` |
| `T:LibTmux.TmuxNotificationEvent` | record | `public, sealed` | None | `LibTmux.TmuxEvent` | value | A tmux notification carried by name with its words unparsed. | `LibTmux` |
| `T:LibTmux.TmuxExitEvent` | record | `public, sealed` | None | `LibTmux.TmuxEvent` | value | The control client ended; always the last event in the stream. | `LibTmux` |
| `T:LibTmux.TmuxCommand` | record | `public, sealed` | None | `object` | value | One tmux command and the arguments it carries. | `LibTmux` |
| `T:LibTmux.TmuxChain` | class | `public, sealed` | None | `object` | reference | Commands tmux runs together, in one process. | `LibTmux` |
| `T:LibTmux.TmuxChaining` | class | `public, static` | None | `object` | value | Turns a request record into a command a chain can carry. | `LibTmux` |
| `T:LibTmux.TmuxWaitChannel` | class | `public, sealed` | `IAsyncDisposable` | `object` | owned | Holds a tmux wait-for registration across timed attempts. | `LibTmux` |

## Public members

### `T:LibTmux.AttachSessionRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.AttachSessionRequest.#ctor(string?,bool,bool,bool,IReadOnlyList<string>?)` | `AttachSessionRequest(string? target = null, bool detachOthers = false, bool readOnly = false, bool exitOnDetach = false, IReadOnlyList<string>? clientFlags = null)` | Public | No | Portable | Creates AttachSessionRequest. |
| `P:LibTmux.AttachSessionRequest.ClientFlags` | `IReadOnlyList<string>? LibTmux.AttachSessionRequest.ClientFlags { get; }` | Public | No | Portable | Gets ClientFlags. |
| `P:LibTmux.AttachSessionRequest.DetachOthers` | `bool LibTmux.AttachSessionRequest.DetachOthers { get; }` | Public | No | Portable | Gets DetachOthers. |
| `P:LibTmux.AttachSessionRequest.ExitOnDetach` | `bool LibTmux.AttachSessionRequest.ExitOnDetach { get; }` | Public | No | Portable | Gets ExitOnDetach. |
| `P:LibTmux.AttachSessionRequest.ReadOnly` | `bool LibTmux.AttachSessionRequest.ReadOnly { get; }` | Public | No | Portable | Gets ReadOnly. |
| `P:LibTmux.AttachSessionRequest.Target` | `string? LibTmux.AttachSessionRequest.Target { get; }` | Public | No | Portable | Gets Target. |

### `T:LibTmux.BindKeyRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.BindKeyRequest.#ctor(string,IReadOnlyList<string>,string?,string?,bool)` | `BindKeyRequest(string key, IReadOnlyList<string> command, string? keyTable = null, string? note = null, bool repeat = false)` | Public | No | Portable | Creates BindKeyRequest. |
| `P:LibTmux.BindKeyRequest.Command` | `IReadOnlyList<string> LibTmux.BindKeyRequest.Command { get; }` | Public | No | Portable | Gets Command. |
| `P:LibTmux.BindKeyRequest.Key` | `string LibTmux.BindKeyRequest.Key { get; }` | Public | No | Portable | Gets Key. |
| `P:LibTmux.BindKeyRequest.KeyTable` | `string? LibTmux.BindKeyRequest.KeyTable { get; }` | Public | No | Portable | Gets KeyTable. |
| `P:LibTmux.BindKeyRequest.Note` | `string? LibTmux.BindKeyRequest.Note { get; }` | Public | No | Portable | Gets Note. |
| `P:LibTmux.BindKeyRequest.Repeat` | `bool LibTmux.BindKeyRequest.Repeat { get; }` | Public | No | Portable | Gets Repeat. |

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
| `M:LibTmux.CapturePaneRequest.#ctor(CapturePanePosition?,CapturePanePosition?,bool,bool,bool,bool,bool,bool,bool,bool,bool,bool,bool,bool)` | `CapturePaneRequest(CapturePanePosition? startLine = null, CapturePanePosition? endLine = null, bool escapeSequences = false, bool escapeNonPrintable = false, bool joinWrappedLines = false, bool preserveTrailingSpaces = false, bool trimTrailingSpaces = false, bool alternateScreen = false, bool quiet = false, bool modeScreen = false, bool pending = false, bool hyperlinks = false, bool lineNumbers = false, bool lineFlags = false)` | Public | No | Portable | Creates CapturePaneRequest. |
| `P:LibTmux.CapturePaneRequest.AlternateScreen` | `bool LibTmux.CapturePaneRequest.AlternateScreen { get; }` | Public | No | Portable | Gets AlternateScreen. |
| `P:LibTmux.CapturePaneRequest.EndLine` | `CapturePanePosition? LibTmux.CapturePaneRequest.EndLine { get; }` | Public | No | Portable | Gets EndLine. |
| `P:LibTmux.CapturePaneRequest.EscapeNonPrintable` | `bool LibTmux.CapturePaneRequest.EscapeNonPrintable { get; }` | Public | No | Portable | Gets EscapeNonPrintable. |
| `P:LibTmux.CapturePaneRequest.EscapeSequences` | `bool LibTmux.CapturePaneRequest.EscapeSequences { get; }` | Public | No | Portable | Gets EscapeSequences. |
| `P:LibTmux.CapturePaneRequest.Hyperlinks` | `bool LibTmux.CapturePaneRequest.Hyperlinks { get; }` | Public | No | Portable | Gets Hyperlinks. |
| `P:LibTmux.CapturePaneRequest.JoinWrappedLines` | `bool LibTmux.CapturePaneRequest.JoinWrappedLines { get; }` | Public | No | Portable | Gets JoinWrappedLines. |
| `P:LibTmux.CapturePaneRequest.LineFlags` | `bool LibTmux.CapturePaneRequest.LineFlags { get; }` | Public | No | Portable | Gets LineFlags. |
| `P:LibTmux.CapturePaneRequest.LineNumbers` | `bool LibTmux.CapturePaneRequest.LineNumbers { get; }` | Public | No | Portable | Gets LineNumbers. |
| `P:LibTmux.CapturePaneRequest.ModeScreen` | `bool LibTmux.CapturePaneRequest.ModeScreen { get; }` | Public | No | Portable | Gets ModeScreen. |
| `P:LibTmux.CapturePaneRequest.Pending` | `bool LibTmux.CapturePaneRequest.Pending { get; }` | Public | No | Portable | Gets Pending. |
| `P:LibTmux.CapturePaneRequest.PreserveTrailingSpaces` | `bool LibTmux.CapturePaneRequest.PreserveTrailingSpaces { get; }` | Public | No | Portable | Gets PreserveTrailingSpaces. |
| `P:LibTmux.CapturePaneRequest.Quiet` | `bool LibTmux.CapturePaneRequest.Quiet { get; }` | Public | No | Portable | Gets Quiet. |
| `P:LibTmux.CapturePaneRequest.StartLine` | `CapturePanePosition? LibTmux.CapturePaneRequest.StartLine { get; }` | Public | No | Portable | Gets StartLine. |
| `P:LibTmux.CapturePaneRequest.TrimTrailingSpaces` | `bool LibTmux.CapturePaneRequest.TrimTrailingSpaces { get; }` | Public | No | Portable | Gets TrimTrailingSpaces. |

### ``T:LibTmux.CapturedRelation`1``

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| ``M:LibTmux.CapturedRelation`1.GetEnumerator()`` | `IEnumerator<T> LibTmux.CapturedRelation<T>.GetEnumerator()` | Public | No | Portable | Enumerates captured items or throws when uncaptured. |
| ``M:LibTmux.CapturedRelation`1.OrEmpty()`` | `IReadOnlyList<T> LibTmux.CapturedRelation<T>.OrEmpty()` | Public | No | Portable | Returns the captured items, or none when nothing was captured. |
| ``M:LibTmux.CapturedRelation`1.System.Collections.IEnumerable.GetEnumerator()`` | `IEnumerator System.Collections.IEnumerable.GetEnumerator()` | Explicit interface | No | Portable | Enumerates captured items through the non-generic interface. |
| ``P:LibTmux.CapturedRelation`1.Count`` | `int LibTmux.CapturedRelation<T>.Count { get; }` | Public | No | Portable | Gets the captured item count or throws when uncaptured. |
| ``P:LibTmux.CapturedRelation`1.IsCaptured`` | `bool LibTmux.CapturedRelation<T>.IsCaptured { get; }` | Public | No | Portable | Gets whether the relation was captured. |
| ``P:LibTmux.CapturedRelation`1.Item(int)`` | `T LibTmux.CapturedRelation<T>.this[int index] { get; }` | Public | No | Portable | Gets a captured item or throws when uncaptured. |

### `T:LibTmux.ChooseTreeRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.ChooseTreeRequest.#ctor(bool,bool,string?,UnsafeTmuxFilter?,ChooseTreeSort?,bool,bool)` | `ChooseTreeRequest(bool sessionsCollapsed = false, bool windowsCollapsed = false, string? format = null, UnsafeTmuxFilter? nativeFilter = null, ChooseTreeSort? sort = null, bool reverse = false, bool zoom = false)` | Public | No | Portable | Creates ChooseTreeRequest. |
| `P:LibTmux.ChooseTreeRequest.Format` | `string? LibTmux.ChooseTreeRequest.Format { get; }` | Public | No | Portable | Gets Format. |
| `P:LibTmux.ChooseTreeRequest.NativeFilter` | `UnsafeTmuxFilter? LibTmux.ChooseTreeRequest.NativeFilter { get; }` | Public | No | Portable | Gets NativeFilter. |
| `P:LibTmux.ChooseTreeRequest.Reverse` | `bool LibTmux.ChooseTreeRequest.Reverse { get; }` | Public | No | Portable | Gets Reverse. |
| `P:LibTmux.ChooseTreeRequest.SessionsCollapsed` | `bool LibTmux.ChooseTreeRequest.SessionsCollapsed { get; }` | Public | No | Portable | Gets SessionsCollapsed. |
| `P:LibTmux.ChooseTreeRequest.Sort` | `ChooseTreeSort? LibTmux.ChooseTreeRequest.Sort { get; }` | Public | No | Portable | Gets Sort. |
| `P:LibTmux.ChooseTreeRequest.WindowsCollapsed` | `bool LibTmux.ChooseTreeRequest.WindowsCollapsed { get; }` | Public | No | Portable | Gets WindowsCollapsed. |
| `P:LibTmux.ChooseTreeRequest.Zoom` | `bool LibTmux.ChooseTreeRequest.Zoom { get; }` | Public | No | Portable | Gets Zoom. |

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
| `P:LibTmux.ClientAttachment.Pane` | `Pane? LibTmux.ClientAttachment.Pane { get; }` | Public | No | Portable | Gets Pane. |
| `P:LibTmux.ClientAttachment.Session` | `Session? LibTmux.ClientAttachment.Session { get; }` | Public | No | Portable | Gets Session. |
| `P:LibTmux.ClientAttachment.Window` | `Window? LibTmux.ClientAttachment.Window { get; }` | Public | No | Portable | Gets Window. |

### `T:LibTmux.CommandPromptRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.CommandPromptRequest.#ctor(string,string?,string?,string?,bool,bool,bool,bool,PromptType?,bool,bool,bool,bool)` | `CommandPromptRequest(string template, string? prompt = null, string? inputs = null, string? targetClient = null, bool oneKey = false, bool keyOnly = false, bool onInputChange = false, bool numeric = false, PromptType? type = null, bool expandFormat = false, bool literal = false, bool backspaceExits = false, bool noFreeze = false)` | Public | No | Portable | Creates CommandPromptRequest. |
| `P:LibTmux.CommandPromptRequest.BackspaceExits` | `bool LibTmux.CommandPromptRequest.BackspaceExits { get; }` | Public | No | Portable | Gets BackspaceExits. |
| `P:LibTmux.CommandPromptRequest.ExpandFormat` | `bool LibTmux.CommandPromptRequest.ExpandFormat { get; }` | Public | No | Portable | Gets ExpandFormat. |
| `P:LibTmux.CommandPromptRequest.Inputs` | `string? LibTmux.CommandPromptRequest.Inputs { get; }` | Public | No | Portable | Gets Inputs. |
| `P:LibTmux.CommandPromptRequest.KeyOnly` | `bool LibTmux.CommandPromptRequest.KeyOnly { get; }` | Public | No | Portable | Gets KeyOnly. |
| `P:LibTmux.CommandPromptRequest.Literal` | `bool LibTmux.CommandPromptRequest.Literal { get; }` | Public | No | Portable | Gets Literal. |
| `P:LibTmux.CommandPromptRequest.NoFreeze` | `bool LibTmux.CommandPromptRequest.NoFreeze { get; }` | Public | No | Portable | Gets NoFreeze. |
| `P:LibTmux.CommandPromptRequest.Numeric` | `bool LibTmux.CommandPromptRequest.Numeric { get; }` | Public | No | Portable | Gets Numeric. |
| `P:LibTmux.CommandPromptRequest.OnInputChange` | `bool LibTmux.CommandPromptRequest.OnInputChange { get; }` | Public | No | Portable | Gets OnInputChange. |
| `P:LibTmux.CommandPromptRequest.OneKey` | `bool LibTmux.CommandPromptRequest.OneKey { get; }` | Public | No | Portable | Gets OneKey. |
| `P:LibTmux.CommandPromptRequest.Prompt` | `string? LibTmux.CommandPromptRequest.Prompt { get; }` | Public | No | Portable | Gets Prompt. |
| `P:LibTmux.CommandPromptRequest.TargetClient` | `string? LibTmux.CommandPromptRequest.TargetClient { get; }` | Public | No | Portable | Gets TargetClient. |
| `P:LibTmux.CommandPromptRequest.Template` | `string LibTmux.CommandPromptRequest.Template { get; }` | Public | No | Portable | Gets Template. |
| `P:LibTmux.CommandPromptRequest.Type` | `PromptType? LibTmux.CommandPromptRequest.Type { get; }` | Public | No | Portable | Gets Type. |

### `T:LibTmux.ConfirmBeforeRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.ConfirmBeforeRequest.#ctor(IReadOnlyList<string>,string?,string?,bool,string?)` | `ConfirmBeforeRequest(IReadOnlyList<string> command, string? prompt = null, string? confirmKey = null, bool defaultYes = false, string? targetClient = null)` | Public | No | Portable | Creates ConfirmBeforeRequest. |
| `P:LibTmux.ConfirmBeforeRequest.Command` | `IReadOnlyList<string> LibTmux.ConfirmBeforeRequest.Command { get; }` | Public | No | Portable | Gets Command. |
| `P:LibTmux.ConfirmBeforeRequest.ConfirmKey` | `string? LibTmux.ConfirmBeforeRequest.ConfirmKey { get; }` | Public | No | Portable | Gets ConfirmKey. |
| `P:LibTmux.ConfirmBeforeRequest.DefaultYes` | `bool LibTmux.ConfirmBeforeRequest.DefaultYes { get; }` | Public | No | Portable | Gets DefaultYes. |
| `P:LibTmux.ConfirmBeforeRequest.Prompt` | `string? LibTmux.ConfirmBeforeRequest.Prompt { get; }` | Public | No | Portable | Gets Prompt. |
| `P:LibTmux.ConfirmBeforeRequest.TargetClient` | `string? LibTmux.ConfirmBeforeRequest.TargetClient { get; }` | Public | No | Portable | Gets TargetClient. |

### `T:LibTmux.ControlModeCommandException`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.ControlModeCommandException.#ctor(string,TmuxCommand,System.Collections.Generic.IReadOnlyList{string},System.Collections.Generic.IReadOnlyList{string},Exception?)` | `ControlModeCommandException(string message, TmuxCommand command, IReadOnlyList<string> outputLines, IReadOnlyList<string> errorLines, Exception? innerException = null)` | Public | No | Portable | Initializes a control-mode command exception. |
| `P:LibTmux.ControlModeCommandException.Command` | `TmuxCommand LibTmux.ControlModeCommandException.Command { get; }` | Public | No | Portable | Gets the command tmux rejected. |
| `P:LibTmux.ControlModeCommandException.ErrorLines` | `IReadOnlyList<string> LibTmux.ControlModeCommandException.ErrorLines { get; }` | Public | No | Portable | Gets the error lines tmux reported. |
| `P:LibTmux.ControlModeCommandException.OutputLines` | `IReadOnlyList<string> LibTmux.ControlModeCommandException.OutputLines { get; }` | Public | No | Portable | Gets output produced before tmux rejected the command. |

### `T:LibTmux.CopyModeRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.CopyModeRequest.#ctor(bool,bool,bool,bool,bool,string?)` | `CopyModeRequest(bool scrollUp = false, bool exitOnBottom = false, bool mouseDrag = false, bool cancel = false, bool pageDown = false, string? sourcePane = null)` | Public | No | Portable | Creates CopyModeRequest. |
| `P:LibTmux.CopyModeRequest.Cancel` | `bool LibTmux.CopyModeRequest.Cancel { get; }` | Public | No | Portable | Gets Cancel. |
| `P:LibTmux.CopyModeRequest.ExitOnBottom` | `bool LibTmux.CopyModeRequest.ExitOnBottom { get; }` | Public | No | Portable | Gets ExitOnBottom. |
| `P:LibTmux.CopyModeRequest.MouseDrag` | `bool LibTmux.CopyModeRequest.MouseDrag { get; }` | Public | No | Portable | Gets MouseDrag. |
| `P:LibTmux.CopyModeRequest.PageDown` | `bool LibTmux.CopyModeRequest.PageDown { get; }` | Public | No | Portable | Gets PageDown. |
| `P:LibTmux.CopyModeRequest.ScrollUp` | `bool LibTmux.CopyModeRequest.ScrollUp { get; }` | Public | No | Portable | Gets ScrollUp. |
| `P:LibTmux.CopyModeRequest.SourcePane` | `string? LibTmux.CopyModeRequest.SourcePane { get; }` | Public | No | Portable | Gets SourcePane. |

### `T:LibTmux.DisplayMenuRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.DisplayMenuRequest.#ctor(IReadOnlyList<TmuxMenuItem>,string?,string?,string?,string?,string?,string?,string?,string?,string?,string?,bool,bool)` | `DisplayMenuRequest(IReadOnlyList<TmuxMenuItem> items, string? title = null, string? targetPane = null, string? targetClient = null, string? x = null, string? y = null, string? startingChoice = null, string? borderLines = null, string? style = null, string? borderStyle = null, string? selectedStyle = null, bool mouse = false, bool stayOpen = false)` | Public | No | Portable | Creates DisplayMenuRequest. |
| `P:LibTmux.DisplayMenuRequest.BorderLines` | `string? LibTmux.DisplayMenuRequest.BorderLines { get; }` | Public | No | Portable | Gets BorderLines. |
| `P:LibTmux.DisplayMenuRequest.BorderStyle` | `string? LibTmux.DisplayMenuRequest.BorderStyle { get; }` | Public | No | Portable | Gets BorderStyle. |
| `P:LibTmux.DisplayMenuRequest.Items` | `IReadOnlyList<TmuxMenuItem> LibTmux.DisplayMenuRequest.Items { get; }` | Public | No | Portable | Gets Items. |
| `P:LibTmux.DisplayMenuRequest.Mouse` | `bool LibTmux.DisplayMenuRequest.Mouse { get; }` | Public | No | Portable | Gets Mouse. |
| `P:LibTmux.DisplayMenuRequest.SelectedStyle` | `string? LibTmux.DisplayMenuRequest.SelectedStyle { get; }` | Public | No | Portable | Gets SelectedStyle. |
| `P:LibTmux.DisplayMenuRequest.StartingChoice` | `string? LibTmux.DisplayMenuRequest.StartingChoice { get; }` | Public | No | Portable | Gets StartingChoice. |
| `P:LibTmux.DisplayMenuRequest.StayOpen` | `bool LibTmux.DisplayMenuRequest.StayOpen { get; }` | Public | No | Portable | Gets StayOpen. |
| `P:LibTmux.DisplayMenuRequest.Style` | `string? LibTmux.DisplayMenuRequest.Style { get; }` | Public | No | Portable | Gets Style. |
| `P:LibTmux.DisplayMenuRequest.TargetClient` | `string? LibTmux.DisplayMenuRequest.TargetClient { get; }` | Public | No | Portable | Gets TargetClient. |
| `P:LibTmux.DisplayMenuRequest.TargetPane` | `string? LibTmux.DisplayMenuRequest.TargetPane { get; }` | Public | No | Portable | Gets TargetPane. |
| `P:LibTmux.DisplayMenuRequest.Title` | `string? LibTmux.DisplayMenuRequest.Title { get; }` | Public | No | Portable | Gets Title. |
| `P:LibTmux.DisplayMenuRequest.X` | `string? LibTmux.DisplayMenuRequest.X { get; }` | Public | No | Portable | Gets X. |
| `P:LibTmux.DisplayMenuRequest.Y` | `string? LibTmux.DisplayMenuRequest.Y { get; }` | Public | No | Portable | Gets Y. |

### `T:LibTmux.DisplayMessageRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.DisplayMessageRequest.#ctor(string,bool,string?,bool,bool,bool,string?,TimeSpan?,bool,bool)` | `DisplayMessageRequest(string message = "", bool returnText = false, string? format = null, bool allFormats = false, bool verbose = false, bool noExpand = false, string? targetClient = null, TimeSpan? delay = null, bool notify = false, bool updatePane = false)` | Public | No | Portable | Creates DisplayMessageRequest. |
| `P:LibTmux.DisplayMessageRequest.AllFormats` | `bool LibTmux.DisplayMessageRequest.AllFormats { get; }` | Public | No | Portable | Gets AllFormats. |
| `P:LibTmux.DisplayMessageRequest.Delay` | `TimeSpan? LibTmux.DisplayMessageRequest.Delay { get; }` | Public | No | Portable | Gets Delay. |
| `P:LibTmux.DisplayMessageRequest.Format` | `string? LibTmux.DisplayMessageRequest.Format { get; }` | Public | No | Portable | Gets Format. |
| `P:LibTmux.DisplayMessageRequest.Message` | `string LibTmux.DisplayMessageRequest.Message { get; }` | Public | No | Portable | Gets Message. |
| `P:LibTmux.DisplayMessageRequest.NoExpand` | `bool LibTmux.DisplayMessageRequest.NoExpand { get; }` | Public | No | Portable | Gets NoExpand. |
| `P:LibTmux.DisplayMessageRequest.Notify` | `bool LibTmux.DisplayMessageRequest.Notify { get; }` | Public | No | Portable | Gets Notify. |
| `P:LibTmux.DisplayMessageRequest.ReturnText` | `bool LibTmux.DisplayMessageRequest.ReturnText { get; }` | Public | No | Portable | Gets ReturnText. |
| `P:LibTmux.DisplayMessageRequest.TargetClient` | `string? LibTmux.DisplayMessageRequest.TargetClient { get; }` | Public | No | Portable | Gets TargetClient. |
| `P:LibTmux.DisplayMessageRequest.UpdatePane` | `bool LibTmux.DisplayMessageRequest.UpdatePane { get; }` | Public | No | Portable | Gets UpdatePane. |
| `P:LibTmux.DisplayMessageRequest.Verbose` | `bool LibTmux.DisplayMessageRequest.Verbose { get; }` | Public | No | Portable | Gets Verbose. |

### `T:LibTmux.DisplayPopupRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.DisplayPopupRequest.#ctor(string?,PopupCloseMode?,bool,string?,string?,string?,string?,string?,string?,string?,string?,string?,string?,IReadOnlyDictionary<string,string>?,bool,bool,bool)` | `DisplayPopupRequest(string? command = null, PopupCloseMode? closeMode = null, bool closeExisting = false, string? targetClient = null, string? width = null, string? height = null, string? x = null, string? y = null, string? startDirectory = null, string? title = null, string? borderLines = null, string? style = null, string? borderStyle = null, IReadOnlyDictionary<string,string>? environment = null, bool noBorder = false, bool closeOnAnyKey = false, bool noKeys = false)` | Public | No | Portable | Creates DisplayPopupRequest. |
| `P:LibTmux.DisplayPopupRequest.BorderLines` | `string? LibTmux.DisplayPopupRequest.BorderLines { get; }` | Public | No | Portable | Gets BorderLines. |
| `P:LibTmux.DisplayPopupRequest.BorderStyle` | `string? LibTmux.DisplayPopupRequest.BorderStyle { get; }` | Public | No | Portable | Gets BorderStyle. |
| `P:LibTmux.DisplayPopupRequest.CloseExisting` | `bool LibTmux.DisplayPopupRequest.CloseExisting { get; }` | Public | No | Portable | Gets CloseExisting. |
| `P:LibTmux.DisplayPopupRequest.CloseMode` | `PopupCloseMode? LibTmux.DisplayPopupRequest.CloseMode { get; }` | Public | No | Portable | Gets CloseMode. |
| `P:LibTmux.DisplayPopupRequest.CloseOnAnyKey` | `bool LibTmux.DisplayPopupRequest.CloseOnAnyKey { get; }` | Public | No | Portable | Gets CloseOnAnyKey. |
| `P:LibTmux.DisplayPopupRequest.Command` | `string? LibTmux.DisplayPopupRequest.Command { get; }` | Public | No | Portable | Gets Command. |
| `P:LibTmux.DisplayPopupRequest.Environment` | `IReadOnlyDictionary<string,string>? LibTmux.DisplayPopupRequest.Environment { get; }` | Public | No | Portable | Gets Environment. |
| `P:LibTmux.DisplayPopupRequest.Height` | `string? LibTmux.DisplayPopupRequest.Height { get; }` | Public | No | Portable | Gets Height. |
| `P:LibTmux.DisplayPopupRequest.NoBorder` | `bool LibTmux.DisplayPopupRequest.NoBorder { get; }` | Public | No | Portable | Gets NoBorder. |
| `P:LibTmux.DisplayPopupRequest.NoKeys` | `bool LibTmux.DisplayPopupRequest.NoKeys { get; }` | Public | No | Portable | Gets NoKeys. |
| `P:LibTmux.DisplayPopupRequest.StartDirectory` | `string? LibTmux.DisplayPopupRequest.StartDirectory { get; }` | Public | No | Portable | Gets StartDirectory. |
| `P:LibTmux.DisplayPopupRequest.Style` | `string? LibTmux.DisplayPopupRequest.Style { get; }` | Public | No | Portable | Gets Style. |
| `P:LibTmux.DisplayPopupRequest.TargetClient` | `string? LibTmux.DisplayPopupRequest.TargetClient { get; }` | Public | No | Portable | Gets TargetClient. |
| `P:LibTmux.DisplayPopupRequest.Title` | `string? LibTmux.DisplayPopupRequest.Title { get; }` | Public | No | Portable | Gets Title. |
| `P:LibTmux.DisplayPopupRequest.Width` | `string? LibTmux.DisplayPopupRequest.Width { get; }` | Public | No | Portable | Gets Width. |
| `P:LibTmux.DisplayPopupRequest.X` | `string? LibTmux.DisplayPopupRequest.X { get; }` | Public | No | Portable | Gets X. |
| `P:LibTmux.DisplayPopupRequest.Y` | `string? LibTmux.DisplayPopupRequest.Y { get; }` | Public | No | Portable | Gets Y. |

### `T:LibTmux.FindWindowRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.FindWindowRequest.#ctor(string,bool,bool,bool,bool,bool)` | `FindWindowRequest(string pattern, bool matchContent = false, bool ignoreCase = false, bool matchName = false, bool regex = false, bool matchTitle = false)` | Public | No | Portable | Creates FindWindowRequest. |
| `P:LibTmux.FindWindowRequest.IgnoreCase` | `bool LibTmux.FindWindowRequest.IgnoreCase { get; }` | Public | No | Portable | Gets IgnoreCase. |
| `P:LibTmux.FindWindowRequest.MatchContent` | `bool LibTmux.FindWindowRequest.MatchContent { get; }` | Public | No | Portable | Gets MatchContent. |
| `P:LibTmux.FindWindowRequest.MatchName` | `bool LibTmux.FindWindowRequest.MatchName { get; }` | Public | No | Portable | Gets MatchName. |
| `P:LibTmux.FindWindowRequest.MatchTitle` | `bool LibTmux.FindWindowRequest.MatchTitle { get; }` | Public | No | Portable | Gets MatchTitle. |
| `P:LibTmux.FindWindowRequest.Pattern` | `string LibTmux.FindWindowRequest.Pattern { get; }` | Public | No | Portable | Gets Pattern. |
| `P:LibTmux.FindWindowRequest.Regex` | `bool LibTmux.FindWindowRequest.Regex { get; }` | Public | No | Portable | Gets Regex. |

### `T:LibTmux.GetOptionRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.GetOptionRequest.#ctor(string,OptionScope?,bool,bool,bool,bool)` | `GetOptionRequest(string name, OptionScope? scope = null, bool global = false, bool includeHooks = false, bool includeInherited = false, bool quiet = false)` | Public | No | Portable | Creates GetOptionRequest. |
| `P:LibTmux.GetOptionRequest.Global` | `bool LibTmux.GetOptionRequest.Global { get; }` | Public | No | Portable | Gets Global. |
| `P:LibTmux.GetOptionRequest.IncludeHooks` | `bool LibTmux.GetOptionRequest.IncludeHooks { get; }` | Public | No | Portable | Gets IncludeHooks. |
| `P:LibTmux.GetOptionRequest.IncludeInherited` | `bool LibTmux.GetOptionRequest.IncludeInherited { get; }` | Public | No | Portable | Gets IncludeInherited. |
| `P:LibTmux.GetOptionRequest.Name` | `string LibTmux.GetOptionRequest.Name { get; }` | Public | No | Portable | Gets Name. |
| `P:LibTmux.GetOptionRequest.Quiet` | `bool LibTmux.GetOptionRequest.Quiet { get; }` | Public | No | Portable | Gets Quiet. |
| `P:LibTmux.GetOptionRequest.Scope` | `OptionScope? LibTmux.GetOptionRequest.Scope { get; }` | Public | No | Portable | Gets Scope. |

### `T:LibTmux.GetOptionsRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.GetOptionsRequest.#ctor(OptionScope?,bool,bool,bool,bool)` | `GetOptionsRequest(OptionScope? scope = null, bool global = false, bool includeHooks = false, bool includeInherited = false, bool quiet = false)` | Public | No | Portable | Creates GetOptionsRequest. |
| `P:LibTmux.GetOptionsRequest.Global` | `bool LibTmux.GetOptionsRequest.Global { get; }` | Public | No | Portable | Gets Global. |
| `P:LibTmux.GetOptionsRequest.IncludeHooks` | `bool LibTmux.GetOptionsRequest.IncludeHooks { get; }` | Public | No | Portable | Gets IncludeHooks. |
| `P:LibTmux.GetOptionsRequest.IncludeInherited` | `bool LibTmux.GetOptionsRequest.IncludeInherited { get; }` | Public | No | Portable | Gets IncludeInherited. |
| `P:LibTmux.GetOptionsRequest.Quiet` | `bool LibTmux.GetOptionsRequest.Quiet { get; }` | Public | No | Portable | Gets Quiet. |
| `P:LibTmux.GetOptionsRequest.Scope` | `OptionScope? LibTmux.GetOptionsRequest.Scope { get; }` | Public | No | Portable | Gets Scope. |

### `T:LibTmux.HookRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.HookRequest.#ctor(string,OptionScope?,bool)` | `HookRequest(string name, OptionScope? scope = null, bool global = false)` | Public | No | Portable | Creates HookRequest. |
| `P:LibTmux.HookRequest.Global` | `bool LibTmux.HookRequest.Global { get; }` | Public | No | Portable | Gets Global. |
| `P:LibTmux.HookRequest.Name` | `string LibTmux.HookRequest.Name { get; }` | Public | No | Portable | Gets Name. |
| `P:LibTmux.HookRequest.Scope` | `OptionScope? LibTmux.HookRequest.Scope { get; }` | Public | No | Portable | Gets Scope. |

### `T:LibTmux.IControlModeSession`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.IControlModeSession.SendAsync(LibTmux.TmuxCommand,System.Threading.CancellationToken)` | `Task<IReadOnlyList<string>> SendAsync(TmuxCommand command, CancellationToken cancellationToken = default)` | Public | No | Portable | Runs one command on this client and reads what it answered. |
| `P:LibTmux.IControlModeSession.Events` | `IAsyncEnumerable<TmuxEvent> LibTmux.IControlModeSession.Events { get; }` | Public | No | Portable | Reads what tmux reports for as long as the client runs. |
| `P:LibTmux.IControlModeSession.IsRunning` | `bool LibTmux.IControlModeSession.IsRunning { get; }` | Public | No | Portable | Gets whether the client is still running. |

### `T:LibTmux.IfShellRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.IfShellRequest.#ctor(string,IReadOnlyList<string>,IReadOnlyList<string>?,bool,string?)` | `IfShellRequest(string shellCommand, IReadOnlyList<string> thenCommand, IReadOnlyList<string>? elseCommand = null, bool background = false, string? targetPane = null)` | Public | No | Portable | Creates IfShellRequest. |
| `P:LibTmux.IfShellRequest.Background` | `bool LibTmux.IfShellRequest.Background { get; }` | Public | No | Portable | Gets Background. |
| `P:LibTmux.IfShellRequest.ElseCommand` | `IReadOnlyList<string>? LibTmux.IfShellRequest.ElseCommand { get; }` | Public | No | Portable | Gets ElseCommand. |
| `P:LibTmux.IfShellRequest.ShellCommand` | `string LibTmux.IfShellRequest.ShellCommand { get; }` | Public | No | Portable | Gets ShellCommand. |
| `P:LibTmux.IfShellRequest.TargetPane` | `string? LibTmux.IfShellRequest.TargetPane { get; }` | Public | No | Portable | Gets TargetPane. |
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
| `M:LibTmux.LinkWindowRequest.#ctor(string,string?,WindowDirection?,bool,bool)` | `LinkWindowRequest(string targetSession, string? targetIndex = null, WindowDirection? direction = null, bool replaceExisting = false, bool detach = false)` | Public | No | Portable | Creates LinkWindowRequest. |
| `P:LibTmux.LinkWindowRequest.Detach` | `bool LibTmux.LinkWindowRequest.Detach { get; }` | Public | No | Portable | Gets Detach. |
| `P:LibTmux.LinkWindowRequest.Direction` | `WindowDirection? LibTmux.LinkWindowRequest.Direction { get; }` | Public | No | Portable | Gets Direction. |
| `P:LibTmux.LinkWindowRequest.ReplaceExisting` | `bool LibTmux.LinkWindowRequest.ReplaceExisting { get; }` | Public | No | Portable | Gets ReplaceExisting. |
| `P:LibTmux.LinkWindowRequest.TargetIndex` | `string? LibTmux.LinkWindowRequest.TargetIndex { get; }` | Public | No | Portable | Gets TargetIndex. |
| `P:LibTmux.LinkWindowRequest.TargetSession` | `string LibTmux.LinkWindowRequest.TargetSession { get; }` | Public | No | Portable | Gets TargetSession. |

### `T:LibTmux.ListBuffersRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.ListBuffersRequest.#ctor(string?,UnsafeTmuxFilter?)` | `ListBuffersRequest(string? format = null, UnsafeTmuxFilter? filter = null)` | Public | No | Portable | Creates ListBuffersRequest. |
| `P:LibTmux.ListBuffersRequest.Filter` | `UnsafeTmuxFilter? LibTmux.ListBuffersRequest.Filter { get; }` | Public | No | Portable | Gets Filter. |
| `P:LibTmux.ListBuffersRequest.Format` | `string? LibTmux.ListBuffersRequest.Format { get; }` | Public | No | Portable | Gets Format. |

### `T:LibTmux.ListHooksRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.ListHooksRequest.#ctor(OptionScope?,bool)` | `ListHooksRequest(OptionScope? scope = null, bool global = false)` | Public | No | Portable | Creates ListHooksRequest. |
| `P:LibTmux.ListHooksRequest.Global` | `bool LibTmux.ListHooksRequest.Global { get; }` | Public | No | Portable | Gets Global. |
| `P:LibTmux.ListHooksRequest.Scope` | `OptionScope? LibTmux.ListHooksRequest.Scope { get; }` | Public | No | Portable | Gets Scope. |

### `T:LibTmux.MovePaneRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.MovePaneRequest.#ctor(string,PaneDirection,string?,bool,bool,bool)` | `MovePaneRequest(string target, PaneDirection direction = PaneDirection.Below, string? size = null, bool detach = true, bool fullWindow = false, bool before = false)` | Public | No | Portable | Creates MovePaneRequest. |
| `P:LibTmux.MovePaneRequest.Before` | `bool LibTmux.MovePaneRequest.Before { get; }` | Public | No | Portable | Gets Before. |
| `P:LibTmux.MovePaneRequest.Detach` | `bool LibTmux.MovePaneRequest.Detach { get; }` | Public | No | Portable | Gets Detach. |
| `P:LibTmux.MovePaneRequest.Direction` | `PaneDirection LibTmux.MovePaneRequest.Direction { get; }` | Public | No | Portable | Gets Direction. |
| `P:LibTmux.MovePaneRequest.FullWindow` | `bool LibTmux.MovePaneRequest.FullWindow { get; }` | Public | No | Portable | Gets FullWindow. |
| `P:LibTmux.MovePaneRequest.Size` | `string? LibTmux.MovePaneRequest.Size { get; }` | Public | No | Portable | Gets Size. |
| `P:LibTmux.MovePaneRequest.Target` | `string LibTmux.MovePaneRequest.Target { get; }` | Public | No | Portable | Gets Target. |

### `T:LibTmux.MoveWindowRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.MoveWindowRequest.#ctor(string,string?,WindowDirection?,bool,bool,bool)` | `MoveWindowRequest(string destination = "", string? session = null, WindowDirection? direction = null, bool noSelect = false, bool replaceExisting = false, bool renumber = false)` | Public | No | Portable | Creates MoveWindowRequest. |
| `P:LibTmux.MoveWindowRequest.Destination` | `string LibTmux.MoveWindowRequest.Destination { get; }` | Public | No | Portable | Gets Destination. |
| `P:LibTmux.MoveWindowRequest.Direction` | `WindowDirection? LibTmux.MoveWindowRequest.Direction { get; }` | Public | No | Portable | Gets Direction. |
| `P:LibTmux.MoveWindowRequest.NoSelect` | `bool LibTmux.MoveWindowRequest.NoSelect { get; }` | Public | No | Portable | Gets NoSelect. |
| `P:LibTmux.MoveWindowRequest.Renumber` | `bool LibTmux.MoveWindowRequest.Renumber { get; }` | Public | No | Portable | Gets Renumber. |
| `P:LibTmux.MoveWindowRequest.ReplaceExisting` | `bool LibTmux.MoveWindowRequest.ReplaceExisting { get; }` | Public | No | Portable | Gets ReplaceExisting. |
| `P:LibTmux.MoveWindowRequest.Session` | `string? LibTmux.MoveWindowRequest.Session { get; }` | Public | No | Portable | Gets Session. |

### `T:LibTmux.NewPaneRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.NewPaneRequest.#ctor(string?,string?,bool,string?,IReadOnlyDictionary<string,string>?,int?,int?,int?,int?,bool,bool,string?,string?,string?,string?,bool)` | `NewPaneRequest(string? target = null, string? startDirectory = null, bool attach = false, string? command = null, IReadOnlyDictionary<string,string>? environment = null, int? width = null, int? height = null, int? x = null, int? y = null, bool zoom = false, bool empty = false, string? style = null, string? activeBorderStyle = null, string? inactiveBorderStyle = null, string? message = null, bool keepOpen = false)` | Public | No | Portable | Creates NewPaneRequest. |
| `P:LibTmux.NewPaneRequest.ActiveBorderStyle` | `string? LibTmux.NewPaneRequest.ActiveBorderStyle { get; }` | Public | No | Portable | Gets ActiveBorderStyle. |
| `P:LibTmux.NewPaneRequest.Attach` | `bool LibTmux.NewPaneRequest.Attach { get; }` | Public | No | Portable | Gets Attach. |
| `P:LibTmux.NewPaneRequest.Command` | `string? LibTmux.NewPaneRequest.Command { get; }` | Public | No | Portable | Gets Command. |
| `P:LibTmux.NewPaneRequest.Empty` | `bool LibTmux.NewPaneRequest.Empty { get; }` | Public | No | Portable | Gets Empty. |
| `P:LibTmux.NewPaneRequest.Environment` | `IReadOnlyDictionary<string,string>? LibTmux.NewPaneRequest.Environment { get; }` | Public | No | Portable | Gets Environment. |
| `P:LibTmux.NewPaneRequest.Height` | `int? LibTmux.NewPaneRequest.Height { get; }` | Public | No | Portable | Gets Height. |
| `P:LibTmux.NewPaneRequest.InactiveBorderStyle` | `string? LibTmux.NewPaneRequest.InactiveBorderStyle { get; }` | Public | No | Portable | Gets InactiveBorderStyle. |
| `P:LibTmux.NewPaneRequest.KeepOpen` | `bool LibTmux.NewPaneRequest.KeepOpen { get; }` | Public | No | Portable | Gets KeepOpen. |
| `P:LibTmux.NewPaneRequest.Message` | `string? LibTmux.NewPaneRequest.Message { get; }` | Public | No | Portable | Gets Message. |
| `P:LibTmux.NewPaneRequest.StartDirectory` | `string? LibTmux.NewPaneRequest.StartDirectory { get; }` | Public | No | Portable | Gets StartDirectory. |
| `P:LibTmux.NewPaneRequest.Style` | `string? LibTmux.NewPaneRequest.Style { get; }` | Public | No | Portable | Gets Style. |
| `P:LibTmux.NewPaneRequest.Target` | `string? LibTmux.NewPaneRequest.Target { get; }` | Public | No | Portable | Gets Target. |
| `P:LibTmux.NewPaneRequest.Width` | `int? LibTmux.NewPaneRequest.Width { get; }` | Public | No | Portable | Gets Width. |
| `P:LibTmux.NewPaneRequest.X` | `int? LibTmux.NewPaneRequest.X { get; }` | Public | No | Portable | Gets X. |
| `P:LibTmux.NewPaneRequest.Y` | `int? LibTmux.NewPaneRequest.Y { get; }` | Public | No | Portable | Gets Y. |
| `P:LibTmux.NewPaneRequest.Zoom` | `bool LibTmux.NewPaneRequest.Zoom { get; }` | Public | No | Portable | Gets Zoom. |

### `T:LibTmux.NewSessionRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.NewSessionRequest.#ctor(string?,bool,bool,string?,string?,string?,string?,string?,IReadOnlyDictionary<string,string>?,bool,bool,string?)` | `NewSessionRequest(string? name = null, bool replaceExisting = false, bool attach = false, string? startDirectory = null, string? windowName = null, string? command = null, string? width = null, string? height = null, IReadOnlyDictionary<string,string>? environment = null, bool detachOthers = false, bool noSize = false, string? clientFlags = null)` | Public | No | Portable | Creates NewSessionRequest. |
| `P:LibTmux.NewSessionRequest.Attach` | `bool LibTmux.NewSessionRequest.Attach { get; }` | Public | No | Portable | Gets Attach. |
| `P:LibTmux.NewSessionRequest.ClientFlags` | `string? LibTmux.NewSessionRequest.ClientFlags { get; }` | Public | No | Portable | Gets ClientFlags. |
| `P:LibTmux.NewSessionRequest.Command` | `string? LibTmux.NewSessionRequest.Command { get; }` | Public | No | Portable | Gets Command. |
| `P:LibTmux.NewSessionRequest.DetachOthers` | `bool LibTmux.NewSessionRequest.DetachOthers { get; }` | Public | No | Portable | Gets DetachOthers. |
| `P:LibTmux.NewSessionRequest.Environment` | `IReadOnlyDictionary<string,string>? LibTmux.NewSessionRequest.Environment { get; }` | Public | No | Portable | Gets Environment. |
| `P:LibTmux.NewSessionRequest.Height` | `string? LibTmux.NewSessionRequest.Height { get; }` | Public | No | Portable | Gets Height. |
| `P:LibTmux.NewSessionRequest.Name` | `string? LibTmux.NewSessionRequest.Name { get; }` | Public | No | Portable | Gets Name. |
| `P:LibTmux.NewSessionRequest.NoSize` | `bool LibTmux.NewSessionRequest.NoSize { get; }` | Public | No | Portable | Gets NoSize. |
| `P:LibTmux.NewSessionRequest.ReplaceExisting` | `bool LibTmux.NewSessionRequest.ReplaceExisting { get; }` | Public | No | Portable | Gets ReplaceExisting. |
| `P:LibTmux.NewSessionRequest.StartDirectory` | `string? LibTmux.NewSessionRequest.StartDirectory { get; }` | Public | No | Portable | Gets StartDirectory. |
| `P:LibTmux.NewSessionRequest.Width` | `string? LibTmux.NewSessionRequest.Width { get; }` | Public | No | Portable | Gets Width. |
| `P:LibTmux.NewSessionRequest.WindowName` | `string? LibTmux.NewSessionRequest.WindowName { get; }` | Public | No | Portable | Gets WindowName. |

### `T:LibTmux.NewWindowRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.NewWindowRequest.#ctor(string?,string?,bool,string?,string?,IReadOnlyDictionary<string,string>?,WindowDirection?,string?,bool,bool)` | `NewWindowRequest(string? name = null, string? startDirectory = null, bool attach = false, string? index = null, string? command = null, IReadOnlyDictionary<string,string>? environment = null, WindowDirection? direction = null, string? targetWindow = null, bool killExisting = false, bool selectExisting = false)` | Public | No | Portable | Creates NewWindowRequest. |
| `P:LibTmux.NewWindowRequest.Attach` | `bool LibTmux.NewWindowRequest.Attach { get; }` | Public | No | Portable | Gets Attach. |
| `P:LibTmux.NewWindowRequest.Command` | `string? LibTmux.NewWindowRequest.Command { get; }` | Public | No | Portable | Gets Command. |
| `P:LibTmux.NewWindowRequest.Direction` | `WindowDirection? LibTmux.NewWindowRequest.Direction { get; }` | Public | No | Portable | Gets Direction. |
| `P:LibTmux.NewWindowRequest.Environment` | `IReadOnlyDictionary<string,string>? LibTmux.NewWindowRequest.Environment { get; }` | Public | No | Portable | Gets Environment. |
| `P:LibTmux.NewWindowRequest.Index` | `string? LibTmux.NewWindowRequest.Index { get; }` | Public | No | Portable | Gets Index. |
| `P:LibTmux.NewWindowRequest.KillExisting` | `bool LibTmux.NewWindowRequest.KillExisting { get; }` | Public | No | Portable | Gets KillExisting. |
| `P:LibTmux.NewWindowRequest.Name` | `string? LibTmux.NewWindowRequest.Name { get; }` | Public | No | Portable | Gets Name. |
| `P:LibTmux.NewWindowRequest.SelectExisting` | `bool LibTmux.NewWindowRequest.SelectExisting { get; }` | Public | No | Portable | Gets SelectExisting. |
| `P:LibTmux.NewWindowRequest.StartDirectory` | `string? LibTmux.NewWindowRequest.StartDirectory { get; }` | Public | No | Portable | Gets StartDirectory. |
| `P:LibTmux.NewWindowRequest.TargetWindow` | `string? LibTmux.NewWindowRequest.TargetWindow { get; }` | Public | No | Portable | Gets TargetWindow. |

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
| `P:LibTmux.Pane.AtBottom` | `bool LibTmux.Pane.AtBottom { get; }` | Public | No | Portable | Gets the captured AtBottom value. |
| `P:LibTmux.Pane.AtLeft` | `bool LibTmux.Pane.AtLeft { get; }` | Public | No | Portable | Gets the captured AtLeft value. |
| `P:LibTmux.Pane.AtRight` | `bool LibTmux.Pane.AtRight { get; }` | Public | No | Portable | Gets the captured AtRight value. |
| `P:LibTmux.Pane.AtTop` | `bool LibTmux.Pane.AtTop { get; }` | Public | No | Portable | Gets the captured AtTop value. |
| `P:LibTmux.Pane.Generation` | `ServerGeneration LibTmux.Pane.Generation { get; }` | Public | No | Portable | Gets the captured Generation value. |
| `P:LibTmux.Pane.Height` | `int LibTmux.Pane.Height { get; }` | Public | No | Portable | Gets the captured Height value. |
| `P:LibTmux.Pane.Hooks` | `TmuxHooks LibTmux.Pane.Hooks { get; }` | Public | No | Portable | Gets the captured Hooks value. |
| `P:LibTmux.Pane.Id` | `PaneId LibTmux.Pane.Id { get; }` | Public | No | Portable | Gets the captured Id value. |
| `P:LibTmux.Pane.Index` | `int LibTmux.Pane.Index { get; }` | Public | No | Portable | Gets the captured Index value. |
| `P:LibTmux.Pane.Options` | `TmuxOptions LibTmux.Pane.Options { get; }` | Public | No | Portable | Gets the captured Options value. |
| `P:LibTmux.Pane.RawFormatFields` | `IReadOnlyDictionary<string,string?> LibTmux.Pane.RawFormatFields { get; }` | Public | No | Portable | Gets copied raw tmux format tokens captured for this snapshot. |
| `P:LibTmux.Pane.Server` | `Server LibTmux.Pane.Server { get; }` | Public | No | Portable | Gets the captured Server value. |
| `P:LibTmux.Pane.Session` | `Session LibTmux.Pane.Session { get; }` | Public | No | Portable | Gets the captured Session value. |
| `P:LibTmux.Pane.Title` | `string? LibTmux.Pane.Title { get; }` | Public | No | Portable | Gets the captured Title value. |
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
| `M:LibTmux.PaneId.Parse(string)` | `static PaneId LibTmux.PaneId.Parse(string text)` | Public | Yes | Portable | Parses a prefixed identifier. |
| `M:LibTmux.PaneId.ToString()` | `string LibTmux.PaneId.ToString()` | Public | No | Portable | Returns the canonical prefixed identifier. |
| `M:LibTmux.PaneId.TryParse(string?,PaneId)` | `static bool LibTmux.PaneId.TryParse(string? text, out PaneId result)` | Public | Yes | Portable | Tries to parse a prefixed identifier without throwing. |
| `P:LibTmux.PaneId.Value` | `int LibTmux.PaneId.Value { get; }` | Public | No | Portable | Gets the nonnegative numeric value. |

### `T:LibTmux.PaneInputMode`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `F:LibTmux.PaneInputMode.Disable` | `Disable = 1` | Public | Implicit | Portable | The Disable value. Value: `1`. |
| `F:LibTmux.PaneInputMode.Enable` | `Enable = 0` | Public | Implicit | Portable | The Enable value. Value: `0`. |

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
| `M:LibTmux.PasteBufferRequest.#ctor(string?,bool,bool,bool,string?,bool)` | `PasteBufferRequest(string? name = null, bool deleteAfter = false, bool useLineFeedSeparator = false, bool bracketed = false, string? separator = null, bool rawBytes = false)` | Public | No | Portable | Creates PasteBufferRequest. |
| `P:LibTmux.PasteBufferRequest.Bracketed` | `bool LibTmux.PasteBufferRequest.Bracketed { get; }` | Public | No | Portable | Gets Bracketed. |
| `P:LibTmux.PasteBufferRequest.DeleteAfter` | `bool LibTmux.PasteBufferRequest.DeleteAfter { get; }` | Public | No | Portable | Gets DeleteAfter. |
| `P:LibTmux.PasteBufferRequest.Name` | `string? LibTmux.PasteBufferRequest.Name { get; }` | Public | No | Portable | Gets Name. |
| `P:LibTmux.PasteBufferRequest.RawBytes` | `bool LibTmux.PasteBufferRequest.RawBytes { get; }` | Public | No | Portable | Gets RawBytes. |
| `P:LibTmux.PasteBufferRequest.Separator` | `string? LibTmux.PasteBufferRequest.Separator { get; }` | Public | No | Portable | Gets Separator. |
| `P:LibTmux.PasteBufferRequest.UseLineFeedSeparator` | `bool LibTmux.PasteBufferRequest.UseLineFeedSeparator { get; }` | Public | No | Portable | Gets UseLineFeedSeparator. |

### `T:LibTmux.PipePaneRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.PipePaneRequest.#ctor(string?,bool,bool,bool)` | `PipePaneRequest(string? command = null, bool outputOnly = false, bool inputOnly = false, bool toggle = false)` | Public | No | Portable | Creates PipePaneRequest. |
| `P:LibTmux.PipePaneRequest.Command` | `string? LibTmux.PipePaneRequest.Command { get; }` | Public | No | Portable | Gets Command. |
| `P:LibTmux.PipePaneRequest.InputOnly` | `bool LibTmux.PipePaneRequest.InputOnly { get; }` | Public | No | Portable | Gets InputOnly. |
| `P:LibTmux.PipePaneRequest.OutputOnly` | `bool LibTmux.PipePaneRequest.OutputOnly { get; }` | Public | No | Portable | Gets OutputOnly. |
| `P:LibTmux.PipePaneRequest.Toggle` | `bool LibTmux.PipePaneRequest.Toggle { get; }` | Public | No | Portable | Gets Toggle. |

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

### `T:LibTmux.Query.AndNode`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.Query.AndNode.#ctor(IReadOnlyList<QueryNode>)` | `AndNode(IReadOnlyList<QueryNode> operands)` | Public | No | Portable | Creates AndNode. |
| `P:LibTmux.Query.AndNode.Operands` | `IReadOnlyList<QueryNode> LibTmux.Query.AndNode.Operands { get; }` | Public | No | Portable | Gets Operands. |

### `T:LibTmux.Query.BooleanConstant`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.Query.BooleanConstant.#ctor(bool)` | `BooleanConstant(bool value)` | Public | No | Portable | Creates BooleanConstant. |
| `P:LibTmux.Query.BooleanConstant.Value` | `bool LibTmux.Query.BooleanConstant.Value { get; }` | Public | No | Portable | Gets Value. |

### `T:LibTmux.Query.ComparisonNode`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.Query.ComparisonNode.#ctor(QueryComparison,QueryNode,QueryNode)` | `ComparisonNode(QueryComparison comparison, QueryNode left, QueryNode right)` | Public | No | Portable | Creates ComparisonNode. |
| `P:LibTmux.Query.ComparisonNode.Left` | `QueryNode LibTmux.Query.ComparisonNode.Left { get; }` | Public | No | Portable | Gets Left. |
| `P:LibTmux.Query.ComparisonNode.Operator` | `QueryComparison LibTmux.Query.ComparisonNode.Operator { get; }` | Public | No | Portable | Gets Operator. |
| `P:LibTmux.Query.ComparisonNode.Right` | `QueryNode LibTmux.Query.ComparisonNode.Right { get; }` | Public | No | Portable | Gets Right. |

### `T:LibTmux.Query.ConstantNode`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.Query.ConstantNode.#ctor(QueryConstant)` | `ConstantNode(QueryConstant value)` | Public | No | Portable | Creates ConstantNode. |
| `P:LibTmux.Query.ConstantNode.Value` | `QueryConstant LibTmux.Query.ConstantNode.Value { get; }` | Public | No | Portable | Gets Value. |

### `T:LibTmux.Query.FieldNode`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.Query.FieldNode.#ctor(QueryTarget,string)` | `FieldNode(QueryTarget target, string wireName)` | Public | No | Portable | Creates FieldNode. |
| `P:LibTmux.Query.FieldNode.Target` | `QueryTarget LibTmux.Query.FieldNode.Target { get; }` | Public | No | Portable | Gets Target. |
| `P:LibTmux.Query.FieldNode.WireName` | `string LibTmux.Query.FieldNode.WireName { get; }` | Public | No | Portable | Gets WireName. |

### `T:LibTmux.Query.Int64Constant`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.Query.Int64Constant.#ctor(long)` | `Int64Constant(long value)` | Public | No | Portable | Creates Int64Constant. |
| `P:LibTmux.Query.Int64Constant.Value` | `long LibTmux.Query.Int64Constant.Value { get; }` | Public | No | Portable | Gets Value. |

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

### `T:LibTmux.Query.NotNode`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.Query.NotNode.#ctor(QueryNode)` | `NotNode(QueryNode operand)` | Public | No | Portable | Creates NotNode. |
| `P:LibTmux.Query.NotNode.Operand` | `QueryNode LibTmux.Query.NotNode.Operand { get; }` | Public | No | Portable | Gets Operand. |

### `T:LibTmux.Query.NullConstant`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.Query.NullConstant.#ctor()` | `NullConstant()` | Public | No | Portable | Creates NullConstant. |

### `T:LibTmux.Query.OrNode`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.Query.OrNode.#ctor(IReadOnlyList<QueryNode>)` | `OrNode(IReadOnlyList<QueryNode> operands)` | Public | No | Portable | Creates OrNode. |
| `P:LibTmux.Query.OrNode.Operands` | `IReadOnlyList<QueryNode> LibTmux.Query.OrNode.Operands { get; }` | Public | No | Portable | Gets Operands. |

### `T:LibTmux.Query.QuantifierNode`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.Query.QuantifierNode.#ctor(QueryQuantifier,FieldNode,QueryNode)` | `QuantifierNode(QueryQuantifier quantifier, FieldNode relation, QueryNode predicate)` | Public | No | Portable | Creates QuantifierNode. |
| `P:LibTmux.Query.QuantifierNode.Predicate` | `QueryNode LibTmux.Query.QuantifierNode.Predicate { get; }` | Public | No | Portable | Gets Predicate. |
| `P:LibTmux.Query.QuantifierNode.Quantifier` | `QueryQuantifier LibTmux.Query.QuantifierNode.Quantifier { get; }` | Public | No | Portable | Gets Quantifier. |
| `P:LibTmux.Query.QuantifierNode.Relation` | `FieldNode LibTmux.Query.QuantifierNode.Relation { get; }` | Public | No | Portable | Gets Relation. |

### `T:LibTmux.Query.QueryComparison`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `F:LibTmux.Query.QueryComparison.Equal` | `Equal = 0` | Public | Implicit | Portable | The Equal value. Value: `0`. |
| `F:LibTmux.Query.QueryComparison.GreaterThan` | `GreaterThan = 4` | Public | Implicit | Portable | The GreaterThan value. Value: `4`. |
| `F:LibTmux.Query.QueryComparison.GreaterThanOrEqual` | `GreaterThanOrEqual = 5` | Public | Implicit | Portable | The GreaterThanOrEqual value. Value: `5`. |
| `F:LibTmux.Query.QueryComparison.LessThan` | `LessThan = 2` | Public | Implicit | Portable | The LessThan value. Value: `2`. |
| `F:LibTmux.Query.QueryComparison.LessThanOrEqual` | `LessThanOrEqual = 3` | Public | Implicit | Portable | The LessThanOrEqual value. Value: `3`. |
| `F:LibTmux.Query.QueryComparison.NotEqual` | `NotEqual = 1` | Public | Implicit | Portable | The NotEqual value. Value: `1`. |

### `T:LibTmux.Query.QueryDocument`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.Query.QueryDocument.#ctor(string,int,QueryTarget,QueryNode)` | `QueryDocument(string schema, int version, QueryTarget target, QueryNode predicate)` | Public | No | Portable | Creates QueryDocument. |
| `P:LibTmux.Query.QueryDocument.Predicate` | `QueryNode LibTmux.Query.QueryDocument.Predicate { get; }` | Public | No | Portable | Gets Predicate. |
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

### `T:LibTmux.Query.QueryQuantifier`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `F:LibTmux.Query.QueryQuantifier.All` | `All = 1` | Public | Implicit | Portable | The All value. Value: `1`. |
| `F:LibTmux.Query.QueryQuantifier.Any` | `Any = 0` | Public | Implicit | Portable | The Any value. Value: `0`. |

### `T:LibTmux.Query.QueryStringOperation`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `F:LibTmux.Query.QueryStringOperation.ContainsOrdinal` | `ContainsOrdinal = 4` | Public | Implicit | Portable | The ContainsOrdinal value. Value: `4`. |
| `F:LibTmux.Query.QueryStringOperation.EndsWithOrdinal` | `EndsWithOrdinal = 3` | Public | Implicit | Portable | The EndsWithOrdinal value. Value: `3`. |
| `F:LibTmux.Query.QueryStringOperation.EqualsOrdinal` | `EqualsOrdinal = 0` | Public | Implicit | Portable | The EqualsOrdinal value. Value: `0`. |
| `F:LibTmux.Query.QueryStringOperation.EqualsOrdinalIgnoreCase` | `EqualsOrdinalIgnoreCase = 1` | Public | Implicit | Portable | The EqualsOrdinalIgnoreCase value. Value: `1`. |
| `F:LibTmux.Query.QueryStringOperation.StartsWithOrdinal` | `StartsWithOrdinal = 2` | Public | Implicit | Portable | The StartsWithOrdinal value. Value: `2`. |

### `T:LibTmux.Query.QueryTarget`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `F:LibTmux.Query.QueryTarget.Client` | `Client = 3` | Public | Implicit | Portable | The Client value. Value: `3`. |
| `F:LibTmux.Query.QueryTarget.Pane` | `Pane = 2` | Public | Implicit | Portable | The Pane value. Value: `2`. |
| `F:LibTmux.Query.QueryTarget.Session` | `Session = 0` | Public | Implicit | Portable | The Session value. Value: `0`. |
| `F:LibTmux.Query.QueryTarget.Window` | `Window = 1` | Public | Implicit | Portable | The Window value. Value: `1`. |

### `T:LibTmux.Query.RegexNode`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.Query.RegexNode.#ctor(QueryNode,string,string,RegexOptions)` | `RegexNode(QueryNode input, string dialect, string pattern, RegexOptions semanticOptions)` | Public | No | Portable | Creates RegexNode. |
| `P:LibTmux.Query.RegexNode.Dialect` | `string LibTmux.Query.RegexNode.Dialect { get; }` | Public | No | Portable | Gets Dialect. |
| `P:LibTmux.Query.RegexNode.Input` | `QueryNode LibTmux.Query.RegexNode.Input { get; }` | Public | No | Portable | Gets Input. |
| `P:LibTmux.Query.RegexNode.Pattern` | `string LibTmux.Query.RegexNode.Pattern { get; }` | Public | No | Portable | Gets Pattern. |
| `P:LibTmux.Query.RegexNode.SemanticOptions` | `RegexOptions LibTmux.Query.RegexNode.SemanticOptions { get; }` | Public | No | Portable | Gets SemanticOptions. |

### `T:LibTmux.Query.StringConstant`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.Query.StringConstant.#ctor(string)` | `StringConstant(string value)` | Public | No | Portable | Creates StringConstant. |
| `P:LibTmux.Query.StringConstant.Value` | `string LibTmux.Query.StringConstant.Value { get; }` | Public | No | Portable | Gets Value. |

### `T:LibTmux.Query.StringNode`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.Query.StringNode.#ctor(QueryStringOperation,QueryNode,QueryNode)` | `StringNode(QueryStringOperation operation, QueryNode left, QueryNode right)` | Public | No | Portable | Creates StringNode. |
| `P:LibTmux.Query.StringNode.Left` | `QueryNode LibTmux.Query.StringNode.Left { get; }` | Public | No | Portable | Gets Left. |
| `P:LibTmux.Query.StringNode.Operator` | `QueryStringOperation LibTmux.Query.StringNode.Operator { get; }` | Public | No | Portable | Gets Operator. |
| `P:LibTmux.Query.StringNode.Right` | `QueryNode LibTmux.Query.StringNode.Right { get; }` | Public | No | Portable | Gets Right. |

### `T:LibTmux.Query.TypedIdConstant`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.Query.TypedIdConstant.#ctor(QueryTarget,string)` | `TypedIdConstant(QueryTarget target, string value)` | Public | No | Portable | Creates TypedIdConstant. |
| `P:LibTmux.Query.TypedIdConstant.Target` | `QueryTarget LibTmux.Query.TypedIdConstant.Target { get; }` | Public | No | Portable | Gets Target. |
| `P:LibTmux.Query.TypedIdConstant.Value` | `string LibTmux.Query.TypedIdConstant.Value { get; }` | Public | No | Portable | Gets Value. |

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
| `M:LibTmux.ResizePaneRequest.#ctor(ResizeDirection?,int?,string?,string?,bool,bool,bool)` | `ResizePaneRequest(ResizeDirection? direction = null, int? adjustment = null, string? width = null, string? height = null, bool zoom = false, bool mouse = false, bool trimBelow = false)` | Public | No | Portable | Creates ResizePaneRequest. |
| `P:LibTmux.ResizePaneRequest.Adjustment` | `int? LibTmux.ResizePaneRequest.Adjustment { get; }` | Public | No | Portable | Gets Adjustment. |
| `P:LibTmux.ResizePaneRequest.Direction` | `ResizeDirection? LibTmux.ResizePaneRequest.Direction { get; }` | Public | No | Portable | Gets Direction. |
| `P:LibTmux.ResizePaneRequest.Height` | `string? LibTmux.ResizePaneRequest.Height { get; }` | Public | No | Portable | Gets Height. |
| `P:LibTmux.ResizePaneRequest.Mouse` | `bool LibTmux.ResizePaneRequest.Mouse { get; }` | Public | No | Portable | Gets Mouse. |
| `P:LibTmux.ResizePaneRequest.TrimBelow` | `bool LibTmux.ResizePaneRequest.TrimBelow { get; }` | Public | No | Portable | Gets TrimBelow. |
| `P:LibTmux.ResizePaneRequest.Width` | `string? LibTmux.ResizePaneRequest.Width { get; }` | Public | No | Portable | Gets Width. |
| `P:LibTmux.ResizePaneRequest.Zoom` | `bool LibTmux.ResizePaneRequest.Zoom { get; }` | Public | No | Portable | Gets Zoom. |

### `T:LibTmux.ResizeWindowRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.ResizeWindowRequest.#ctor(ResizeDirection?,int?,int?,int?,WindowResizeMode?)` | `ResizeWindowRequest(ResizeDirection? direction = null, int? adjustment = null, int? width = null, int? height = null, WindowResizeMode? mode = null)` | Public | No | Portable | Creates ResizeWindowRequest. |
| `P:LibTmux.ResizeWindowRequest.Adjustment` | `int? LibTmux.ResizeWindowRequest.Adjustment { get; }` | Public | No | Portable | Gets Adjustment. |
| `P:LibTmux.ResizeWindowRequest.Direction` | `ResizeDirection? LibTmux.ResizeWindowRequest.Direction { get; }` | Public | No | Portable | Gets Direction. |
| `P:LibTmux.ResizeWindowRequest.Height` | `int? LibTmux.ResizeWindowRequest.Height { get; }` | Public | No | Portable | Gets Height. |
| `P:LibTmux.ResizeWindowRequest.Mode` | `WindowResizeMode? LibTmux.ResizeWindowRequest.Mode { get; }` | Public | No | Portable | Gets Mode. |
| `P:LibTmux.ResizeWindowRequest.Width` | `int? LibTmux.ResizeWindowRequest.Width { get; }` | Public | No | Portable | Gets Width. |

### `T:LibTmux.RespawnRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.RespawnRequest.#ctor(string?,string?,IReadOnlyDictionary<string,string>?,bool)` | `RespawnRequest(string? command = null, string? startDirectory = null, IReadOnlyDictionary<string,string>? environment = null, bool killExistingProcess = false)` | Public | No | Portable | Creates RespawnRequest. |
| `P:LibTmux.RespawnRequest.Command` | `string? LibTmux.RespawnRequest.Command { get; }` | Public | No | Portable | Gets Command. |
| `P:LibTmux.RespawnRequest.Environment` | `IReadOnlyDictionary<string,string>? LibTmux.RespawnRequest.Environment { get; }` | Public | No | Portable | Gets Environment. |
| `P:LibTmux.RespawnRequest.KillExistingProcess` | `bool LibTmux.RespawnRequest.KillExistingProcess { get; }` | Public | No | Portable | Gets KillExistingProcess. |
| `P:LibTmux.RespawnRequest.StartDirectory` | `string? LibTmux.RespawnRequest.StartDirectory { get; }` | Public | No | Portable | Gets StartDirectory. |

### `T:LibTmux.RunShellRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.RunShellRequest.#ctor(string,IReadOnlyList<string>?,bool,TimeSpan?,bool,string?,string?,bool)` | `RunShellRequest(string command, IReadOnlyList<string>? arguments = null, bool background = false, TimeSpan? delay = null, bool asTmuxCommand = false, string? targetPane = null, string? workingDirectory = null, bool showStandardError = false)` | Public | No | Portable | Creates RunShellRequest. |
| `P:LibTmux.RunShellRequest.Arguments` | `IReadOnlyList<string>? LibTmux.RunShellRequest.Arguments { get; }` | Public | No | Portable | Gets Arguments. |
| `P:LibTmux.RunShellRequest.AsTmuxCommand` | `bool LibTmux.RunShellRequest.AsTmuxCommand { get; }` | Public | No | Portable | Gets AsTmuxCommand. |
| `P:LibTmux.RunShellRequest.Background` | `bool LibTmux.RunShellRequest.Background { get; }` | Public | No | Portable | Gets Background. |
| `P:LibTmux.RunShellRequest.Command` | `string LibTmux.RunShellRequest.Command { get; }` | Public | No | Portable | Gets Command. |
| `P:LibTmux.RunShellRequest.Delay` | `TimeSpan? LibTmux.RunShellRequest.Delay { get; }` | Public | No | Portable | Gets Delay. |
| `P:LibTmux.RunShellRequest.ShowStandardError` | `bool LibTmux.RunShellRequest.ShowStandardError { get; }` | Public | No | Portable | Gets ShowStandardError. |
| `P:LibTmux.RunShellRequest.TargetPane` | `string? LibTmux.RunShellRequest.TargetPane { get; }` | Public | No | Portable | Gets TargetPane. |
| `P:LibTmux.RunShellRequest.WorkingDirectory` | `string? LibTmux.RunShellRequest.WorkingDirectory { get; }` | Public | No | Portable | Gets WorkingDirectory. |

### `T:LibTmux.SelectLayoutMode`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `F:LibTmux.SelectLayoutMode.Next` | `Next = 1` | Public | Implicit | Portable | The Next value. Value: `1`. |
| `F:LibTmux.SelectLayoutMode.Previous` | `Previous = 2` | Public | Implicit | Portable | The Previous value. Value: `2`. |
| `F:LibTmux.SelectLayoutMode.Spread` | `Spread = 0` | Public | Implicit | Portable | The Spread value. Value: `0`. |

### `T:LibTmux.SelectLayoutRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.SelectLayoutRequest.#ctor(string?,SelectLayoutMode?)` | `SelectLayoutRequest(string? layout = null, SelectLayoutMode? mode = null)` | Public | No | Portable | Creates SelectLayoutRequest. |
| `P:LibTmux.SelectLayoutRequest.Layout` | `string? LibTmux.SelectLayoutRequest.Layout { get; }` | Public | No | Portable | Gets Layout. |
| `P:LibTmux.SelectLayoutRequest.Mode` | `SelectLayoutMode? LibTmux.SelectLayoutRequest.Mode { get; }` | Public | No | Portable | Gets Mode. |

### `T:LibTmux.SelectPaneRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.SelectPaneRequest.#ctor(PaneSelectDirection?,bool,bool?,bool?,bool)` | `SelectPaneRequest(PaneSelectDirection? direction = null, bool keepZoom = false, bool? mark = null, bool? inputEnabled = null, bool last = false)` | Public | No | Portable | Creates SelectPaneRequest. |
| `P:LibTmux.SelectPaneRequest.Direction` | `PaneSelectDirection? LibTmux.SelectPaneRequest.Direction { get; }` | Public | No | Portable | Gets Direction. |
| `P:LibTmux.SelectPaneRequest.InputEnabled` | `bool? LibTmux.SelectPaneRequest.InputEnabled { get; }` | Public | No | Portable | Gets InputEnabled. |
| `P:LibTmux.SelectPaneRequest.KeepZoom` | `bool LibTmux.SelectPaneRequest.KeepZoom { get; }` | Public | No | Portable | Gets KeepZoom. |
| `P:LibTmux.SelectPaneRequest.Last` | `bool LibTmux.SelectPaneRequest.Last { get; }` | Public | No | Portable | Gets Last. |
| `P:LibTmux.SelectPaneRequest.Mark` | `bool? LibTmux.SelectPaneRequest.Mark { get; }` | Public | No | Portable | Gets Mark. |

### `T:LibTmux.SendKeysRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.SendKeysRequest.#ctor(string?,bool,bool,bool,bool,string?,int?,bool,bool,string?,bool)` | `SendKeysRequest(string? text = null, bool enter = true, bool suppressHistory = false, bool literal = false, bool reset = false, string? copyModeCommand = null, int? repeat = null, bool expandFormats = false, bool hexKeys = false, string? targetClient = null, bool keyName = false)` | Public | No | Portable | Creates SendKeysRequest. |
| `P:LibTmux.SendKeysRequest.CopyModeCommand` | `string? LibTmux.SendKeysRequest.CopyModeCommand { get; }` | Public | No | Portable | Gets CopyModeCommand. |
| `P:LibTmux.SendKeysRequest.Enter` | `bool LibTmux.SendKeysRequest.Enter { get; }` | Public | No | Portable | Gets Enter. |
| `P:LibTmux.SendKeysRequest.ExpandFormats` | `bool LibTmux.SendKeysRequest.ExpandFormats { get; }` | Public | No | Portable | Gets ExpandFormats. |
| `P:LibTmux.SendKeysRequest.HexKeys` | `bool LibTmux.SendKeysRequest.HexKeys { get; }` | Public | No | Portable | Gets HexKeys. |
| `P:LibTmux.SendKeysRequest.KeyName` | `bool LibTmux.SendKeysRequest.KeyName { get; }` | Public | No | Portable | Gets KeyName. |
| `P:LibTmux.SendKeysRequest.Literal` | `bool LibTmux.SendKeysRequest.Literal { get; }` | Public | No | Portable | Gets Literal. |
| `P:LibTmux.SendKeysRequest.Repeat` | `int? LibTmux.SendKeysRequest.Repeat { get; }` | Public | No | Portable | Gets Repeat. |
| `P:LibTmux.SendKeysRequest.Reset` | `bool LibTmux.SendKeysRequest.Reset { get; }` | Public | No | Portable | Gets Reset. |
| `P:LibTmux.SendKeysRequest.SuppressHistory` | `bool LibTmux.SendKeysRequest.SuppressHistory { get; }` | Public | No | Portable | Gets SuppressHistory. |
| `P:LibTmux.SendKeysRequest.TargetClient` | `string? LibTmux.SendKeysRequest.TargetClient { get; }` | Public | No | Portable | Gets TargetClient. |
| `P:LibTmux.SendKeysRequest.Text` | `string? LibTmux.SendKeysRequest.Text { get; }` | Public | No | Portable | Gets Text. |

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
| `M:LibTmux.Server.FromEnvironment(IReadOnlyDictionary<string,string>?)` | `static Server LibTmux.Server.FromEnvironment(IReadOnlyDictionary<string,string>? environment = null)` | Public | Yes | Portable | Parses a tmux endpoint from an environment snapshot without starting a process. |
| `M:LibTmux.Server.GetAttachedSessionsAsync(CancellationToken)` | `Task<IReadOnlyList<Session>> LibTmux.Server.GetAttachedSessionsAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Returns attached sessions or captured empty on any list-command failure. List error policy: empty-on-any-list-command-failure. |
| `M:LibTmux.Server.GetBufferAsync(string?,CancellationToken)` | `Task<string> LibTmux.Server.GetBufferAsync(string? name = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs GetBuffer. |
| `M:LibTmux.Server.GetBufferLinesAsync(ListBuffersRequest?,CancellationToken)` | `Task<IReadOnlyList<string>> LibTmux.Server.GetBufferLinesAsync(ListBuffersRequest? request = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs GetBufferLines. |
| `M:LibTmux.Server.GetBuffersAsync(CancellationToken)` | `Task<IReadOnlyList<TmuxBuffer>> LibTmux.Server.GetBuffersAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Gets typed paste-buffer snapshots using the canonical projection. |
| `M:LibTmux.Server.GetClientsAsync(CancellationToken)` | `Task<IReadOnlyList<Client>> LibTmux.Server.GetClientsAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Returns clients or captured empty on any list-command failure. List error policy: empty-on-any-list-command-failure. |
| `M:LibTmux.Server.GetCommandsAsync(string?,CancellationToken)` | `Task<IReadOnlyList<string>> LibTmux.Server.GetCommandsAsync(string? name = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs GetCommands. |
| `M:LibTmux.Server.GetKeysAsync(string?,string?,CancellationToken)` | `Task<IReadOnlyList<string>> LibTmux.Server.GetKeysAsync(string? keyTable = null, string? format = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs GetKeys. |
| `M:LibTmux.Server.GetMessagesAsync(string?,ShowMessagesMode,CancellationToken)` | `Task<IReadOnlyList<string>> LibTmux.Server.GetMessagesAsync(string? targetClient = null, ShowMessagesMode mode = ShowMessagesMode.Messages, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs GetMessages. |
| `M:LibTmux.Server.GetPaneAsync(PaneId,CancellationToken)` | `Task<Pane> LibTmux.Server.GetPaneAsync(PaneId id, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Gets one pane or throws TmuxObjectNotFoundException. |
| `M:LibTmux.Server.GetPanesAsync(CancellationToken)` | `Task<IReadOnlyList<Pane>> LibTmux.Server.GetPanesAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Returns server-wide panes, suppressing only missing-daemon or missing-socket failures. List error policy: empty-on-missing-daemon-or-socket. |
| `M:LibTmux.Server.GetPromptHistoryAsync(PromptType?,CancellationToken)` | `Task<IReadOnlyList<string>> LibTmux.Server.GetPromptHistoryAsync(PromptType? type = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs GetPromptHistory. |
| `M:LibTmux.Server.GetSessionAsync(SessionId,CancellationToken)` | `Task<Session> LibTmux.Server.GetSessionAsync(SessionId id, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Gets one session or throws TmuxObjectNotFoundException. |
| `M:LibTmux.Server.GetSessionsAsync(CancellationToken)` | `Task<IReadOnlyList<Session>> LibTmux.Server.GetSessionsAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Returns a captured empty list on any underlying list-command failure. List error policy: empty-on-any-list-command-failure. |
| `M:LibTmux.Server.GetWindowAsync(WindowId,CancellationToken)` | `Task<Window> LibTmux.Server.GetWindowAsync(WindowId id, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Gets one canonical session-scoped window view or throws TmuxObjectNotFoundException. |
| `M:LibTmux.Server.GetWindowsAsync(CancellationToken)` | `Task<IReadOnlyList<Window>> LibTmux.Server.GetWindowsAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Returns server-wide window views, suppressing only missing-daemon or missing-socket failures. List error policy: empty-on-missing-daemon-or-socket. |
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
| `M:LibTmux.Server.RaiseIfDeadAsync(CancellationToken)` | `Task LibTmux.Server.RaiseIfDeadAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs RaiseIfDead. |
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
| `M:LibTmux.Server.UnbindKeyAsync(UnbindKeyRequest,CancellationToken)` | `Task LibTmux.Server.UnbindKeyAsync(UnbindKeyRequest request, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs UnbindKey. |
| `M:LibTmux.Server.WaitForAsync(WaitForRequest,CancellationToken)` | `Task LibTmux.Server.WaitForAsync(WaitForRequest request, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs WaitFor. |
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
| `M:LibTmux.ServerAccessRequest.#ctor(string?,string?,bool,bool,bool)` | `ServerAccessRequest(string? allowUser = null, string? denyUser = null, bool list = false, bool readOnly = false, bool readWrite = false)` | Public | No | Portable | Creates ServerAccessRequest. |
| `P:LibTmux.ServerAccessRequest.AllowUser` | `string? LibTmux.ServerAccessRequest.AllowUser { get; }` | Public | No | Portable | Gets AllowUser. |
| `P:LibTmux.ServerAccessRequest.DenyUser` | `string? LibTmux.ServerAccessRequest.DenyUser { get; }` | Public | No | Portable | Gets DenyUser. |
| `P:LibTmux.ServerAccessRequest.List` | `bool LibTmux.ServerAccessRequest.List { get; }` | Public | No | Portable | Gets List. |
| `P:LibTmux.ServerAccessRequest.ReadOnly` | `bool LibTmux.ServerAccessRequest.ReadOnly { get; }` | Public | No | Portable | Gets ReadOnly. |
| `P:LibTmux.ServerAccessRequest.ReadWrite` | `bool LibTmux.ServerAccessRequest.ReadWrite { get; }` | Public | No | Portable | Gets ReadWrite. |

### `T:LibTmux.ServerConnectionOptions`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.ServerConnectionOptions.#ctor(string,string?,string?,Func<string>?,string?,TmuxColorMode,Func<Server,CancellationToken,ValueTask>?,IReadOnlyDictionary<string,string?>?,ILogger?)` | `ServerConnectionOptions(string tmuxBinaryPath = "tmux", string? socketName = null, string? socketPath = null, Func<string>? socketNameFactory = null, string? configurationFile = null, TmuxColorMode colorMode = TmuxColorMode.Default, Func<Server,CancellationToken,ValueTask>? initializeAsync = null, IReadOnlyDictionary<string,string?>? childEnvironment = null, ILogger? logger = null)` | Public | No | Portable | Creates ServerConnectionOptions. |
| `P:LibTmux.ServerConnectionOptions.ChildEnvironment` | `IReadOnlyDictionary<string,string?>? LibTmux.ServerConnectionOptions.ChildEnvironment { get; }` | Public | No | Portable | Gets ChildEnvironment. |
| `P:LibTmux.ServerConnectionOptions.ColorMode` | `TmuxColorMode LibTmux.ServerConnectionOptions.ColorMode { get; }` | Public | No | Portable | Gets ColorMode. |
| `P:LibTmux.ServerConnectionOptions.ConfigurationFile` | `string? LibTmux.ServerConnectionOptions.ConfigurationFile { get; }` | Public | No | Portable | Gets ConfigurationFile. |
| `P:LibTmux.ServerConnectionOptions.Default` | `static ServerConnectionOptions LibTmux.ServerConnectionOptions.Default { get; }` | Public | Yes | Portable | Gets conventional connection defaults using the tmux executable on PATH. |
| `P:LibTmux.ServerConnectionOptions.InitializeAsync` | `Func<Server,CancellationToken,ValueTask>? LibTmux.ServerConnectionOptions.InitializeAsync { get; }` | Public | No | Portable | Gets InitializeAsync. |
| `P:LibTmux.ServerConnectionOptions.Logger` | `ILogger? LibTmux.ServerConnectionOptions.Logger { get; }` | Public | No | Portable | Gets Logger. |
| `P:LibTmux.ServerConnectionOptions.SocketName` | `string? LibTmux.ServerConnectionOptions.SocketName { get; }` | Public | No | Portable | Gets SocketName. |
| `P:LibTmux.ServerConnectionOptions.SocketNameFactory` | `Func<string>? LibTmux.ServerConnectionOptions.SocketNameFactory { get; }` | Public | No | Portable | Gets SocketNameFactory. |
| `P:LibTmux.ServerConnectionOptions.SocketPath` | `string? LibTmux.ServerConnectionOptions.SocketPath { get; }` | Public | No | Portable | Gets SocketPath. |
| `P:LibTmux.ServerConnectionOptions.TmuxBinaryPath` | `string LibTmux.ServerConnectionOptions.TmuxBinaryPath { get; }` | Public | No | Portable | Gets TmuxBinaryPath. |

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
| `M:LibTmux.Session.FromEnvironmentAsync(IReadOnlyDictionary<string,string>?,CancellationToken)` | `static Task<Session> LibTmux.Session.FromEnvironmentAsync(IReadOnlyDictionary<string,string>? environment = null, CancellationToken cancellationToken = default)` | Public | Yes | `UnsupportedOSPlatform("windows")` | Performs FromEnvironment. |
| `M:LibTmux.Session.GetPanesAsync(CancellationToken)` | `Task<IReadOnlyList<Pane>> LibTmux.Session.GetPanesAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs loud child pane traversal. List error policy: loud. |
| `M:LibTmux.Session.GetWindowAsync(string,CancellationToken)` | `Task<Window?> LibTmux.Session.GetWindowAsync(string target, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Gets one session-scoped window view. |
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
| `P:LibTmux.Session.ActivePane` | `Pane? LibTmux.Session.ActivePane { get; }` | Public | No | Portable | Gets the captured ActivePane value. |
| `P:LibTmux.Session.ActiveWindow` | `Window? LibTmux.Session.ActiveWindow { get; }` | Public | No | Portable | Gets the captured ActiveWindow value. |
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
| `M:LibTmux.SessionId.Parse(string)` | `static SessionId LibTmux.SessionId.Parse(string text)` | Public | Yes | Portable | Parses a prefixed identifier. |
| `M:LibTmux.SessionId.ToString()` | `string LibTmux.SessionId.ToString()` | Public | No | Portable | Returns the canonical prefixed identifier. |
| `M:LibTmux.SessionId.TryParse(string?,SessionId)` | `static bool LibTmux.SessionId.TryParse(string? text, out SessionId result)` | Public | Yes | Portable | Tries to parse a prefixed identifier without throwing. |
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
| `M:LibTmux.SetHookRequest.#ctor(string,string,OptionScope?,bool,bool,bool,bool)` | `SetHookRequest(string name, string value, OptionScope? scope = null, bool global = false, bool unset = false, bool runImmediately = false, bool append = false)` | Public | No | Portable | Creates SetHookRequest. |
| `P:LibTmux.SetHookRequest.Append` | `bool LibTmux.SetHookRequest.Append { get; }` | Public | No | Portable | Gets Append. |
| `P:LibTmux.SetHookRequest.Global` | `bool LibTmux.SetHookRequest.Global { get; }` | Public | No | Portable | Gets Global. |
| `P:LibTmux.SetHookRequest.Name` | `string LibTmux.SetHookRequest.Name { get; }` | Public | No | Portable | Gets Name. |
| `P:LibTmux.SetHookRequest.RunImmediately` | `bool LibTmux.SetHookRequest.RunImmediately { get; }` | Public | No | Portable | Gets RunImmediately. |
| `P:LibTmux.SetHookRequest.Scope` | `OptionScope? LibTmux.SetHookRequest.Scope { get; }` | Public | No | Portable | Gets Scope. |
| `P:LibTmux.SetHookRequest.Unset` | `bool LibTmux.SetHookRequest.Unset { get; }` | Public | No | Portable | Gets Unset. |
| `P:LibTmux.SetHookRequest.Value` | `string LibTmux.SetHookRequest.Value { get; }` | Public | No | Portable | Gets Value. |

### `T:LibTmux.SetHooksRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.SetHooksRequest.#ctor(string,IReadOnlyDictionary<int,string>,OptionScope?,bool,bool)` | `SetHooksRequest(string name, IReadOnlyDictionary<int,string> values, OptionScope? scope = null, bool global = false, bool clearExisting = false)` | Public | No | Portable | Creates SetHooksRequest. |
| `P:LibTmux.SetHooksRequest.ClearExisting` | `bool LibTmux.SetHooksRequest.ClearExisting { get; }` | Public | No | Portable | Gets ClearExisting. |
| `P:LibTmux.SetHooksRequest.Global` | `bool LibTmux.SetHooksRequest.Global { get; }` | Public | No | Portable | Gets Global. |
| `P:LibTmux.SetHooksRequest.Name` | `string LibTmux.SetHooksRequest.Name { get; }` | Public | No | Portable | Gets Name. |
| `P:LibTmux.SetHooksRequest.Scope` | `OptionScope? LibTmux.SetHooksRequest.Scope { get; }` | Public | No | Portable | Gets Scope. |
| `P:LibTmux.SetHooksRequest.Values` | `IReadOnlyDictionary<int,string> LibTmux.SetHooksRequest.Values { get; }` | Public | No | Portable | Gets Values. |

### `T:LibTmux.SetOptionRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.SetOptionRequest.#ctor(string,string,OptionScope?,bool,bool,bool,bool,bool)` | `SetOptionRequest(string name, string value, OptionScope? scope = null, bool expandFormat = false, bool preventOverwrite = false, bool quiet = false, bool append = false, bool global = false)` | Public | No | Portable | Creates SetOptionRequest. |
| `P:LibTmux.SetOptionRequest.Append` | `bool LibTmux.SetOptionRequest.Append { get; }` | Public | No | Portable | Gets Append. |
| `P:LibTmux.SetOptionRequest.ExpandFormat` | `bool LibTmux.SetOptionRequest.ExpandFormat { get; }` | Public | No | Portable | Gets ExpandFormat. |
| `P:LibTmux.SetOptionRequest.Global` | `bool LibTmux.SetOptionRequest.Global { get; }` | Public | No | Portable | Gets Global. |
| `P:LibTmux.SetOptionRequest.Name` | `string LibTmux.SetOptionRequest.Name { get; }` | Public | No | Portable | Gets Name. |
| `P:LibTmux.SetOptionRequest.PreventOverwrite` | `bool LibTmux.SetOptionRequest.PreventOverwrite { get; }` | Public | No | Portable | Gets PreventOverwrite. |
| `P:LibTmux.SetOptionRequest.Quiet` | `bool LibTmux.SetOptionRequest.Quiet { get; }` | Public | No | Portable | Gets Quiet. |
| `P:LibTmux.SetOptionRequest.Scope` | `OptionScope? LibTmux.SetOptionRequest.Scope { get; }` | Public | No | Portable | Gets Scope. |
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
| `M:LibTmux.SplitPaneRequest.#ctor(string?,string?,bool,PaneDirection?,bool,bool,string?,string?,int?,IReadOnlyDictionary<string,string>?,bool,string?,string?,string?,string?,bool)` | `SplitPaneRequest(string? target = null, string? startDirectory = null, bool attach = false, PaneDirection? direction = null, bool fullWindow = false, bool zoom = false, string? command = null, string? size = null, int? percentage = null, IReadOnlyDictionary<string,string>? environment = null, bool empty = false, string? style = null, string? activeBorderStyle = null, string? inactiveBorderStyle = null, string? message = null, bool keepOpen = false)` | Public | No | Portable | Creates SplitPaneRequest. |
| `P:LibTmux.SplitPaneRequest.ActiveBorderStyle` | `string? LibTmux.SplitPaneRequest.ActiveBorderStyle { get; }` | Public | No | Portable | Gets ActiveBorderStyle. |
| `P:LibTmux.SplitPaneRequest.Attach` | `bool LibTmux.SplitPaneRequest.Attach { get; }` | Public | No | Portable | Gets Attach. |
| `P:LibTmux.SplitPaneRequest.Command` | `string? LibTmux.SplitPaneRequest.Command { get; }` | Public | No | Portable | Gets Command. |
| `P:LibTmux.SplitPaneRequest.Direction` | `PaneDirection? LibTmux.SplitPaneRequest.Direction { get; }` | Public | No | Portable | Gets Direction. |
| `P:LibTmux.SplitPaneRequest.Empty` | `bool LibTmux.SplitPaneRequest.Empty { get; }` | Public | No | Portable | Gets Empty. |
| `P:LibTmux.SplitPaneRequest.Environment` | `IReadOnlyDictionary<string,string>? LibTmux.SplitPaneRequest.Environment { get; }` | Public | No | Portable | Gets Environment. |
| `P:LibTmux.SplitPaneRequest.FullWindow` | `bool LibTmux.SplitPaneRequest.FullWindow { get; }` | Public | No | Portable | Gets FullWindow. |
| `P:LibTmux.SplitPaneRequest.InactiveBorderStyle` | `string? LibTmux.SplitPaneRequest.InactiveBorderStyle { get; }` | Public | No | Portable | Gets InactiveBorderStyle. |
| `P:LibTmux.SplitPaneRequest.KeepOpen` | `bool LibTmux.SplitPaneRequest.KeepOpen { get; }` | Public | No | Portable | Gets KeepOpen. |
| `P:LibTmux.SplitPaneRequest.Message` | `string? LibTmux.SplitPaneRequest.Message { get; }` | Public | No | Portable | Gets Message. |
| `P:LibTmux.SplitPaneRequest.Percentage` | `int? LibTmux.SplitPaneRequest.Percentage { get; }` | Public | No | Portable | Gets Percentage. |
| `P:LibTmux.SplitPaneRequest.Size` | `string? LibTmux.SplitPaneRequest.Size { get; }` | Public | No | Portable | Gets Size. |
| `P:LibTmux.SplitPaneRequest.StartDirectory` | `string? LibTmux.SplitPaneRequest.StartDirectory { get; }` | Public | No | Portable | Gets StartDirectory. |
| `P:LibTmux.SplitPaneRequest.Style` | `string? LibTmux.SplitPaneRequest.Style { get; }` | Public | No | Portable | Gets Style. |
| `P:LibTmux.SplitPaneRequest.Target` | `string? LibTmux.SplitPaneRequest.Target { get; }` | Public | No | Portable | Gets Target. |
| `P:LibTmux.SplitPaneRequest.Zoom` | `bool LibTmux.SplitPaneRequest.Zoom { get; }` | Public | No | Portable | Gets Zoom. |

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
| `M:LibTmux.SwapPaneRequest.#ctor(string?,PaneSwapDirection?,bool,bool)` | `SwapPaneRequest(string? target = null, PaneSwapDirection? direction = null, bool detach = false, bool keepZoom = false)` | Public | No | Portable | Creates SwapPaneRequest. |
| `P:LibTmux.SwapPaneRequest.Detach` | `bool LibTmux.SwapPaneRequest.Detach { get; }` | Public | No | Portable | Gets Detach. |
| `P:LibTmux.SwapPaneRequest.Direction` | `PaneSwapDirection? LibTmux.SwapPaneRequest.Direction { get; }` | Public | No | Portable | Gets Direction. |
| `P:LibTmux.SwapPaneRequest.KeepZoom` | `bool LibTmux.SwapPaneRequest.KeepZoom { get; }` | Public | No | Portable | Gets KeepZoom. |
| `P:LibTmux.SwapPaneRequest.Target` | `string? LibTmux.SwapPaneRequest.Target { get; }` | Public | No | Portable | Gets Target. |

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

### `T:LibTmux.Testing.TmuxWait`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.Testing.TmuxWait.UntilAsync(Func<CancellationToken,Task<bool>>,TimeSpan,TimeSpan,bool,CancellationToken)` | `static Task<bool> LibTmux.Testing.TmuxWait.UntilAsync(Func<CancellationToken,Task<bool>> probe, TimeSpan timeout, TimeSpan interval, bool throwOnTimeout = true, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Polls a Boolean probe and optionally returns false on timeout. |
| ``M:LibTmux.Testing.TmuxWait.UntilAsync``1(Func<CancellationToken,Task<T>>,Func<T,bool>,TimeSpan,TimeSpan,CancellationToken)`` | `static Task<T> LibTmux.Testing.TmuxWait.UntilAsync<T>(Func<CancellationToken,Task<T>> probe, Func<T,bool> predicate, TimeSpan timeout, TimeSpan interval, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Polls with a deadline and caller cancellation. |

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
| `M:LibTmux.TmuxChain.Then(string,string[])` | `TmuxChain Then(string name, params string[] arguments)` | Public | No | Portable | Adds one command by name and returns the longer chain. |
| `P:LibTmux.TmuxChain.Commands` | `IReadOnlyList<TmuxCommand> LibTmux.TmuxChain.Commands { get; }` | Public | No | Portable | Gets the commands this chain will run, in order. |

### `T:LibTmux.TmuxChaining`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.AttachSessionRequest,LibTmux.Session,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this AttachSessionRequest request, Session session, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Runs a attach request on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.BindKeyRequest,LibTmux.Server,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this BindKeyRequest request, Server server, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Runs a key-binding request on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.CapturePaneRequest,LibTmux.Pane,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this CapturePaneRequest request, Pane pane, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Runs a capture request on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.ChooseTreeRequest,LibTmux.Pane,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this ChooseTreeRequest request, Pane pane, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Runs a chooser request on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.CommandPromptRequest,LibTmux.Server,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this CommandPromptRequest request, Server server, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Runs a prompt request on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.ConfirmBeforeRequest,LibTmux.Server,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this ConfirmBeforeRequest request, Server server, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Runs a confirmation request on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.CopyModeRequest,LibTmux.Pane,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this CopyModeRequest request, Pane pane, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Runs a copy-mode request on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.DisplayMenuRequest,LibTmux.Server,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this DisplayMenuRequest request, Server server, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Runs a menu request on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.DisplayMessageRequest,LibTmux.Server,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this DisplayMessageRequest request, Server server, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Runs a message request on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.DisplayPopupRequest,LibTmux.Pane,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this DisplayPopupRequest request, Pane pane, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Runs a popup request on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.FindWindowRequest,LibTmux.Pane,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this FindWindowRequest request, Pane pane, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Runs a window-search request on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.GetOptionRequest,LibTmux.TmuxOptions,LibTmux.Server,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this GetOptionRequest request, TmuxOptions options, Server server, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Runs a named option read on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.GetOptionsRequest,LibTmux.TmuxOptions,LibTmux.Server,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this GetOptionsRequest request, TmuxOptions options, Server server, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Runs a whole-scope option read on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.HookRequest,LibTmux.TmuxHooks,LibTmux.Server,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this HookRequest request, TmuxHooks hooks, Server server, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Runs a named hook on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.IfShellRequest,LibTmux.Server,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this IfShellRequest request, Server server, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Runs a conditional request on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.LinkWindowRequest,LibTmux.Window,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this LinkWindowRequest request, Window window, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Runs a link request on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.ListBuffersRequest,LibTmux.Server,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this ListBuffersRequest request, Server server, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Runs a buffer-listing request on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.ListHooksRequest,LibTmux.TmuxHooks,LibTmux.Server,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this ListHooksRequest request, TmuxHooks hooks, Server server, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Runs a hook listing on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.MovePaneRequest,LibTmux.Pane,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this MovePaneRequest request, Pane pane, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Runs a pane-move request on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.MoveWindowRequest,LibTmux.Window,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this MoveWindowRequest request, Window window, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Runs a window-move request on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.NewPaneRequest,LibTmux.Pane,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this NewPaneRequest request, Pane pane, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Runs a floating-pane request on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.NewSessionRequest,LibTmux.Server,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this NewSessionRequest request, Server server, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Runs a session request on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.NewWindowRequest,LibTmux.Session,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this NewWindowRequest request, Session session, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Runs a window request on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.PasteBufferRequest,LibTmux.Pane,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this PasteBufferRequest request, Pane pane, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Runs a paste request on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.PipePaneRequest,LibTmux.Pane,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this PipePaneRequest request, Pane pane, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Runs a pane-piping request on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.ResizePaneRequest,LibTmux.Pane,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this ResizePaneRequest request, Pane pane, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Runs a pane-resize request on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.ResizeWindowRequest,LibTmux.Window,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this ResizeWindowRequest request, Window window, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Runs a window-resize request on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.RespawnRequest,LibTmux.Pane,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this RespawnRequest request, Pane pane, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Runs a respawn request on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.RunShellRequest,LibTmux.Server,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this RunShellRequest request, Server server, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Runs a shell request on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.SelectLayoutRequest,LibTmux.Window,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this SelectLayoutRequest request, Window window, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Runs a layout request on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.SelectPaneRequest,LibTmux.Pane,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this SelectPaneRequest request, Pane pane, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Runs a pane-selection request on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.SendKeysRequest,LibTmux.Pane,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this SendKeysRequest request, Pane pane, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Runs a key request on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.ServerAccessRequest,LibTmux.Server,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this ServerAccessRequest request, Server server, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Runs an access request on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.SetHookRequest,LibTmux.TmuxHooks,LibTmux.Server,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this SetHookRequest request, TmuxHooks hooks, Server server, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Runs a hook request on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.SetHooksRequest,LibTmux.TmuxHooks,LibTmux.Server,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this SetHooksRequest request, TmuxHooks hooks, Server server, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Runs a multi-entry hook request in one invocation. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.SetOptionRequest,LibTmux.TmuxOptions,LibTmux.Server,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this SetOptionRequest request, TmuxOptions options, Server server, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Runs an option request on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.SplitPaneRequest,LibTmux.Pane,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this SplitPaneRequest request, Pane pane, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Runs a split request on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.SwapPaneRequest,LibTmux.Pane,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this SwapPaneRequest request, Pane pane, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Runs a pane-swap request on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.UnbindKeyRequest,LibTmux.Server,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this UnbindKeyRequest request, Server server, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Runs a key-unbinding request on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.UnsetOptionRequest,LibTmux.TmuxOptions,LibTmux.Server,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this UnsetOptionRequest request, TmuxOptions options, Server server, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Runs an unset request on its own. |
| `M:LibTmux.TmuxChaining.ExecuteAsync(LibTmux.WaitForRequest,LibTmux.Server,System.Threading.CancellationToken)` | `static static Task<TmuxCommandResult> ExecuteAsync(this WaitForRequest request, Server server, CancellationToken cancellationToken = default)` | Public | Yes | Portable | Runs a channel request on its own. |
| `M:LibTmux.TmuxChaining.ToCommand(LibTmux.AttachSessionRequest,LibTmux.Session)` | `static static TmuxCommand ToCommand(this AttachSessionRequest request, Session session)` | Public | Yes | Portable | Returns a attach request as one tmux command. |
| `M:LibTmux.TmuxChaining.ToCommand(LibTmux.BindKeyRequest)` | `static static TmuxCommand ToCommand(this BindKeyRequest request)` | Public | Yes | Portable | Returns a key-binding request as one tmux command. |
| `M:LibTmux.TmuxChaining.ToCommand(LibTmux.CapturePaneRequest,LibTmux.Pane)` | `static static TmuxCommand ToCommand(this CapturePaneRequest request, Pane pane)` | Public | Yes | Portable | Returns a capture request as one tmux command. |
| `M:LibTmux.TmuxChaining.ToCommand(LibTmux.ChooseTreeRequest,LibTmux.Pane)` | `static static TmuxCommand ToCommand(this ChooseTreeRequest request, Pane pane)` | Public | Yes | Portable | Returns a chooser request as one tmux command. |
| `M:LibTmux.TmuxChaining.ToCommand(LibTmux.CommandPromptRequest,LibTmux.Server)` | `static static TmuxCommand ToCommand(this CommandPromptRequest request, Server server)` | Public | Yes | Portable | Returns a prompt request as one tmux command. |
| `M:LibTmux.TmuxChaining.ToCommand(LibTmux.ConfirmBeforeRequest,LibTmux.Server)` | `static static TmuxCommand ToCommand(this ConfirmBeforeRequest request, Server server)` | Public | Yes | Portable | Returns a confirmation request as one tmux command. |
| `M:LibTmux.TmuxChaining.ToCommand(LibTmux.CopyModeRequest,LibTmux.Pane)` | `static static TmuxCommand ToCommand(this CopyModeRequest request, Pane pane)` | Public | Yes | Portable | Returns a copy-mode request as one tmux command. |
| `M:LibTmux.TmuxChaining.ToCommand(LibTmux.DisplayMenuRequest,LibTmux.Server)` | `static static TmuxCommand ToCommand(this DisplayMenuRequest request, Server server)` | Public | Yes | Portable | Returns a menu request as one tmux command. |
| `M:LibTmux.TmuxChaining.ToCommand(LibTmux.DisplayMessageRequest,LibTmux.Server)` | `static static TmuxCommand ToCommand(this DisplayMessageRequest request, Server server)` | Public | Yes | Portable | Returns a message request as one tmux command. |
| `M:LibTmux.TmuxChaining.ToCommand(LibTmux.DisplayPopupRequest,LibTmux.Pane)` | `static static TmuxCommand ToCommand(this DisplayPopupRequest request, Pane pane)` | Public | Yes | Portable | Returns a popup request as one tmux command. |
| `M:LibTmux.TmuxChaining.ToCommand(LibTmux.FindWindowRequest,LibTmux.Pane)` | `static static TmuxCommand ToCommand(this FindWindowRequest request, Pane pane)` | Public | Yes | Portable | Returns a window-search request as one tmux command. |
| `M:LibTmux.TmuxChaining.ToCommand(LibTmux.GetOptionRequest,LibTmux.TmuxOptions)` | `static static TmuxCommand ToCommand(this GetOptionRequest request, TmuxOptions options)` | Public | Yes | Portable | Returns a named option read as one tmux command. |
| `M:LibTmux.TmuxChaining.ToCommand(LibTmux.GetOptionsRequest,LibTmux.TmuxOptions)` | `static static TmuxCommand ToCommand(this GetOptionsRequest request, TmuxOptions options)` | Public | Yes | Portable | Returns a whole-scope option read as one tmux command. |
| `M:LibTmux.TmuxChaining.ToCommand(LibTmux.IfShellRequest)` | `static static TmuxCommand ToCommand(this IfShellRequest request)` | Public | Yes | Portable | Returns a conditional request as one tmux command. |
| `M:LibTmux.TmuxChaining.ToCommand(LibTmux.LinkWindowRequest,LibTmux.Window)` | `static static TmuxCommand ToCommand(this LinkWindowRequest request, Window window)` | Public | Yes | Portable | Returns a link request as one tmux command. |
| `M:LibTmux.TmuxChaining.ToCommand(LibTmux.ListBuffersRequest)` | `static static TmuxCommand ToCommand(this ListBuffersRequest request)` | Public | Yes | Portable | Returns a buffer-listing request as one tmux command. |
| `M:LibTmux.TmuxChaining.ToCommand(LibTmux.ListHooksRequest,LibTmux.TmuxHooks)` | `static static TmuxCommand ToCommand(this ListHooksRequest request, TmuxHooks hooks)` | Public | Yes | Portable | Returns a hook listing as one tmux command. |
| `M:LibTmux.TmuxChaining.ToCommand(LibTmux.MovePaneRequest,LibTmux.Pane)` | `static static TmuxCommand ToCommand(this MovePaneRequest request, Pane pane)` | Public | Yes | Portable | Returns a pane-move request as one tmux command. |
| `M:LibTmux.TmuxChaining.ToCommand(LibTmux.MoveWindowRequest,LibTmux.Window)` | `static static TmuxCommand ToCommand(this MoveWindowRequest request, Window window)` | Public | Yes | Portable | Returns a window-move request as one tmux command. |
| `M:LibTmux.TmuxChaining.ToCommand(LibTmux.NewPaneRequest,LibTmux.Pane)` | `static static TmuxCommand ToCommand(this NewPaneRequest request, Pane pane)` | Public | Yes | Portable | Returns a floating-pane request as one tmux command. |
| `M:LibTmux.TmuxChaining.ToCommand(LibTmux.NewSessionRequest)` | `static static TmuxCommand ToCommand(this NewSessionRequest request)` | Public | Yes | Portable | Returns a session request as one tmux command. |
| `M:LibTmux.TmuxChaining.ToCommand(LibTmux.NewWindowRequest,string)` | `static static TmuxCommand ToCommand(this NewWindowRequest request, string target)` | Public | Yes | Portable | Returns a window request as one tmux command. |
| `M:LibTmux.TmuxChaining.ToCommand(LibTmux.PasteBufferRequest,LibTmux.Pane)` | `static static TmuxCommand ToCommand(this PasteBufferRequest request, Pane pane)` | Public | Yes | Portable | Returns a paste request as one tmux command. |
| `M:LibTmux.TmuxChaining.ToCommand(LibTmux.PipePaneRequest,LibTmux.Pane)` | `static static TmuxCommand ToCommand(this PipePaneRequest request, Pane pane)` | Public | Yes | Portable | Returns a pane-piping request as one tmux command. |
| `M:LibTmux.TmuxChaining.ToCommand(LibTmux.ResizePaneRequest,LibTmux.Pane)` | `static static TmuxCommand ToCommand(this ResizePaneRequest request, Pane pane)` | Public | Yes | Portable | Returns a pane-resize request as one tmux command. |
| `M:LibTmux.TmuxChaining.ToCommand(LibTmux.ResizeWindowRequest,LibTmux.Window)` | `static static TmuxCommand ToCommand(this ResizeWindowRequest request, Window window)` | Public | Yes | Portable | Returns a window-resize request as one tmux command. |
| `M:LibTmux.TmuxChaining.ToCommand(LibTmux.RespawnRequest,LibTmux.Pane)` | `static static TmuxCommand ToCommand(this RespawnRequest request, Pane pane)` | Public | Yes | Portable | Returns a respawn request as one tmux command. |
| `M:LibTmux.TmuxChaining.ToCommand(LibTmux.RunShellRequest,LibTmux.Server)` | `static static TmuxCommand ToCommand(this RunShellRequest request, Server server)` | Public | Yes | Portable | Returns a shell request as one tmux command. |
| `M:LibTmux.TmuxChaining.ToCommand(LibTmux.SelectLayoutRequest,LibTmux.Window)` | `static static TmuxCommand ToCommand(this SelectLayoutRequest request, Window window)` | Public | Yes | Portable | Returns a layout request as one tmux command for a window. |
| `M:LibTmux.TmuxChaining.ToCommand(LibTmux.SelectPaneRequest,LibTmux.Pane)` | `static static TmuxCommand ToCommand(this SelectPaneRequest request, Pane pane)` | Public | Yes | Portable | Returns a pane-selection request as one tmux command. |
| `M:LibTmux.TmuxChaining.ToCommand(LibTmux.SendKeysRequest,LibTmux.Pane)` | `static static TmuxCommand ToCommand(this SendKeysRequest request, Pane pane)` | Public | Yes | Portable | Returns a key request as one tmux command for a pane. |
| `M:LibTmux.TmuxChaining.ToCommand(LibTmux.ServerAccessRequest,LibTmux.Server)` | `static static TmuxCommand ToCommand(this ServerAccessRequest request, Server server)` | Public | Yes | Portable | Returns an access request as one tmux command. |
| `M:LibTmux.TmuxChaining.ToCommand(LibTmux.SetHookRequest,LibTmux.TmuxHooks)` | `static static TmuxCommand ToCommand(this SetHookRequest request, TmuxHooks hooks)` | Public | Yes | Portable | Returns a hook request as one tmux command. |
| `M:LibTmux.TmuxChaining.ToCommand(LibTmux.SetOptionRequest,LibTmux.TmuxOptions)` | `static static TmuxCommand ToCommand(this SetOptionRequest request, TmuxOptions options)` | Public | Yes | Portable | Returns an option request as one tmux command. |
| `M:LibTmux.TmuxChaining.ToCommand(LibTmux.SplitPaneRequest,LibTmux.Pane)` | `static static TmuxCommand ToCommand(this SplitPaneRequest request, Pane pane)` | Public | Yes | Portable | Returns a split request as one tmux command. |
| `M:LibTmux.TmuxChaining.ToCommand(LibTmux.SwapPaneRequest,LibTmux.Pane)` | `static static TmuxCommand ToCommand(this SwapPaneRequest request, Pane pane)` | Public | Yes | Portable | Returns a pane-swap request as one tmux command. |
| `M:LibTmux.TmuxChaining.ToCommand(LibTmux.UnbindKeyRequest)` | `static static TmuxCommand ToCommand(this UnbindKeyRequest request)` | Public | Yes | Portable | Returns a key-unbinding request as one tmux command. |
| `M:LibTmux.TmuxChaining.ToCommand(LibTmux.UnsetOptionRequest,LibTmux.TmuxOptions)` | `static static TmuxCommand ToCommand(this UnsetOptionRequest request, TmuxOptions options)` | Public | Yes | Portable | Returns an unset request as one tmux command. |
| `M:LibTmux.TmuxChaining.ToCommand(LibTmux.WaitForRequest)` | `static static TmuxCommand ToCommand(this WaitForRequest request)` | Public | Yes | Portable | Returns a channel request as one tmux command. |
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
| `P:LibTmux.TmuxCommand.Arguments` | `IReadOnlyList<string> LibTmux.TmuxCommand.Arguments { get; }` | Public | No | Portable | Gets Arguments. |
| `P:LibTmux.TmuxCommand.Name` | `string LibTmux.TmuxCommand.Name { get; }` | Public | No | Portable | Gets Name. |

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
| `P:LibTmux.TmuxEventsDroppedEvent.Count` | `long LibTmux.TmuxEventsDroppedEvent.Count { get; }` | Public | No | Portable | Gets the events discarded since the previous loss report. |
| `P:LibTmux.TmuxEventsDroppedEvent.TotalDropped` | `long LibTmux.TmuxEventsDroppedEvent.TotalDropped { get; }` | Public | No | Portable | Gets the events discarded over this control client's lifetime. |

### `T:LibTmux.TmuxExitEvent`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.TmuxExitEvent.#ctor(string?)` | `TmuxExitEvent(string? Reason)` | Public | No | Portable | Creates TmuxExitEvent. |
| `P:LibTmux.TmuxExitEvent.Reason` | `string? LibTmux.TmuxExitEvent.Reason { get; }` | Public | No | Portable | Gets Reason. |

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
| `P:LibTmux.TmuxNotificationEvent.Arguments` | `IReadOnlyList<string> LibTmux.TmuxNotificationEvent.Arguments { get; }` | Public | No | Portable | Gets Arguments. |
| `P:LibTmux.TmuxNotificationEvent.Name` | `string LibTmux.TmuxNotificationEvent.Name { get; }` | Public | No | Portable | Gets Name. |

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
| `M:LibTmux.TmuxOption.#ctor(string,TmuxOptionValue,int?)` | `TmuxOption(string name, TmuxOptionValue value, int? index)` | Public | No | Portable | Creates TmuxOption. |
| `P:LibTmux.TmuxOption.Index` | `int? LibTmux.TmuxOption.Index { get; }` | Public | No | Portable | Gets Index. |
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
| `M:LibTmux.TmuxOutputEvent.#ctor(string,string)` | `TmuxOutputEvent(string PaneId, string Data)` | Public | No | Portable | Creates TmuxOutputEvent. |
| `P:LibTmux.TmuxOutputEvent.Data` | `string LibTmux.TmuxOutputEvent.Data { get; }` | Public | No | Portable | Gets Data. |
| `P:LibTmux.TmuxOutputEvent.PaneId` | `string LibTmux.TmuxOutputEvent.PaneId { get; }` | Public | No | Portable | Gets PaneId. |

### `T:LibTmux.TmuxPaneException`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.TmuxPaneException.#ctor(string,PaneId,Exception?)` | `TmuxPaneException(string message, PaneId paneId, Exception? innerException = null)` | Public | No | Portable | Creates TmuxPaneException. |
| `P:LibTmux.TmuxPaneException.PaneId` | `PaneId LibTmux.TmuxPaneException.PaneId { get; }` | Public | No | Portable | Gets PaneId. |

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

### `T:LibTmux.TmuxWaitChannel`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.TmuxWaitChannel.DisposeAsync()` | `ValueTask LibTmux.TmuxWaitChannel.DisposeAsync()` | Public | No | `UnsupportedOSPlatform("windows")` | Withdraws the waiter from tmux. |
| `M:LibTmux.TmuxWaitChannel.WaitAsync(TimeSpan,CancellationToken)` | `Task<bool> LibTmux.TmuxWaitChannel.WaitAsync(TimeSpan budget, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Waits for the signal, giving this attempt a budget. |
| `P:LibTmux.TmuxWaitChannel.Channel` | `string LibTmux.TmuxWaitChannel.Channel { get; }` | Public | No | Portable | Gets the channel being waited on. |
| `P:LibTmux.TmuxWaitChannel.Signalled` | `bool LibTmux.TmuxWaitChannel.Signalled { get; }` | Public | No | Portable | Gets whether something really signalled the channel. |

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
| `P:LibTmux.TmuxWindowException.WindowId` | `WindowId LibTmux.TmuxWindowException.WindowId { get; }` | Public | No | Portable | Gets WindowId. |

### `T:LibTmux.UnbindKeyRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.UnbindKeyRequest.#ctor(string?,string?,bool,bool)` | `UnbindKeyRequest(string? key = null, string? keyTable = null, bool all = false, bool quiet = false)` | Public | No | Portable | Creates UnbindKeyRequest. |
| `P:LibTmux.UnbindKeyRequest.All` | `bool LibTmux.UnbindKeyRequest.All { get; }` | Public | No | Portable | Gets All. |
| `P:LibTmux.UnbindKeyRequest.Key` | `string? LibTmux.UnbindKeyRequest.Key { get; }` | Public | No | Portable | Gets Key. |
| `P:LibTmux.UnbindKeyRequest.KeyTable` | `string? LibTmux.UnbindKeyRequest.KeyTable { get; }` | Public | No | Portable | Gets KeyTable. |
| `P:LibTmux.UnbindKeyRequest.Quiet` | `bool LibTmux.UnbindKeyRequest.Quiet { get; }` | Public | No | Portable | Gets Quiet. |

### `T:LibTmux.UnsafeTmuxFilter`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.UnsafeTmuxFilter.#ctor(string)` | `UnsafeTmuxFilter(string value)` | Public | No | Portable | Creates UnsafeTmuxFilter. |
| `P:LibTmux.UnsafeTmuxFilter.Value` | `string LibTmux.UnsafeTmuxFilter.Value { get; }` | Public | No | Portable | Gets Value. |

### `T:LibTmux.UnsetOptionRequest`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.UnsetOptionRequest.#ctor(string,OptionScope?,bool,bool,bool)` | `UnsetOptionRequest(string name, OptionScope? scope = null, bool global = false, bool unsetPaneOverrides = false, bool quiet = false)` | Public | No | Portable | Creates UnsetOptionRequest. |
| `P:LibTmux.UnsetOptionRequest.Global` | `bool LibTmux.UnsetOptionRequest.Global { get; }` | Public | No | Portable | Gets Global. |
| `P:LibTmux.UnsetOptionRequest.Name` | `string LibTmux.UnsetOptionRequest.Name { get; }` | Public | No | Portable | Gets Name. |
| `P:LibTmux.UnsetOptionRequest.Quiet` | `bool LibTmux.UnsetOptionRequest.Quiet { get; }` | Public | No | Portable | Gets Quiet. |
| `P:LibTmux.UnsetOptionRequest.Scope` | `OptionScope? LibTmux.UnsetOptionRequest.Scope { get; }` | Public | No | Portable | Gets Scope. |
| `P:LibTmux.UnsetOptionRequest.UnsetPaneOverrides` | `bool LibTmux.UnsetOptionRequest.UnsetPaneOverrides { get; }` | Public | No | Portable | Gets UnsetPaneOverrides. |

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
| `P:LibTmux.WaitForRequest.Channel` | `string LibTmux.WaitForRequest.Channel { get; }` | Public | No | Portable | Gets Channel. |
| `P:LibTmux.WaitForRequest.Mode` | `TmuxWaitMode LibTmux.WaitForRequest.Mode { get; }` | Public | No | Portable | Gets Mode. |

### `T:LibTmux.Window`

| Member ID | Declaration | Visibility | Static | Platform | Notes |
| --- | --- | --- | --- | --- | --- |
| `M:LibTmux.Window.CreatePaneAsync(NewPaneRequest?,CancellationToken)` | `Task<Pane> LibTmux.Window.CreatePaneAsync(NewPaneRequest? request = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs CreatePane. |
| `M:LibTmux.Window.CreateWindowAsync(NewWindowRequest?,CancellationToken)` | `Task<Window> LibTmux.Window.CreateWindowAsync(NewWindowRequest? request = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs CreateWindow. |
| `M:LibTmux.Window.DisplayMessageAsync(DisplayMessageRequest,CancellationToken)` | `Task<IReadOnlyList<string>?> LibTmux.Window.DisplayMessageAsync(DisplayMessageRequest request, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Performs DisplayMessage. |
| `M:LibTmux.Window.ExecuteCommandAsync(IReadOnlyList<string>,string?,CancellationToken)` | `Task<TmuxCommandResult> LibTmux.Window.ExecuteCommandAsync(IReadOnlyList<string> arguments, string? targetOverride = null, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Executes a raw command with stable target injection for the entity handle. |
| `M:LibTmux.Window.FromEnvironmentAsync(IReadOnlyDictionary<string,string>?,CancellationToken)` | `static Task<Window> LibTmux.Window.FromEnvironmentAsync(IReadOnlyDictionary<string,string>? environment = null, CancellationToken cancellationToken = default)` | Public | Yes | `UnsupportedOSPlatform("windows")` | Performs FromEnvironment. |
| `M:LibTmux.Window.GetLinkedSessionsAsync(CancellationToken)` | `Task<IReadOnlyList<Session>> LibTmux.Window.GetLinkedSessionsAsync(CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Returns captured empty when either required listing fails. List error policy: empty-if-either-required-list-fails. |
| `M:LibTmux.Window.GetPaneAsync(string,CancellationToken)` | `Task<Pane?> LibTmux.Window.GetPaneAsync(string target, CancellationToken cancellationToken = default)` | Public | No | `UnsupportedOSPlatform("windows")` | Gets one pane in this window. |
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
| `P:LibTmux.Window.ActivePane` | `Pane? LibTmux.Window.ActivePane { get; }` | Public | No | Portable | Gets the captured ActivePane value. |
| `P:LibTmux.Window.Edge` | `SessionWindowEdge LibTmux.Window.Edge { get; }` | Public | No | Portable | Gets the captured Edge value. |
| `P:LibTmux.Window.EntityKey` | `WindowEntityKey LibTmux.Window.EntityKey { get; }` | Public | No | Portable | Gets the captured EntityKey value. |
| `P:LibTmux.Window.Generation` | `ServerGeneration LibTmux.Window.Generation { get; }` | Public | No | Portable | Gets the captured Generation value. |
| `P:LibTmux.Window.Height` | `int LibTmux.Window.Height { get; }` | Public | No | Portable | Gets the captured Height value. |
| `P:LibTmux.Window.Hooks` | `TmuxHooks LibTmux.Window.Hooks { get; }` | Public | No | Portable | Gets the captured Hooks value. |
| `P:LibTmux.Window.Id` | `WindowId LibTmux.Window.Id { get; }` | Public | No | Portable | Gets the captured Id value. |
| `P:LibTmux.Window.Index` | `int LibTmux.Window.Index { get; }` | Public | No | Portable | Gets the captured Index value. |
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
| `M:LibTmux.WindowId.Parse(string)` | `static WindowId LibTmux.WindowId.Parse(string text)` | Public | Yes | Portable | Parses a prefixed identifier. |
| `M:LibTmux.WindowId.ToString()` | `string LibTmux.WindowId.ToString()` | Public | No | Portable | Returns the canonical prefixed identifier. |
| `M:LibTmux.WindowId.TryParse(string?,WindowId)` | `static bool LibTmux.WindowId.TryParse(string? text, out WindowId result)` | Public | Yes | Portable | Tries to parse a prefixed identifier without throwing. |
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
