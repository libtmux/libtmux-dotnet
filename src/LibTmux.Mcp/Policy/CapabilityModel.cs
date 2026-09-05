using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace LibTmux.Mcp;

internal enum Toolset
{
    Inspect,
    Manage,
    Execute,
    Teardown,
}

internal enum ProcessReach
{
    None,
    ConfiguredProcess,
    PaneInput,
    PaneCommand,
}

internal enum Effect
{
    Observe,
    Change,
    Delete,
}

internal enum OutputClass
{
    TmuxMetadata,
    TerminalContent,
    ProcessEnvironment,
    ConfiguredCommand,
}

internal enum InputSink
{
    None,
    TmuxLookup,
    TmuxState,
    PaneInput,
    ShellCommand,
    ProcessArgv,
    Regex,
    TmuxFormat,
    NestedTool,
}

internal sealed record CapabilityAnnotations(
    bool ReadOnly,
    bool Destructive,
    bool Idempotent,
    bool OpenWorld)
{
    internal static CapabilityAnnotations Conservative { get; } = new(
        ReadOnly: false,
        Destructive: true,
        Idempotent: false,
        OpenWorld: true);
}

[UnsupportedOSPlatform("windows")]
internal sealed record ToolDefinition(
    string Name,
    string Title,
    string Description,
    Toolset Toolset,
    ProcessReach Reach,
    ImmutableHashSet<Effect> Effects,
    ImmutableHashSet<OutputClass> OutputClasses,
    bool MayExposeSecrets,
    bool MayReturnUntrustedContent,
    bool AmplifiesFutureInput,
    CapabilityAnnotations Annotations,
    MethodInfo Handler,
    FrozenDictionary<string, ImmutableHashSet<InputSink>> InputSinks,
    FrozenDictionary<string, string> InputLiteralization,
    ImmutableHashSet<string> NestedAuthority,
    bool SelfBounded = false,
    bool BatchEligible = false)
{
    internal const string CapabilityMetadataKey = "com.git-pull.libtmux-mcp/capability";

    internal JsonElement InputSchema { get; init; }

    internal JsonElement OutputSchema { get; init; }

    internal McpServerTool CreateTool()
    {
        McpServerTool tool = CreateNativeTool(includeCapabilityMetadata: true);
        tool.ProtocolTool.InputSchema = InputSchema;
        tool.ProtocolTool.OutputSchema = OutputSchema;

        return tool;
    }

    internal McpServerTool CreateNativeTool(bool includeCapabilityMetadata)
    {
        JsonObject? metadata = includeCapabilityMetadata
            ? new JsonObject { [CapabilityMetadataKey] = CapabilityRow() }
            : null;
        return McpServerTool.Create(
            Handler,
            context => context.Services!.GetRequiredService<CapabilityTools>(),
            new McpServerToolCreateOptions
            {
                Name = Name,
                Title = Title,
                Description = Description,
                ReadOnly = Annotations.ReadOnly,
                Destructive = Annotations.Destructive,
                Idempotent = Annotations.Idempotent,
                OpenWorld = Annotations.OpenWorld,
                UseStructuredContent = true,
                SerializerOptions = ToolJson.Options,
                Meta = metadata,
            });
    }

    internal JsonObject CapabilityRow() => new()
    {
        ["name"] = Name,
        ["title"] = Title,
        ["description"] = Description,
        ["toolset"] = Kebab(Toolset),
        ["processReach"] = Kebab(Reach),
        ["tmuxEffects"] = JsonSerializer.SerializeToNode(
            Effects.Select(Kebab).Order(StringComparer.Ordinal)),
        ["outputClasses"] = JsonSerializer.SerializeToNode(
            OutputClasses.Select(Kebab).Order(StringComparer.Ordinal)),
        ["mayExposeSecrets"] = MayExposeSecrets,
        ["mayReturnUntrustedContent"] = MayReturnUntrustedContent,
        ["amplifiesFutureInput"] = AmplifiesFutureInput,
        ["annotations"] = new JsonObject
        {
            ["readOnlyHint"] = Annotations.ReadOnly,
            ["destructiveHint"] = Annotations.Destructive,
            ["idempotentHint"] = Annotations.Idempotent,
            ["openWorldHint"] = Annotations.OpenWorld,
        },
        ["inputLiteralization"] = JsonSerializer.SerializeToNode(InputLiteralization),
        ["nestedAuthority"] = JsonSerializer.SerializeToNode(
            NestedAuthority.Order(StringComparer.Ordinal)),
        ["inputSchema"] = JsonNode.Parse(InputSchema.GetRawText()),
        ["outputSchema"] = JsonNode.Parse(OutputSchema.GetRawText()),
    };

    private static string Kebab<T>(T value)
        where T : struct, Enum => value.ToString()
            .Replace("ConfiguredProcess", "configured-process", StringComparison.Ordinal)
            .Replace("PaneInput", "pane-input", StringComparison.Ordinal)
            .Replace("PaneCommand", "pane-command", StringComparison.Ordinal)
            .Replace("TmuxMetadata", "tmux-metadata", StringComparison.Ordinal)
            .Replace("TerminalContent", "terminal-content", StringComparison.Ordinal)
            .Replace("ProcessEnvironment", "process-environment", StringComparison.Ordinal)
            .Replace("ConfiguredCommand", "configured-command", StringComparison.Ordinal)
            .Replace("TmuxLookup", "tmux-lookup", StringComparison.Ordinal)
            .Replace("TmuxState", "tmux-state", StringComparison.Ordinal)
            .Replace("ShellCommand", "shell-command", StringComparison.Ordinal)
            .Replace("ProcessArgv", "process-argv", StringComparison.Ordinal)
            .Replace("TmuxFormat", "tmux-format", StringComparison.Ordinal)
            .Replace("NestedTool", "nested-tool", StringComparison.Ordinal)
            .ToLowerInvariant();
}

