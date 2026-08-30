using LibTmux.Internal;

namespace LibTmux.UnitTests.Connection;

/// <summary>Answers the version banner a connection reads before its first command.</summary>
internal static class FakeMultiplexer
{
    internal const string TmuxBanner = "tmux 3.7\n";

    /// <summary>Wraps a fake transport so it need only model the commands under test.</summary>
    /// <remarks>
    /// Every connection reads <c>-V</c> once to learn which multiplexer answered.
    /// Intercepting it here keeps that reading out of a fake's own bookkeeping.
    /// </remarks>
    internal static Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>>
        AnsweringVersion(
            Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>> execute,
            string banner = TmuxBanner) =>
        (request, cancellationToken) =>
            request.LogicalArguments is [string only] && only == "-V"
                ? Task.FromResult(Banner(request.LogicalArguments, banner))
                : execute(request, cancellationToken);

    private static TmuxCommandResult Banner(IReadOnlyList<string> arguments, string banner)
    {
        byte[] output = System.Text.Encoding.UTF8.GetBytes(banner);
        return new TmuxCommandResult(
            arguments,
            0,
            output,
            ReadOnlyMemory<byte>.Empty,
            Utf8BackslashDecoder.ProjectOutputLines(output),
            []);
    }
}
