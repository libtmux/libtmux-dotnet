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

    /// <summary>Reads what the pane has printed since a position, and where this read finished.</summary>
    /// <param name="position">Where the last read finished, or null to start from what the pane shows now.</param>
    /// <param name="cancellationToken">Cancels the tmux commands.</param>
    /// <returns>The new lines and the position to pass next time; a read without a position returns none.</returns>
    /// <remarks>
    /// A read captures what is new rather than everything the pane holds, so
    /// it costs about the same however long the pane has been printing, until
    /// history nears <c>history-limit</c>: a read then captures all of it to
    /// find its place. Lines a program rewrote in place, such as a progress
    /// bar or a prompt redraw, are reported again when they change. A pane
    /// whose program has exited reads as it stands.
    /// </remarks>
    /// <exception cref="ArgumentException">The position came from another pane.</exception>
    /// <exception cref="TmuxObjectNotFoundException">tmux no longer has the pane.</exception>
    /// <exception cref="TmuxPaneException">
    /// The pane runs a different program than when the position was taken, its
    /// program has exited, or it changed during every read attempt.
    /// </exception>
    [UnsupportedOSPlatform("windows")]
    public async Task<PaneOutputSince> ReadOutputSinceAsync(
        PaneOutputPosition? position = null,
        CancellationToken cancellationToken = default)
    {
        if (position is not null && position.Cursor.PaneId != _id.ToString())
        {
            throw new ArgumentException(
                $"The position was taken on pane {position.Cursor.PaneId}, not {_id}.",
                nameof(position));
        }

        PaneRead read;
        try
        {
            read = position is null
                ? await PaneReader.ReadVisibleAsync(this, null, PaneReader.Failure, cancellationToken)
                    .ConfigureAwait(false)
                : await PaneReader.ReadSinceAsync(this, position.Cursor, PaneReader.Failure, cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException and not TmuxPaneException)
        {
            Exception explained = await PaneReader.ExplainFailureAsync(this, error, cancellationToken)
                .ConfigureAwait(false);
            if (ReferenceEquals(explained, error))
            {
                throw;
            }

            throw explained;
        }

        return new PaneOutputSince(
            position is null ? [] : read.Lines,
            new PaneOutputPosition(PaneCursor.Build(this, read.State, read.CursorRows)),
            position is not null && read.LinesMissed);
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
            ServerUtilities.EndOptions(arguments);
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
