using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace LibTmux.Internal;

internal sealed class TmuxConnection
{
    internal const string GenerationFormat = "#{pid}:#{start_time}";
    private readonly Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>> _execute;
    private readonly Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>>
        _executeVersion;
    private readonly TmuxEndpointIdentity _endpointIdentity;
    private readonly bool _processBacked;
    private readonly TmuxGenerationGuard _generationGuard;
    private readonly object _implementationGate = new();
    private readonly TmuxEntityLookup _entityLookup;
    private readonly PsmuxSessionRouter _psmuxRouter;
    private readonly string? _resolvedSocketName;
    private readonly string? _resolvedSocketPath;
    private int _implementation;
    private string? _detectedVersionLine;

    internal TmuxConnection(ServerConnectionOptions options)
        : this(TmuxConnectionEndpoint.Resolve(options), execute: null, markerFactory: null)
    {
    }

    internal TmuxConnection(
        ServerConnectionOptions options,
        Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>> execute,
        Func<string>? markerFactory = null,
        TmuxImplementation implementation = TmuxImplementation.Tmux)
        : this(TmuxConnectionEndpoint.Resolve(options), execute, markerFactory, implementation)
    {
    }

    private TmuxConnection(
        ResolvedTmuxConnection resolved,
        Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>>? execute,
        Func<string>? markerFactory,
        TmuxImplementation implementation = TmuxImplementation.Unknown)
    {
        Options = resolved.Options;
        _resolvedSocketName = resolved.SocketName;
        _resolvedSocketPath = resolved.SocketPath;
        PrefixArguments = resolved.PrefixArguments;
        _endpointIdentity = resolved.EndpointIdentity;
        _processBacked = execute is null;
        _implementation = (int)(execute is null ? TmuxImplementation.Unknown : implementation);

        if (execute is null)
        {
            Process Launch(ProcessStartInfo startInfo)
            {
                bool forwardPsmuxDataDirectoryThroughWsl =
                    Options.PsmuxPreview is not null
                    && !OperatingSystem.IsWindows()
                    && string.Equals(
                        Path.GetExtension(Options.TmuxBinaryPath),
                        ".exe",
                        StringComparison.OrdinalIgnoreCase);
                ApplyChildEnvironment(
                    startInfo,
                    resolved.ChildEnvironment,
                    forwardPsmuxDataDirectoryThroughWsl);
                return Process.Start(startInfo)
                    ?? throw new InvalidOperationException("The tmux client process did not start.");
            }

            async ValueTask VerifyBeforeStartAsync(
                ProcessStartInfo _,
                CancellationToken cancellationToken)
            {
                if (Options.PsmuxPreview is PsmuxPreviewOptions psmuxPreview)
                {
                    await PsmuxBinaryTrust.VerifyAsync(
                            Options.TmuxBinaryPath,
                            psmuxPreview.ExpectedBinarySha256,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            var transport = new TmuxProcessTransport(
                Options.TmuxBinaryPath,
                PrefixArguments,
                launcher: Launch,
                beforeStart: VerifyBeforeStartAsync);
            var versionTransport = new TmuxProcessTransport(
                Options.TmuxBinaryPath,
                launcher: Launch,
                beforeStart: VerifyBeforeStartAsync);
            _execute = (request, cancellationToken) =>
                transport.ExecuteAsync(request, cancellationToken);
            _executeVersion = (request, cancellationToken) =>
                versionTransport.ExecuteAsync(request, cancellationToken);
        }
        else
        {
            _execute = execute;
            _executeVersion = execute;
        }

        _psmuxRouter = new PsmuxSessionRouter(ExecuteRawSingleAsync);
        _entityLookup = new TmuxEntityLookup(ExecuteSingleAsync);

        CommandContext = Options.Logger is ILogger logger
            ? new TmuxCommandContext(logger, Options.SocketName ?? Options.SocketPath)
            : null;
        ServerDispatcher = new TmuxCommandDispatcher(
            ExecuteSingleAsync,
            CommandContext,
            ExecuteGroupAsync);
        _generationGuard = new TmuxGenerationGuard(
            _execute,
            markerFactory ?? (static () => $"libtmux_stale_{Guid.NewGuid():N}"));
    }

    internal ServerConnectionOptions Options { get; }

    internal IReadOnlyList<string> PrefixArguments { get; }

    internal TmuxCommandDispatcher ServerDispatcher { get; }

    internal TmuxCommandContext? CommandContext { get; }

    internal bool IsPsmux => CurrentImplementation is TmuxImplementation.Psmux;

    private TmuxImplementation CurrentImplementation =>
        (TmuxImplementation)Volatile.Read(ref _implementation);

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

    internal async Task<(ServerGeneration Generation, string RawVersion)> DiscoverAsync(
        CancellationToken cancellationToken)
    {
        TmuxImplementation implementation = await EnsureImplementationAsync(cancellationToken)
            .ConfigureAwait(false);

        if (implementation is TmuxImplementation.Psmux)
        {
            PsmuxSessionState session = await _psmuxRouter.DiscoverSessionAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            return (session.Generation, RequireDetectedVersionLine());
        }

        TmuxCommandResult generationResult = await ExecuteRawSingleAsync(
                ["display-message", "-p", GenerationFormat],
                cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccessful(generationResult, "server generation discovery");
        if (generationResult.StandardOutputLines.Count != 1)
        {
            throw new InvalidDataException("tmux did not report exactly one server generation.");
        }

        ServerGeneration generation = ParseGeneration(generationResult.StandardOutputLines[0]);
        string? rawVersion = Volatile.Read(ref _detectedVersionLine);
        if (rawVersion is null)
        {
            (TmuxImplementation detected, rawVersion) = await DetectImplementationAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            if (detected != implementation)
            {
                throw new InvalidDataException(
                    "The selected multiplexer changed implementation during discovery.");
            }

            PublishImplementation(detected, rawVersion);
        }

        return (generation, rawVersion);
    }

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
            (arguments, cancellationToken) => ExecuteGuardedAsync(
                generation,
                arguments,
                cancellationToken),
            CommandContext);
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

    /// <summary>Runs one command under a generation guard.</summary>
    private Task<TmuxCommandResult> ExecuteGuardedAsync(
        ServerGeneration expected,
        IReadOnlyList<string> logicalArguments,
        CancellationToken cancellationToken) =>
        ExecuteGuardedGroupAsync(expected, [logicalArguments], cancellationToken);

    /// <summary>Runs several commands under one generation guard.</summary>
    /// <remarks>The tmux path guards the batch in one invocation. The psmux
    /// preview uses separate best-effort preflights and accepts one command.</remarks>
    internal async Task<TmuxCommandResult> ExecuteGuardedGroupAsync(
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

        TmuxImplementation implementation = await EnsureImplementationAsync(cancellationToken)
            .ConfigureAwait(false);
        if (implementation is TmuxImplementation.Psmux)
        {
            return await _psmuxRouter.ExecuteGuardedAsync(
                    expected,
                    commands,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await _generationGuard.ExecuteAsync(expected, commands, cancellationToken)
            .ConfigureAwait(false);
    }

    private static void ValidateLiveGeneration(ServerGeneration generation)
    {
        if (generation.ProcessId <= 0 || generation.StartTime <= 0)
        {
            throw new ArgumentException("A live handle requires a positive server generation.", nameof(generation));
        }
    }

    private async Task<TmuxCommandResult> ExecuteSingleAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        TmuxImplementation implementation = await EnsureImplementationAsync(cancellationToken)
            .ConfigureAwait(false);
        if (implementation is not TmuxImplementation.Psmux)
        {
            return await ExecuteRawSingleAsync(arguments, cancellationToken).ConfigureAwait(false);
        }

        return await _psmuxRouter.ExecuteSingleAsync(arguments, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<TmuxCommandResult> ExecuteGroupAsync(
        IReadOnlyList<IReadOnlyList<string>> commands,
        CancellationToken cancellationToken)
    {
        TmuxImplementation implementation = await EnsureImplementationAsync(cancellationToken)
            .ConfigureAwait(false);
        if (implementation is TmuxImplementation.Psmux)
        {
            if (commands.Count != 1)
            {
                throw new NotSupportedException(
                    "psmux does not preserve tmux grouped-command semantics.");
            }

            return await ExecuteSingleAsync(commands[0], cancellationToken).ConfigureAwait(false);
        }

        return await _execute(TmuxCommandRequest.Group([.. commands]), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<TmuxImplementation> EnsureImplementationAsync(
        CancellationToken cancellationToken)
    {
        if (Options.PsmuxPreview is not null)
        {
            ValidatePsmuxConnection();
        }
        else if (_processBacked
            && (OperatingSystem.IsWindows()
                || string.Equals(
                    Path.GetExtension(Options.TmuxBinaryPath),
                    ".exe",
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new PlatformNotSupportedException(
                "Windows executables require the explicit PsmuxServer query facade.");
        }

        TmuxImplementation implementation = CurrentImplementation;
        if (implementation is not TmuxImplementation.Unknown)
        {
            if (implementation is TmuxImplementation.Psmux)
            {
                ValidatePsmuxConnection();
            }

            return implementation;
        }

        (implementation, string rawVersion) = await DetectImplementationAsync(cancellationToken)
            .ConfigureAwait(false);
        PublishImplementation(implementation, rawVersion);
        return implementation;
    }

    private async Task<(TmuxImplementation Implementation, string RawVersion)>
        DetectImplementationAsync(CancellationToken cancellationToken)
    {
        TmuxCommandResult result = await _executeVersion(
                TmuxCommandRequest.Single(["-V"]),
                cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccessful(result, "multiplexer version discovery");
        if (!TmuxVersionBannerParser.TryParse(
                result.StandardOutputLines,
                out TmuxVersionBanner banner))
        {
            throw new InvalidDataException(
                "The multiplexer did not report a recognized version banner.");
        }

        if (banner.Implementation is TmuxImplementation.Psmux)
        {
            if (Options.PsmuxPreview is null)
            {
                throw new NotSupportedException(
                    "psmux requires the explicit PsmuxServer query facade.");
            }

            if (!string.Equals(
                    banner.Version,
                    PsmuxCompatibility.SupportedVersion,
                    StringComparison.Ordinal))
            {
                throw new NotSupportedException(
                    $"The psmux preview supports exactly version {PsmuxCompatibility.SupportedVersion}.");
            }

            if (!string.Equals(
                    banner.ImplementationLine,
                    PsmuxCompatibility.SupportedImplementationLine,
                    StringComparison.Ordinal))
            {
                throw new NotSupportedException(
                    $"The psmux preview supports exactly {PsmuxCompatibility.SupportedImplementationLine}.");
            }

            ValidatePsmuxConnection();
        }
        else if (Options.PsmuxPreview is not null)
        {
            throw new NotSupportedException(
                "The trusted psmux preview executable reported a tmux banner.");
        }

        return (banner.Implementation, banner.RawVersion);
    }

    private void PublishImplementation(TmuxImplementation implementation, string rawVersion)
    {
        lock (_implementationGate)
        {
            TmuxImplementation observed = CurrentImplementation;
            if (observed is not TmuxImplementation.Unknown && observed != implementation)
            {
                throw new InvalidDataException(
                    "The selected multiplexer changed implementation during discovery.");
            }

            _detectedVersionLine ??= rawVersion;
            Volatile.Write(ref _implementation, (int)implementation);
        }
    }

    private string RequireDetectedVersionLine() =>
        Volatile.Read(ref _detectedVersionLine)
        ?? throw new InvalidOperationException("The multiplexer version was not detected.");

    private void ValidatePsmuxConnection()
    {
        if (Options.PsmuxPreview is null)
        {
            throw new NotSupportedException(
                "psmux requires the explicit PsmuxServer query facade.");
        }

        if (Options.SocketPath is not null)
        {
            throw new NotSupportedException(
                "psmux connections require a socket name because -S does not select a namespace.");
        }

        if (string.IsNullOrEmpty(_resolvedSocketName)
            || string.Equals(_resolvedSocketName, "default", StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                "psmux connections require a non-default socket name for endpoint isolation.");
        }

        PsmuxTargetGrammar.ValidateName(_resolvedSocketName, "namespace");

        if (Options.ColorMode is not TmuxColorMode.Default)
        {
            throw new NotSupportedException(
                "psmux does not honor tmux's forced client color modes.");
        }

        if (Options.ConfigurationFile is not null)
        {
            throw new NotSupportedException(
                "psmux cannot apply a per-client configuration file to a pre-existing session.");
        }
    }

    private async Task<TmuxCommandResult> ExecuteRawSingleAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? preserveArguments = null)
    {
        TmuxCommandRequest request = TmuxCommandRequest.Single(arguments);
        TmuxCommandResult result;
        try
        {
            result = await _execute(request, cancellationToken).ConfigureAwait(false);
        }
        catch (TmuxTransportException error) when (preserveArguments is not null)
        {
            throw new TmuxTransportException(
                error.Message,
                preserveArguments,
                error.Dispatch,
                error.InnerException);
        }

        return preserveArguments is null
            ? result
            : TmuxCommandResultProjection.Remap(
                result,
                preserveArguments,
                result.StandardOutput);
    }

    private static void EnsureSuccessful(TmuxCommandResult result, string operation)
    {
        if (result.ExitCode != 0 || result.StandardErrorLines.Count > 0)
        {
            throw new TmuxCommandException($"{operation} failed.", result);
        }
    }

}

internal enum TmuxImplementation
{
    Unknown,
    Tmux,
    Psmux,
}
