using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;

namespace LibTmux.Testing;

/// <summary>Makes the tmux objects a test needs, each owning its own cleanup.</summary>
/// <remarks>
/// Every scope this hands out is disposable, and disposing unwinds as far as
/// the scope owns: a session scope removes the session, a server scope kills
/// the server. A test that takes a scope therefore cannot leak a tmux server,
/// even when it fails part way through.
/// </remarks>
[SuppressMessage(
    "Performance",
    "CA1822:Mark members as static",
    Justification = "The reviewed surface is an instance so one factory holds one run's names.")]
[UnsupportedOSPlatform("windows")]
public sealed class TmuxTestFactory
{
    private readonly TmuxNameGenerator _names;

    /// <summary>Initializes a factory.</summary>
    public TmuxTestFactory() => _names = new TmuxNameGenerator();

    /// <summary>Starts a server this test owns, with its environment.</summary>
    /// <param name="options">How to reach tmux, or null for a socket of its own.</param>
    /// <param name="cancellationToken">Cancels the tmux commands.</param>
    /// <returns>The context owning the server.</returns>
    public async Task<TmuxTestContext> CreateContextAsync(
        TmuxTestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        TmuxTestOptions settings = options ?? TmuxTestOptions.Default;
        TemporaryServerScope scope = await TemporaryServerScope
            .StartAsync(settings.ConnectionOptions, cancellationToken)
            .ConfigureAwait(false);
        return new TmuxTestContext(scope, DescribeEnvironment(settings));
    }

    /// <summary>Starts a server this test owns.</summary>
    /// <param name="options">How to reach tmux, or null for a socket of its own.</param>
    /// <param name="cancellationToken">Cancels the tmux commands.</param>
    /// <returns>The scope owning the server.</returns>
    public Task<TemporaryServerScope> CreateServerAsync(
        TmuxTestOptions? options = null,
        CancellationToken cancellationToken = default) =>
        TemporaryServerScope.StartAsync(
            (options ?? TmuxTestOptions.Default).ConnectionOptions,
            cancellationToken);

    /// <summary>Starts a server and a session in it, both owned by this test.</summary>
    /// <param name="options">How to reach tmux, or null for a socket of its own.</param>
    /// <param name="cancellationToken">Cancels the tmux commands.</param>
    /// <returns>The scope owning the session.</returns>
    /// <remarks>
    /// The server this makes is left to the session's own scope, which is a
    /// session on a server nobody else will kill.
    /// </remarks>
    public async Task<TemporarySessionScope> CreateSessionAsync(
        TmuxTestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        TmuxTestOptions settings = options ?? TmuxTestOptions.Default;
        TemporaryServerScope scope = await CreateServerAsync(settings, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            string name = await _names
                .CreateAvailableSessionNameAsync(
                    scope.Server,
                    settings.SessionNamePrefix,
                    cancellationToken)
                .ConfigureAwait(false);
            return await TemporarySessionScope
                .StartAsync(
                    scope.Server,
                    new NewSessionRequest(name: name),
                    scope,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            await TemporaryScopeCleanup.DisposeAfterFailureAsync(scope, error)
                .ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Starts a session on a server the caller already has.</summary>
    /// <param name="server">The server to start it on.</param>
    /// <param name="options">What to name it, or null for a name of its own.</param>
    /// <param name="cancellationToken">Cancels the tmux commands.</param>
    /// <returns>The scope owning the session.</returns>
    public async Task<TemporarySessionScope> CreateSessionAsync(
        Server server,
        TmuxTestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        TmuxTestOptions settings = options ?? TmuxTestOptions.Default;
        string name = await _names
            .CreateAvailableSessionNameAsync(
                server,
                settings.SessionNamePrefix,
                cancellationToken)
            .ConfigureAwait(false);
        return await TemporarySessionScope
            .StartAsync(
                server,
                new NewSessionRequest(name: name),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Starts a server, a session, and a window, all owned by this test.</summary>
    /// <param name="options">How to reach tmux, or null for a socket of its own.</param>
    /// <param name="cancellationToken">Cancels the tmux commands.</param>
    /// <returns>The scope owning the window.</returns>
    public async Task<TemporaryWindowScope> CreateWindowAsync(
        TmuxTestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        TmuxTestOptions settings = options ?? TmuxTestOptions.Default;
        TemporarySessionScope session = await CreateSessionAsync(settings, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            string name = await _names
                .CreateAvailableWindowNameAsync(
                    session.Session,
                    settings.SessionNamePrefix,
                    cancellationToken)
                .ConfigureAwait(false);
            return await TemporaryWindowScope
                .StartAsync(
                    session.Session,
                    new NewWindowRequest(name: name),
                    session,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            await TemporaryScopeCleanup.DisposeAfterFailureAsync(session, error)
                .ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Starts a window in a session the caller already has.</summary>
    /// <param name="session">The session to start it in.</param>
    /// <param name="options">What to name it, or null for a name of its own.</param>
    /// <param name="cancellationToken">Cancels the tmux commands.</param>
    /// <returns>The scope owning the window.</returns>
    public async Task<TemporaryWindowScope> CreateWindowAsync(
        Session session,
        TmuxTestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        TmuxTestOptions settings = options ?? TmuxTestOptions.Default;
        string name = await _names
            .CreateAvailableWindowNameAsync(
                session,
                settings.SessionNamePrefix,
                cancellationToken)
            .ConfigureAwait(false);
        return await TemporaryWindowScope
            .StartAsync(
                session,
                new NewWindowRequest(name: name),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Starts a server, session, window, and pane a test can type into.</summary>
    /// <param name="options">How to reach tmux, or null for a socket of its own.</param>
    /// <param name="cancellationToken">Cancels the tmux commands.</param>
    /// <returns>The scope owning all four.</returns>
    public async Task<TemporaryHierarchyScope> CreateHierarchyAsync(
        TmuxTestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        TmuxTestOptions settings = options ?? TmuxTestOptions.Default;
        TemporaryServerScope scope = await CreateServerAsync(settings, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            // A server started on its own holds no sessions, so the first one
            // is made here rather than looked for.
            TemporarySessionScope owned = await CreateSessionAsync(
                    scope.Server,
                    settings,
                    cancellationToken)
                .ConfigureAwait(false);
            Session session = owned.Session;
            Window window = (await session.GetWindowsAsync(cancellationToken)
                .ConfigureAwait(false))[0];
            Pane pane = (await window.GetPanesAsync(cancellationToken).ConfigureAwait(false))[0];
            return new TemporaryHierarchyScope(scope, session, window, pane);
        }
        catch
        {
            // The scope owns a live tmux server, and nothing else is holding it
            // yet, so it has to be unwound here or it would outlive the failure.
            await scope.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static TestEnvironment DescribeEnvironment(TmuxTestOptions options)
    {
        // TMUX and TMUX_PANE must not carry over: they would point a client at
        // the developer's own server and pane instead of the test's.
        Dictionary<string, string?> variables = new(StringComparer.Ordinal)
        {
            ["TMUX"] = null,
            ["TMUX_PANE"] = null,
        };

        if (options.ConnectionOptions.SocketPath is string path)
        {
            variables["LIBTMUX_SOCKET_PATH"] = path;
        }

        return new TestEnvironment(Directory.GetCurrentDirectory(), variables);
    }
}
