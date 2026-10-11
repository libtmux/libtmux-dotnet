using System.Runtime.Versioning;
using System.Text;
using LibTmux.Internal;
using Microsoft.Extensions.Logging.Abstractions;

namespace LibTmux.UnitTests.Transport;

[UnsupportedOSPlatform("windows")]
public sealed class TmuxCommandDispatcherTests
{
    [UnixFact]
    public async Task Only_a_has_session_command_reads_its_error_as_output()
    {
        var dispatcher = new TmuxCommandDispatcher(
            static (arguments, _) => Task.FromResult(Result(arguments, exitCode: 1, error: "can't find session: x")));
        CancellationToken token = TestContext.Current.CancellationToken;

        TmuxCommandResult asked = await dispatcher.ExecuteAsync(["has-session", "-t", "=x"], token);
        TmuxCommandResult typed = await dispatcher.ExecuteAsync(["send-keys", "-l", "has-session"], token);

        Assert.Equal(["can't find session: x"], asked.StandardOutputLines);
        Assert.Empty(typed.StandardOutputLines);
    }

    [Fact]
    public async Task A_blocking_command_outlives_the_command_timeout()
    {
        int calls = 0;
        var dispatcher = new TmuxCommandDispatcher(
            async (arguments, cancellationToken) =>
            {
                // The first command never finishes by itself, so only the
                // command timeout can end it; a timer race cannot let it win.
                if (Interlocked.Increment(ref calls) == 1)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                return Result(arguments, exitCode: 0, error: null);
            },
            new TmuxCommandContext(NullLogger.Instance, "dispatch", TimeSpan.FromMilliseconds(10)));
        CancellationToken token = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<TmuxTransportException>(() => dispatcher.ExecuteAsync(["wait-for", "ready"], token));
        TmuxCommandResult waited = await dispatcher.ExecuteBlockingAsync(["wait-for", "ready"], token);

        Assert.Equal(0, waited.ExitCode);
    }

    private static TmuxCommandResult Result(IReadOnlyList<string> arguments, int exitCode, string? error) =>
        new(
            arguments,
            exitCode,
            ReadOnlyMemory<byte>.Empty,
            error is null ? ReadOnlyMemory<byte>.Empty : Encoding.UTF8.GetBytes(error + "\n"),
            [],
            error is null ? [] : [error]);
}
