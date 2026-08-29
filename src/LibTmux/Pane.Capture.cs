using System.Globalization;
using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

public sealed partial class Pane
{
    /// <summary>Reads the pane's contents.</summary>
    /// <param name="request">What to capture.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The captured lines.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<IReadOnlyList<string>> CaptureAsync(
        CapturePaneRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        List<string> arguments = BuildCaptureArguments(["-p"], request ?? new CapturePaneRequest());
        TmuxCommandResult result = await _commandDispatcher
            .ExecuteAsync(arguments, cancellationToken)
            .ConfigureAwait(false);
        TmuxCommandFailure.ThrowIfFailed(result, "capture-pane");
        return result.StandardOutputLines;
    }

    /// <summary>Captures the pane's contents into a tmux buffer.</summary>
    /// <param name="bufferName">The buffer to write.</param>
    /// <param name="request">What to capture.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task CaptureToBufferAsync(
        string bufferName,
        CapturePaneRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bufferName);
        // tmux checks for printing before buffering and takes the first it
        // finds, so a buffer name only lands when nothing asks it to print.
        return RunAsync(
            BuildCaptureArguments(["-b", bufferName], request ?? new CapturePaneRequest()),
            cancellationToken);
    }


    /// <summary>Pipes the pane's input or output through a command.</summary>
    /// <param name="request">What to pipe.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task PipeAsync(
        PipePaneRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        PipePaneRequest options = request ?? new PipePaneRequest();
        List<string> arguments = BuildPipePaneArguments(options);
        return RunAsync(arguments, cancellationToken);
    }

    internal List<string> BuildPipePaneArguments(PipePaneRequest request)
    {
        List<string> arguments = ["pipe-pane", "-t", Target];
        if (request.OutputOnly)
        {
            arguments.Add("-O");
        }

        if (request.InputOnly)
        {
            arguments.Add("-I");
        }

        if (request.Toggle)
        {
            arguments.Add("-o");
        }

        if (request.Command is not null)
        {
            arguments.Add(request.Command);
        }

        return arguments;
    }

    internal List<string> BuildCaptureArguments(List<string> head, CapturePaneRequest options)
    {
        List<string> arguments = ["capture-pane", "-t", Target, .. head];
        AddValue(arguments, "-S", Position(options.StartLine));
        AddValue(arguments, "-E", Position(options.EndLine));
        if (options.EscapeSequences)
        {
            arguments.Add("-e");
        }

        if (options.EscapeNonPrintable)
        {
            arguments.Add("-C");
        }

        if (options.JoinWrappedLines)
        {
            arguments.Add("-J");
        }

        if (options.PreserveTrailingSpaces)
        {
            arguments.Add("-N");
        }

        if (options.TrimTrailingSpaces && Requires(CaptureTrimCapability, LogTrimUnsupported))
        {
            arguments.Add("-T");
        }

        if (options.AlternateScreen)
        {
            arguments.Add("-a");
        }

        if (options.Quiet)
        {
            arguments.Add("-q");
        }

        if (options.ModeScreen && Requires(CaptureModeScreenCapability, LogModeScreenUnsupported))
        {
            arguments.Add("-M");
        }

        if (options.Pending)
        {
            arguments.Add("-P");
        }

        AddCaptureMetadata(arguments, options);
        return arguments;

        static string? Position(CapturePanePosition? position) => position is null
            ? null
            : position.Value.LineNumber?.ToString(CultureInfo.InvariantCulture) ?? "-";
    }

    private void AddCaptureMetadata(List<string> arguments, CapturePaneRequest options)
    {
        if (!options.Hyperlinks && !options.LineNumbers && !options.LineFlags)
        {
            return;
        }

        if (!Requires(CaptureMetadataCapability, LogCaptureMetadataUnsupported))
        {
            return;
        }

        if (options.Hyperlinks)
        {
            arguments.Add("-H");
        }

        if (options.LineNumbers)
        {
            arguments.Add("-L");
        }

        if (options.LineFlags)
        {
            arguments.Add("-F");
        }
    }
}
