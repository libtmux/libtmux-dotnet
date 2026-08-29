namespace LibTmux.UnitTests.Chaining;

public sealed class TmuxCommandTests
{
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
