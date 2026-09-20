using System.Globalization;
using System.Text;

namespace LibTmux.Internal;

/// <summary>Executes tmux commands only while a materialized server generation is live.</summary>
internal sealed class TmuxGenerationGuard(
    Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>> execute,
    Func<string> markerFactory)
{
    internal async Task<TmuxCommandResult> ExecuteAsync(
        ServerGeneration expected,
        IReadOnlyList<IReadOnlyList<string>> commands,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> logicalArguments = [.. commands.SelectMany(static command => command)];
        string marker = markerFactory();
        ArgumentException.ThrowIfNullOrWhiteSpace(marker);
        string generationText = GenerationText(expected);
        TmuxCommandRequest request = CreateRequest(expected, commands, marker);

        TmuxCommandResult grouped;
        try
        {
            // tmux scans the entire command list for server-starting commands
            // before the guard can run. A generation-bound request must disable
            // client-side startup, including loading the server configuration.
            grouped = await execute(request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TmuxTransportException error)
        {
            throw new TmuxTransportException(
                error.Message,
                logicalArguments,
                error.Dispatch,
                error.InnerException);
        }

        if (!TryStripGenerationPrefix(
                grouped.StandardOutput.Span,
                out ServerGeneration actual,
                out byte[] remainingOutput))
        {
            bool exactMarkerFailure = grouped.ExitCode == 1
                && IsExactMarkerFailure(grouped.StandardError.Span, marker);
            if (grouped.ExitCode != 0 && !exactMarkerFailure)
            {
                return TmuxCommandResultProjection.Remap(
                    grouped,
                    logicalArguments,
                    grouped.StandardOutput);
            }

            throw new TmuxCommandException(
                "tmux did not return a valid leading generation line.",
                grouped);
        }

        if (grouped.ExitCode == 1 && IsExactMarkerFailure(grouped.StandardError.Span, marker))
        {
            throw new StaleServerGenerationException(
                $"The tmux server generation changed from {generationText} to "
                + $"{actual.ProcessId.ToString(CultureInfo.InvariantCulture)}:"
                + $"{actual.StartTime.ToString(CultureInfo.InvariantCulture)}.",
                expected,
                actual);
        }

        return TmuxCommandResultProjection.Remap(grouped, logicalArguments, remainingOutput);
    }

    // The planner budgets the same encoded request used by dispatch. The
    // native connection owns its fixed-length marker; custom markers are an
    // internal test seam.
    internal const int MarkerLength = 46;

    internal static TmuxCommandRequest CreateRequest(
        ServerGeneration expected,
        IReadOnlyList<IReadOnlyList<string>> commands,
        string marker) =>
        TmuxCommandRequest.Group(preventServerStart: true, [
            ["display-message", "-p", TmuxConnection.GenerationFormat],
            ["if-shell", "-F", $"#{{==:{TmuxConnection.GenerationFormat},{GenerationText(expected)}}}", string.Empty, marker],
            .. commands]);

    private static string GenerationText(ServerGeneration expected) =>
        $"{expected.ProcessId.ToString(CultureInfo.InvariantCulture)}:"
        + expected.StartTime.ToString(CultureInfo.InvariantCulture);

    private static bool TryStripGenerationPrefix(
        ReadOnlySpan<byte> standardOutput,
        out ServerGeneration generation,
        out byte[] remainingOutput)
    {
        int lineEnd = standardOutput.IndexOf((byte)'\n');
        if (lineEnd < 0)
        {
            generation = default;
            remainingOutput = [];
            return false;
        }

        ReadOnlySpan<byte> generationBytes = standardOutput[..lineEnd];
        if (!generationBytes.IsEmpty && generationBytes[^1] == '\r')
        {
            generationBytes = generationBytes[..^1];
        }

        try
        {
            generation = TmuxConnection.ParseGeneration(Encoding.UTF8.GetString(generationBytes));
        }
        catch (LibTmuxException)
        {
            generation = default;
            remainingOutput = [];
            return false;
        }

        remainingOutput = standardOutput[(lineEnd + 1)..].ToArray();
        return true;
    }

    private static bool IsExactMarkerFailure(ReadOnlySpan<byte> standardError, string marker)
    {
        byte[] expected = Encoding.UTF8.GetBytes($"unknown command: {marker}\n");
        return standardError.SequenceEqual(expected);
    }
}

internal static class TmuxCommandResultProjection
{
    internal static TmuxCommandResult Remap(
        TmuxCommandResult result,
        IReadOnlyList<string> logicalArguments,
        ReadOnlyMemory<byte> standardOutput) =>
        new(
            logicalArguments,
            result.ExitCode,
            standardOutput,
            result.StandardError,
            Utf8BackslashDecoder.ProjectOutputLines(standardOutput.Span),
            Utf8BackslashDecoder.ProjectErrorLines(result.StandardError.Span));
}
