using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux.UnitTests.Connection;

/// <summary>What an interceptor sees, and what it can do in tmux's place.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class DispatchInterceptionTests
{
    [ConnectionUnixFact]
    public async Task An_interceptor_sees_the_version_probe_and_every_command()
    {
        List<IReadOnlyList<string>> seen = [];
        int sent = 0;
        TmuxConnection connection = Connect(
            (invocation, next, token) =>
            {
                seen.Add(invocation.Arguments);
                return next(token);
            },
            () => sent++);

        await connection.DiscoverAsync(TestContext.Current.CancellationToken);
        await connection.ServerDispatcher.ExecuteAsync(
            ["list-sessions", "-F", "#{session_id}"],
            TestContext.Current.CancellationToken);

        Assert.Contains(seen, arguments => arguments is ["-V"]);
        Assert.Contains(seen, arguments => arguments is ["list-sessions", "-F", "#{session_id}"]);
        // The fake answers the version probe itself, so only the rest reach it.
        Assert.Equal(seen.Count(arguments => arguments is not ["-V"]), sent);
    }

    [ConnectionUnixFact]
    public async Task An_interceptor_can_answer_in_place_of_tmux()
    {
        int sent = 0;
        TmuxConnection connection = Connect(
            (invocation, next, token) => invocation.Arguments is ["list-sessions"]
                ? Task.FromResult(Answer(invocation.Arguments, "$7\n"))
                : next(token),
            () => sent++);

        TmuxCommandResult result = await connection.ServerDispatcher.ExecuteAsync(
            ["list-sessions"],
            TestContext.Current.CancellationToken);

        Assert.Equal(["$7"], result.StandardOutputLines);
        Assert.Equal(0, sent);
    }

    [ConnectionUnixFact]
    public async Task An_interceptor_can_run_a_command_again()
    {
        int sent = 0;
        TmuxConnection connection = Connect(
            async (_, next, token) =>
            {
                await next(token);
                return await next(token);
            },
            () => sent++);

        await connection.ServerDispatcher.ExecuteAsync(
            ["kill-session"],
            TestContext.Current.CancellationToken);

        Assert.Equal(2, sent);
    }

    private static TmuxConnection Connect(TmuxInterceptor interceptor, Action onSend) =>
        new(
            new ServerConnectionOptions { Interceptor = interceptor },
            FakeMultiplexer.AnsweringVersion((request, _) =>
            {
                onSend();
                return Task.FromResult(Answer(request.LogicalArguments, "3:4\n"));
            }));

    private static TmuxCommandResult Answer(IReadOnlyList<string> arguments, string output)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(output);
        return new TmuxCommandResult(
            arguments,
            0,
            bytes,
            ReadOnlyMemory<byte>.Empty,
            Utf8BackslashDecoder.ProjectOutputLines(bytes),
            []);
    }
}
