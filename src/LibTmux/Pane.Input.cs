using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

public sealed partial class Pane
{
    internal List<string> BuildSendKeysArguments(SendKeysRequest request)
    {
        List<string> arguments = ["send-keys", "-t", Target];
        if (request.Reset)
        {
            arguments.Add("-R");
        }

        if (request.ExpandFormats)
        {
            arguments.Add("-F");
        }

        if (request.HexKeys)
        {
            arguments.Add("-H");
        }

        AddClientKeys(arguments, request);
        if (request.Literal)
        {
            arguments.Add("-l");
        }

        AddValue(arguments, "-N", request.Repeat);
        if (request.CopyModeCommand is not null)
        {
            arguments.Add("-X");
            arguments.Add(request.CopyModeCommand);
        }
        else if (request.Text is not null)
        {
            // There is no tmux flag for keeping a line out of shell history;
            // a leading space is the shell convention that does it.
            arguments.Add(request.SuppressHistory ? $" {request.Text}" : request.Text);
        }

        return arguments;
    }

    /// <summary>Sends keys to the pane.</summary>
    /// <param name="request">What to send.</param>
    /// <param name="cancellationToken">Cancels the tmux commands.</param>
    /// <exception cref="ArgumentException">The request sends nothing.</exception>
    /// <exception cref="LibTmuxException">
    /// Text was sent, but a requested Enter failed. Its dispatch state is
    /// unknown, so the whole request must not be retried.
    /// </exception>
    [UnsupportedOSPlatform("windows")]
    public async Task SendKeysAsync(
        SendKeysRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Text is null
            && request.CopyModeCommand is null
            && !request.Reset
            && request.Repeat is null)
        {
            throw new ArgumentException("The request sends no keys.", nameof(request));
        }

        List<string> arguments = BuildSendKeysArguments(request);
        var sequence = new TmuxMutationSequence(
            "The text was sent, but Enter failed. The pane may already have "
            + "acted on the text; do not retry the whole request.");
        await sequence.MutateAsync(() => RunAsync(arguments, cancellationToken))
            .ConfigureAwait(false);

        // Enter rides in its own command: appended to a literal send it would
        // type the five characters of its name instead of pressing the key.
        if (request.CopyModeCommand is null && request.Text is not null && request.Enter)
        {
            await sequence.MutateAsync(
                    () => RunAsync(["send-keys", "-t", Target, "Enter"], cancellationToken))
                .ConfigureAwait(false);
        }
    }

    /// <summary>Types text into the pane.</summary>
    /// <param name="text">The text to type.</param>
    /// <param name="enter">Whether Enter follows the text.</param>
    /// <param name="cancellationToken">Cancels the tmux commands.</param>
    /// <exception cref="LibTmuxException">
    /// Text was sent, but Enter failed. Its dispatch state is unknown, so the
    /// whole request must not be retried.
    /// </exception>
    [UnsupportedOSPlatform("windows")]
    public Task SendTextAsync(
        string text,
        bool enter = true,
        CancellationToken cancellationToken = default) =>
        SendKeysAsync(new SendKeysRequest(text, enter, literal: true), cancellationToken);

    /// <summary>Sends the configured prefix key to the pane.</summary>
    /// <param name="secondary">Whether the secondary prefix is sent.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task SendPrefixAsync(
        bool secondary = false,
        CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["send-prefix", "-t", Target];
        if (secondary)
        {
            arguments.Add("-2");
        }

        return RunAsync(arguments, cancellationToken);
    }

    /// <summary>Presses Enter in the pane.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>A replacement handle carrying the state afterwards.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<Pane> EnterAsync(CancellationToken cancellationToken = default)
    {
        return await TmuxMutationSequence.RunAsync(
                () => RunAsync(["send-keys", "-t", Target, "Enter"], cancellationToken),
                () => RefreshAsync(cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Clears the pane by running the shell's reset.</summary>
    /// <param name="cancellationToken">Cancels the tmux commands.</param>
    /// <returns>A replacement handle carrying the state afterwards.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<Pane> ClearAsync(CancellationToken cancellationToken = default)
    {
        return await TmuxMutationSequence.RunAsync(
                () => SendKeysAsync(new SendKeysRequest("reset"), cancellationToken),
                () => RefreshAsync(cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Resets the pane's terminal state and drops its history.</summary>
    /// <param name="cancellationToken">Cancels the tmux commands.</param>
    /// <returns>A replacement handle carrying the state afterwards.</returns>
    /// <remarks>
    /// Python groups the two tmux commands so nothing runs between them. This
    /// dispatches them in turn, because the transport carries one command per
    /// call and a trailing separator would reach tmux as data.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public async Task<Pane> ResetAsync(CancellationToken cancellationToken = default)
    {
        var sequence = new TmuxMutationSequence();
        await sequence.MutateAsync(
                () => RunAsync(["send-keys", "-t", Target, "-R"], cancellationToken))
            .ConfigureAwait(false);
        await sequence.MutateAsync(
                () => RunAsync(["clear-history", "-t", Target], cancellationToken))
            .ConfigureAwait(false);
        return await sequence.ObserveAsync(() => RefreshAsync(cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Drops the pane's scrollback history.</summary>
    /// <param name="resetHyperlinks">Whether stored hyperlinks are dropped too.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task ClearHistoryAsync(
        bool resetHyperlinks = false,
        CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["clear-history", "-t", Target];
        if (resetHyperlinks && Requires(ClearHistoryHyperlinksCapability, LogHyperlinksUnsupported))
        {
            arguments.Add("-H");
        }

        return RunAsync(arguments, cancellationToken);
    }


    /// <summary>Builds the arguments a paste request sends.</summary>
    /// <remarks>
    /// Pasting raw bytes arrived in tmux 3.7, so this stays on the pane that
    /// knows which tmux is answering.
    /// </remarks>
    internal List<string> BuildPasteBufferArguments(PasteBufferRequest request)
    {
        List<string> arguments = ["paste-buffer", "-t", Target];
        if (request.DeleteAfter)
        {
            arguments.Add("-d");
        }

        if (request.UseLineFeedSeparator)
        {
            arguments.Add("-r");
        }

        if (request.Bracketed)
        {
            arguments.Add("-p");
        }

        AddValue(arguments, "-b", request.Name);
        AddValue(arguments, "-s", request.Separator);
        if (request.RawBytes && Requires(PasteRawBytesCapability, LogRawPasteUnsupported))
        {
            arguments.Add("-S");
        }

        return arguments;
    }

    /// <summary>Pastes a tmux buffer into the pane.</summary>
    /// <param name="request">Which buffer and how.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task PasteBufferAsync(
        PasteBufferRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        PasteBufferRequest options = request ?? new PasteBufferRequest();
        List<string> arguments = BuildPasteBufferArguments(options);
        return RunAsync(arguments, cancellationToken);
    }

    private void AddClientKeys(List<string> arguments, SendKeysRequest request)
    {
        if (!request.KeyName && request.TargetClient is null)
        {
            return;
        }

        if (!Requires(SendKeysClientCapability, LogClientKeysUnsupported))
        {
            return;
        }

        if (request.KeyName)
        {
            arguments.Add("-K");
        }

        AddValue(arguments, "-c", request.TargetClient);
    }
}