[UnsupportedOSPlatform("windows")]
internal sealed class CapabilityRegistry
{
    private static readonly ImmutableArray<ToolDefinition> AllDefinitions = BuildManifest();

    private CapabilityRegistry(
        CapabilitySelection selection,
        IEnumerable<ToolDefinition> definitions,
        IEnumerable<ToolDefinition> dispatchDefinitions)
    {
        Selection = selection;
        Definitions = [.. definitions];
        Validate(Definitions);
        ImmutableArray<ToolDefinition> dispatch = [.. dispatchDefinitions];
        Validate(dispatch);
        DispatchByName = dispatch.ToFrozenDictionary(
            definition => definition.Name,
            StringComparer.Ordinal);
        FrozenDictionary<string, McpServerTool> registrations = dispatch
            .ToFrozenDictionary(
                definition => definition.Name,
                definition => definition.CreateTool(),
                StringComparer.Ordinal);
        Tools = [.. Definitions.Select(definition => registrations[definition.Name])];
        DispatchSchemas = dispatch.ToFrozenDictionary(
            definition => definition.Name,
            definition => definition.InputSchema,
            StringComparer.Ordinal);
        ValidateRegistrationParity(Definitions, Tools);
    }

    internal ImmutableArray<ToolDefinition> Definitions { get; }

    internal CapabilitySelection Selection { get; }

    internal ImmutableArray<McpServerTool> Tools { get; }

    internal FrozenDictionary<string, ToolDefinition> DispatchByName { get; }

    internal FrozenDictionary<string, JsonElement> DispatchSchemas { get; }

    internal FrozenDictionary<string, ToolDefinition> ByName =>
        Definitions.ToFrozenDictionary(definition => definition.Name, StringComparer.Ordinal);

    internal static ImmutableArray<ToolDefinition> Manifest => AllDefinitions;

    internal static CapabilityRegistry All() => new(
        CapabilitySelection.All,
        AllDefinitions,
        AllDefinitions);

    internal static CapabilityRegistry Select(CapabilitySelection selection)
    {
        ImmutableArray<ToolDefinition> visible =
        [
            .. AllDefinitions.Where(selection.Includes).Select(definition =>
                string.Equals(definition.Name, "call_read_tools_batch", StringComparison.Ordinal)
                    ? ShapeReadBatch(
                        definition,
                        definition.NestedAuthority
                            .Except(selection.ExcludedNames, StringComparer.Ordinal))
                    : definition),
        ];
        HashSet<string> dispatchNames = visible.Select(definition => definition.Name)
            .Concat(visible.SelectMany(definition => definition.NestedAuthority))
            .ToHashSet(StringComparer.Ordinal);
        Dictionary<string, ToolDefinition> visibleByName = visible.ToDictionary(
            definition => definition.Name,
            StringComparer.Ordinal);
        IEnumerable<ToolDefinition> dispatch = AllDefinitions
            .Where(definition => dispatchNames.Contains(definition.Name))
            .Select(definition => visibleByName.GetValueOrDefault(definition.Name, definition));
        return new CapabilityRegistry(selection, visible, dispatch);
    }

    private static ImmutableArray<ToolDefinition> BuildManifest()
    {
        ImmutableArray<ToolDefinition> definitions =
            [.. BuildDefinitions().Select(AttachNativeSchemas)];
        ToolDefinition batch = definitions.Single(definition =>
            string.Equals(definition.Name, "call_read_tools_batch", StringComparison.Ordinal));
        ToolDefinition shaped = ShapeReadBatch(batch, batch.NestedAuthority, definitions);
        ToolDefinition sendBatch = definitions.Single(definition =>
            string.Equals(definition.Name, "send_keys_batch", StringComparison.Ordinal));
        ToolDefinition shapedSendBatch = ShapeSendKeysBatch(sendBatch);
        return
        [
            .. definitions.Select(definition =>
                string.Equals(definition.Name, batch.Name, StringComparison.Ordinal)
                    ? shaped
                    : string.Equals(definition.Name, sendBatch.Name, StringComparison.Ordinal)
                        ? shapedSendBatch
                    : definition),
        ];
    }

    private static ToolDefinition ShapeSendKeysBatch(ToolDefinition definition)
    {
        JsonObject schema = JsonNode.Parse(definition.InputSchema.GetRawText())!.AsObject();
        JsonObject properties = schema["properties"]!.AsObject();
        JsonObject operations = properties["operations"]!.AsObject();
        operations["minItems"] = 1;
        operations["maxItems"] = 64;
        properties["onError"]!.AsObject()["enum"] = new JsonArray("continue", "stop");
        return definition with
        {
            InputSchema = JsonSerializer.SerializeToElement(schema, ToolJson.Options),
        };
    }

    private static ToolDefinition AttachNativeSchemas(ToolDefinition definition)
    {
        McpServerTool native = definition.CreateNativeTool(includeCapabilityMetadata: false);
        JsonElement output = native.ProtocolTool.OutputSchema
            ?? throw new InvalidOperationException(
                $"Capability '{definition.Name}' has no native output schema.");
        return definition with
        {
            InputSchema = CloseNativeInputSchema(native.ProtocolTool.InputSchema),
            OutputSchema = output.Clone(),
        };
    }

    private static JsonElement CloseNativeInputSchema(JsonElement input)
    {
        JsonNode node = JsonNode.Parse(input.GetRawText())!;
        CloseObjects(node);
        return JsonSerializer.SerializeToElement(node, ToolJson.Options);
    }

    private static void CloseObjects(JsonNode node)
    {
        if (node is JsonArray array)
        {
            foreach (JsonNode? child in array)
            {
                if (child is not null)
                {
                    CloseObjects(child);
                }
            }

            return;
        }

        if (node is not JsonObject schema)
        {
            return;
        }

        if (schema["type"] is JsonValue type
            && type.TryGetValue(out string? value)
            && value == "object"
            && !schema.ContainsKey("additionalProperties"))
        {
            schema["additionalProperties"] = false;
        }

        foreach ((_, JsonNode? child) in schema.ToArray())
        {
            if (child is not null)
            {
                CloseObjects(child);
            }
        }
    }

