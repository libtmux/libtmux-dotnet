using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace LibTmux.Internal;

internal sealed class TmuxConnection
{
    internal const string GenerationFormat = "#{pid}:#{start_time}";
    private readonly MultiplexerDialect _dialect;
    private readonly TmuxEndpointIdentity _endpointIdentity;
    private readonly TmuxEntityLookup _entityLookup;
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

        _entityLookup = new TmuxEntityLookup(ExecuteSingleAsync);
        CommandContext = Options.Logger is ILogger logger
            ? new TmuxCommandContext(logger, Options.SocketName ?? Options.SocketPath)
            : null;
        ServerDispatcher = new TmuxCommandDispatcher(
            ExecuteSingleAsync,
            CommandContext,
            ExecuteGroupAsync);
    }

    internal ServerConnectionOptions Options { get; }

    internal IReadOnlyList<string> PrefixArguments { get; }

    internal TmuxCommandDispatcher ServerDispatcher { get; }

    internal TmuxCommandContext? CommandContext { get; }

    internal bool IsPsmux => _dialect.IsPsmux;

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

    internal Task<(ServerGeneration Generation, string RawVersion)> DiscoverAsync(
        CancellationToken cancellationToken) =>
        _dialect.DiscoverAsync(cancellationToken);

    internal Task<(ServerGeneration Generation, SessionId Id)?> FindSessionAsync(
        SessionId id,
        CancellationToken cancellationToken) =>
        _entityLookup.FindSessionAsync(id, cancellationToken);

    internal Task<(ServerGeneration Generation, WindowId Id)?> FindWindowAsync(
        WindowId id,
        CancellationToken cancellationToken) =>
        _entityLookup.FindWindowAsync(id, cancellationToken);

    internal Task<(ServerGeneration Generation, PaneId Id)?> FindPaneAsync(
        PaneId id,
        CancellationToken cancellationToken) =>
        _entityLookup.FindPaneAsync(id, cancellationToken);

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
            throw new InvalidDataException("tmux reported a malformed server generation.");
        }

        try
        {
            return new ServerGeneration(processId, startTime);
        }
        catch (ArgumentOutOfRangeException error)
        {
            throw new InvalidDataException("tmux reported a nonpositive server generation.", error);
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

        var transport = new TmuxProcessTransport(
            Options.TmuxBinaryPath,
            PrefixArguments,
            launcher: Launch,
            beforeStart: VerifyBeforeStartAsync);
        var versionTransport = new TmuxProcessTransport(
            Options.TmuxBinaryPath,
            launcher: Launch,
            beforeStart: VerifyBeforeStartAsync);
        return (transport.ExecuteAsync, versionTransport.ExecuteAsync);
    }

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
