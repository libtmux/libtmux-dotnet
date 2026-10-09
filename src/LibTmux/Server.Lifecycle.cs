using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

// Liveness may answer false; teardown failures propagate because command
// delivery is part of the answer.
public sealed partial class Server
{
    /// <summary>Starts the tmux server without creating a session.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <remarks>
    /// No handle comes back because there may be nothing to describe: a tmux
    /// server holding no sessions exits as soon as it starts. Materialize with
    /// <see cref="ConnectAsync(CancellationToken)" /> once a session exists.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public async Task StartServerAsync(CancellationToken cancellationToken = default)
    {
        TmuxCommandResult result = await Dispatch(["start-server"], cancellationToken)
            .ConfigureAwait(false);
        TmuxCommandFailure.ThrowIfFailed(result, "start-server");
    }

    /// <summary>Reports whether a tmux server is answering.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>True when the server responded.</returns>
    /// <remarks>A probe may answer no; it never reports a failure.</remarks>
    [UnsupportedOSPlatform("windows")]
    public async Task<bool> IsAliveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            TmuxCommandResult result = await Dispatch(["list-sessions"], cancellationToken)
                .ConfigureAwait(false);
            return result.ExitCode == 0;
        }
        catch (LibTmuxException)
        {
            return false;
        }
    }

    /// <summary>Throws unless a tmux server is answering.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public async Task ThrowIfDeadAsync(CancellationToken cancellationToken = default)
    {
        TmuxCommandResult result = await Dispatch(["list-sessions"], cancellationToken)
            .ConfigureAwait(false);
        TmuxCommandFailure.ThrowIfFailed(result, "list-sessions");
    }

    /// <summary>Stops the tmux server.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <remarks>
    /// A server that is already gone is the requested outcome, so an absent
    /// daemon succeeds rather than failing.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public async Task KillAsync(CancellationToken cancellationToken = default)
    {
        TmuxCommandResult result = await Dispatch(["kill-server"], cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0 && !TmuxCommandFailure.NamesMissingServer(result) && !NamesDyingServer(result))
        {
            TmuxCommandFailure.ThrowIfFailed(result, "kill-server");
        }
    }

    /// <summary>Stops one session.</summary>
    /// <param name="target">The session to stop.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public async Task KillSessionAsync(
        string target,
        CancellationToken cancellationToken = default)
    {
        SessionName.Validate(target);
        // tmux treats -t as a prefix match, so an unanchored target could
        // resolve to, and kill, a different session that merely starts with
        // the requested name.
        TmuxCommandResult result = await Dispatch(
                ["kill-session", "-t", $"={target}"],
                cancellationToken)
            .ConfigureAwait(false);
        TmuxCommandFailure.ThrowIfFailed(result, "kill-session");
    }

    /// <summary>Reports whether a session exists.</summary>
    /// <param name="target">The session to look for.</param>
    /// <param name="exact">Whether the name must match in full.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>True when tmux reports the session.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<bool> HasSessionAsync(
        string target,
        bool exact = true,
        CancellationToken cancellationToken = default)
    {
        // The anchored form does not stop target splitting: tmux answers yes to
        // "=alpha:0" whenever session alpha has a window 0, which would make
        // this a window question about a name CreateSessionAsync would refuse.
        SessionName.Validate(target);
        // tmux treats -t as a prefix match, so an exact question needs the
        // anchored form or it would answer about a different session.
        TmuxCommandResult result = await Dispatch(
                ["has-session", "-t", exact ? $"={target}" : target],
                cancellationToken)
            .ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    /// <summary>Creates a session.</summary>
    /// <param name="request">The session to create.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The created session.</returns>
    /// <exception cref="TmuxSessionExistsException">The name is already taken.</exception>
    /// <exception cref="ArgumentException">The name is invalid, or generation-bound creation requests replacement.</exception>
    /// <remarks>
    /// Set <see cref="NewSessionRequest.ExpectedGeneration" /> to require one daemon before
    /// creation. Readback always uses the generation reported by creation itself. A daemon
    /// change after creation reports a partial failure rather than returning its replacement.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public async Task<Session> CreateSessionAsync(
        NewSessionRequest? request = null,
        CancellationToken cancellationToken = default) =>
        (await CreateSessionWithReceiptAsync(request, cancellationToken).ConfigureAwait(false)).Session;

    /// <summary>Creates a session and returns its initial window and pane identities.</summary>
    /// <param name="request">The session to create.</param>
    /// <param name="cancellationToken">Cancels creation and readback.</param>
    /// <returns>The created session and child identifiers from the same creation reply.</returns>
    /// <exception cref="ArgumentException">The name is invalid, or generation-bound creation requests replacement.</exception>
    /// <exception cref="TmuxSessionExistsException">The name is already taken.</exception>
    /// <exception cref="LibTmuxException">Creation or readback failed; a failure after creation has unknown dispatch outcome.</exception>
    /// <remarks>
    /// The returned session is bound to the creating daemon. Its child identifiers are
    /// creation-time facts, not a snapshot or a promise that the children still belong
    /// to the session. Failed readback rolls back its known session ID against the creating daemon.
    /// An initial command without a valid receipt has an unknown outcome; inspect the endpoint before retrying.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public async Task<SessionCreationResult> CreateSessionWithReceiptAsync(
        NewSessionRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        NewSessionRequest options = request ?? new NewSessionRequest();
        options.ValidateGenerationBinding();
        var sequence = new TmuxMutationSequence();
        if (options.Name is not null)
        {
            SessionName.Validate(options.Name);

            // tmux -A attaches and needs a terminal; replacement must kill first.
            if (options.ReplaceExisting
                && await HasSessionAsync(options.Name, true, cancellationToken)
                    .ConfigureAwait(false))
            {
                await sequence
                    .MutateAsync(() => KillSessionAsync(options.Name, cancellationToken))
                    .ConfigureAwait(false);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        using CancellationTokenSource acquisition = new(OwnedCleanup.Timeout);
        TmuxCommandResult result = await sequence.MutateAsync(
                () => OwnedCleanup.DispatchCreationAsync(() => DispatchCreationAsync([.. BuildNewSessionArguments(options, TmuxCreationReceipt.Format)], acquisition.Token, options.ExpectedGeneration ?? (Connection!.OwnershipToken is null ? null : Generation)), "new-session", acquisition.Token),
                value =>
                {
                    if (value.ExitCode != 0
                        && options.Name is not null
                        && value.StandardErrorLines.Any(static line =>
                            line.Contains("duplicate session", StringComparison.Ordinal)))
                    {
                        throw new TmuxSessionExistsException(
                            string.Join('\n', value.StandardErrorLines),
                            options.Name);
                    }

                    TmuxCommandFailure.ThrowIfFailed(value, "new-session");
                })
            .ConfigureAwait(false);
        TmuxCreationReceipt receipt = sequence.Observe(() => TmuxCreationReceipt.Parse(result));
        var accepted = new Server(Connection!.WithOwnershipToken(receipt.OwnershipToken), receipt.Generation, Connection.VerifiedRawVersion);
        return await OwnedCleanup.CompleteAcquisitionAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (options.ExpectedGeneration is ServerGeneration expected && receipt.Generation != expected)
            {
                throw new StaleServerGenerationException("The creating daemon generation changed.", expected, receipt.Generation);
            }

            Server materialized = await accepted.InspectAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new TmuxObjectNotFoundException("The creating daemon disappeared before readback.", Connection.SocketPath);
            if (Generation != receipt.Generation && ConnectionOptions.InitializeAsync is { } initialize)
            {
                await initialize(materialized, cancellationToken).ConfigureAwait(false);
            }
            IReadOnlyDictionary<string, string?>? row = await RelationReader.FindAsync(
                materialized, "list-sessions", "session_id", receipt.SessionId.ToString(),
                inSession: null, cancellationToken).ConfigureAwait(false);
            Session session = row is null
                ? throw new TmuxObjectNotFoundException(
                    $"tmux did not report the created session '{receipt.SessionId}'.", receipt.SessionId.ToString())
                : RelationReader.ToSession(materialized, row);
            cancellationToken.ThrowIfCancellationRequested();
            return new SessionCreationResult(session, receipt.WindowId, receipt.WindowIndex, receipt.PaneId);
        }, accepted, receipt.Generation, "session", receipt.SessionId.ToString()).ConfigureAwait(false);
    }

    /// <summary>Starts a daemon at a captured endpoint and accepts destruction responsibility.</summary>
    /// <param name="options">The endpoint and normal tmux startup configuration.</param>
    /// <param name="cancellationToken">Cancels acquisition before the owner is returned.</param>
    /// <returns>An owner bound to the daemon proven to have started in this call.</returns>
    /// <remarks>
    /// A temporary session keeps startup alive until its generation and startup marker have been read.
    /// Owned daemons keep running without sessions until disposal. Give whole-server owners disposable endpoints.
    /// Concurrent library calls using the same socket path serialize within this process.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A daemon already owns the endpoint or another starter won the race.</exception>
    [UnsupportedOSPlatform("windows")]
    public static async Task<OwnedServerScope> CreateOwnedAsync(
        ServerConnectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        FoundOrCreated<Server> result = await Open(options).FindOrCreateAsync(cancellationToken).ConfigureAwait(false);
        return result.Owner as OwnedServerScope
            ?? throw new InvalidOperationException("A daemon already owns the endpoint. Inspect it and call AdoptAsync to accept its destruction responsibility.");
    }

    /// <summary>Creates a session and takes ownership of it.</summary>
    /// <param name="request">The session to create.</param>
    /// <param name="cancellationToken">Cancels the tmux commands.</param>
    /// <returns>A scope that stops the session when disposed.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<OwnedSessionScope> CreateOwnedSessionAsync(
        NewSessionRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        var sequence = new TmuxMutationSequence();
        if (request is { ReplaceExisting: true })
        {
            throw new ArgumentException("Owned creation cannot replace an existing session.", nameof(request));
        }

        Session created = await sequence
            .MutateAsync(() => CreateSessionAsync(request, cancellationToken))
            .ConfigureAwait(false);
        return sequence.Observe(() => new OwnedSessionScope(created));
    }

    /// <summary>Attaches a client to a session on this server.</summary>
    /// <param name="request">Attachment options naming the session.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public async Task AttachSessionAsync(
        AttachSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Target is null)
        {
            // A server-level attach has no session to fall back to, unlike the
            // session-level overload which attaches itself.
            throw new ArgumentException(
                "A server-level attach must name a target session.",
                nameof(request));
        }

        TmuxCommandResult result = await Dispatch(
                [.. Session.BuildAttachArguments(request, request.Target)],
                cancellationToken)
            .ConfigureAwait(false);
        TmuxCommandFailure.ThrowIfFailed(result, "attach-session");
    }

    internal static IEnumerable<string> BuildNewSessionArguments(
        NewSessionRequest options,
        string format = "#{session_id}")
    {
        options.ValidateGenerationBinding();
        if (options.Name is not null)
        {
            SessionName.Validate(options.Name);
        }

        yield return "new-session";
        yield return "-P";
        yield return "-F";
        yield return format;
        if (!options.Attach)
        {
            yield return "-d";
        }

        if (options.DetachOthers)
        {
            yield return "-D";
        }

        if (options.NoSize)
        {
            yield return "-X";
        }

        foreach ((string flag, string? value) in new[]
        {
            ("-s", options.Name),
            ("-c", StartDirectory.Resolve(options.StartDirectory)),
            ("-n", options.WindowName),
            ("-x", options.Width),
            ("-y", options.Height),
            ("-f", options.ClientFlags),
        })
        {
            if (value is not null)
            {
                yield return flag;
                yield return value;
            }
        }

        if (options.Environment is not null)
        {
            foreach ((string key, string value) in options.Environment)
            {
                yield return "-e";
                yield return $"{key}={value}";
            }
        }

        if (options.Command is not null)
        {
            yield return "--";
            yield return options.Command;
        }
    }

    // A dying server is already stopping, which is the requested outcome.
    private static bool NamesDyingServer(TmuxCommandResult result) =>
        result.StandardErrorLines.Any(static line =>
            line.Contains("server exited unexpectedly", StringComparison.Ordinal));

    [UnsupportedOSPlatform("windows")]
    private Task<TmuxCommandResult> DispatchCreationAsync(
        IReadOnlyList<string> arguments, CancellationToken cancellationToken, ServerGeneration? expectedGeneration = null)
    {
        TmuxConnection connection = Connection ?? throw new InvalidOperationException("The server handle has no connection.");
        TmuxCommandDispatcher dispatcher = expectedGeneration is { } expected
            ? connection.CreateEntityDispatcher(expected) : connection.ServerDispatcher;
        return dispatcher.ExecuteGroupAsync([.. TmuxOwnershipIdentity.InitializeCommands(), arguments], cancellationToken);
    }

    [UnsupportedOSPlatform("windows")]
    private Task<TmuxCommandResult> Dispatch(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        ServerGeneration? expectedGeneration = null) =>
        Connection is null
            ? throw new InvalidOperationException("The server handle has no connection.")
            : (expectedGeneration is ServerGeneration expected
                ? Connection.CreateEntityDispatcher(expected)
                : Connection.ServerDispatcher).ExecuteAsync(arguments, cancellationToken);
}

