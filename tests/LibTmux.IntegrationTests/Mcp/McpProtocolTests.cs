using System.Collections.Immutable;
using System.IO.Pipelines;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LibTmux.IntegrationTests;

[Collection("tmux control clients")]
[UnsupportedOSPlatform("windows")]
public sealed class McpProtocolTests
{
    [UnixFact]
    public async Task The_wire_surface_is_the_pinned_cross_port_inventory()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string[] expected =
        [
            "list_sessions", "list_windows", "list_panes", "get_server_info",
            "get_session_info", "get_window_info", "get_pane_info", "capture_pane",
            "capture_since", "snapshot_pane", "search_panes", "find_pane_by_position",
            "wait_for_text", "get_tmux_variables", "show_option", "show_environment",
            "show_hooks", "call_read_tools_batch", "rename_session", "rename_window",
            "select_window", "select_pane", "select_layout", "resize_window",
            "resize_pane", "move_window", "swap_pane", "set_pane_title",
            "enter_copy_mode", "exit_copy_mode", "wait_for_channel", "signal_channel",
            "set_mouse_enabled", "set_history_limit", "create_session", "create_window",
            "split_window", "respawn_pane", "run_shell_command", "send_keys",
            "send_keys_batch", "paste_text", "set_synchronize_panes",
            "clear_pane_scrollback", "kill_pane", "kill_window", "kill_session",
        ];

        await using ProtocolHarness harness = await ProtocolHarness.StartAsync(token);
        IList<McpClientTool> tools = await harness.Client.ListToolsAsync(cancellationToken: token);
        Assert.Equal(
            expected.Order(StringComparer.Ordinal),
            tools.Select(tool => tool.Name).Order(StringComparer.Ordinal));
        Assert.All(tools, tool =>
        {
            ToolAnnotations annotations = Assert.IsType<ToolAnnotations>(
                tool.ProtocolTool.Annotations);
            Assert.False(annotations.ReadOnlyHint);
            Assert.True(annotations.DestructiveHint);
            Assert.False(annotations.IdempotentHint);
            Assert.True(annotations.OpenWorldHint);
            Assert.False(string.IsNullOrWhiteSpace(tool.ProtocolTool.Title));
            Assert.False(string.IsNullOrWhiteSpace(tool.ProtocolTool.Description));
            Assert.False(tool.ProtocolTool.InputSchema.GetProperty("properties")
                .TryGetProperty("socketName", out _));
        });

        foreach (string spawn in new[]
        {
            "create_session", "create_window", "split_window", "respawn_pane",
        })
        {
            JsonElement schema = tools.Single(tool => tool.Name == spawn)
                .ProtocolTool.InputSchema;
            HashSet<string> properties = schema.GetProperty("properties")
                .EnumerateObject()
                .Select(property => property.Name)
                .ToHashSet(StringComparer.Ordinal);
            Assert.DoesNotContain("command", properties);
            Assert.DoesNotContain("environment", properties);
            Assert.DoesNotContain("socketName", properties);
        }

