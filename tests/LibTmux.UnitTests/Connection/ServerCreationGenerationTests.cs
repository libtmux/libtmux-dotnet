using System.Runtime.Versioning;
using System.Text;
using LibTmux.Internal;

namespace LibTmux.UnitTests.Connection;

[UnsupportedOSPlatform("windows")]
public sealed class ServerCreationGenerationTests
{
    [Theory]
    [InlineData("bad:name")]
    [InlineData("bad.name")]
    public void Session_commands_reject_names_that_direct_creation_rejects(string name)
    {
        var request = new NewSessionRequest { Name = name };

        Assert.Throws<ArgumentException>(() => request.ToCommand());
    }

    [Fact]
    public void Session_commands_retain_the_expected_generation()
    {
        var expected = new ServerGeneration(91, 901);
        var request = new NewSessionRequest { Name = "build", ExpectedGeneration = expected };

        Assert.Equal(expected, request.ToCommand().RequiredGeneration);
        Assert.Contains("#{session_id}", request.ToCommand().Arguments);
        Assert.Null(new NewSessionRequest().ToCommand().RequiredGeneration);
        Assert.Throws<ArgumentOutOfRangeException>(() => new NewSessionRequest
        {
            ExpectedGeneration = default(ServerGeneration),
        });
    }

    [Fact]
    public async Task Generation_bound_replacement_is_rejected_before_any_dispatch()
    {
        var request = new NewSessionRequest
        {
            Name = "build",
            ExpectedGeneration = new ServerGeneration(91, 901),
            ReplaceExisting = true,
        };
        int dispatches = 0;
        var connection = new TmuxConnection(new ServerConnectionOptions(), (command, _) =>
        {
            dispatches++;
            throw new InvalidOperationException("Invalid creation dispatched.");
        });
        var server = new Server(connection, null, null);

        Assert.Throws<ArgumentException>(() => request.ToCommand());
        await Assert.ThrowsAsync<ArgumentException>(() => server.CreateSessionAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(0, dispatches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replacement_after_creation_cannot_be_initialized_or_returned_by_readback(bool bindBeforeCreation)
    {
        var expected = new ServerGeneration(91, 901);
        var actual = new ServerGeneration(91, 902);
        int initializers = 0;
        List<TmuxCommandRequest> requests = [];
        var connection = new TmuxConnection(new ServerConnectionOptions
        {
            InitializeAsync = (_, _) =>
            {
                initializers++;
                return ValueTask.CompletedTask;
            },
        }, FakeMultiplexer.AnsweringVersion((command, _) =>
        {
            requests.Add(command);
            if (command.LogicalArguments.Contains("new-session", StringComparer.Ordinal))
            {
                string prefix = command.LogicalArguments.Contains("if-shell", StringComparer.Ordinal) ? "91:901\n" : string.Empty;
                string receipt = command.LogicalArguments.Contains(
                    TmuxCreationReceipt.Format, StringComparer.Ordinal)
                    ? "91:901\t$2\t@3\t%4\t0\n"
                    : "$2\n";
                return Task.FromResult(Result(command, prefix + receipt));
            }
            if (command.LogicalArguments is ["display-message", "-p", TmuxConnection.GenerationFormat])
            {
                return Task.FromResult(Result(command, "91:902\n"));
            }
            throw new InvalidOperationException("Readback reached a replacement daemon.");
        }));
        var server = new Server(connection, expected, "tmux 3.7");

        LibTmuxException failure = await Assert.ThrowsAsync<LibTmuxException>(() => server.CreateSessionAsync(
            new NewSessionRequest
            {
                Name = "build",
                ExpectedGeneration = bindBeforeCreation ? expected : null,
            }, TestContext.Current.CancellationToken));

        StaleServerGenerationException stale = Assert.IsType<StaleServerGenerationException>(failure.InnerException);
        Assert.Equal(expected, stale.Expected);
        Assert.Equal(actual, stale.Actual);
        Assert.Equal(TmuxDispatchState.Unknown, failure.Dispatch);
        Assert.Equal(bindBeforeCreation, requests[0].LogicalArguments.Contains("if-shell", StringComparer.Ordinal));
        Assert.Equal(2, requests.Count);
        Assert.Equal(0, initializers);
    }

    [Fact]
    public async Task Window_receipts_reject_select_existing_before_dispatch()
    {
        int dispatches = 0;
        var connection = new TmuxConnection(new ServerConnectionOptions(), (_, _) =>
        {
            dispatches++;
            throw new InvalidOperationException("Invalid receipt creation dispatched.");
        });
        var generation = new ServerGeneration(91, 901);
        var server = new Server(connection, generation, "tmux 3.7");
        var session = new Session(server, connection, generation, new SessionId(2), new Dictionary<string, string?>());
        var request = new NewWindowRequest { SelectExisting = true };

        await Assert.ThrowsAsync<ArgumentException>(() => session.CreateWindowWithReceiptAsync(
            request, TestContext.Current.CancellationToken));

        Assert.Equal(0, dispatches);
        Assert.Contains("#{window_id}", request.ToCommand(session).Arguments);
    }

    private static TmuxCommandResult Result(TmuxCommandRequest request, string output)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(output);
        return new TmuxCommandResult(request.LogicalArguments, 0, bytes, ReadOnlyMemory<byte>.Empty,
            Utf8BackslashDecoder.ProjectOutputLines(bytes), []);
    }
}
