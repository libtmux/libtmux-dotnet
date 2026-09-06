using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LibTmux.Engineering;
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
    public async Task An_oversized_request_id_is_rejected_before_tool_dispatch()
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        CancellationToken token = timeout.Token;
        string nonce = Guid.NewGuid().ToString("N")[..8];
        string socketName = $"lt-id-{nonce}";
        string root = Path.Combine(WorkspaceSocketRoot.Root, $"request-id-{nonce}");
        Directory.CreateDirectory(root);
        Server endpoint = Server.Open(new ServerConnectionOptions(
            tmuxBinaryPath: System.Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux",
            socketName: socketName,
            configurationFile: "/dev/null",
            childEnvironment: new Dictionary<string, string?> { ["TMUX_TMPDIR"] = root }));

        var startInfo = new ProcessStartInfo(
            Path.Combine(AppContext.BaseDirectory, "LibTmux.Mcp"))
        {
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.Environment["LIBTMUX_SOCKET"] = socketName;
        startInfo.Environment["LIBTMUX_TOOLSETS"] = "inspect,manage,execute";
        startInfo.Environment["LIBTMUX_TMUX_CONFIG"] = "/dev/null";
        startInfo.Environment["TMUX_TMPDIR"] = root;

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The MCP test process did not start.");
        Task<string> standardError = process.StandardError.ReadToEndAsync(token);
        process.StandardInput.AutoFlush = true;

        try
        {
            await WriteAsync(process, new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "initialize",
                ["params"] = new JsonObject
                {
                    ["protocolVersion"] = "2025-06-18",
                    ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject
                    {
                        ["name"] = "request-id-test",
                        ["version"] = "1",
                    },
                },
            }, token);
            JsonNode initialized = await ReadAsync(process, token);
            Assert.NotNull(initialized["result"]);
            await WriteAsync(process, new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/initialized",
            }, token);

            const int MaximumRequestIdBytes = RequestIdBudgetFilter.MaximumSerializedBytes;
            string acceptedId = new('i', MaximumRequestIdBytes - 2);
            await WriteAsync(process, ToolCall(acceptedId, "list_sessions", new JsonObject()), token);
            JsonNode accepted = await ReadAsync(process, token);
            Assert.Equal(acceptedId, accepted["id"]?.GetValue<string>());

            string marker = $"oversized-id-must-not-run-{nonce}";
            await WriteAsync(
                process,
                ToolCall(
                    new string('i', 1_000_000),
                    "create_session",
                    new JsonObject { ["name"] = marker }),
                token);
            JsonNode rejected = await ReadAsync(process, token);

            TmuxCommandResult sessions = await endpoint.ExecuteCommandAsync(
                ["list-sessions", "-F", "#{session_name}"],
                token);
            Assert.DoesNotContain(marker, sessions.StandardOutputLines);
            Assert.Null(rejected["id"]);
            Assert.Equal(-32600, rejected["error"]?["code"]?.GetValue<int>());
            Assert.Contains(
                MaximumRequestIdBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                rejected["error"]?["message"]?.GetValue<string>(),
                StringComparison.Ordinal);
            Assert.True(Encoding.UTF8.GetByteCount(rejected.ToJsonString()) < 1_000);
        }
        finally
        {
            process.StandardInput.Close();
            try
            {
                await process.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            _ = await standardError;
            try
            {
                await endpoint.KillAsync(CancellationToken.None);
            }
            catch (LibTmuxException)
            {
            }

            Directory.Delete(root, recursive: true);
        }
    }

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
            "wait_for_channel", "signal_channel", "set_mouse_enabled", "set_history_limit",
            "create_session", "create_window", "split_window", "respawn_pane",
            "run_shell_command", "send_keys", "send_keys_batch", "paste_text",
            "set_synchronize_panes",
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

        JsonElement searchPattern = tools.Single(tool => tool.Name == "search_panes")
            .ProtocolTool.InputSchema.GetProperty("properties").GetProperty("pattern");
        Assert.Contains(
            "at most 999 UTF-8 bytes",
            searchPattern.GetProperty("description").GetString(),
            StringComparison.Ordinal);
        JsonElement waitProperties = tools.Single(tool => tool.Name == "wait_for_text")
            .ProtocolTool.InputSchema.GetProperty("properties");
        foreach (string name in new[] { "patterns", "stopPatterns" })
        {
            string description = waitProperties.GetProperty(name)
                .GetProperty("description").GetString()!;
            Assert.Contains("at most 32 entries", description, StringComparison.Ordinal);
            Assert.Contains("16384 UTF-8 bytes", description, StringComparison.Ordinal);
            Assert.Contains("at most 999 UTF-8 bytes", description, StringComparison.Ordinal);
        }

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
        Assert.Equal(45, document.RootElement.GetProperty("toolCount").GetInt32());
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
        Assert.Equal(18, rows.EnumerateArray().Count(row =>
            row.GetProperty("toolset").GetString() == "inspect"));
        Assert.Equal(14, rows.EnumerateArray().Count(row =>
            row.GetProperty("toolset").GetString() == "manage"));
        Assert.Equal(9, rows.EnumerateArray().Count(row =>
            row.GetProperty("toolset").GetString() == "execute"));
        Assert.Equal(4, rows.EnumerateArray().Count(row =>
            row.GetProperty("toolset").GetString() == "teardown"));
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

            // The row says only what the protocol cannot. Carrying a second
            // copy of the schemas cost 54 KB and the output one was wrong.
            //
            // This absence is asserted rather than the two being compared,
            // because comparing them could not have caught the disagreement:
            // the SDK wraps a non-object output schema as {"result": ...} when
            // it serializes, so two in-process representations agree while the
            // bytes on the wire do not. Anything whose contract is the wire
            // shape has to be asserted against raw bytes.
            Assert.False(disclosed.TryGetProperty("inputSchema", out _));
            Assert.False(disclosed.TryGetProperty("outputSchema", out _));
            Assert.False(disclosed.TryGetProperty("description", out _));
            Assert.False(disclosed.TryGetProperty("annotations", out _));
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
        Assert.Equal("send_keys", sendKeys.GetProperty("name").GetString());
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
            "synchronized input cohort",
            tools.Single(tool => tool.Name == "set_synchronize_panes").Description,
            StringComparison.Ordinal);
        string runDescription = tools.Single(tool => tool.Name == "run_shell_command").Description;
        Assert.Contains("only the pane you name", runDescription, StringComparison.Ordinal);
        Assert.Contains("send_keys", runDescription, StringComparison.Ordinal);
        Assert.Contains("singular", runDescription, StringComparison.Ordinal);
        JsonElement synchronizeEnabled = tools.Single(tool => tool.Name == "set_synchronize_panes")
            .ProtocolTool.InputSchema.GetProperty("properties").GetProperty("enabled");
        string synchronizeEnabledDescription = synchronizeEnabled.GetProperty("description").GetString()!;
        Assert.Contains("inherited", synchronizeEnabledDescription, StringComparison.Ordinal);
        Assert.Contains("overrides", synchronizeEnabledDescription, StringComparison.Ordinal);
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
            tools.Single(tool => tool.Name == "show_option").Description,
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
        JsonElement pasteSchema = tools.Single(tool => tool.Name == "paste_text")
            .ProtocolTool.InputSchema.GetProperty("properties");
        Assert.True(pasteSchema.TryGetProperty("enter", out _));

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

        CapabilityRegistry environmentOnly = CapabilityRegistry.Select(new CapabilitySelection(
            ImmutableHashSet.Create(Toolset.Inspect),
            ImmutableHashSet.Create<string>(StringComparer.Ordinal),
            expectedNested.Where(name => name != "show_environment")
                .ToImmutableHashSet(StringComparer.Ordinal)));
        ToolDefinition environmentBatch = environmentOnly.Definitions
            .Single(row => row.Name == "call_read_tools_batch");
        Assert.Equal(OutputClass.ProcessEnvironment, Assert.Single(environmentBatch.OutputClasses));
        Assert.StartsWith(
            "Read the tmux environment;",
            environmentBatch.Description,
            StringComparison.Ordinal);

        // Naming only the batch grants only the batch. Its authority is trimmed
        // by the whole selection, so nesting can never widen what was selected
        // — and with nothing left to reach it is not published at all, rather
        // than offered as a tool that can only refuse.
        CapabilityRegistry zeroAuthority = CapabilityRegistry.Select(new CapabilitySelection(
            ImmutableHashSet<Toolset>.Empty,
            ImmutableHashSet.Create(StringComparer.Ordinal, "call_read_tools_batch"),
            ImmutableHashSet.Create<string>(StringComparer.Ordinal)));
        Assert.Empty(zeroAuthority.Definitions);
        Assert.Empty(zeroAuthority.Tools);

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
            ["PATH"] = System.Environment.GetEnvironmentVariable("PATH"),
        };

        try
        {
            McpStartup created = await McpStartup.ResolveAsync(
                name => environment.GetValueOrDefault(name), token);
            Assert.Equal("created", created.Disclosure.ServerState);
            Assert.Equal("minimal", created.Disclosure.ConfigurationProvenance);
            Assert.Equal("default-dedicated", created.Disclosure.SocketProvenance);
            Assert.False(string.IsNullOrWhiteSpace(created.Disclosure.ResolvedSocketPath));
            Assert.True(Path.IsPathFullyQualified(created.ConnectionOptions.TmuxBinaryPath));
            Assert.Null(created.ConnectionOptions.SocketName);
            Assert.Equal(
                created.Disclosure.ResolvedSocketPath,
                created.ConnectionOptions.SocketPath);
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
            Assert.Null(existing.ConnectionOptions.SocketName);
            Assert.Equal(
                created.Disclosure.ResolvedSocketPath,
                existing.ConnectionOptions.SocketPath);
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
            ["PATH"] = System.Environment.GetEnvironmentVariable("PATH"),
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

    [UnixFact]
    public async Task A_direct_call_is_held_to_the_schema_a_batch_call_is()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ProtocolHarness harness = await ProtocolHarness.StartAsync(token);
        _ = await harness.Client.CallToolAsync(
            "create_session",
            new Dictionary<string, object?> { ["name"] = "schema-probe" },
            cancellationToken: token);

        // Every schema declares additionalProperties false, and the SDK's
        // binder dropped what it did not recognize. A caller who misspells an
        // argument then reads a success for a call that ignored it.
        CallToolResult undeclared = await harness.Client.CallToolAsync(
            "list_panes",
            new Dictionary<string, object?> { ["sessionName"] = "schema-probe" },
            cancellationToken: token);
        Assert.True(undeclared.IsError ?? false);
        Assert.Contains(
            "is not a declared property",
            Assert.IsType<TextContentBlock>(Assert.Single(undeclared.Content)).Text,
            StringComparison.Ordinal);

        CallToolResult coerced = await harness.Client.CallToolAsync(
            "capture_pane",
            new Dictionary<string, object?> { ["maxLines"] = "5" },
            cancellationToken: token);
        Assert.True(coerced.IsError ?? false);
        Assert.Contains(
            "has the wrong JSON type",
            Assert.IsType<TextContentBlock>(Assert.Single(coerced.Content)).Text,
            StringComparison.Ordinal);

        // A dispatching tool's operations stay opaque, because a oneOf over
        // every alternative can only report that none matched. The rest of its
        // object still has to hold: `onErrors` used to be dropped, silently
        // reverting the batch to stop-on-first-failure.
        CallToolResult typo = await harness.Client.CallToolAsync(
            "call_read_tools_batch",
            new Dictionary<string, object?>
            {
                ["operations"] = new object[] { new { tool = "list_sessions", arguments = new { } } },
                ["onErrors"] = "continue",
            },
            cancellationToken: token);
        Assert.True(typo.IsError ?? false);
        Assert.Contains(
            "arguments.onErrors",
            Assert.IsType<TextContentBlock>(Assert.Single(typo.Content)).Text,
            StringComparison.Ordinal);

        // Both paths refuse the same input for the same stated reason.
        CallToolResult batched = await harness.Client.CallToolAsync(
            "call_read_tools_batch",
            new Dictionary<string, object?>
            {
                ["operations"] = new object[]
                {
                    new { tool = "capture_pane", arguments = new { maxLines = "5" } },
                },
            },
            cancellationToken: token);
        Assert.Contains(
            "has the wrong JSON type",
            JsonSerializer.Serialize(Structured(batched), ToolJson.Options),
            StringComparison.Ordinal);

        // An integer beyond Int32 is schema-valid without a bound, so it used
        // to reach the SDK's binder and answer in System.Text.Json's words.
        CallToolResult huge = await harness.Client.CallToolAsync(
            "capture_pane",
            new Dictionary<string, object?> { ["maxLines"] = 1_000_000_000_000L },
            cancellationToken: token);
        Assert.True(huge.IsError ?? false);
        string hugeText = Assert.IsType<TextContentBlock>(Assert.Single(huge.Content)).Text;
        Assert.Contains("outside the allowed range", hugeText, StringComparison.Ordinal);
        Assert.DoesNotContain("System.", hugeText, StringComparison.Ordinal);

        // A malformed operations array named an internal type for the same
        // reason: nothing in the outer schema constrained its items.
        CallToolResult shapes = await harness.Client.CallToolAsync(
            "call_read_tools_batch",
            new Dictionary<string, object?> { ["operations"] = new object[] { 1, 2 } },
            cancellationToken: token);
        Assert.True(shapes.IsError ?? false);
        Assert.DoesNotContain(
            "LibTmux.Mcp",
            Assert.IsType<TextContentBlock>(Assert.Single(shapes.Content)).Text,
            StringComparison.Ordinal);

        // A declared argument of the declared type still runs.
        CallToolResult accepted = await harness.Client.CallToolAsync(
            "list_panes",
            new Dictionary<string, object?> { ["session"] = "schema-probe" },
            cancellationToken: token);
        Assert.False(
            accepted.IsError ?? false,
            JsonSerializer.Serialize(accepted, ToolJson.Options));
    }

    [UnixFact]
    public async Task A_refusal_this_server_wrote_is_not_called_unexpected()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ProtocolHarness harness = await ProtocolHarness.StartAsync(token);

        _ = await harness.Client.CallToolAsync(
            "create_session",
            new Dictionary<string, object?> { ["name"] = "refusal-probe" },
            cancellationToken: token);
        CallToolResult refused = await harness.Client.CallToolAsync(
            "capture_pane",
            new Dictionary<string, object?> { ["paneId"] = "%999" },
            cancellationToken: token);

        Assert.True(refused.IsError ?? false);
        string message = Assert.IsType<TextContentBlock>(Assert.Single(refused.Content)).Text;

        // The refusal already names the cause and the cure; the unexpected
        // backstop told the caller to go read a log instead.
        Assert.Contains("%999", message, StringComparison.Ordinal);
        Assert.Contains("list_panes", message, StringComparison.Ordinal);
        Assert.DoesNotContain("This is unexpected", message, StringComparison.Ordinal);
        Assert.DoesNotContain("may have acted", message, StringComparison.Ordinal);

        // The batch dispatches inner operations directly, so the same failure
        // used to read differently depending on how it was called.
        CallToolResult batched = await harness.Client.CallToolAsync(
            "call_read_tools_batch",
            new Dictionary<string, object?>
            {
                ["operations"] = new object[]
                {
                    new { tool = "capture_pane", arguments = new { paneId = "%999" } },
                },
                ["onError"] = "continue",
            },
            cancellationToken: token);
        string inner = Structured(batched).GetProperty("results")[0]
            .GetProperty("error").GetString()!;
        Assert.Equal(message["capture_pane failed. ".Length..], inner);

        // .NET argument validation is a refusal too, and the parameter name it
        // appends names nothing the caller wrote.
        CallToolResult invalid = await harness.Client.CallToolAsync(
            "create_session",
            new Dictionary<string, object?> { ["name"] = "a:b" },
            cancellationToken: token);
        string rejected = Assert.IsType<TextContentBlock>(Assert.Single(invalid.Content)).Text;
        Assert.DoesNotContain("This is unexpected", rejected, StringComparison.Ordinal);
        Assert.DoesNotContain("(Parameter", rejected, StringComparison.Ordinal);

        // A null arriving for a field the schema invites is bad input, not a
        // broken invariant inside this server.
        CallToolResult nulled = await harness.Client.CallToolAsync(
            "send_keys",
            new Dictionary<string, object?> { ["keys"] = null },
            cancellationToken: token);
        string refusedNull = Assert.IsType<TextContentBlock>(Assert.Single(nulled.Content)).Text;
        Assert.DoesNotContain("This is unexpected", refusedNull, StringComparison.Ordinal);
        Assert.DoesNotContain("(Parameter", refusedNull, StringComparison.Ordinal);

        // A layout is checked before anything is sent, so the refusal cannot
        // have changed tmux and must not tell the caller it might have.
        CallToolResult layout = await harness.Client.CallToolAsync(
            "select_layout",
            new Dictionary<string, object?> { ["layout"] = "not-a-real-layout" },
            cancellationToken: token);
        string refusedLayout = Assert.IsType<TextContentBlock>(Assert.Single(layout.Content)).Text;
        Assert.Contains("not-a-real-layout", refusedLayout, StringComparison.Ordinal);
        Assert.DoesNotContain("may have acted", refusedLayout, StringComparison.Ordinal);

        // tmux validates a name before it renames anything (cmd-rename-session.c
        // check_name), exercising the plain TmuxCommandException path.
        CallToolResult badName = await harness.Client.CallToolAsync(
            "rename_session",
            new Dictionary<string, object?> { ["name"] = "bad\nname" },
            cancellationToken: token);
        // tmux only began validating names in 3.7 — older servers accept the
        // newline — so this asserts the advice, not the refusal.
        string refusedName = Assert.IsType<TextContentBlock>(Assert.Single(badName.Content)).Text;
        Assert.DoesNotContain("may have acted", refusedName, StringComparison.Ordinal);
        if (badName.IsError ?? false)
        {
            Assert.Contains("invalid session name", refusedName, StringComparison.Ordinal);
        }
    }

    private static readonly string[] NeverArrives = ["TEXT_THAT_NEVER_ARRIVES"];

    [UnixFact]
    public async Task A_call_that_waits_does_not_hold_up_another()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ProtocolHarness harness = await ProtocolHarness.StartAsync(token);
        _ = await harness.Client.CallToolAsync(
            "create_session",
            new Dictionary<string, object?> { ["name"] = "concurrency-probe" },
            cancellationToken: token);

        Task<CallToolResult> waiting = harness.Client.CallToolAsync(
            "wait_for_text",
            new Dictionary<string, object?>
            {
                ["patterns"] = NeverArrives,
                ["timeoutSeconds"] = 5,
            },
            cancellationToken: token).AsTask();

        CallToolResult listed = await harness.Client.CallToolAsync(
            "list_sessions",
            cancellationToken: token);

        // The instructions tell a model to wait rather than poll, so a wait
        // that held the connection would make that advice cost it every other
        // call for up to the ceiling.
        Assert.False(listed.IsError ?? false);
        Assert.False(waiting.IsCompleted);
        _ = await waiting;
    }

    private static JsonElement Structured(CallToolResult result) =>
        Assert.IsType<JsonElement>(result.StructuredContent);

    private static JsonObject ToolCall(string id, string name, JsonObject arguments) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["method"] = "tools/call",
        ["params"] = new JsonObject
        {
            ["name"] = name,
            ["arguments"] = arguments,
        },
    };

    private static async Task WriteAsync(
        Process process,
        JsonNode message,
        CancellationToken cancellationToken) =>
        await process.StandardInput.WriteLineAsync(
            message.ToJsonString(ToolJson.Options).AsMemory(),
            cancellationToken);

    private static async Task<JsonNode> ReadAsync(
        Process process,
        CancellationToken cancellationToken)
    {
        string line = await process.StandardOutput.ReadLineAsync(cancellationToken)
            ?? throw new InvalidOperationException("The MCP test process closed without a reply.");
        return JsonNode.Parse(line)
            ?? throw new InvalidOperationException("The MCP test process returned JSON null.");
    }

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