        IList<McpClientResource> resources = await harness.Client.ListResourcesAsync(
            cancellationToken: token);
        McpClientResource capability = Assert.Single(resources);
        Assert.Equal("tmux://capabilities", capability.Uri);
        ReadResourceResult read = await harness.Client.ReadResourceAsync(
            capability.Uri,
            cancellationToken: token);
        TextResourceContents content = Assert.IsType<TextResourceContents>(
            Assert.Single(read.Contents));
        using JsonDocument document = JsonDocument.Parse(content.Text);
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(document.RootElement.GetProperty("frozen").GetBoolean());
        Assert.Equal(47, document.RootElement.GetProperty("toolCount").GetInt32());
        Assert.Equal(0, document.RootElement.GetProperty("hostCommandTools").GetInt32());
        Assert.Equal(
            "interface-shaping-not-authorization",
            document.RootElement.GetProperty("toolFilteringBoundary").GetString());
        Assert.Equal("tmux-user", document.RootElement.GetProperty("executionAuthority").GetString());
        Assert.Equal("none", document.RootElement.GetProperty("operatingSystemBoundary").GetString());
        Assert.Equal(
            expected.Order(StringComparer.Ordinal),
            document.RootElement.GetProperty("effectiveTools").EnumerateArray()
                .Select(name => name.GetString()).Order(StringComparer.Ordinal));
        JsonElement socket = document.RootElement.GetProperty("socket");
        Assert.Equal("default-dedicated", socket.GetProperty("selectionProvenance").GetString());
        Assert.Equal("created", socket.GetProperty("serverState").GetString());
        Assert.Equal("minimal", socket.GetProperty("configurationProvenance").GetString());
        Assert.Equal("tmux-objects-only", socket.GetProperty("namespaceBoundary").GetString());
        Assert.True(socket.TryGetProperty("selector", out _));
        JsonElement boundary = document.RootElement.GetProperty("boundary");
        Assert.Equal(4, boundary.EnumerateObject().Count());
        Assert.True(boundary.GetProperty("oneSocketPerProcess").GetBoolean());
        Assert.False(boundary.GetProperty("perCallSocketSelection").GetBoolean());
        Assert.False(boundary.GetProperty("hostCommandExecution").GetBoolean());
        Assert.False(boundary.GetProperty("dynamicResources").GetBoolean());
        JsonElement connection = document.RootElement.GetProperty("connection");
        Assert.Equal(socket.GetProperty("selector").GetString(),
            connection.GetProperty("socketSelector").GetString());
        Assert.Equal(socket.GetProperty("selectionProvenance").GetString(),
            connection.GetProperty("socketProvenance").GetString());
        Assert.Equal(socket.GetProperty("serverState").GetString(),
            connection.GetProperty("serverState").GetString());
        Assert.Equal(socket.GetProperty("configurationProvenance").GetString(),
            connection.GetProperty("configurationProvenance").GetString());
        Assert.True(connection.TryGetProperty("resolvedSocketPath", out _));
        Assert.Contains(" -N -L '",
            connection.GetProperty("attachCommand").GetString(),
            StringComparison.Ordinal);
        JsonElement rows = document.RootElement.GetProperty("tools");
        Assert.Equal(
            expected.Order(StringComparer.Ordinal),
            rows.EnumerateArray()
                .Select(row => row.GetProperty("name").GetString())
                .Order(StringComparer.Ordinal));
        foreach (McpClientTool tool in tools)
        {
            JsonObject metadata = Assert.IsType<JsonObject>(tool.ProtocolTool.Meta);
            KeyValuePair<string, JsonNode?> capabilityMetadata = Assert.Single(metadata);
            Assert.Equal("com.git-pull.libtmux-mcp/capability", capabilityMetadata.Key);
            JsonElement disclosed = rows.EnumerateArray()
                .Single(row => row.GetProperty("name").GetString() == tool.Name);
            Assert.True(JsonNode.DeepEquals(
                JsonNode.Parse(disclosed.GetRawText()),
                capabilityMetadata.Value));
            Assert.True(JsonElement.DeepEquals(
                tool.ProtocolTool.InputSchema,
                disclosed.GetProperty("inputSchema")));
            Assert.True(JsonElement.DeepEquals(
                tool.ProtocolTool.OutputSchema!.Value,
                disclosed.GetProperty("outputSchema")));
        }

