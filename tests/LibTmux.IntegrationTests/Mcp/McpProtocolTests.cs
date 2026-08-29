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
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LibTmux.IntegrationTests;

/// <summary>What a client actually receives over the wire.</summary>
/// <remarks>
/// The tool tests check what the tools do to tmux. These check the contract a
/// client reads before calling anything: the names, the annotations it gates
/// on, the schemas it validates against, and the guidance that decides whether
/// it routes a question here at all. None of that is visible from calling a
/// method directly.
/// </remarks>
[Collection("tmux control clients")]
[UnsupportedOSPlatform("windows")]
public sealed class McpProtocolTests
{
    [UnixFact]
    public async Task Reading_tools_are_annotated_so_a_client_does_not_prompt_for_a_listing()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ProtocolHarness harness = await ProtocolHarness.StartAsync(token);

        IList<McpClientTool> tools = await harness.Client.ListToolsAsync(cancellationToken: token);
        McpClientTool listing = tools.Single(tool => tool.Name == "tmux_list_panes");

        Assert.True(listing.ProtocolTool.Annotations?.ReadOnlyHint);

        // The MCP spec defines destructive=false as additive-only. Every
        // mutating tmux tool can replace state or run caller-supplied input.
        Assert.All(
            tools.Where(tool => tool.ProtocolTool.Annotations?.ReadOnlyHint != true),
            tool => Assert.True(
                tool.ProtocolTool.Annotations?.DestructiveHint,
                $"{tool.Name} can change non-additive state but is not marked destructive"));

