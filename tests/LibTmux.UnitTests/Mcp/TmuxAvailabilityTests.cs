using System.Runtime.Versioning;
using LibTmux.Mcp;

namespace LibTmux.UnitTests.Mcp;

[UnsupportedOSPlatform("windows")]
public sealed class TmuxAvailabilityTests
{
    [Fact]
    public void A_missing_server_reads_as_absent()
    {
        TmuxCommandException error = CommandFailure(["no server running on /tmp/tmux-1000/default"]);

        Assert.True(TmuxAvailability.IsServerAbsent(error));
    }

    [Fact]
    public void A_missing_socket_file_reads_as_absent()
    {
        TmuxCommandException error = CommandFailure(
            ["error connecting to /tmp/tmux-1000/default (No such file or directory)"]);

        Assert.True(TmuxAvailability.IsServerAbsent(error));
    }

    [Fact]
    public void A_permission_error_does_not_read_as_absent()
    {
        // "error connecting to" alone also covers a socket tmux cannot open
        // because of its permissions, which names a live daemon, not an
        // absent one.
        TmuxCommandException error = CommandFailure(
            ["error connecting to /tmp/tmux-1000/default (Permission denied)"]);

        Assert.False(TmuxAvailability.IsServerAbsent(error));
    }

    [Fact]
    public void An_unrelated_command_failure_does_not_read_as_absent()
    {
        TmuxCommandException error = CommandFailure(["can't find pane %99"]);

        Assert.False(TmuxAvailability.IsServerAbsent(error));
    }

    [Fact]
    public void The_exception_message_alone_names_no_cause()
    {
        // A command exception's message is only the operation that failed,
        // never tmux's own wording, so a check against the message alone
        // could never see an absent server.
        TmuxCommandException error = CommandFailure(["no server running on /tmp/tmux-1000/default"]);

        Assert.DoesNotContain("no server running", error.Message, StringComparison.Ordinal);
        Assert.True(TmuxAvailability.IsServerAbsent(error));
    }

    [Fact]
    public void A_non_command_exception_does_not_read_as_absent()
    {
        var error = new TmuxCommandNotFoundException("tmux was not found.", "/usr/bin/tmux");

        Assert.False(TmuxAvailability.IsServerAbsent(error));
    }

    private static TmuxCommandException CommandFailure(IReadOnlyList<string> standardErrorLines)
    {
        var result = new TmuxCommandResult(
            ["list-panes", "-a"],
            exitCode: 1,
            standardOutput: ReadOnlyMemory<byte>.Empty,
            standardError: ReadOnlyMemory<byte>.Empty,
            standardOutputLines: [],
            standardErrorLines: standardErrorLines);
        return new TmuxCommandException("list-panes failed.", result);
    }
}