        JsonElement batch = rows.EnumerateArray()
            .Single(row => row.GetProperty("name").GetString() == "call_read_tools_batch");
        JsonElement showOption = rows.EnumerateArray()
            .Single(row => row.GetProperty("name").GetString() == "show_option");
        JsonElement synchronize = rows.EnumerateArray()
            .Single(row => row.GetProperty("name").GetString() == "set_synchronize_panes");
        JsonElement sendKeys = rows.EnumerateArray()
            .Single(row => row.GetProperty("name").GetString() == "send_keys");
        JsonElement renameSession = rows.EnumerateArray()
            .Single(row => row.GetProperty("name").GetString() == "rename_session");
        Assert.Equal(
            ["change", "observe"],
            sendKeys.GetProperty("tmuxEffects").EnumerateArray()
                .Select(value => value.GetString()).ToArray());
        Assert.True(sendKeys.TryGetProperty("inputSchema", out _));
        Assert.True(sendKeys.TryGetProperty("outputSchema", out _));
        Assert.All(rows.EnumerateArray(), row =>
        {
            Assert.False(row.TryGetProperty("inputSinks", out _));
            Assert.False(row.TryGetProperty("tmuxFormatControls", out _));
        });
        Assert.Equal(
            "double-hash-once",
            renameSession.GetProperty("inputLiteralization").GetProperty("name").GetString());
        JsonElement variables = rows.EnumerateArray()
            .Single(row => row.GetProperty("name").GetString() == "get_tmux_variables");
        Assert.Equal(
            "validated-variable-name",
            variables.GetProperty("inputLiteralization").GetProperty("names").GetString());
        AssertCapabilitySets(rows, "capture_since", ["observe"],
            ["terminal-content", "tmux-metadata"]);
        AssertCapabilitySets(rows, "run_shell_command", ["change", "observe"],
            ["terminal-content", "tmux-metadata"]);
        AssertCapabilitySets(rows, "show_environment", ["observe"],
            ["process-environment"]);
        AssertCapabilitySets(rows, "show_hooks", ["observe"], ["configured-command"]);
        AssertCapabilitySets(rows, "call_read_tools_batch", ["observe"],
            ["configured-command", "process-environment", "terminal-content", "tmux-metadata"]);
        Assert.True(synchronize.GetProperty("amplifiesFutureInput").GetBoolean());
        Assert.Contains(
            "subsequent input is copied to every pane",
            synchronize.GetProperty("description").GetString(),
            StringComparison.Ordinal);
        Assert.All(
            rows.EnumerateArray().Where(row => row.GetProperty("name").GetString()
                != "set_synchronize_panes"),
            row => Assert.False(row.GetProperty("amplifiesFutureInput").GetBoolean()));
        string[] nested = batch.GetProperty("nestedAuthority")
            .EnumerateArray()
            .Select(name => name.GetString()!)
            .ToArray();
        string[] expectedNested =
        [
            "capture_pane", "capture_since", "find_pane_by_position",
            "get_pane_info", "get_server_info", "get_session_info",
            "get_tmux_variables", "get_window_info", "list_panes", "list_sessions",
            "list_windows", "search_panes", "show_environment", "show_hooks",
            "show_option", "snapshot_pane",
        ];
        Assert.Equal(expectedNested, nested);
        Assert.Contains(
            "configured-command",
            showOption.GetProperty("outputClasses").EnumerateArray()
                .Select(value => value.GetString()));
        Assert.StartsWith(
            "Read configured tmux commands;",
            showOption.GetProperty("description").GetString(),
            StringComparison.Ordinal);

        JsonElement batchSchema = tools.Single(tool => tool.Name == "call_read_tools_batch")
            .ProtocolTool.InputSchema.GetProperty("properties").GetProperty("operations");
        Assert.Equal(1, batchSchema.GetProperty("minItems").GetInt32());
        Assert.Equal(16, batchSchema.GetProperty("maxItems").GetInt32());
        JsonElement alternatives = batchSchema.GetProperty("items").GetProperty("oneOf");
        Assert.Equal(expectedNested.Length, alternatives.GetArrayLength());
        Assert.Equal(
            expectedNested,
            alternatives.EnumerateArray()
                .Select(item => item.GetProperty("properties").GetProperty("tool")
                    .GetProperty("const").GetString()));
        Assert.All(alternatives.EnumerateArray(), item => Assert.False(
            item.GetProperty("properties").GetProperty("arguments")
                .GetProperty("additionalProperties").GetBoolean()));
        string?[] onErrorValues = tools.Single(tool => tool.Name == "call_read_tools_batch")
            .ProtocolTool.InputSchema.GetProperty("properties").GetProperty("onError")
            .GetProperty("enum").EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.Equal(2, onErrorValues.Length);
        Assert.Equal("continue", onErrorValues[0]);
        Assert.Equal("stop", onErrorValues[1]);
        JsonElement sendBatchSchema = tools.Single(tool => tool.Name == "send_keys_batch")
            .ProtocolTool.InputSchema.GetProperty("properties");
        Assert.True(sendBatchSchema.TryGetProperty("operations", out JsonElement sendOperations));
        Assert.False(sendBatchSchema.TryGetProperty("steps", out _));
        Assert.Equal(1, sendOperations.GetProperty("minItems").GetInt32());
        Assert.Equal(64, sendOperations.GetProperty("maxItems").GetInt32());
        Assert.Equal(
            ["continue", "stop"],
            sendBatchSchema.GetProperty("onError").GetProperty("enum")
                .EnumerateArray().Select(value => value.GetString()).ToArray());