        McpClientTool split = tools.Single(tool => tool.Name == "tmux_split_pane");
        Assert.True(split.ProtocolTool.Annotations?.DestructiveHint);
        Assert.True(split.ProtocolTool.Annotations?.OpenWorldHint);
        Assert.True(tools.Single(tool => tool.Name == "tmux_create_session")
            .ProtocolTool.Annotations?.OpenWorldHint);
        Assert.True(tools.Single(tool => tool.Name == "tmux_create_window")
            .ProtocolTool.Annotations?.OpenWorldHint);
    }

    [UnixFact]
    public async Task Every_tool_says_what_it_answers()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ProtocolHarness harness = await ProtocolHarness.StartAsync(token);

        IList<McpClientTool> tools = await harness.Client.ListToolsAsync(cancellationToken: token);

        Assert.NotEmpty(tools);
        foreach (McpClientTool tool in tools)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(tool.ProtocolTool.Description),
                $"{tool.Name} has no description");

            // A schema is what lets a client destructure a result instead of
            // re-parsing prose out of it. tmux_display_message is exempt: it
            // answers whatever tmux expanded, which has no shape.
            if (tool.Name != "tmux_display_message")
            {
                Assert.True(
                    tool.ProtocolTool.OutputSchema.HasValue,
                    $"{tool.Name} advertises no output schema");
            }
        }
    }

    [UnixFact]
    public async Task A_result_arrives_as_structured_content()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ProtocolHarness harness = await ProtocolHarness.StartAsync(token);

        // Deliberately against a socket with no tmux server behind it: that is
        // the first thing an assistant asks, and it must be an answer rather
        // than an error.
        CallToolResult listed = await harness.Client.CallToolAsync(
            "tmux_list_sessions",
            cancellationToken: token);

        Assert.NotEqual(true, listed.IsError);
        Assert.NotNull(listed.StructuredContent);
    }

    [UnixFact]
    public async Task Nullable_session_fields_still_satisfy_the_advertised_output_schema()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ProtocolHarness harness = await ProtocolHarness.StartAsync(token);

        await harness.Client.CallToolAsync(
            "tmux_create_session",
            new Dictionary<string, object?> { ["name"] = "schema" },
            cancellationToken: token);

        CallToolResult listed = await harness.Client.CallToolAsync(
            "tmux_list_sessions",
            cancellationToken: token);
        CallToolResult hierarchy = await harness.Client.CallToolAsync(
            "tmux_hierarchy",
            cancellationToken: token);

        Assert.NotEqual(true, listed.IsError);
        Assert.NotEqual(true, hierarchy.IsError);

        JsonElement listedSession = listed.StructuredContent!.Value[0];
        JsonElement hierarchySession = hierarchy.StructuredContent!.Value
            .GetProperty("sessions")[0];
        Assert.True(listedSession.TryGetProperty("width", out _));
        Assert.True(listedSession.TryGetProperty("height", out _));
        Assert.True(hierarchySession.TryGetProperty("width", out _));
        Assert.True(hierarchySession.TryGetProperty("height", out _));
    }

    [UnixFact]
    public async Task The_destructive_tier_is_absent_unless_the_operator_asks_for_it()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ProtocolHarness harness = await ProtocolHarness.StartAsync(
            token,
            SafetyTier.Mutating);

        IList<McpClientTool> tools = await harness.Client.ListToolsAsync(cancellationToken: token);
        IEnumerable<string> names = tools.Select(tool => tool.Name);

        // Not registered rather than refused: a tool that is not in the list
        // cannot be called by name, guessed at, or argued for.
        Assert.DoesNotContain("tmux_kill_session", names);
        Assert.DoesNotContain("tmux_kill_server", names);
        Assert.Contains("tmux_split_pane", names);
        Assert.Contains("tmux_list_panes", names);
    }

    [UnixFact]
    public async Task The_readonly_tier_offers_nothing_that_changes_tmux()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ProtocolHarness harness = await ProtocolHarness.StartAsync(
            token,
            SafetyTier.ReadOnly);

        IList<McpClientTool> tools = await harness.Client.ListToolsAsync(cancellationToken: token);

        Assert.All(
            tools,
            tool => Assert.True(
                tool.ProtocolTool.Annotations?.ReadOnlyHint == true,
                $"{tool.Name} is offered at the readonly tier but is not annotated read-only"));
    }

    [UnixFact]
    public async Task The_hierarchy_is_readable_as_a_resource()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ProtocolHarness harness = await ProtocolHarness.StartAsync(token);

        await harness.Client.CallToolAsync(
            "tmux_create_session",
            new Dictionary<string, object?> { ["name"] = "probe" },
            cancellationToken: token);

        IList<McpClientResource> resources = await harness.Client.ListResourcesAsync(
            cancellationToken: token);
        Assert.Contains(resources, resource => resource.Uri == "tmux://hierarchy");

        ReadResourceResult read = await harness.Client.ReadResourceAsync(
            "tmux://hierarchy",
            cancellationToken: token);
        Assert.NotEmpty(read.Contents);
    }

    [UnixFact]
    public async Task The_recipes_are_offered_as_prompts()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ProtocolHarness harness = await ProtocolHarness.StartAsync(token);

        IList<McpClientPrompt> prompts = await harness.Client.ListPromptsAsync(
            cancellationToken: token);

        Assert.Contains(prompts, prompt => prompt.Name == "tmux_run_and_report");
        Assert.Contains(prompts, prompt => prompt.Name == "tmux_diagnose_pane");
    }

    [UnixFact]
    public async Task A_failure_arrives_as_an_error_result_rather_than_a_dropped_connection()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ProtocolHarness harness = await ProtocolHarness.StartAsync(token);

        // A session has to exist first, or the failure under test would be
        // "no server running" rather than "no such pane".
        await harness.Client.CallToolAsync(
            "tmux_create_session",
            new Dictionary<string, object?> { ["name"] = "probe" },
            cancellationToken: token);

        CallToolResult failed = await harness.Client.CallToolAsync(
            "tmux_capture_pane",
            new Dictionary<string, object?> { ["paneId"] = "%999" },
            cancellationToken: token);

        Assert.True(failed.IsError);
        string text = Assert.IsType<TextContentBlock>(failed.Content[0]).Text;

        // The message has to name what to do next. "An error occurred" costs a
        // model a turn and teaches it nothing.
        Assert.Contains("%999", text, StringComparison.Ordinal);
        Assert.Contains("tmux_list_panes", text, StringComparison.Ordinal);
    }

    [UnixFact]
    public async Task A_subscribed_client_is_told_when_the_hierarchy_changes()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ProtocolHarness harness = await ProtocolHarness.StartAsync(token);

        await harness.Client.CallToolAsync(
            "tmux_create_session",
            new Dictionary<string, object?> { ["name"] = "watched" },
            cancellationToken: token);

        TaskCompletionSource<JsonNode?> acknowledged = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<JsonNode?> updated = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using IAsyncDisposable ack = harness.Client.RegisterNotificationHandler(
            NotificationMethods.SubscriptionsAcknowledgedNotification,
            (notification, _) =>
            {
                acknowledged.TrySetResult(notification.Params);
                return default;
            });
        await using IAsyncDisposable changed = harness.Client.RegisterNotificationHandler(
            NotificationMethods.ResourceUpdatedNotification,
            (notification, _) =>
            {
                updated.TrySetResult(notification.Params);
                return default;
            });

        // The listen request IS the stream: over stdio it stays open for as
        // long as the subscription lives, so it is started rather than
        // awaited, and cancelled to end the subscription.
        using CancellationTokenSource listening = CancellationTokenSource
            .CreateLinkedTokenSource(token);
        JsonRpcRequest listenRequest = new()
        {
            Method = RequestMethods.SubscriptionsListen,
            Params = JsonSerializer.SerializeToNode(
                new SubscriptionsListenRequestParams
                {
                    Notifications = new SubscriptionsListenNotifications
                    {
                        ResourceSubscriptions = ["tmux://hierarchy"],
                    },
                },
                McpJsonUtilities.DefaultOptions),
        };
        Task stream = harness.Client.SendRequestAsync(listenRequest, listening.Token);

        Task acknowledgementDeadline = Task.Delay(TimeSpan.FromSeconds(15), token);
        Task acknowledgementOutcome = await Task.WhenAny(
            acknowledged.Task,
            stream,
            acknowledgementDeadline);
        if (acknowledgementOutcome == stream)
        {
            await stream;
        }

        Assert.Same(acknowledged.Task, acknowledgementOutcome);

        // A window appearing is a structural change, which is what tmux
        // reports to a control client without being asked.
        await harness.Client.CallToolAsync(
            "tmux_create_window",
            new Dictionary<string, object?> { ["name"] = "second" },
            cancellationToken: token);

        Assert.True(
            await Task.WhenAny(updated.Task, Task.Delay(TimeSpan.FromSeconds(20), token))
                == updated.Task,
            "no resources/updated notification arrived within 20s");

        JsonNode? parameters = await updated.Task;
        Assert.Equal(
            "tmux://hierarchy",
            Assert.IsType<JsonObject>(parameters)["uri"]?.GetValue<string>());

        // Tagged with the stream it belongs to, which is what lets a client
        // sharing one channel tell two subscriptions apart.
        JsonNode subscriptionId = Assert.IsType<JsonObject>(parameters)["_meta"]?
            ["io.modelcontextprotocol/subscriptionId"]
            ?? throw new Xunit.Sdk.XunitException("the event has no subscription id");
        _ = subscriptionId.GetValue<long>();

        await listening.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream);
    }

    [UnixFact]
    public async Task A_subscription_acknowledgement_preserves_a_string_request_id()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ProtocolHarness harness = await ProtocolHarness.StartAsync(token);
        TaskCompletionSource<JsonNode?> acknowledged = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using IAsyncDisposable ack = harness.Client.RegisterNotificationHandler(
            NotificationMethods.SubscriptionsAcknowledgedNotification,
            (notification, _) =>
            {
                acknowledged.TrySetResult(notification.Params);
                return default;
            });
        using CancellationTokenSource listening = CancellationTokenSource
            .CreateLinkedTokenSource(token);
        const string expectedId = "listen-string-id";
        Task stream = harness.Client.SendRequestAsync(
            new JsonRpcRequest
            {
                Id = new RequestId(expectedId),
                Method = RequestMethods.SubscriptionsListen,
                Params = JsonSerializer.SerializeToNode(
                    new SubscriptionsListenRequestParams
                    {
                        Notifications = new SubscriptionsListenNotifications(),
                    },
                    McpJsonUtilities.DefaultOptions),
            },
            listening.Token);

        Task acknowledgementDeadline = Task.Delay(TimeSpan.FromSeconds(15), token);
        Task acknowledgementOutcome = await Task.WhenAny(
            acknowledged.Task,
            stream,
            acknowledgementDeadline);
        if (acknowledgementOutcome == stream)
        {
            await stream;
        }

        Assert.Same(acknowledged.Task, acknowledgementOutcome);
        JsonNode? parameters = await acknowledged.Task;
        JsonNode subscriptionId = Assert.IsType<JsonObject>(parameters)["_meta"]?
            ["io.modelcontextprotocol/subscriptionId"]
            ?? throw new Xunit.Sdk.XunitException("the acknowledgement has no subscription id");
        Assert.Equal(expectedId, subscriptionId.GetValue<string>());

        await listening.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream);
    }

    [UnixFact]
    public async Task A_waiting_tool_can_be_started_as_a_task_and_collected_later()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ProtocolHarness harness = await ProtocolHarness.StartAsync(token);

        CallToolResult made = await harness.Client.CallToolAsync(
            "tmux_create_session",
            new Dictionary<string, object?> { ["name"] = "tasked" },
            cancellationToken: token);
        string pane = made.StructuredContent!.Value.GetProperty("paneId").GetString()!;

        CallToolResult started = await harness.Client.CallToolAsync(
            "tmux_start_job",
            new Dictionary<string, object?>
            {
                ["command"] = "echo TASKED && exit 7",
                ["paneId"] = pane,
            },
            cancellationToken: token);
        string jobId = started.StructuredContent!.Value
            .GetProperty("jobId")
            .GetString()!;

        CallToolResult finished = await harness.Client.CallToolWithPollingAsync(
            new CallToolRequestParams
            {
                Name = "tmux_job",
                Arguments = new Dictionary<string, JsonElement>
                {
                    ["jobId"] = JsonSerializer.SerializeToElement(jobId),
                    ["waitSeconds"] = JsonSerializer.SerializeToElement(20),
                },
            },
            cancellationToken: token);

        Assert.NotEqual(true, finished.IsError);
        Assert.Equal(
            7,
            finished.StructuredContent!.Value
                .GetProperty("job")
                .GetProperty("exitStatus")
                .GetInt32());
    }

    [UnixFact]
    public async Task Run_stays_synchronous_when_a_client_requests_a_task()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ProtocolHarness harness = await ProtocolHarness.StartAsync(token);
        CallToolResult made = await harness.Client.CallToolAsync(
            "tmux_create_session",
            new Dictionary<string, object?> { ["name"] = "run-sync" },
            cancellationToken: token);
        string pane = made.StructuredContent!.Value.GetProperty("paneId").GetString()!;

        ResultOrCreatedTask<CallToolResult> answered = await harness.Client.CallToolAsTaskAsync(
            new CallToolRequestParams
            {
                Name = "tmux_run",
                Arguments = new Dictionary<string, JsonElement>
                {
                    ["command"] = JsonSerializer.SerializeToElement("exit 0"),
                    ["paneId"] = JsonSerializer.SerializeToElement(pane),
                },
            },
            cancellationToken: token);

        Assert.False(answered.IsTask);
        Assert.NotNull(answered.Result);
    }

    [UnixFact]
    public async Task A_listing_stays_a_plain_call_rather_than_becoming_a_task()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ProtocolHarness harness = await ProtocolHarness.StartAsync(token);

        ResultOrCreatedTask<CallToolResult> answered = await harness.Client.CallToolAsTaskAsync(
            new CallToolRequestParams { Name = "tmux_list_sessions" },
            cancellationToken: token);

        // A listing answers in milliseconds. Making it a task would cost a
        // second round trip to collect an answer that was already there.
        Assert.False(answered.IsTask);
        Assert.NotNull(answered.Result);
    }

    /// <summary>A server and a client joined by a pipe, over a throwaway socket.</summary>
    /// <remarks>
    /// Composed through <see cref="McpServerComposition" /> rather than wired
    /// by hand, so what these tests check is what the executable actually
    /// serves.
    /// </remarks>
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

        internal static async Task<ProtocolHarness> StartAsync(
            CancellationToken cancellationToken,
            SafetyTier tier = SafetyTier.Destructive)
        {
            ServiceCollection services = new();
            services.AddLogging();
            string socketName = $"ltp-{Guid.NewGuid():N}"[..20];
            McpServerComposition.Add(
                services,
                // Ten seconds clamped what these tests ask for, so a job that
                // waited on a shell starting under load reported no exit status
                // rather than the one it was about to produce.
                new ServerPolicy { Tier = tier, WaitCeiling = TimeSpan.FromSeconds(20) },
                new ServerConnectionOptions(
                    tmuxBinaryPath: System.Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux",
                    socketName: socketName,
                    configurationFile: "/dev/null"),
                callerPaneId: null);
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

            // The tools start a tmux server on this socket, and nothing else
            // here owns it. Left behind it keeps running: a suite run leaked
            // one per test until the machine carried dozens of idle servers
            // and unrelated timing tests began to fail.
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
                // No server was ever started on it, which is the common case
                // for a test that only listed things.
            }
        }
    }
}
