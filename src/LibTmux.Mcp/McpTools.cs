using System.Runtime.Versioning;

namespace LibTmux.Mcp;

/// <summary>Builds the tool classes without hosting a server.</summary>
/// <remarks>
/// <para>
/// The server resolves these from its container, which is the right shape when
/// something is speaking the protocol. An application that wants one answer
/// from tmux — the exit status of a command, what is new in a pane — wants a
/// call rather than a protocol, and should not have to assemble a container to
/// get one.
/// </para>
/// <para>
/// Each call builds its own connection cache and its own activity hub, so two
/// of these share nothing. Hold one for as long as you would hold a
/// connection; building one per call gives up the caching that makes the
/// second call cheaper than the first.
/// </para>
/// </remarks>
[UnsupportedOSPlatform("windows")]
public static class McpTools
{
    /// <summary>Builds the tools that only read.</summary>
    /// <param name="options">How to reach tmux, or null for the ambient server.</param>
    /// <param name="policy">What the tools may spend, or null for the defaults.</param>
    /// <returns>The reading tools.</returns>
    public static ReadTools Reading(
        ServerConnectionOptions? options = null,
        ServerPolicy? policy = null) =>
        new(Accessor(options), policy ?? new ServerPolicy(), new PaneActivityHub());

    /// <summary>Builds the tools that change tmux, and the reading ones they need.</summary>
    /// <param name="options">How to reach tmux, or null for the ambient server.</param>
    /// <param name="policy">What the tools may spend, or null for the defaults.</param>
    /// <param name="jobs">Where background commands are tracked, or null for a new store.</param>
    /// <returns>The changing tools.</returns>
    /// <remarks>
    /// Pass the same <paramref name="jobs" /> to every caller that needs to
    /// collect a job somebody else started; a handle is only meaningful to the
    /// store that issued it. Dispose the returned tools asynchronously. The
    /// factory disposes its connection cache, activity hub, and any job store
    /// it created; a supplied <paramref name="jobs" /> remains caller-owned.
    /// </remarks>
    public static WriteTools Writing(
        ServerConnectionOptions? options = null,
        ServerPolicy? policy = null,
        JobStore? jobs = null)
    {
        JobStore effectiveJobs = jobs ?? new JobStore();
        WriteTools.ResourceOwnership ownership = WriteTools.ResourceOwnership.Connection
            | WriteTools.ResourceOwnership.Activity;
        if (jobs is null)
        {
            ownership |= WriteTools.ResourceOwnership.Jobs;
        }

        return new WriteTools(
            Accessor(options),
            policy ?? new ServerPolicy(),
            new PaneActivityHub(),
            effectiveJobs,
            ownership);
    }

    /// <summary>Builds the tools that remove what they act on.</summary>
    /// <param name="options">How to reach tmux, or null for the ambient server.</param>
    /// <returns>The removing tools.</returns>
    /// <remarks>
    /// Nothing here is recoverable, and nothing gates it: a caller reaching
    /// this has already decided, where a model reaching the server has the
    /// tier deciding for it.
    /// </remarks>
    public static DestructiveTools Removing(ServerConnectionOptions? options = null) =>
        new(Accessor(options));

    /// <summary>Builds the tools that only read, over a connected server.</summary>
    /// <param name="server">The server every call reaches.</param>
    /// <param name="policy">What the tools may spend, or null for the defaults.</param>
    /// <returns>The reading tools.</returns>
    /// <remarks>
    /// Prefer this when a connection is already in hand. Passing options
    /// instead makes the tools resolve a socket of their own, which is a
    /// different server whenever the environment says so.
    /// </remarks>
    public static ReadTools Reading(Server server, ServerPolicy? policy = null) =>
        new(new TmuxConnectionAccessor(server), policy ?? new ServerPolicy(), new PaneActivityHub());

    /// <summary>Builds the tools that change tmux, over a connected server.</summary>
    /// <param name="server">The server every call reaches.</param>
    /// <param name="policy">What the tools may spend, or null for the defaults.</param>
    /// <param name="jobs">Where background commands are tracked, or null for a new store.</param>
    /// <returns>The changing tools.</returns>
    /// <remarks>
    /// Dispose the returned tools asynchronously. The factory disposes its
    /// connection cache, activity hub, and any job store it created; the
    /// supplied <paramref name="server" /> and <paramref name="jobs" /> remain
    /// caller-owned.
    /// </remarks>
    public static WriteTools Writing(
        Server server,
        ServerPolicy? policy = null,
        JobStore? jobs = null)
    {
        JobStore effectiveJobs = jobs ?? new JobStore();
        WriteTools.ResourceOwnership ownership = WriteTools.ResourceOwnership.Connection
            | WriteTools.ResourceOwnership.Activity;
        if (jobs is null)
        {
            ownership |= WriteTools.ResourceOwnership.Jobs;
        }

        return new WriteTools(
            new TmuxConnectionAccessor(server),
            policy ?? new ServerPolicy(),
            new PaneActivityHub(),
            effectiveJobs,
            ownership);
    }

    private static TmuxConnectionAccessor Accessor(ServerConnectionOptions? options) =>
        new(options, options?.SocketName);
}