        CallToolResult batched = await harness.Client.CallToolAsync(
            "call_read_tools_batch",
            new Dictionary<string, object?>
            {
                ["operations"] = new object[]
                {
                    new { tool = "list_sessions", arguments = new { } },
                    new { tool = "get_server_info", arguments = new { } },
                },
            },
            cancellationToken: token);
        Assert.False(
            batched.IsError ?? false,
            JsonSerializer.Serialize(batched, ToolJson.Options));
        JsonElement batchResult = Structured(batched);
        Assert.Equal(2, batchResult.GetProperty("succeeded").GetInt32());
        Assert.Equal(0, batchResult.GetProperty("failed").GetInt32());
        Assert.Equal("stop", batchResult.GetProperty("onError").GetString());
        JsonElement firstNested = batchResult.GetProperty("results")[0].GetProperty("result");
        Assert.False(firstNested.GetProperty("isError").GetBoolean());
        Assert.True(firstNested.TryGetProperty("content", out _));
        Assert.True(firstNested.TryGetProperty("structuredContent", out _));

        CallToolResult continued = await harness.Client.CallToolAsync(
            "call_read_tools_batch",
            new Dictionary<string, object?>
            {
                ["operations"] = new object[]
                {
                    new
                    {
                        tool = "get_session_info",
                        arguments = new { session = "missing-batch-session" },
                    },
                    new { tool = "list_sessions", arguments = new { } },
                },
                ["onError"] = "continue",
            },
            cancellationToken: token);
        Assert.False(
            continued.IsError ?? false,
            JsonSerializer.Serialize(continued, ToolJson.Options));
        JsonElement continuedResult = Structured(continued);
        Assert.Equal(1, continuedResult.GetProperty("succeeded").GetInt32());
        Assert.Equal(1, continuedResult.GetProperty("failed").GetInt32());
        Assert.Equal(2, continuedResult.GetProperty("results").GetArrayLength());
        Assert.False(continuedResult.GetProperty("results")[0]
            .GetProperty("success").GetBoolean());
        Assert.True(continuedResult.GetProperty("results")[1]
            .GetProperty("success").GetBoolean());

        string suffix = Guid.NewGuid().ToString("N")[..8];
        string synchronizedSession = $"sync-#{{pid}}-{suffix}";
        CallToolResult created = await harness.Client.CallToolAsync(
            "create_session",
            new Dictionary<string, object?> { ["name"] = synchronizedSession },
            cancellationToken: token);
        JsonElement createdResult = Structured(created);
        string sessionId = createdResult.GetProperty("sessionId").GetString()!;
        string paneId = createdResult.GetProperty("paneId").GetString()!;
        string windowId = createdResult.GetProperty("windowId").GetString()!;
        CallToolResult initialSession = await harness.Client.CallToolAsync(
            "get_session_info",
            new Dictionary<string, object?> { ["session"] = sessionId },
            cancellationToken: token);
        Assert.Equal(
            synchronizedSession,
            Structured(initialSession).GetProperty("name").GetString());

        string renamedSession = $"renamed-#{{pid}}-{suffix}";
        _ = await harness.Client.CallToolAsync(
            "rename_session",
            new Dictionary<string, object?>
            {
                ["session"] = sessionId,
                ["name"] = renamedSession,
            },
            cancellationToken: token);
        CallToolResult renamedSessionInfo = await harness.Client.CallToolAsync(
            "get_session_info",
            new Dictionary<string, object?> { ["session"] = sessionId },
            cancellationToken: token);
        Assert.Equal(
            renamedSession,
            Structured(renamedSessionInfo).GetProperty("name").GetString());

        string literalWindowName = $"window-#{{pid}}-{suffix}";
        CallToolResult createdWindow = await harness.Client.CallToolAsync(
            "create_window",
            new Dictionary<string, object?>
            {
                ["session"] = sessionId,
                ["name"] = literalWindowName,
            },
            cancellationToken: token);
        string createdWindowId = Structured(createdWindow).GetProperty("windowId").GetString()!;
        CallToolResult createdWindowInfo = await harness.Client.CallToolAsync(
            "get_window_info",
            new Dictionary<string, object?> { ["windowId"] = createdWindowId },
            cancellationToken: token);
        Assert.Equal(
            literalWindowName,
            Structured(createdWindowInfo).GetProperty("name").GetString());

