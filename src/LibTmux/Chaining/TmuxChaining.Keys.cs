using System.Runtime.Versioning;

namespace LibTmux;

// Builds and executes key-binding requests.
public static partial class TmuxChaining
{
    /// <summary>Returns a key-binding request as one tmux command.</summary>
    /// <param name="request">The binding to add.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request" /> is null.</exception>
    public static TmuxCommand ToCommand(this BindKeyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Command([.. Server.BuildBindKeyArguments(request)]);
    }

    /// <summary>Returns a key-unbinding request as one tmux command.</summary>
    /// <param name="request">The binding to remove.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request" /> is null.</exception>
    public static TmuxCommand ToCommand(this UnbindKeyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Command([.. Server.BuildUnbindKeyArguments(request)]);
    }

    /// <summary>Runs a key-binding request on its own.</summary>
    /// <param name="request">The binding to add.</param>
    /// <param name="server">The server to bind on.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed, which is nothing for an ordinary bind.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this BindKeyRequest request,
        Server server,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.Chain().Then(request.ToCommand()).ExecuteAsync(cancellationToken);
    }

    /// <summary>Runs a key-unbinding request on its own.</summary>
    /// <param name="request">The binding to remove.</param>
    /// <param name="server">The server to unbind on.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed, which is nothing for an ordinary unbind.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this UnbindKeyRequest request,
        Server server,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.Chain().Then(request.ToCommand()).ExecuteAsync(cancellationToken);
    }
}