/// <summary>Owns a server and stops it when disposed.</summary>
/// <remarks>
/// Ownership is explicit: a handle obtained any other way never tears its
/// server down, so a caller cannot accidentally kill a server it merely
/// connected to.
/// </remarks>
public sealed class OwnedServerScope : IOwnedTmuxResource<Server>
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);
    private readonly OwnedCleanup _cleanup;
    private readonly (int Id, DateTime Started)? _process;

    [UnsupportedOSPlatform("windows")]
    internal OwnedServerScope(Server value)
    {
        ServerGeneration generation = value.Generation
            ?? throw new InvalidOperationException("Server ownership requires a captured daemon generation.");
        Value = value;
        _process = Identify(generation.ProcessId);
        _cleanup = new OwnedCleanup(StopAsync);
    }

    /// <summary>Gets the generation-bound server owned by this scope.</summary>
    public Server Value { get; }

    /// <summary>Stops the accepted daemon and waits for its captured process to exit.</summary>
    /// <returns>The shared cleanup attempt.</returns>
    /// <remarks>
    /// Cleanup uses an independent five-second deadline. Concurrent calls share an attempt;
    /// failed attempts can be retried and successful repeated disposal is harmless.
    /// A replacement daemon is rejected by a native guard before any kill command.
    /// </remarks>
    /// <exception cref="LibTmuxException">Cleanup failed or the endpoint belongs to a replacement daemon.</exception>
    [UnsupportedOSPlatform("windows")]
    public ValueTask DisposeAsync() => _cleanup.DisposeAsync();

    [UnsupportedOSPlatform("windows")]
    private async Task StopAsync(CancellationToken token)
    {
        await OwnedCleanup.DestroyAsync(Value, Value.Generation!.Value, "server", null, token).ConfigureAwait(false);
        if (_process is { } process)
        {
            await WaitForExitAsync(process, token).ConfigureAwait(false);
        }
    }

    private static (int Id, DateTime Started)? Identify(int? processId)
    {
        if (processId is not int id)
        {
            return null;
        }

        try
        {
            using Process process = Process.GetProcessById(id);
            return (id, process.StartTime);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // Already gone, so there is nothing to wait for.
            return null;
        }
    }

    // kill-server answers once tmux has the command; the server may still be
    // ending its panes, with its socket accepting connections.
    internal static async Task WaitForExitAsync((int Id, DateTime Started) process, CancellationToken cancellationToken)
    {
        Process server;
        try
        {
            server = Process.GetProcessById(process.Id);
            if (server.StartTime != process.Started)
            {
                // Another process took the ID after the server exited.
                server.Dispose();
                return;
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return;
        }

        using (server)
        {
            try
            {
                await server.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested)
            {
                throw new LibTmuxException(
                    "The owned tmux server was told to exit but had not within " + CleanupTimeout.TotalSeconds + " seconds.",
                    error);
            }
        }
    }
}