        string renamedWindow = $"renamed-#{{pid}}-{suffix}";
        _ = await harness.Client.CallToolAsync(
            "rename_window",
            new Dictionary<string, object?>
            {
                ["windowId"] = createdWindowId,
                ["name"] = renamedWindow,
            },
            cancellationToken: token);
        CallToolResult renamedWindowInfo = await harness.Client.CallToolAsync(
            "get_window_info",
            new Dictionary<string, object?> { ["windowId"] = createdWindowId },
            cancellationToken: token);
        Assert.Equal(
            renamedWindow,
            Structured(renamedWindowInfo).GetProperty("name").GetString());

        CallToolResult split = await harness.Client.CallToolAsync(
            "split_window",
            new Dictionary<string, object?> { ["paneId"] = paneId },
            cancellationToken: token);
        string secondPaneId = Structured(split).GetProperty("paneId").GetString()!;
        _ = await harness.Client.CallToolAsync(
            "set_synchronize_panes",
            new Dictionary<string, object?>
            {
                ["enabled"] = true,
                ["windowId"] = windowId,
            },
            cancellationToken: token);

        CallToolResult sent = await harness.Client.CallToolAsync(
            "send_keys",
            new Dictionary<string, object?>
            {
                ["keys"] = "Escape",
                ["paneId"] = paneId,
                ["literal"] = false,
            },
            cancellationToken: token);
        Assert.Equal(
            new[] { paneId, secondPaneId }.Order(StringComparer.Ordinal),
            TargetPaneIds(sent));

        CallToolResult sentBatch = await harness.Client.CallToolAsync(
            "send_keys_batch",
            new Dictionary<string, object?>
            {
                ["operations"] = new[]
                {
                    new { paneId, keys = "Escape", literal = false },
                },
            },
            cancellationToken: token);
        Assert.Equal(
            new[] { paneId, secondPaneId }.Order(StringComparer.Ordinal),
            Structured(sentBatch).GetProperty("results")[0].GetProperty("targetPaneIds")
                .EnumerateArray().Select(value => value.GetString()!)
                .Order(StringComparer.Ordinal));

        for (int mask = 0; mask < 16; mask++)
        {
            ImmutableHashSet<Toolset> selected = Enum.GetValues<Toolset>()
                .Where(toolset => (mask & (1 << (int)toolset)) != 0)
                .ToImmutableHashSet();
            CapabilityRegistry registry = CapabilityRegistry.Select(new CapabilitySelection(
                selected,
                ImmutableHashSet.Create<string>(StringComparer.Ordinal),
                ImmutableHashSet.Create<string>(StringComparer.Ordinal)));
            Assert.Equal(
                CapabilityRegistry.Manifest.Count(definition => selected.Contains(definition.Toolset)),
                registry.Definitions.Length);
        }

