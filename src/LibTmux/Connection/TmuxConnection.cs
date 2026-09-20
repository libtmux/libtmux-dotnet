using System.Diagnostics;
using System.Globalization;

namespace LibTmux.Internal;

internal sealed class TmuxConnection
{
    internal const string GenerationFormat = "#{pid}:#{start_time}";
    private readonly MultiplexerDialect _dialect;
    private readonly TmuxEndpointIdentity _endpointIdentity;
    private readonly string? _resolvedSocketName;
    private readonly string? _resolvedSocketPath;

    internal TmuxConnection(ServerConnectionOptions options)
        : this(TmuxConnectionEndpoint.Resolve(options), execute: null, markerFactory: null)
    {
    }

    internal TmuxConnection(
        ServerConnectionOptions options,
        Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>> execute,
        Func<string>? markerFactory = null)
        : this(TmuxConnectionEndpoint.Resolve(options), execute, markerFactory)
    {
    }

    private TmuxConnection(
        ResolvedTmuxConnection resolved,
        Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>>? execute,
        Func<string>? markerFactory)
    {
        Options = resolved.Options;
        _resolvedSocketName = resolved.SocketName;
        _resolvedSocketPath = resolved.SocketPath;
        PrefixArguments = resolved.PrefixArguments;
        _endpointIdentity = resolved.EndpointIdentity;

        (
            Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>> send,
            Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>> sendVersion) =
            execute is null
                ? CreateProcessTransports(resolved)
                : (execute, execute);
        if (Options.Interceptor is TmuxInterceptor interceptor)
        {
            // Wrapped below both dialects and the generation guard, so it sees
            // every client the connection starts, as tmux receives it.
            send = Intercept(send, interceptor);
            sendVersion = Intercept(sendVersion, interceptor);
        }

        // The psmux preview is reached only through its own facade, which
        // supplies these options; nothing detects its way into it.
        _dialect = Options.PsmuxPreview is null
            ? new TmuxDialect(
                send,
                sendVersion,
                markerFactory ?? (static () => $"libtmux_stale_{Guid.NewGuid():N}"),
                processBacked: execute is null,
                Options.TmuxBinaryPath)
            : new PsmuxDialect(send, sendVersion, Options, _resolvedSocketName);

        // Built whether or not a logger is set: the socket and the timeout it
        // carries are read by tracing and dispatch, not only by logging.
        CommandContext = new TmuxCommandContext(
            Options.Logger,
            Options.SocketName ?? Options.SocketPath,
            Options.CommandTimeout);
        ServerDispatcher = new TmuxCommandDispatcher(
            ExecuteSingleAsync,
            CommandContext,
            ExecuteGroupAsync);
    }

    internal ServerConnectionOptions Options { get; }

    internal IReadOnlyList<string> PrefixArguments { get; }

    internal TmuxCommandDispatcher ServerDispatcher { get; }

    internal TmuxCommandContext CommandContext { get; }

    internal bool IsPsmux => _dialect.IsPsmux;

    internal string VerifiedRawVersion => _dialect.VerifiedRawVersion;

    internal bool HasSameEndpoint(TmuxConnection other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return _endpointIdentity == other._endpointIdentity;
    }

    internal int GetEndpointHashCode() => _endpointIdentity.GetHashCode();

    internal string GetEndpointFingerprint() => _endpointIdentity.Fingerprint();

    /// <summary>The socket this connection resolved to, not what was asked for.</summary>
    /// <remarks>
    /// A name factory or <c>LIBTMUX_SOCKET_NAME</c> leaves the options empty, so
    /// anything that records or asserts an endpoint has to read it from here.
    /// </remarks>
    internal (string? SocketName, string? SocketPath) ResolvedSocket =>
        (_resolvedSocketName, _resolvedSocketPath);

    /// <summary>Reads the version and generation of the server behind this connection.</summary>
    /// <remarks>
    /// Discovery is two tmux commands rather than one, and it runs before a
    /// dispatcher exists, so the timeout is applied here: a tmux that stops
    /// answering must not hang a caller who set one.
    /// </remarks>
    internal async Task<(ServerGeneration Generation, string RawVersion)> DiscoverAsync(
        CancellationToken cancellationToken)
    {
        using var deadline = new TmuxCommandDispatcher.Deadline(
            Options.CommandTimeout,
            cancellationToken);
        try
        {
            return await _dialect.DiscoverAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error)
            when (deadline.Expired && !cancellationToken.IsCancellationRequested)
        {
            throw new TmuxTransportException(
                $"tmux did not answer within {Options.CommandTimeout}.",
                ["display-message", "-p", GenerationFormat],
                TmuxDispatchState.Unknown,
                error);
        }
    }