    private static ToolDefinition ShapeReadBatch(
        ToolDefinition definition,
        IEnumerable<string> nestedNames,
        IEnumerable<ToolDefinition>? candidates = null)
    {
        ImmutableHashSet<string> names = nestedNames.ToImmutableHashSet(StringComparer.Ordinal);
        ImmutableArray<ToolDefinition> nested =
        [
            .. (candidates ?? AllDefinitions)
                .Where(candidate => names.Contains(candidate.Name))
                .OrderBy(candidate => candidate.Name, StringComparer.Ordinal),
        ];
        ImmutableHashSet<Effect> effects = nested.Length == 0
            ? E(Effect.Observe)
            : nested.SelectMany(candidate => candidate.Effects).ToImmutableHashSet();
        ImmutableHashSet<OutputClass> outputs = nested
            .SelectMany(candidate => candidate.OutputClasses)
            .ToImmutableHashSet();
        const string Detail = "Execute up to 16 declared inspect operations serially; "
            + "inner operations receive no separate approval.";
        ToolDefinition shaped = definition with
        {
            Effects = effects,
            OutputClasses = outputs,
            MayExposeSecrets = nested.Any(candidate => candidate.MayExposeSecrets),
            MayReturnUntrustedContent = nested.Any(
                candidate => candidate.MayReturnUntrustedContent),
            NestedAuthority = names,
            InputSchema = BuildBatchInputSchema(definition.InputSchema, nested),
        };
        return shaped with { Description = $"{ControlledOpener(shaped)} {Detail}" };
    }

    private static JsonElement BuildBatchInputSchema(
        JsonElement nativeSchema,
        ImmutableArray<ToolDefinition> nested)
    {
        JsonObject schema = JsonNode.Parse(nativeSchema.GetRawText())!.AsObject();
        JsonObject properties = schema["properties"]!.AsObject();
        JsonObject operations = properties["operations"]!.AsObject();
        operations["minItems"] = 1;
        operations["maxItems"] = 16;
        if (nested.Length == 0)
        {
            operations["items"] = false;
        }
        else
        {
            var alternatives = new JsonArray();
            foreach (ToolDefinition target in nested)
            {
                alternatives.Add(new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["tool"] = new JsonObject { ["const"] = target.Name },
                        ["arguments"] = InlineLocalReferences(target.InputSchema),
                    },
                    ["required"] = new JsonArray("tool"),
                    ["additionalProperties"] = false,
                });
            }