        Dictionary<string, string?> selectedEnvironment = new(StringComparer.Ordinal)
        {
            [ServerPolicy.SafetyVariable] = null,
            [CapabilitySelection.ToolsetsVariable] = "inspect,execute",
            [CapabilitySelection.ToolsVariable] = "kill_session",
            [CapabilitySelection.ExcludeToolsVariable] = "kill_session,capture_pane",
        };
        CapabilitySelection selectedByName = CapabilitySelection.FromEnvironment(
            name => selectedEnvironment.GetValueOrDefault(name),
            []);
        CapabilityRegistry selectedRegistry = CapabilityRegistry.Select(selectedByName);
        Assert.DoesNotContain(selectedRegistry.Definitions, row => row.Name == "kill_session");
        Assert.DoesNotContain(selectedRegistry.Definitions, row => row.Name == "capture_pane");
        Assert.Contains(selectedRegistry.Definitions, row => row.Name == "create_session");
        using JsonDocument selectedDisclosure = JsonDocument.Parse(
            new CapabilityResource(
                selectedRegistry,
                new McpRuntimeDisclosure(
                    "name:test",
                    "operator-current",
                    "unknown",
                    "existing",
                    ResolvedSocketPath: "",
                    AttachCommand: "tmux -N -L 'test' attach",
                    TeardownExplicitlySelected: false))
                .Read());
        JsonElement selectedBatch = selectedDisclosure.RootElement.GetProperty("tools")
            .EnumerateArray()
            .Single(row => row.GetProperty("name").GetString() == "call_read_tools_batch");
        Assert.DoesNotContain(
            selectedBatch.GetProperty("nestedAuthority").EnumerateArray(),
            name => name.GetString() == "capture_pane");
        Assert.Equal(15, selectedBatch.GetProperty("nestedAuthority").GetArrayLength());
        JsonElement selectedBatchSchema = selectedRegistry.Tools
            .Single(tool => tool.ProtocolTool.Name == "call_read_tools_batch")
            .ProtocolTool.InputSchema.GetProperty("properties").GetProperty("operations");
        Assert.DoesNotContain(
            selectedBatchSchema.GetProperty("items").GetProperty("oneOf")
                .EnumerateArray()
                .Select(alternative => alternative.GetProperty("properties")
                    .GetProperty("tool").GetProperty("const").GetString()),
            name => name == "capture_pane");

        CapabilityRegistry aggregateOnly = CapabilityRegistry.Select(new CapabilitySelection(
            ImmutableHashSet<Toolset>.Empty,
            ImmutableHashSet.Create(StringComparer.Ordinal, "call_read_tools_batch"),
            ImmutableHashSet.Create<string>(StringComparer.Ordinal)));
        ToolDefinition aggregate = Assert.Single(aggregateOnly.Definitions);
        Assert.Equal(16, aggregate.NestedAuthority.Count);

        CapabilityRegistry environmentOnly = CapabilityRegistry.Select(new CapabilitySelection(
            ImmutableHashSet<Toolset>.Empty,
            ImmutableHashSet.Create(StringComparer.Ordinal, "call_read_tools_batch"),
            expectedNested.Where(name => name != "show_environment")
                .ToImmutableHashSet(StringComparer.Ordinal)));
        ToolDefinition environmentBatch = Assert.Single(environmentOnly.Definitions);
        Assert.Equal(OutputClass.ProcessEnvironment, Assert.Single(environmentBatch.OutputClasses));
        Assert.StartsWith(
            "Read the tmux environment;",
            environmentBatch.Description,
            StringComparison.Ordinal);

        CapabilityRegistry zeroAuthority = CapabilityRegistry.Select(new CapabilitySelection(
            ImmutableHashSet<Toolset>.Empty,
            ImmutableHashSet.Create(StringComparer.Ordinal, "call_read_tools_batch"),
            expectedNested.ToImmutableHashSet(StringComparer.Ordinal)));
        JsonElement zeroCalls = Assert.Single(zeroAuthority.Tools).ProtocolTool.InputSchema
            .GetProperty("properties").GetProperty("operations");
        Assert.Equal(JsonValueKind.False, zeroCalls.GetProperty("items").ValueKind);
        ToolDefinition zeroBatch = Assert.Single(zeroAuthority.Definitions);
        Assert.Equal(Effect.Observe, Assert.Single(zeroBatch.Effects));
        Assert.Empty(zeroBatch.OutputClasses);
        Assert.StartsWith(
            "Inspect tmux metadata;",
            zeroBatch.Description,
            StringComparison.Ordinal);

        selectedEnvironment[CapabilitySelection.ToolsVariable] = "not_a_tool";
        Assert.Throws<McpException>(() => CapabilitySelection.FromEnvironment(
            name => selectedEnvironment.GetValueOrDefault(name),
            []));
        selectedEnvironment[CapabilitySelection.ToolsVariable] = null;
        selectedEnvironment[CapabilitySelection.ToolsetsVariable] = string.Empty;
        Assert.Empty(CapabilitySelection.FromEnvironment(
            name => selectedEnvironment.GetValueOrDefault(name),
            [Toolset.Inspect]).Toolsets);
        selectedEnvironment[CapabilitySelection.ToolsetsVariable] = "inspect,";
        Assert.Throws<McpException>(() => CapabilitySelection.FromEnvironment(
            name => selectedEnvironment.GetValueOrDefault(name),
            []));
        selectedEnvironment[CapabilitySelection.ToolsetsVariable] = "inspect";
        selectedEnvironment[ServerPolicy.SafetyVariable] = string.Empty;
        Assert.Throws<McpException>(() => CapabilitySelection.FromEnvironment(
            name => selectedEnvironment.GetValueOrDefault(name),
            []));

