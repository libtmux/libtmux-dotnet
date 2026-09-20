using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;

namespace LibTmux.Internal;

internal sealed partial class TmuxGenerationGuard
{
    internal static readonly TimeSpan AttachmentAcknowledgementBudget = TimeSpan.FromSeconds(5);

    [UnsupportedOSPlatform("windows")]
    internal async Task<TmuxCommandResult> ExecuteAttachmentAsync(
        ServerGeneration expected,
        IReadOnlyList<string> arguments,
        TimeSpan acknowledgementBudget,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(acknowledgementBudget.Ticks);
        string marker = markerFactory();
        ArgumentException.ThrowIfNullOrWhiteSpace(marker);
        string channel = $"libtmux_attach_{Guid.NewGuid():N}";
        var receiptServer = new Server(new TmuxCommandDispatcher(
            (command, token) => ExecuteAsync(expected, [command], token)));
        using var clientLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var receiptLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        TmuxWaitChannel? wait = null;
        Task<bool>? receipt = null;
        Task<TmuxCommandResult>? client = null;
        TmuxCommandResult? result = null;
        Exception? failure = null;
        List<Exception> cleanupFailures = [];
        try
        {
            wait = receiptServer.OpenWaitChannel(channel);
            receipt = wait.WaitAsync(acknowledgementBudget, receiptLifetime.Token);
            if (receipt.IsFaulted || receipt.IsCanceled)
            {
                await receipt.ConfigureAwait(false);
            }

            TmuxCommandRequest request = CreateRequest(expected,
                [arguments, ["wait-for", "-S", channel]], marker);
            client = ExecuteRequestAsync(request, arguments, clientLifetime.Token);
            Task first = await Task.WhenAny(client, receipt).ConfigureAwait(false);
            if (first == client && (await client.ConfigureAwait(false)).ExitCode != 0)
            {
                result = InterpretResult(expected, arguments, marker, await client.ConfigureAwait(false));
            }
            else
            {
                if (!await receipt.ConfigureAwait(false))
                {
                    throw new TmuxProtocolException(
                        "tmux did not acknowledge attachment within its handshake deadline.",
                        TmuxDispatchState.Unknown);
                }

                cancellationToken.ThrowIfCancellationRequested();
                TmuxCommandResult completed = await client.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                // Attachment discards the guard's stdout prefix. Only its own
                // positive receipt, observed before withdrawal, replaces it.
                result = completed.ExitCode == 0 && completed.StandardOutput.IsEmpty
                    ? TmuxCommandResultProjection.Remap(completed, arguments, completed.StandardOutput)
                    : InterpretResult(expected, arguments, marker, completed);
            }
        }
        catch (Exception error)
        {
            failure = error;
        }

        // Failure is latched before cancellation or withdrawal can signal the
        // channel. Neither cleanup operation can manufacture an acknowledgement.
        try
        {
            await clientLifetime.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            cleanupFailures.Add(error);
        }

        if (client is not null)
        {
            try
            {
                await client.ConfigureAwait(false);
            }
            catch (TmuxOperationCanceledException cancelled)
                when (failure is OperationCanceledException and not TmuxOperationCanceledException)
            {
                failure = new TmuxOperationCanceledException(failure.Message,
                    cancelled.CancellationToken, cancelled.CommandMayHaveExecuted, cancelled.ClientProcessId,
                    new AggregateException(failure, cancelled));
            }
            catch (OperationCanceledException) when (clientLifetime.IsCancellationRequested)
            {
            }
            catch (Exception error) when (ReferenceEquals(error, failure))
            {
            }
            catch (Exception error)
            {
                cleanupFailures.Add(error);
            }
        }

        try
        {
            await receiptLifetime.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            cleanupFailures.Add(error);
        }

        if (receipt is not null)
        {
            try
            {
                await receipt.ConfigureAwait(false);
            }
            catch (Exception) when (failure is not null || result is not null)
            {
                // The owned wait, including its failure, is settled by CloseAsync.
            }
        }

        if (wait is not null)
        {
            try
            {
                await wait.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                cleanupFailures.Add(error);
            }
        }

        if (cleanupFailures.Count > 0)
        {
            if (failure is not null)
            {
                cleanupFailures.Insert(0, failure);
            }
            var causes = new AggregateException(cleanupFailures);
            const string Message = "Attachment cleanup failed; the owned client or receipt registration may remain.";
            if (failure is TmuxOperationCanceledException cancellation)
            {
                throw new TmuxOperationCanceledException(Message, cancellation.CancellationToken,
                    cancellation.CommandMayHaveExecuted, cancellation.ClientProcessId, causes);
            }
            if (failure is OperationCanceledException cancelled)
            {
                throw new OperationCanceledException(Message, causes, cancelled.CancellationToken);
            }
            throw new TmuxTransportException(Message, arguments, TmuxDispatchState.Unknown, causes);
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        return result!;
    }
}
