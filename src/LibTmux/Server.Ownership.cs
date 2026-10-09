using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

public sealed partial class Server
{
    /// <summary>Accepts responsibility for destroying the daemon observed at this endpoint.</summary>
    /// <param name="cancellationToken">Cancels inspection before ownership transfers.</param>
    /// <returns>An owner bound to the inspected daemon generation.</returns>
    /// <exception cref="TmuxObjectNotFoundException">No daemon is listening.</exception>
    [UnsupportedOSPlatform("windows")]
    public async Task<OwnedServerScope> AdoptAsync(CancellationToken cancellationToken = default)
    {
        Server value = await TmuxOwnershipIdentity.CaptureAsync(this, cancellationToken).ConfigureAwait(false);
        _ = await value.InspectAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new TmuxObjectNotFoundException("No daemon is listening at the endpoint.", Connection!.SocketPath);
        return new OwnedServerScope(value);
    }

    [UnsupportedOSPlatform("windows")]
    private async Task<FoundOrCreated<Server>> FindOrCreateServerCoreAsync(CancellationToken cancellationToken)
    {
        if (await InspectAsync(cancellationToken).ConfigureAwait(false) is { } existing)
        {
            return new FoundOrCreated<Server>(existing, null);
        }
        if (Generation is not null)
        {
            throw new InvalidOperationException("A generation-bound handle cannot start a replacement daemon.");
        }

        string marker = "LIBTMUX_START_" + Guid.NewGuid().ToString("N");
        string nonce = Guid.NewGuid().ToString("N");
        TmuxConnection connection = Connection!.WithStartupMarker(marker, nonce);
        var starting = new Server(connection, null, null);
        cancellationToken.ThrowIfCancellationRequested();
        using CancellationTokenSource acquisition = new(OwnedCleanup.Timeout);
        var sequence = new TmuxMutationSequence();
        TmuxCommandResult result = await sequence.MutateAsync(
            () => OwnedCleanup.DispatchCreationAsync(() => starting.DispatchCreationAsync([.. BuildNewSessionArguments(new NewSessionRequest
            {
                Name = "libtmux-start-" + Guid.NewGuid().ToString("N"),
            }, TmuxCreationReceipt.Format)], acquisition.Token), "new-session", acquisition.Token),
            static value =>
            {
                if (!TmuxCreationReceipt.TryParse(value, out _))
                {
                    TmuxCommandFailure.ThrowIfFailed(value, "new-session");
                }
            }).ConfigureAwait(false);
        TmuxCreationReceipt receipt = sequence.Observe(() => TmuxCreationReceipt.Parse(result));
        connection = connection.WithOwnershipToken(receipt.OwnershipToken);
        var materialized = new Server(connection, receipt.Generation, connection.VerifiedRawVersion);
        OwnedServerScope? owner = null;
        TmuxCommandException? creationFailure = null;
        try
        {
            TmuxCommandFailure.ThrowIfFailed(result, "new-session");
        }
        catch (TmuxCommandException error)
        {
            creationFailure = error;
        }
        try
        {
            TmuxCommandDispatcher dispatcher = connection.CreateEntityDispatcher(receipt.Generation);
            TmuxCommandResult startup = await dispatcher.ExecuteAsync(["show-environment", "-g", marker], acquisition.Token)
                .ConfigureAwait(false);
            if (startup.ExitCode != 0 && !startup.StandardErrorLines.Any(line => line == "unknown variable: " + marker))
            {
                TmuxCommandFailure.ThrowIfFailed(startup, "startup ownership verification");
            }
            if (startup.StandardOutputLines is [string entry] && entry == marker + "=" + nonce)
            {
                owner = new OwnedServerScope(materialized);
            }
            if (creationFailure is not null)
            {
                ExceptionDispatchInfo.Capture(creationFailure).Throw();
            }
            if (owner is null)
            {
                await OwnedCleanup.DestroyAsync(materialized, receipt.Generation, "session", receipt.SessionId.ToString(), acquisition.Token)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return new FoundOrCreated<Server>(materialized, null);
            }

            cancellationToken.ThrowIfCancellationRequested();
            TmuxCommandResult configured = await dispatcher.ExecuteAsync(["set-option", "-s", "exit-empty", "off"], cancellationToken)
                .ConfigureAwait(false);
            TmuxCommandFailure.ThrowIfFailed(configured, "set-option");
            await OwnedCleanup.DestroyAsync(materialized, receipt.Generation, "session", receipt.SessionId.ToString(), cancellationToken)
                .ConfigureAwait(false);
            if (ConnectionOptions.InitializeAsync is { } initialize)
            {
                await initialize(materialized, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new FoundOrCreated<Server>(materialized, owner);
        }
        catch (Exception error)
        {
            Exception failure = error;
            if (creationFailure is not null)
            {
                failure = new LibTmuxException(TmuxMutationSequence.PartialFailureMessage, TmuxDispatchState.Unknown, creationFailure);
                if (!ReferenceEquals(error, creationFailure))
                {
                    await OwnedScope.PreserveCleanupAsync(failure, () => Task.FromException(error)).ConfigureAwait(false);
                }
            }
            if (owner is not null)
            {
                await OwnedScope.PreserveCleanupAsync(failure, () => owner.DisposeAsync().AsTask()).ConfigureAwait(false);
            }
            else
            {
                await OwnedCleanup.RollbackAsync(failure, materialized, receipt.Generation, "session", receipt.SessionId.ToString())
                    .ConfigureAwait(false);
            }
            if (!ReferenceEquals(failure, error))
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
            throw;
        }
    }
}