    internal TmuxCommandDispatcher CreateEntityDispatcher(ServerGeneration generation)
    {
        ValidateLiveGeneration(generation);
        return new TmuxCommandDispatcher(
            (arguments, cancellationToken) => ExecuteGuardedGroupAsync(
                generation,
                [arguments],
                cancellationToken),
            CommandContext);
    }

    /// <summary>Runs several commands under one generation guard.</summary>
    internal Task<TmuxCommandResult> ExecuteGuardedGroupAsync(
        ServerGeneration expected,
        IReadOnlyList<IReadOnlyList<string>> commands,
        CancellationToken cancellationToken)
    {
        ValidateLiveGeneration(expected);
        ArgumentNullException.ThrowIfNull(commands);
        if (commands.Count == 0)
        {
            throw new InvalidOperationException("A guarded run needs at least one command.");
        }

        foreach (IReadOnlyList<string> command in commands)
        {
            TmuxCommandDispatcher.ValidateArguments(command);
        }

        return _dialect.ExecuteGuardedAsync(expected, commands, cancellationToken);
    }

    internal static ServerGeneration ParseGeneration(string text)
    {
        string[] fields = text.Split(':');
        if (fields.Length != 2
            || !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out int processId)
            || !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out long startTime))
        {
            throw new TmuxProtocolException(
                "tmux reported a malformed server generation.",
                TmuxDispatchState.Dispatched);
        }

        try
        {
            return new ServerGeneration(processId, startTime);
        }
        catch (ArgumentOutOfRangeException error)
        {
            throw new TmuxProtocolException(
                "tmux reported a nonpositive server generation.",
                TmuxDispatchState.Dispatched,
                error);
        }
    }

    internal static void ApplyChildEnvironment(
        ProcessStartInfo startInfo,
        IReadOnlyDictionary<string, string?>? childEnvironment,
        bool forwardPsmuxDataDirectoryThroughWsl = false) =>
        PsmuxProcessEnvironment.Apply(
            startInfo,
            childEnvironment,
            forwardPsmuxDataDirectoryThroughWsl);

    private static void ValidateLiveGeneration(ServerGeneration generation)
    {
        if (generation.ProcessId <= 0 || generation.StartTime <= 0)
        {
            throw new ArgumentException("A live handle requires a positive server generation.", nameof(generation));
        }
    }

    /// <summary>Builds the two transports a process-backed connection needs.</summary>
    /// <remarks>
    /// The version transport carries no endpoint arguments: <c>-V</c> answers
    /// from the client, and a socket naming nothing running would fail it.
    /// </remarks>
    private (
        Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>> Send,
        Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>> SendVersion)
        CreateProcessTransports(ResolvedTmuxConnection resolved)
    {
        Process Launch(ProcessStartInfo startInfo)
        {
            ApplyChildEnvironment(
                startInfo,
                resolved.ChildEnvironment,
                PsmuxProcessEnvironment.ForwardsDataDirectoryThroughWsl(Options));
            return Process.Start(startInfo)
                ?? throw new InvalidOperationException("The tmux client process did not start.");
        }

        ValueTask VerifyBeforeStartAsync(
            ProcessStartInfo _,
            CancellationToken cancellationToken) =>
            PsmuxBinaryTrust.VerifyIfPreviewAsync(Options, cancellationToken);

        TmuxTransportLimits? limits = Options.MaxCapturedBytesPerStream is int ceiling
            ? new TmuxTransportLimits(MaxCapturedBytesPerStream: ceiling)
            : null;
        var transport = new TmuxProcessTransport(
            Options.TmuxBinaryPath,
            PrefixArguments,
            limits,
            launcher: Launch,
            beforeStart: VerifyBeforeStartAsync);
        var versionTransport = new TmuxProcessTransport(
            Options.TmuxBinaryPath,
            limits: limits,
            launcher: Launch,
            beforeStart: VerifyBeforeStartAsync);
        return (transport.ExecuteAsync, versionTransport.ExecuteAsync);
    }

    /// <summary>Routes each request through an interceptor before tmux.</summary>
    internal static Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>> Intercept(
        Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>> send,
        TmuxInterceptor interceptor) =>
        async (request, cancellationToken) =>
        {
            Task<TmuxCommandResult>? pending = interceptor(
                new TmuxInvocation(request.LogicalArguments),
                token => send(request, token),
                cancellationToken);
            return await (pending
                    ?? throw new InvalidOperationException("The interceptor returned no task."))
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("The interceptor returned no result.");
        };

    private Task<TmuxCommandResult> ExecuteSingleAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        _dialect.ExecuteSingleAsync(arguments, cancellationToken);

    private Task<TmuxCommandResult> ExecuteGroupAsync(
        IReadOnlyList<IReadOnlyList<string>> commands,
        CancellationToken cancellationToken) =>
        _dialect.ExecuteGroupAsync(commands, cancellationToken);
}

internal enum TmuxImplementation
{
    Unknown,
    Tmux,
    Psmux,
}
