using System.Runtime.Versioning;
using System.Text;
using LibTmux.Internal;

namespace LibTmux.UnitTests.Entities;

[UnsupportedOSPlatform("windows")]
public sealed class LayoutValidationTests
{
    private const string Socket = "/tmp/libtmux-dotnet-test/layout-validation";
    private static readonly ServerGeneration Generation = new(91, 901);

    [Theory]
    [InlineData("3.7c", "3.3a", "main-h", true)]
    [InlineData("3.3a", "3.7c", "main-h", false)]
    [InlineData("3.7c", "3.3a", "main-horizontal-mirrored", false)]
    [InlineData("3.3a", "3.7c", "main-horizontal-mirrored", true)]
    [InlineData("3.7c", "next-3.8", "main-horizontal-mirrored", false)]
    [InlineData("3.3a", "3.5-rc1", "main-h", false)]
    public async Task Daemon_version_controls_names_instead_of_client_banner(
        string client, string daemon, string layout, bool accepted)
    {
        int queries = 0;
        Server server = CreateServer(client, request =>
        {
            queries++;
            Assert.Equal(["display-message", "-p", "#{pid}:#{start_time}\t#{version}"], request.LogicalArguments);
            return Result(request, 0, $"91:901\t{daemon}\n");
        });

        Task validation = server.ValidateLayoutsAsync([(layout, 1)], TestContext.Current.CancellationToken);
        if (accepted) await validation;
        else await Assert.ThrowsAsync<ArgumentException>(() => validation);
        Assert.Equal(1, queries);
    }

    [Fact]
    public async Task A_batch_reads_one_daemon_version_and_validates_all_syntax_first()
    {
        int queries = 0;
        Server server = CreateServer("3.7c", request =>
        {
            queries++;
            return Result(request, 0, "91:901\t3.3a\n");
        });
        await Assert.ThrowsAsync<ArgumentException>(() => server.ValidateLayoutsAsync(
            [("main-h", 1), ("32d2,80x24,0,0{}", 1)], TestContext.Current.CancellationToken));
        Assert.Equal(0, queries);
        await server.ValidateLayoutsAsync([("main-h", 1), ("main-v", 2), ("main-h", 3)], TestContext.Current.CancellationToken);
        Assert.Equal(1, queries);
    }

    [Theory]
    [InlineData("no server running on " + Socket, true)]
    [InlineData("error connecting to " + Socket + " (No such file or directory)", true)]
    [InlineData("error connecting to " + Socket + " (Connection refused)", true)]
    [InlineData("error connecting to " + Socket + " (Permission denied)", false)]
    [InlineData("error connecting to " + Socket + " (Connection timed out)", false)]
    [InlineData("error connecting to /other (Connection refused)", false)]
    [InlineData("unexpected daemon failure", false)]
    public async Task Only_exact_cold_endpoint_errors_use_the_client(string error, bool cold)
    {
        Server server = CreateServer("3.3a", request => Result(request, 1, error: error + "\n"), materialized: false);
        Task validation = server.ValidateLayoutsAsync([("main-h", 1)], TestContext.Current.CancellationToken);
        if (cold) await validation;
        else await Assert.ThrowsAsync<TmuxCommandException>(() => validation);
    }

    [Theory]
    [InlineData("no server running on /tmp/tmux-1000/named", true)]
    [InlineData("error connecting to /tmp/tmux-1000/named (No such file or directory)", true)]
    [InlineData("error connecting to /tmp/tmux-1000/named (Connection refused)", true)]
    [InlineData("error connecting to /tmp/tmux-1000/named (Permission denied)", false)]
    [InlineData("no server running on named", false)]
    [InlineData("unexpected daemon failure", false)]
    public async Task A_named_socket_accepts_any_cold_socket_root(string error, bool cold)
    {
        Server server = CreateServer("3.3a", request => Result(request, 1, error: error + "\n"), materialized: false, socketName: "named");
        Assert.Null(server.Connection!.ResolvedSocket.SocketPath);
        Task validation = server.ValidateLayoutsAsync([("main-h", 1)], TestContext.Current.CancellationToken);
        if (cold) await validation;
        else await Assert.ThrowsAsync<TmuxCommandException>(() => validation);
    }

