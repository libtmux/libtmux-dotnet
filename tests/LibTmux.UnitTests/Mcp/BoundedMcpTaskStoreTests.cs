using System.Text.Json;
using System.Text.Json.Nodes;
using LibTmux.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

#pragma warning disable MCPEXP002

namespace LibTmux.UnitTests.Mcp;

public sealed class BoundedMcpTaskStoreTests
{
    private static readonly JsonElement EmptyResult = JsonSerializer.SerializeToElement(new { });

    [Fact]
    public async Task Active_admission_is_atomic_under_a_flood()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var store = new BoundedMcpTaskStore(
            maximumActiveTasks: 3,
            maximumRetainedTasks: 100);

        Task<McpTaskInfo?>[] attempts = Enumerable.Range(0, 100)
            .Select(_ => Task.Run(async () =>
            {
                try
                {
                    return await store.CreateTaskAsync(cancellationToken: cancellationToken);
                }
                catch (McpProtocolException)
                {
                    return null;
                }
            }))
            .ToArray();

        McpTaskInfo[] admitted = (await Task.WhenAll(attempts))
            .OfType<McpTaskInfo>()
            .ToArray();

        Assert.Equal(3, admitted.Length);
        foreach (McpTaskInfo task in admitted)
        {
            await store.SetCompletedAsync(task.TaskId, EmptyResult, cancellationToken);
        }
    }

    [Fact]
    public async Task Client_cancellation_retains_admission_until_background_finalizes()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider();
        var store = new BoundedMcpTaskStore(
            maximumActiveTasks: 1,
            maximumRetainedTasks: 4,
            timeToLive: TimeSpan.FromMinutes(1),
            timeProvider: clock);
        McpTaskInfo task = await store.CreateTaskAsync(cancellationToken: cancellationToken);

        using (store.EnterClientCancellation(task.TaskId))
        {
            Assert.True(await store.SetCancelledAsync(task.TaskId, cancellationToken));
        }

        using (store.EnterClientCancellation(task.TaskId))
        {
            Assert.False(await store.SetCancelledAsync(task.TaskId, cancellationToken));
        }

        Assert.Equal(
            McpTaskStatus.Cancelled,
            (await store.GetTaskAsync(task.TaskId, cancellationToken))?.Status);
        McpProtocolException busy = await Assert.ThrowsAsync<McpProtocolException>(
            () => store.CreateTaskAsync(cancellationToken: cancellationToken));
        Assert.Contains("wait for it to stop", busy.Message, StringComparison.Ordinal);

        clock.Advance(TimeSpan.FromHours(1));
        Assert.NotNull(await store.GetTaskAsync(task.TaskId, cancellationToken));
        Assert.False(await store.SetCancelledAsync(task.TaskId, cancellationToken));

        McpTaskInfo replacement = await store.CreateTaskAsync(
            cancellationToken: cancellationToken);
        Assert.NotNull(replacement);
        Assert.Null(await store.GetTaskAsync(task.TaskId, cancellationToken));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Background_completion_releases_a_cancelled_execution(bool completed)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var store = new BoundedMcpTaskStore(
            maximumActiveTasks: 1,
            maximumRetainedTasks: 4);
        McpTaskInfo task = await store.CreateTaskAsync(cancellationToken: cancellationToken);
        using (store.EnterClientCancellation(task.TaskId))
        {
            _ = await store.SetCancelledAsync(task.TaskId, cancellationToken);
        }

        if (completed)
        {
            await store.SetCompletedAsync(task.TaskId, EmptyResult, cancellationToken);
        }
        else
        {
            await store.SetFailedAsync(task.TaskId, EmptyResult, cancellationToken);
        }

        Assert.Equal(
            McpTaskStatus.Cancelled,
            (await store.GetTaskAsync(task.TaskId, cancellationToken))?.Status);
        Assert.NotNull(await store.CreateTaskAsync(cancellationToken: cancellationToken));
    }

    [Fact]
    public async Task Retained_capacity_reclaims_only_after_the_advertised_ttl()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider();
        var store = new BoundedMcpTaskStore(
            maximumActiveTasks: 2,
            maximumRetainedTasks: 2,
            timeToLive: TimeSpan.FromMinutes(5),
            timeProvider: clock);
        McpTaskInfo first = await store.CreateTaskAsync(cancellationToken: cancellationToken);
        McpTaskInfo second = await store.CreateTaskAsync(cancellationToken: cancellationToken);
        await store.SetCompletedAsync(first.TaskId, EmptyResult, cancellationToken);
        await store.SetCompletedAsync(second.TaskId, EmptyResult, cancellationToken);

        McpProtocolException full = await Assert.ThrowsAsync<McpProtocolException>(
            () => store.CreateTaskAsync(cancellationToken: cancellationToken));
        Assert.Contains("after one expires", full.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("collect", full.Message, StringComparison.OrdinalIgnoreCase);

        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.NotNull(await store.CreateTaskAsync(cancellationToken: cancellationToken));
        Assert.Null(await store.GetTaskAsync(first.TaskId, cancellationToken));
        Assert.Null(await store.GetTaskAsync(second.TaskId, cancellationToken));
    }

    [Fact]
    public void Cancellation_wrapper_fails_closed_without_exactly_one_sdk_handler()
    {
        var wrapper = new BoundedMcpTaskCancellationOptions(new BoundedMcpTaskStore());
        var missing = new McpServerOptions { RequestHandlers = [] };
        var duplicate = new McpServerOptions
        {
            RequestHandlers =
            [
                Handler("tasks/cancel"),
                Handler("tasks/cancel"),
            ],
        };

        Assert.Throws<InvalidOperationException>(() => wrapper.Configure(missing));
        Assert.Throws<InvalidOperationException>(() => wrapper.Configure(duplicate));
    }

    [Fact]
    public async Task Composed_cancellation_handler_cannot_release_execution_admission()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var store = new BoundedMcpTaskStore(
            maximumActiveTasks: 1,
            maximumRetainedTasks: 4);
        McpTaskInfo task = await store.CreateTaskAsync(cancellationToken: cancellationToken);
        var options = new McpServerOptions
        {
            RequestHandlers =
            [
                new McpServerRequestHandler
                {
                    Method = "tasks/cancel",
                    Handler = async (request, cancellationToken) =>
                    {
                        string taskId = request.Params!["taskId"]!.GetValue<string>();
                        _ = await store.SetCancelledAsync(taskId, cancellationToken);
                        return null;
                    },
                },
            ],
        };
        new BoundedMcpTaskCancellationOptions(store).Configure(options);
        var request = new JsonRpcRequest
        {
            Method = "tasks/cancel",
            Params = new JsonObject { ["taskId"] = task.TaskId },
        };

        await using McpServer server = McpServer.Create(
            new StreamServerTransport(Stream.Null, Stream.Null), options);
        var context = new RequestContext<JsonNode?>(server, request, request.Params);
        await options.RequestHandlers![0].Handler(context, cancellationToken);
        await options.RequestHandlers[0].Handler(context, cancellationToken);

        McpProtocolException busy = await Assert.ThrowsAsync<McpProtocolException>(
            () => store.CreateTaskAsync(cancellationToken: cancellationToken));
        Assert.Contains("active MCP tasks", busy.Message, StringComparison.Ordinal);

        Assert.False(await store.SetCancelledAsync(task.TaskId, cancellationToken));
        Assert.NotNull(await store.CreateTaskAsync(cancellationToken: cancellationToken));
    }

    private static McpServerRequestHandler Handler(string method) => new()
    {
        Method = method,
        Handler = static (_, _) => ValueTask.FromResult<System.Text.Json.Nodes.JsonNode?>(null),
    };

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan duration) => _now += duration;
    }
}
