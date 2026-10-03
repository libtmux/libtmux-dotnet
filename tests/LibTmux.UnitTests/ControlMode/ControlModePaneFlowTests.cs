using System.Threading.Channels;
using LibTmux.Internal;

namespace LibTmux.UnitTests.ControlMode;

public sealed class ControlModePaneFlowTests
{
    private static readonly PaneId Flooding = new(1);

    [Fact]
    public async Task Discarded_output_pauses_its_pane_once_and_a_drained_reader_resumes_it()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var sent = Channel.CreateUnbounded<string>();
        var flow = new ControlModePaneFlow(Recorder(sent, _ => Task.CompletedTask), capacity: 8);

        flow.OutputDiscarded(Flooding);
        flow.OutputDiscarded(Flooding);
        Assert.Equal("refresh-client -A %1:pause", await NextAsync(sent, token));
        flow.OutputDiscarded(Flooding);
        flow.Dequeued(5);
        flow.Dequeued(2);

        Assert.Equal("refresh-client -A %1:continue", await NextAsync(sent, token));
        flow.Stop();
        Assert.False(sent.Reader.TryRead(out string? extra), extra);
    }

    [Fact]
    public async Task A_reader_that_drains_while_a_pause_is_in_flight_still_resumes_the_pane()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var sent = Channel.CreateUnbounded<string>();
        var pauseSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var flow = new ControlModePaneFlow(
            Recorder(sent, command => command.EndsWith(":pause", StringComparison.Ordinal) ? pauseSent.Task : Task.CompletedTask),
            capacity: 8);

        flow.OutputDiscarded(Flooding);
        Assert.Equal("refresh-client -A %1:pause", await NextAsync(sent, token));
        flow.Dequeued(0);
        pauseSent.SetResult();

        Assert.Equal("refresh-client -A %1:continue", await NextAsync(sent, token));
        flow.Stop();
    }

    [Fact]
    public async Task A_resume_that_fails_is_sent_again()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var sent = Channel.CreateUnbounded<string>();
        int resumes = 0;
        var flow = new ControlModePaneFlow(
            Recorder(sent, command => command.EndsWith(":continue", StringComparison.Ordinal) && ++resumes == 1
                ? Task.FromException(new InvalidOperationException("limit reached"))
                : Task.CompletedTask),
            capacity: 8);

        flow.OutputDiscarded(Flooding);
        Assert.Equal("refresh-client -A %1:pause", await NextAsync(sent, token));
        flow.Dequeued(0);

        Assert.Equal("refresh-client -A %1:continue", await NextAsync(sent, token));
        Assert.Equal("refresh-client -A %1:continue", await NextAsync(sent, token));
        flow.Stop();
    }

    private static async Task<string> NextAsync(Channel<string> sent, CancellationToken token) =>
        await sent.Reader.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), token);

    private static Func<TmuxCommand, Task> Recorder(Channel<string> sent, Func<string, Task> reply) =>
        command =>
        {
            string text = string.Join(' ', command.Arguments.Prepend(command.Name));
            sent.Writer.TryWrite(text);
            return reply(text);
        };
}