    [Theory]
    [InlineData("91:901\tgarbage\n")]
    [InlineData("3.7c\n")]
    [InlineData("91:901\t3.7c\n91:901\t3.7c\n")]
    public async Task Malformed_version_replies_do_not_use_the_client(string output)
    {
        Server server = CreateServer("3.7c", request => Result(request, 0, output));
        await Assert.ThrowsAsync<InvalidDataException>(() => server.ValidateLayoutsAsync(
            [("main-horizontal-mirrored", 1)], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_bound_server_rejects_a_replacement_daemon()
    {
        Server server = CreateServer("3.7c", request => Result(request, 0, "92:902\t3.7c\n"));
        await Assert.ThrowsAsync<StaleServerGenerationException>(() => server.ValidateLayoutsAsync(
            [("main-horizontal-mirrored", 1)], TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Direct_and_chained_names_are_refused_before_any_mutation(bool chained)
    {
        int queries = 0;
        Server server = CreateServer("3.7c", request =>
        {
            queries++;
            Assert.DoesNotContain("select-layout", request.LogicalArguments);
            Assert.DoesNotContain("set-option", request.LogicalArguments);
            return Result(request, 0, "91:901\t3.3a\n");
        });
        Window window = new(server, server.Connection!, Generation, new WindowId(1));
        var request = new SelectLayoutRequest("main-horizontal-mirrored");
        TmuxCommand command = request.ToCommand(window);
        Assert.Equal(0, queries);
        TmuxWindowException error = await Assert.ThrowsAsync<TmuxWindowException>(() => chained
            ? server.Chain().Then("set-option", "-g", "@changed", "yes").Then(command).ExecuteAsync(TestContext.Current.CancellationToken)
            : window.SelectLayoutAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(TmuxDispatchState.NotDispatched, error.Dispatch);
        Assert.Equal(1, queries);
    }

    [Fact]
    public async Task Cancellation_propagates_without_a_mutating_dispatch()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var connection = new TmuxConnection(new ServerConnectionOptions(socketPath: Socket), (request, token) =>
        {
            if (request.LogicalArguments is ["-V"]) return Task.FromResult(Result(request, 0, "tmux 3.7c\n"));
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Cancellation was lost.");
        });
        var server = new Server(connection, Generation, "tmux 3.7c");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server.ValidateLayoutsAsync(
            [("main-horizontal-mirrored", 1)], cancellation.Token));
    }

    [Fact]
    public async Task A_chain_validates_once_through_its_execution_connection()
    {
        Server original = CreateServer("3.7c", _ => throw new InvalidOperationException("Original connection is closed."));
        Window window = new(original, original.Connection!, Generation, new WindowId(1));
        TmuxCommand command = new SelectLayoutRequest("main-horizontal-mirrored").ToCommand(window);
        int queries = 0;
        Server execution = CreateServer("3.7c", request =>
        {
            queries++;
            return Result(request, 0, "91:901\t3.3a\n");
        }, materialized: false);

        TmuxWindowException error = await Assert.ThrowsAsync<TmuxWindowException>(() =>
            execution.Chain().Then([command, command]).ExecuteAsync(TestContext.Current.CancellationToken));

        Assert.Equal(window.Id, error.WindowId);
        Assert.Equal(TmuxDispatchState.NotDispatched, error.Dispatch);
        Assert.Equal(1, queries);
    }

    [Fact]
    public void Typed_layout_guard_participates_in_command_equality_and_raw_replacement()
    {
        Server server = CreateServer("3.7c", _ => throw new InvalidOperationException("Rendering performed I/O."));
        Window window = new(server, server.Connection!, Generation, new WindowId(1));
        TmuxCommand typed = new SelectLayoutRequest("main-h").ToCommand(window);
        TmuxCommand same = new SelectLayoutRequest("main-h").ToCommand(window);
        TmuxCommand raw = new(typed.Name, typed.Arguments) { RequiredGeneration = typed.RequiredGeneration };

        Assert.Equal(typed, same);
        Assert.Equal(typed.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(typed, raw);
        Assert.Equal(raw, typed with { Name = typed.Name });
        Assert.Equal(raw, typed with { Arguments = typed.Arguments });
    }

    private static Server CreateServer(string client, Func<TmuxCommandRequest, TmuxCommandResult> execute, bool materialized = true, string? socketName = null)
    {
        var connection = new TmuxConnection(socketName is null ? new ServerConnectionOptions(socketPath: Socket) : new ServerConnectionOptions(socketName: socketName), (request, _) =>
            Task.FromResult(request.LogicalArguments is ["-V"]
                ? Result(request, 0, $"tmux {client}\n") : execute(request)));
        return new Server(connection, materialized ? Generation : null, $"tmux {client}");
    }

    private static TmuxCommandResult Result(TmuxCommandRequest request, int code, string output = "", string error = "") =>
        new(request.LogicalArguments, code, Encoding.UTF8.GetBytes(output), Encoding.UTF8.GetBytes(error),
            output.Split('\n', StringSplitOptions.RemoveEmptyEntries), error.Split('\n', StringSplitOptions.RemoveEmptyEntries));
}
