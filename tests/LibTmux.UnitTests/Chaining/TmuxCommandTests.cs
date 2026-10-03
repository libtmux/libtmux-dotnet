using System.Runtime.Versioning;
using System.Text;
using LibTmux.Internal;

namespace LibTmux.UnitTests.Chaining;

public sealed class TmuxCommandTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [UnsupportedOSPlatform("windows")]
    public async Task Placement_guards_do_not_replace_logical_result_or_failure_arguments(int outcome, bool generationGuard)
    {
        var cause = new IOException("pipe failed");
        var generation = new ServerGeneration(92, 902);
        TmuxCommand command = TmuxCommand.Create("move-window", "-s", "$1:5", "-t", "$1:3") with
        {
            RequiredWindowPlacement = new WindowEntityKey(new SessionId(1), new WindowId(2), 5),
            RequiredGeneration = generationGuard ? generation : null,
        };
        TmuxCommand tail = TmuxCommand.Create("display-message", "-p", "payload");
        IReadOnlyList<string> expected = TmuxCommandRequest.Group(command.ToArguments(), tail.ToArguments()).LogicalArguments;
        Task<TmuxCommandResult> Execute(TmuxCommandRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.Contains("if-shell", request.LogicalArguments);
            if (outcome == 2)
            {
                throw new TmuxTransportException("transport failed", request.LogicalArguments, TmuxDispatchState.Unknown, cause);
            }

            string output = generationGuard ? "92:902\npayload\n" : "payload\n";
            return Task.FromResult(new TmuxCommandResult(
                request.LogicalArguments, outcome, Encoding.UTF8.GetBytes(output),
                outcome == 1 ? "native failure\n"u8.ToArray() : [],
                generationGuard ? ["92:902", "payload"] : ["payload"],
                outcome == 1 ? ["native failure"] : []));
        }

        var dispatcher = new TmuxCommandDispatcher(
            (arguments, token) => Execute(TmuxCommandRequest.Single(arguments), token),
            executeGroup: (commands, token) => Execute(TmuxCommandRequest.Group([.. commands]), token));
        var guard = new TmuxGenerationGuard(Execute, () => "generation_changed");
        var chain = new TmuxChain(dispatcher, [command, tail], generationGuard ? guard.ExecuteAsync : null);
        if (outcome == 2)
        {
            TmuxTransportException error = await Assert.ThrowsAsync<TmuxTransportException>(() => chain.ExecuteAsync(TestContext.Current.CancellationToken));
            Assert.Equal(expected, error.Arguments);
            Assert.Equal(TmuxDispatchState.Unknown, error.Dispatch);
            Assert.Same(cause, error.InnerException);
            return;
        }

        TmuxCommandResult result = outcome == 1
            ? (await Assert.ThrowsAsync<TmuxCommandException>(() => chain.ExecuteAsync(TestContext.Current.CancellationToken))).Result
            : await chain.ExecuteAsync(TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.Arguments);
        Assert.Equal(["payload"], result.StandardOutputLines);
        Assert.Equal(outcome, result.ExitCode);
    }

    [Fact]
    public void Placement_guards_participate_in_command_identity_and_rendered_byte_limits()
    {
        var placement = new WindowEntityKey(new SessionId(1), new WindowId(2), 5);
        TmuxCommand command = TmuxCommand.Create("move-window", "-s", "$1:5", "-t", "$1:3") with
        {
            RequiredWindowPlacement = placement,
            RequiredWindowPaneMembership = TmuxWindowPlacementGuard.CreatePaneMembership([new PaneId(1), new PaneId(9)]),
            RequiredTargetWindow = ("%9", new WindowId(2)),
        };
        TmuxCommand same = command with { RequiredWindowPlacement = placement };
        TmuxCommand otherWindow = command with { RequiredWindowPlacement = placement with { WindowId = new WindowId(3) } };
        Assert.Equal(command, same);
        Assert.Equal(command.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(command, otherWindow);
        Assert.NotEqual(command, command with { RequiredWindowPaneMembership = TmuxWindowPlacementGuard.CreatePaneMembership([new PaneId(1)]) });
        Assert.NotEqual(command, command with { RequiredTargetWindow = ("%1", new WindowId(2)) });
        Assert.NotEqual(command, command with { RequiredTargetWindow = ("%9", new WindowId(3)) });
        Assert.Equal(command.ToArguments(), otherWindow.ToArguments());
        string rendered = ControlModeCommandRenderer.Render(command);
        Assert.Contains(" ; 'move-window' ", rendered, StringComparison.Ordinal);
        Assert.Contains("#{==:#{pane_id},%9}", rendered, StringComparison.Ordinal);
        Assert.Contains("'-t' '%9' '#{==:#{window_id},@2}'", rendered, StringComparison.Ordinal);
        Assert.Equal(Encoding.UTF8.GetByteCount(rendered), ControlModeCommandRenderer.GetRenderedByteCount(command));
    }

    [Fact]
    public void Command_tokens_reject_nul_and_null_arguments()
    {
        Assert.Throws<ArgumentException>(() => TmuxCommand.Create("bad\0name"));
        Assert.Throws<ArgumentException>(
            () => TmuxCommand.Create("display-message", "bad\0argument"));
        Assert.Throws<ArgumentException>(
            () => new TmuxCommand("display-message", [null!]));
    }

    [Fact]
    public void Command_arguments_are_owned_by_the_value()
    {
        var arguments = new List<string> { "original" };

        var command = new TmuxCommand("display-message", arguments);
        arguments[0] = "mutated";
        arguments.Add("injected");

        Assert.Equal(["original"], command.Arguments);
    }

    [Fact]
    public void Equal_commands_compare_their_argument_values()
    {
        var left = new TmuxCommand("display-message", ["-p", "value"]);
        var right = new TmuxCommand("display-message", ["-p", "value"]);

        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }
}