        foreach (string literalNameTool in new[]
        {
            "create_session", "create_window", "rename_session", "rename_window",
        })
        {
            ToolDefinition definition = CapabilityRegistry.Manifest
                .Single(candidate => candidate.Name == literalNameTool);
            Assert.Contains(InputSink.TmuxFormat, definition.InputSinks["name"]);
            Assert.Equal("double-hash-once", definition.InputLiteralization["name"]);
        }
    }

    [UnixFact]
    public async Task Default_startup_proves_new_minimal_daemon_ownership()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string root = Path.Combine(Path.GetTempPath(), $"libtmux-mcp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Dictionary<string, string?> environment = new(StringComparer.Ordinal)
        {
            ["TMUX_TMPDIR"] = root,
            ["LIBTMUX_TMUX"] = System.Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux",
        };

        try
        {
            McpStartup created = await McpStartup.ResolveAsync(
                name => environment.GetValueOrDefault(name), token);
            Assert.Equal("created", created.Disclosure.ServerState);
            Assert.Equal("minimal", created.Disclosure.ConfigurationProvenance);
            Assert.Equal("default-dedicated", created.Disclosure.SocketProvenance);
            Assert.False(string.IsNullOrWhiteSpace(created.Disclosure.ResolvedSocketPath));
            Assert.Contains(" -N -S '", created.Disclosure.AttachCommand, StringComparison.Ordinal);
            Assert.Contains(Toolset.Teardown, created.Selection.Toolsets);
            Assert.True(File.Exists(created.ConnectionOptions.ConfigurationFile));
            Assert.Contains("@libtmux_mcp_owner", await File.ReadAllTextAsync(
                created.ConnectionOptions.ConfigurationFile!, token), StringComparison.Ordinal);
            TmuxCommandResult globalEnvironment = await Server.Open(created.ConnectionOptions)
                .ExecuteCommandAsync(["show-environment", "-g"], token);
            Assert.DoesNotContain(
                globalEnvironment.StandardOutputLines,
                line => line.StartsWith("LIBTMUX_MCP_OWNER=", StringComparison.Ordinal));

            McpStartup existing = await McpStartup.ResolveAsync(
                name => environment.GetValueOrDefault(name), token);
            Assert.Equal("existing", existing.Disclosure.ServerState);
            Assert.Equal("unknown", existing.Disclosure.ConfigurationProvenance);
            Assert.DoesNotContain(Toolset.Teardown, existing.Selection.Toolsets);

            IAsyncDisposable owner = Assert.IsAssignableFrom<IAsyncDisposable>(created);
            await owner.DisposeAsync();
            TmuxCommandResult afterCleanup = await Server.Open(created.ConnectionOptions)
                .ExecuteCommandAsync(["list-sessions"], token);
            Assert.NotEqual(0, afterCleanup.ExitCode);
        }
        finally
        {
            try
            {
                await Server.Open(new ServerConnectionOptions(
                        tmuxBinaryPath: environment["LIBTMUX_TMUX"]!,
                        socketName: "libtmux-mcp",
                        configurationFile: McpStartup.MinimalConfigurationPath,
                        childEnvironment: new Dictionary<string, string?>
                        {
                            ["TMUX_TMPDIR"] = root,
                        }))
                    .KillAsync(CancellationToken.None);
            }
            catch (LibTmuxException)
            {
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [UnixFact]
    public async Task Default_cleanup_refuses_a_foreign_owner_marker()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string root = Path.Combine(Path.GetTempPath(), $"libtmux-mcp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Dictionary<string, string?> environment = new(StringComparer.Ordinal)
        {
            ["TMUX_TMPDIR"] = root,
            ["LIBTMUX_TMUX"] = System.Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux",
        };

        try
        {
            McpStartup created = await McpStartup.ResolveAsync(
                name => environment.GetValueOrDefault(name), token);
            Server server = Server.Open(created.ConnectionOptions);
            _ = await server.ExecuteCommandAsync(
                ["set-option", "-g", "@libtmux_mcp_owner", "foreign-owner"],
                token);

            IAsyncDisposable owner = Assert.IsAssignableFrom<IAsyncDisposable>(created);
            await owner.DisposeAsync();

            TmuxCommandResult marker = await server.ExecuteCommandAsync(
                ["show-options", "-gqv", "@libtmux_mcp_owner"],
                token);
            Assert.Equal(0, marker.ExitCode);
            Assert.Equal("foreign-owner", Assert.Single(marker.StandardOutputLines));
            await server.KillAsync(token);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static JsonElement Structured(CallToolResult result) =>
        Assert.IsType<JsonElement>(result.StructuredContent);

    private static IEnumerable<string> TargetPaneIds(CallToolResult result) =>
        Structured(result).GetProperty("targetPaneIds")
            .EnumerateArray()
            .Select(value => value.GetString()!)
            .Order(StringComparer.Ordinal);

    private static void AssertCapabilitySets(
        JsonElement rows,
        string name,
        string[] effects,
        string[] outputs)
    {
        JsonElement row = rows.EnumerateArray()
            .Single(candidate => candidate.GetProperty("name").GetString() == name);
        Assert.Equal(effects, row.GetProperty("tmuxEffects").EnumerateArray()
            .Select(value => value.GetString()).ToArray());
        Assert.Equal(outputs, row.GetProperty("outputClasses").EnumerateArray()
            .Select(value => value.GetString()).ToArray());
    }

    private sealed class ProtocolHarness : IAsyncDisposable
    {
        private readonly McpServer _server;
        private readonly ServiceProvider _services;
        private readonly string _socketName;

        private ProtocolHarness(
            McpServer server,
            McpClient client,
            ServiceProvider services,
            string socketName)
        {
            _server = server;
            Client = client;
            _services = services;
            _socketName = socketName;
        }

        internal McpClient Client { get; }

        internal static async Task<ProtocolHarness> StartAsync(CancellationToken cancellationToken)
        {
            ServiceCollection services = new();
            services.AddLogging();
            string socketName = $"ltp-{Guid.NewGuid():N}"[..20];
            McpServerComposition.Add(
                services,
                new ServerPolicy { WaitCeiling = TimeSpan.FromSeconds(20) },
                new ServerConnectionOptions(
                    tmuxBinaryPath: System.Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux",
                    socketName: socketName,
                    configurationFile: "/dev/null"),
                callerPaneId: null,
                CapabilitySelection.All,
                new McpRuntimeDisclosure(
                    $"name:{socketName}",
                    "default-dedicated",
                    "minimal",
                    ServerState: "created",
                    ResolvedSocketPath: "",
                    AttachCommand: $"tmux -N -L '{socketName}' attach",
                    TeardownExplicitlySelected: false));
            ServiceProvider provider = services.BuildServiceProvider();

            Pipe clientToServer = new();
            Pipe serverToClient = new();
            McpServer server = McpServer.Create(
                new StreamServerTransport(
                    clientToServer.Reader.AsStream(),
                    serverToClient.Writer.AsStream()),
                provider.GetRequiredService<IOptions<McpServerOptions>>().Value,
                provider.GetRequiredService<ILoggerFactory>(),
                provider);
            _ = server.RunAsync(CancellationToken.None);
            McpClient client = await McpClient.CreateAsync(
                new StreamClientTransport(
                    clientToServer.Writer.AsStream(),
                    serverToClient.Reader.AsStream()),
                cancellationToken: cancellationToken);
            return new ProtocolHarness(server, client, provider, socketName);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync().ConfigureAwait(false);
            await _server.DisposeAsync().ConfigureAwait(false);
            await _services.DisposeAsync().ConfigureAwait(false);
            try
            {
                Server tmux = await Server.ConnectAsync(
                        new ServerConnectionOptions(
                            tmuxBinaryPath: System.Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux",
                            socketName: _socketName,
                            configurationFile: "/dev/null"),
                        CancellationToken.None)
                    .ConfigureAwait(false);
                await tmux.KillAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (LibTmuxException)
            {
            }
        }
    }
}