            operations["items"] = new JsonObject { ["oneOf"] = alternatives };
        }

        JsonObject onError = properties["onError"]!.AsObject();
        onError["enum"] = new JsonArray("continue", "stop");
        return JsonSerializer.SerializeToElement(schema, ToolJson.Options);
    }

    private static JsonNode InlineLocalReferences(JsonElement schema)
    {
        JsonNode root = JsonNode.Parse(schema.GetRawText())!;
        JsonObject? definitions = root["$defs"] as JsonObject;
        return Expand(root, definitions, []);
    }

    private static JsonNode Expand(
        JsonNode node,
        JsonObject? definitions,
        HashSet<string> resolving)
    {
        if (node is JsonArray array)
        {
            var expanded = new JsonArray();
            foreach (JsonNode? item in array)
            {
                expanded.Add(item is null ? null : Expand(item, definitions, resolving));
            }

            return expanded;
        }

        if (node is not JsonObject source)
        {
            return node.DeepClone();
        }

        if (source["$ref"] is JsonValue referenceValue
            && referenceValue.TryGetValue(out string? reference)
            && reference is not null
            && reference.StartsWith("#/$defs/", StringComparison.Ordinal)
            && definitions is not null)
        {
            string key = reference[8..].Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
            if (!resolving.Add(key) || definitions[key] is not JsonNode target)
            {
                throw new InvalidOperationException($"Cannot inline schema reference '{reference}'.");
            }

            JsonNode expanded = Expand(target, definitions, resolving);
            resolving.Remove(key);
            return expanded;
        }

        var result = new JsonObject();
        foreach ((string key, JsonNode? value) in source)
        {
            if (key != "$defs")
            {
                result[key] = value is null ? null : Expand(value, definitions, resolving);
            }
        }

        return result;
    }

    private static ImmutableArray<ToolDefinition> BuildDefinitions()
    {
        const string Metadata = "Inspect tmux metadata; accepts no client-supplied executable input.";
        const string PaneOutput = "Read pane output; accepts no client-supplied executable input. Returned content may be sensitive or untrusted.";
        const string Environment = "Read the tmux environment; accepts no client-supplied executable input. A listing answers names without values, and a named variable is still withheld when the name reads as a credential.";
        const string Configuration = "Read configured tmux commands; accepts no client-supplied executable input. Returned values may contain executable configuration.";
        const string Manage = "Change tmux state; no client-supplied executable input.";
        const string Spawn = "Start a pane's configured process; accepts no command payload.";
        const string Input = "Send input to a pane's program; a shell that receives it runs it with your user's permissions.";
        const string Command = "Run a shell command in a pane with your user's permissions.";
        const string Teardown = "Delete tmux state; accepts no command payload.";

        ImmutableHashSet<string> batch = ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "list_sessions", "list_windows", "list_panes", "get_server_info",
            "get_session_info", "get_window_info", "get_pane_info", "capture_pane",
            "capture_since", "snapshot_pane", "search_panes", "find_pane_by_position",
            "get_tmux_variables", "show_option", "show_environment", "show_hooks");

        return
        [
            Inspect("list_sessions", "List sessions", Metadata, nameof(CapabilityTools.ListSessionsAsync), S(), batchEligible: true, detail: "List the tmux sessions. This reads names and sizes, not terminal text — to find what a pane is showing, use search_panes."),
            Inspect("list_windows", "List windows", Metadata, nameof(CapabilityTools.ListWindowsAsync), S(("session", InputSink.TmuxLookup)), batchEligible: true, detail: "List tmux windows, optionally within one session. This reads names and layouts, not terminal text — to find what a pane is showing, use search_panes."),
            Inspect("list_panes", "List panes", Metadata, nameof(CapabilityTools.ListPanesAsync), S(("session", InputSink.TmuxLookup), ("windowId", InputSink.TmuxLookup)), batchEligible: true, detail: "List tmux panes, optionally within one session or window. Filter for isCaller=true to answer 'which pane am I in?'. This reads sizes and running commands, not terminal text — for that use search_panes."),
            Inspect("get_server_info", "Get server info", Metadata, nameof(CapabilityTools.GetServerInfoAsync), S(), batchEligible: true, detail: "Read the tmux server's version and how many sessions, windows and panes it holds. Use to confirm a socket is alive and which tmux is running it."),
            Inspect("get_session_info", "Get session info", Metadata, nameof(CapabilityTools.GetSessionInfoAsync), S(("session", InputSink.TmuxLookup)), batchEligible: true),
            Inspect("get_window_info", "Get window info", Metadata, nameof(CapabilityTools.GetWindowInfoAsync), S(("windowId", InputSink.TmuxLookup)), batchEligible: true),
            Inspect("get_pane_info", "Get pane info", Metadata, nameof(CapabilityTools.GetPaneInfoAsync), S(("paneId", InputSink.TmuxLookup)), batchEligible: true),
            Inspect("capture_pane", "Capture pane", PaneOutput, nameof(CapabilityTools.CapturePaneAsync), S(("paneId", InputSink.TmuxLookup), ("includeHistory", InputSink.None), ("maxLines", InputSink.None), ("joinWrappedLines", InputSink.None)), terminal: true, batchEligible: true, detail: "Read the text a pane is showing, and optionally its scrollback. The newest lines are always kept; anything dropped to fit the budget is reported. To watch a pane across several turns, use capture_since instead — it returns only what is new."),
            Inspect("capture_since", "Capture since", PaneOutput, nameof(CapabilityTools.CaptureSinceAsync), S(("paneId", InputSink.TmuxLookup), ("cursor", InputSink.None), ("maxLines", InputSink.None)), terminal: true, batchEligible: true, detail: "Read only what a pane has printed since the last call. Pass back the cursor each time. Use this to watch a long-running process across turns: the tenth read costs what the first did, where re-capturing the pane would return everything again. Call with no cursor to start watching from now."),
            Inspect("snapshot_pane", "Snapshot pane", PaneOutput, nameof(CapabilityTools.SnapshotPaneAsync), S(("paneId", InputSink.TmuxLookup), ("maxLines", InputSink.None)), terminal: true, batchEligible: true, detail: "Read a pane's visible content together with its cursor position, size and running command, in one call. Prefer this over capture_pane plus list_panes: it is one round trip and the cursor is guaranteed to describe the text returned with it."),
            Inspect("search_panes", "Search panes", PaneOutput, nameof(CapabilityTools.SearchPanesAsync), S(("pattern", InputSink.Regex), ("session", InputSink.TmuxLookup), ("includeHistory", InputSink.None), ("ignoreCase", InputSink.None), ("maxMatchesPerPane", InputSink.None)), terminal: true, batchEligible: true, detail: "Find which panes are showing text matching a regular expression. This is the tool for 'which pane has the error', 'where is the build running', or any question about what a pane CONTAINS — the list tools only see names and sizes."),
            Inspect("find_pane_by_position", "Find pane by position", Metadata, nameof(CapabilityTools.FindPaneByPositionAsync), S(("windowId", InputSink.TmuxLookup), ("position", InputSink.None)), batchEligible: true, detail: "Find the pane sitting at an index within a window. Answers nothing rather than failing when no pane is at that position."),
            Inspect("wait_for_text", "Wait for text", PaneOutput, nameof(CapabilityTools.WaitForTextAsync), S(("paneId", InputSink.TmuxLookup), ("patterns", InputSink.Regex), ("stopPatterns", InputSink.Regex), ("timeoutSeconds", InputSink.None), ("ignoreCase", InputSink.None)), terminal: true, selfBounded: true, detail: "Wait until a pane prints something matching one of these patterns, then return. Use for output you did NOT start — a server's ready line, another process's progress, a person typing. Only output arriving AFTER the call counts: text already on screen never matches, so a pattern visible in the returned tail can still time out. For a command you are running yourself, run_shell_command is better: it reports the real exit status instead of guessing from text. Never poll capture_pane in a loop; this call does the waiting."),
            Inspect("get_tmux_variables", "Get tmux variables", Configuration, nameof(CapabilityTools.GetTmuxVariablesAsync), SM(M("names", InputSink.TmuxLookup, InputSink.TmuxFormat), M("paneId", InputSink.TmuxLookup)), configured: true, batchEligible: true, inputLiteralization: F(("names", "validated-variable-name")), detail: "Expand named tmux format variables for a pane, such as session_name or window_width. Use it for fields nothing else answers; get_pane_info already returns the common ones, and show_option reads configuration rather than live state."),
            Inspect("show_option", "Show option", Configuration, nameof(CapabilityTools.ShowOptionAsync), S(("name", InputSink.TmuxLookup), ("scope", InputSink.None), ("paneId", InputSink.TmuxLookup)), configured: true, untrusted: true, batchEligible: true, detail: "Read tmux options at the server, session, window or pane level. Omit the name to list them all. Reading history-limit before a long tail tells you how much output the pane can hold before it starts dropping lines."),
            Inspect("show_environment", "Show environment", Environment, nameof(CapabilityTools.ShowEnvironmentAsync), S(("name", InputSink.TmuxLookup), ("session", InputSink.TmuxLookup)), environment: true, secrets: true, batchEligible: true, metadata: false, detail: "Read what a NEW pane will inherit, at the server or session level — not what an already-running shell has."),
            Inspect("show_hooks", "Show hooks", Configuration, nameof(CapabilityTools.ShowHooksAsync), S(("scope", InputSink.None), ("paneId", InputSink.TmuxLookup)), configured: true, untrusted: true, batchEligible: true, metadata: false, detail: "Read the hooks tmux will run on its own events. Read-only on purpose: a hook written here would outlive this conversation and keep firing with nobody left who knows why. Put hooks you want to keep in your tmux config file."),
            Inspect("call_read_tools_batch", "Call read tools batch", PaneOutput, nameof(CapabilityTools.CallReadToolsBatchAsync), S(("operations", InputSink.NestedTool), ("onError", InputSink.None)), terminal: true, environment: true, configured: true, secrets: true, nested: batch, detail: "Execute up to 16 declared inspect operations serially; inner operations receive no separate approval."),

            ManageTool("rename_session", "Rename session", Manage, nameof(CapabilityTools.RenameSessionAsync), SM(M("name", InputSink.TmuxState, InputSink.TmuxFormat), M("session", InputSink.TmuxLookup)), inputLiteralization: F(("name", "double-hash-once")), detail: "Rename a tmux session. Its id does not change, so anything holding one still works."),
            ManageTool("rename_window", "Rename window", Manage, nameof(CapabilityTools.RenameWindowAsync), SM(M("name", InputSink.TmuxState, InputSink.TmuxFormat), M("windowId", InputSink.TmuxLookup)), inputLiteralization: F(("name", "double-hash-once")), detail: "Rename a tmux window. Its id does not change."),
            ManageTool("select_window", "Select window", Manage, nameof(CapabilityTools.SelectWindowAsync), S(("windowId", InputSink.TmuxLookup)), detail: "Make a window the current one in its session."),
            ManageTool("select_pane", "Select pane", Manage, nameof(CapabilityTools.SelectPaneAsync), S(("paneId", InputSink.TmuxLookup)), detail: "Make a pane the active one in its window. This changes what a watching human sees; targeting a pane by id does not require selecting it first."),
            ManageTool("select_layout", "Select layout", Manage, nameof(CapabilityTools.SelectLayoutAsync), S(("windowId", InputSink.TmuxLookup), ("layout", InputSink.TmuxState)), detail: "Arrange a window's panes with a named layout — even-horizontal, even-vertical, main-horizontal, main-vertical, tiled — or a layout string read from list_windows."),
            ManageTool("resize_window", "Resize window", Manage, nameof(CapabilityTools.ResizeWindowAsync), S(("windowId", InputSink.TmuxLookup), ("width", InputSink.None), ("height", InputSink.None))),
            ManageTool("resize_pane", "Resize pane", Manage, nameof(CapabilityTools.ResizePaneAsync), S(("paneId", InputSink.TmuxLookup), ("width", InputSink.None), ("height", InputSink.None), ("zoom", InputSink.None)), detail: "Resize a pane, or zoom it to fill its window. Widening a pane before reading it is the fix for output that comes back wrapped across rows."),
            ManageTool("move_window", "Move window", Manage, nameof(CapabilityTools.MoveWindowAsync), S(("windowId", InputSink.TmuxLookup), ("destination", InputSink.TmuxState), ("session", InputSink.TmuxLookup), ("replaceExisting", InputSink.None)), detail: "Move a window to another index, or into another session. With replaceExisting it takes an index that is already occupied by killing the window there, which needs the teardown toolset."),
            ManageTool("swap_pane", "Swap pane", Manage, nameof(CapabilityTools.SwapPaneAsync), S(("paneId", InputSink.TmuxLookup), ("targetPaneId", InputSink.TmuxLookup), ("detach", InputSink.None), ("keepZoom", InputSink.None))),
            ManageTool("set_pane_title", "Set pane title", Manage, nameof(CapabilityTools.SetPaneTitleAsync), SM(M("title", InputSink.TmuxState, InputSink.TmuxFormat), M("paneId", InputSink.TmuxLookup)), inputLiteralization: F(("title", "double-hash-once")), detail: "Set a pane's title. Useful for labelling panes you created so a human watching can tell which is which."),
            ManageTool("enter_copy_mode", "Enter copy mode", Manage, nameof(CapabilityTools.EnterCopyModeAsync), S(("paneId", InputSink.TmuxLookup)), detail: "Enter a pane's scrollback so it can be scrolled and searched. While a pane is in copy mode the input tools do not reach its program: send_keys and paste_text fail with 'not in a mode'. Call exit_copy_mode before sending input again."),
            ManageTool("exit_copy_mode", "Exit copy mode", Manage, nameof(CapabilityTools.ExitCopyModeAsync), S(("paneId", InputSink.TmuxLookup)), detail: "Leave a pane's copy mode and hand it back to its program. Do this before send_keys or paste_text. It is safe on a pane that is not in copy mode."),
            ManageTool("wait_for_channel", "Wait for channel", Manage, nameof(CapabilityTools.WaitForChannelAsync), S(("channel", InputSink.TmuxState), ("timeoutSeconds", InputSink.None)), selfBounded: true, stateOnly: true, detail: "Block until something signals a tmux wait-for channel with 'tmux wait-for -S <channel>'. Use when you composed a shell command that signals it. For an ordinary command whose completion you want, run_shell_command already does this and also reports the exit status."),
            ManageTool("signal_channel", "Signal channel", Manage, nameof(CapabilityTools.SignalChannelAsync), S(("channel", InputSink.TmuxState)), stateOnly: true, detail: "Signal a tmux wait-for channel, releasing whatever waits on it. The channel latches: signalling before anyone waits still satisfies the next wait, so a handoff cannot be lost to a race."),
            ManageTool("set_mouse_enabled", "Set mouse enabled", Manage, nameof(CapabilityTools.SetMouseEnabledAsync), S(("enabled", InputSink.TmuxState)), stateOnly: true, detail: "Turn tmux mouse support on or off. This sets the global option, so it applies to every session on this server and changes what a human watching can do with their mouse."),
            ManageTool("set_history_limit", "Set history limit", Manage, nameof(CapabilityTools.SetHistoryLimitAsync), S(("lines", InputSink.TmuxState), ("session", InputSink.TmuxLookup)), stateOnly: true, detail: "Set how many scrollback lines tmux keeps. This is a SESSION option, so it covers every window in the session rather than one pane. Raise it before starting something that prints a lot: no capture can return lines tmux has already discarded."),

            Execute("create_session", "Create session", Spawn, ProcessReach.ConfiguredProcess, nameof(CapabilityTools.CreateSessionAsync), SM(M("name", InputSink.TmuxState, InputSink.TmuxFormat), M("startDirectory", InputSink.TmuxState, InputSink.TmuxFormat), M("width", InputSink.TmuxState), M("height", InputSink.TmuxState)), inputLiteralization: F(("name", "double-hash-once"), ("startDirectory", "double-hash-once")), detail: "Create a detached tmux session and return its ids. Give a width and height when nothing will attach to it: a session with no client keeps tmux's default 80x24, which truncates wide output."),
            Execute("create_window", "Create window", Spawn, ProcessReach.ConfiguredProcess, nameof(CapabilityTools.CreateWindowAsync), SM(M("session", InputSink.TmuxLookup), M("name", InputSink.TmuxState, InputSink.TmuxFormat), M("startDirectory", InputSink.TmuxState, InputSink.TmuxFormat)), inputLiteralization: F(("name", "double-hash-once"), ("startDirectory", "double-hash-once")), detail: "Create a window in a tmux session and return its ids."),
            Execute("split_window", "Split window", Spawn, ProcessReach.ConfiguredProcess, nameof(CapabilityTools.SplitWindowAsync), SM(M("paneId", InputSink.TmuxLookup), M("direction", InputSink.TmuxState), M("startDirectory", InputSink.TmuxState, InputSink.TmuxFormat), M("percentage", InputSink.TmuxState)), inputLiteralization: F(("startDirectory", "double-hash-once")), detail: "Split a pane and return the NEW pane's id. Use that id for what you put in it — pane ids stay valid across layout changes, where window names and indexes do not."),
            Execute("respawn_pane", "Respawn pane", Spawn, ProcessReach.ConfiguredProcess, nameof(CapabilityTools.RespawnPaneAsync), SM(M("paneId", InputSink.TmuxLookup), M("startDirectory", InputSink.TmuxState, InputSink.TmuxFormat), M("killExistingProcess", InputSink.None)), effects: E(Effect.Observe, Effect.Change, Effect.Delete), inputLiteralization: F(("startDirectory", "double-hash-once")), detail: "Only restarts a pane whose command has ALREADY EXITED. killExistingProcess overrides that and kills what is running first — an editor holding unsaved changes, a build part way through. It reruns the command the pane was created with rather than running something new."),
            Execute("run_shell_command", "Run shell command", Command, ProcessReach.PaneCommand, nameof(CapabilityTools.RunShellCommandAsync), SM(M("command", InputSink.PaneInput, InputSink.ShellCommand), M("paneId", InputSink.TmuxLookup), M("timeoutSeconds", InputSink.None), M("maxLines", InputSink.None), M("suppressHistory", InputSink.None)), terminal: true, detail: "Run a shell command in a pane, wait for it to finish, and report its real exit status and output. This is the tool for 'run X and tell me if it worked'. Do NOT send keys and then poll a capture in a loop — this waits deterministically and costs one call. The command runs in a subshell, so cd and export do not persist. Check linesMissed and anchorLost. A timed-out command MAY STILL BE RUNNING; inspect it and do not retry it."),
            Execute("send_keys", "Send keys", Input, ProcessReach.PaneInput, nameof(CapabilityTools.SendKeysAsync), S(("keys", InputSink.PaneInput), ("paneId", InputSink.TmuxLookup), ("enter", InputSink.PaneInput), ("literal", InputSink.None), ("suppressHistory", InputSink.None)), detail: "Send raw keystrokes to a pane and return immediately. Use for driving an interactive program — a key in vim, a menu choice, Ctrl-C. Set literal=false to send named keys such as C-c, Escape or F5. To run a shell command and learn whether it worked, use run_shell_command instead; this reports nothing about what happens next."),
            Execute("send_keys_batch", "Send keys batch", Input, ProcessReach.PaneInput, nameof(CapabilityTools.SendKeysBatchAsync), SM(M("operations", InputSink.PaneInput, InputSink.TmuxLookup), M("onError", InputSink.None)), detail: "Send several keystrokes to one pane in order, in a single call. Use for a short interactive sequence — open a file, move, type, save — instead of one call per key. A batch has at most 64 steps and 64 KiB of UTF-8 text. Each delay is 0-2000 ms and all delays together must fit the server wait ceiling."),
            Execute("paste_text", "Paste text", Input, ProcessReach.PaneInput, nameof(CapabilityTools.PasteTextAsync), S(("text", InputSink.PaneInput), ("paneId", InputSink.TmuxLookup), ("bracketed", InputSink.None)), detail: "Paste a block of text into a pane through a tmux buffer. Use for multi-line text, or anything an editor would mangle if typed — bracketed paste stops auto-indent. The temporary buffer is deleted afterwards; if cleanup fails, the result identifies what remains."),
            Execute("set_synchronize_panes", "Set synchronize panes", Manage, ProcessReach.None, nameof(CapabilityTools.SetSynchronizePanesAsync), S(("enabled", InputSink.TmuxState), ("windowId", InputSink.TmuxLookup)), effects: E(Effect.Change), amplifiesFutureInput: true, detail: "Enabling synchronize-panes means subsequent input is copied to every pane in the window."),

            TeardownTool("clear_pane_scrollback", "Clear pane scrollback", Teardown, nameof(CapabilityTools.ClearPaneScrollbackAsync), S(("paneId", InputSink.TmuxLookup))),
            TeardownTool("kill_pane", "Kill pane", Teardown, nameof(CapabilityTools.KillPaneAsync), S(("paneId", InputSink.TmuxLookup)), observes: true),
            TeardownTool("kill_window", "Kill window", Teardown, nameof(CapabilityTools.KillWindowAsync), S(("windowId", InputSink.TmuxLookup)), observes: true),
            TeardownTool("kill_session", "Kill session", Teardown, nameof(CapabilityTools.KillSessionAsync), S(("session", InputSink.TmuxLookup)), observes: true),
        ];
    }

    private static ToolDefinition Inspect(
        string name,
        string title,
        string opener,
        string method,
        FrozenDictionary<string, ImmutableHashSet<InputSink>> sinks,
        bool terminal = false,
        bool environment = false,
        bool configured = false,
        bool secrets = false,
        bool untrusted = false,
        bool selfBounded = false,
        bool batchEligible = false,
        FrozenDictionary<string, string>? inputLiteralization = null,
        ImmutableHashSet<string>? nested = null,
        string? detail = null,
        ImmutableHashSet<Effect>? effects = null,
        bool metadata = true) => new(
            name, title, opener + " " + (detail ?? title + "."), Toolset.Inspect, ProcessReach.None,
            effects ?? E(Effect.Observe), O(
                metadata ? OutputClass.TmuxMetadata : null,
                terminal ? OutputClass.TerminalContent : null,
                environment ? OutputClass.ProcessEnvironment : null,
                configured ? OutputClass.ConfiguredCommand : null),
            secrets || terminal, untrusted || terminal || configured, false,
            CapabilityAnnotations.Conservative, Method(method), sinks,
            inputLiteralization ?? F(),
            nested ?? ImmutableHashSet<string>.Empty,
            selfBounded, batchEligible);

    private static ToolDefinition ManageTool(
        string name,
        string title,
        string opener,
        string method,
        FrozenDictionary<string, ImmutableHashSet<InputSink>> sinks,
        FrozenDictionary<string, string>? inputLiteralization = null,
        bool selfBounded = false,
        bool stateOnly = false,
        string? detail = null) => new(
            name, title, opener + " " + (detail ?? title + "."), Toolset.Manage, ProcessReach.None,
            stateOnly ? E(Effect.Change) : E(Effect.Observe, Effect.Change),
            O(OutputClass.TmuxMetadata), false, false, false,
            CapabilityAnnotations.Conservative, Method(method), sinks,
            inputLiteralization ?? F(),
            ImmutableHashSet<string>.Empty,
            selfBounded);

    private static ToolDefinition Execute(
        string name,
        string title,
        string opener,
        ProcessReach reach,
        string method,
        FrozenDictionary<string, ImmutableHashSet<InputSink>> sinks,
        bool terminal = false,
        ImmutableHashSet<Effect>? effects = null,
        FrozenDictionary<string, string>? inputLiteralization = null,
        bool amplifiesFutureInput = false,
        string? detail = null) => new(
            name, title, opener + " " + (detail ?? title + "."), Toolset.Execute, reach,
            effects ?? E(Effect.Observe, Effect.Change),
            terminal
                ? O(OutputClass.TmuxMetadata, OutputClass.TerminalContent)
                : O(OutputClass.TmuxMetadata),
            terminal, terminal, amplifiesFutureInput, CapabilityAnnotations.Conservative,
            Method(method), sinks, inputLiteralization ?? F(), ImmutableHashSet<string>.Empty);

    private static ToolDefinition TeardownTool(
        string name,
        string title,
        string opener,
        string method,
        FrozenDictionary<string, ImmutableHashSet<InputSink>> sinks,
        bool observes = false,
        string? detail = null) => new(
            name, title, opener + " " + (detail ?? title + "."), Toolset.Teardown, ProcessReach.None,
            observes ? E(Effect.Observe, Effect.Delete) : E(Effect.Delete),
            O(OutputClass.TmuxMetadata), false, false, false,
            CapabilityAnnotations.Conservative, Method(method), sinks,
            F(),
            ImmutableHashSet<string>.Empty);

    private static MethodInfo Method(string name) => typeof(CapabilityTools).GetMethod(name)
        ?? throw new InvalidOperationException($"Capability handler '{name}' is missing.");

    private static ImmutableHashSet<Effect> E(params Effect[] values) => [.. values];

    private static ImmutableHashSet<OutputClass> O(params OutputClass?[] values) =>
        [.. values.OfType<OutputClass>()];

    private static FrozenDictionary<string, ImmutableHashSet<InputSink>> S(
        params (string Name, InputSink Sink)[] values) => values.ToFrozenDictionary(
            value => value.Name,
            value => ImmutableHashSet.Create(value.Sink),
            StringComparer.Ordinal);

    private static KeyValuePair<string, ImmutableHashSet<InputSink>> M(
        string name,
        params InputSink[] sinks) => new(name, [.. sinks]);

    private static FrozenDictionary<string, ImmutableHashSet<InputSink>> SM(
        params KeyValuePair<string, ImmutableHashSet<InputSink>>[] values) =>
        values.ToFrozenDictionary(StringComparer.Ordinal);

    private static FrozenDictionary<string, string> F(
        params (string Name, string Control)[] values) => values.ToFrozenDictionary(
            value => value.Name,
            value => value.Control,
            StringComparer.Ordinal);

    private static void Validate(ImmutableArray<ToolDefinition> definitions)
    {
        string[] duplicateNames = definitions.GroupBy(definition => definition.Name, StringComparer.Ordinal)
            .Where(group => group.Count() != 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateNames.Length > 0)
        {
            throw new InvalidOperationException(
                $"Capability names must be unique: {string.Join(", ", duplicateNames)}.");
        }

        foreach (ToolDefinition definition in definitions)
        {
            if (!Enum.IsDefined(definition.Toolset) || !Enum.IsDefined(definition.Reach)
                || definition.Effects.Count == 0
                || definition.Effects.Any(effect => !Enum.IsDefined(effect))
                || (definition.OutputClasses.Count == 0
                    && !(definition.Name == "call_read_tools_batch"
                        && definition.NestedAuthority.Count == 0))
                || definition.OutputClasses.Any(output => !Enum.IsDefined(output)))
            {
                throw new InvalidOperationException($"Capability '{definition.Name}' has invalid enum metadata.");
            }

            if (definition.Annotations != CapabilityAnnotations.Conservative)
            {
                throw new InvalidOperationException(
                    $"Capability '{definition.Name}' has optimistic MCP annotations.");
            }

            if (definition.AmplifiesFutureInput !=
                string.Equals(definition.Name, "set_synchronize_panes", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Capability '{definition.Name}' has an invalid future-input amplification claim.");
            }

            string[] parameters = definition.Handler.GetParameters()
                .Where(parameter => parameter.ParameterType != typeof(CancellationToken))
                .Where(parameter => !parameter.ParameterType.IsGenericType
                    || parameter.ParameterType.GetGenericTypeDefinition() != typeof(IProgress<>))
                .Select(parameter => parameter.Name!)
                .ToArray();
            if (!parameters.ToHashSet(StringComparer.Ordinal).SetEquals(definition.InputSinks.Keys))
            {
                throw new InvalidOperationException(
                    $"Capability '{definition.Name}' schema fields and input sinks differ.");
            }

            if (definition.InputSinks.ContainsKey("socketName"))
            {
                throw new InvalidOperationException(
                    $"Capability '{definition.Name}' exposes a per-call socket selector.");
            }

            if (definition.InputSinks.Values.Any(sinks => sinks.Count == 0))
            {
                throw new InvalidOperationException($"Capability '{definition.Name}' has an empty input sink set.");
            }

            if (definition.InputSinks.Values.Any(sinks => sinks.Contains(InputSink.ProcessArgv)))
            {
                throw new InvalidOperationException($"Capability '{definition.Name}' exposes process argv.");
            }

            HashSet<string> formatFields = definition.InputSinks
                .Where(pair => pair.Value.Contains(InputSink.TmuxFormat))
                .Select(pair => pair.Key)
                .ToHashSet(StringComparer.Ordinal);
            if (!formatFields.SetEquals(definition.InputLiteralization.Keys)
                || definition.InputLiteralization.Values.Any(control => control is not
                    ("double-hash-once" or "validated-variable-name")))
            {
                throw new InvalidOperationException(
                    $"Capability '{definition.Name}' exposes an uncontrolled tmux format input.");
            }

            bool hasPaneInput = definition.InputSinks.Values.Any(sinks => sinks.Contains(InputSink.PaneInput));
            bool hasShell = definition.InputSinks.Values.Any(sinks => sinks.Contains(InputSink.ShellCommand));
            if ((definition.Reach == ProcessReach.PaneInput) != (hasPaneInput && !hasShell)
                || (definition.Reach == ProcessReach.PaneCommand) != hasShell
                || (definition.Reach == ProcessReach.ConfiguredProcess && (hasPaneInput || hasShell)))
            {
                throw new InvalidOperationException($"Capability '{definition.Name}' reach and input sinks differ.");
            }

            string opener = ControlledOpener(definition);
            if (!definition.Description.StartsWith(opener, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Capability '{definition.Name}' has the wrong controlled opener.");
            }
        }
    }

    private static string ControlledOpener(ToolDefinition definition)
    {
        if (definition.Toolset == Toolset.Teardown)
        {
            return "Delete tmux state; accepts no command payload.";
        }

        if (definition.Reach == ProcessReach.PaneCommand)
        {
            return "Run a shell command in a pane with your user's permissions.";
        }

        if (definition.Reach == ProcessReach.PaneInput)
        {
            return "Send input to a pane's program; a shell that receives it runs it with your user's permissions.";
        }

        if (definition.Reach == ProcessReach.ConfiguredProcess)
        {
            return "Start a pane's configured process; accepts no command payload.";
        }

        if (definition.Toolset == Toolset.Manage || definition.Toolset == Toolset.Execute)
        {
            return "Change tmux state; no client-supplied executable input.";
        }

        if (definition.OutputClasses.Contains(OutputClass.TerminalContent))
        {
            return "Read pane output; accepts no client-supplied executable input. Returned content may be sensitive or untrusted.";
        }

        if (definition.OutputClasses.Contains(OutputClass.ProcessEnvironment))
        {
            return "Read the tmux environment; accepts no client-supplied executable input. A listing answers names without values, and a named variable is still withheld when the name reads as a credential.";
        }

        if (definition.OutputClasses.Contains(OutputClass.ConfiguredCommand))
        {
            return "Read configured tmux commands; accepts no client-supplied executable input. Returned values may contain executable configuration.";
        }

        return "Inspect tmux metadata; accepts no client-supplied executable input.";
    }

    private static void ValidateRegistrationParity(
        ImmutableArray<ToolDefinition> definitions,
        ImmutableArray<McpServerTool> tools)
    {
        string[] expected = [.. definitions.Select(definition => definition.Name)];
        string[] actual = [.. tools.Select(tool => tool.ProtocolTool.Name)];
        if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Capability manifest and SDK registrations differ.");
        }

        for (int index = 0; index < definitions.Length; index++)
        {
            JsonElement schema = tools[index].ProtocolTool.InputSchema;
            HashSet<string> properties = schema.TryGetProperty("properties", out JsonElement node)
                ? node.EnumerateObject().Select(property => property.Name)
                    .ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            if (!properties.SetEquals(definitions[index].InputSinks.Keys))
            {
                throw new InvalidOperationException(
                    $"Capability '{definitions[index].Name}' native schema and input sinks differ.");
            }
        }
    }
}
