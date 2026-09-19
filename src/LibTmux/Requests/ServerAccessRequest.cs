using System.Runtime.Versioning;

namespace LibTmux;

/// <summary>Describes one <c>server-access</c> invocation.</summary>
/// <remarks>
/// tmux lets other users attach to a server over its socket. Access is granted
/// per user, and read-only or read-write says what a granted user may do.
/// </remarks>
public sealed record ServerAccessRequest : ITmuxRequest<Server>
{
    /// <summary>Gets the user to grant access to.</summary>
    public string? AllowUser { get; init; }

    /// <summary>Gets the user to take access from.</summary>
    public string? DenyUser { get; init; }

    /// <summary>Gets whether the current list is reported.</summary>
    public bool List { get; init; }

    /// <summary>Gets whether the granted user may only look.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>Gets whether the granted user may also act.</summary>
    public bool ReadWrite { get; init; }

    /// <summary>Resolves the one user named, refusing contradictory instructions.</summary>
    /// <returns>The user argument, or null when none is named.</returns>
    /// <exception cref="ArgumentException">
    /// Access is both granted and taken away, or the granted user may both
    /// only look and also act.
    /// </exception>
    /// <remarks>
    /// Initializers cannot check one property against another, so the pairing
    /// is settled where the user is derived. Every caller that sends a change
    /// needs this value, so none can reach tmux having skipped the check.
    /// </remarks>
    internal string? ResolveUser()
    {
        if (AllowUser is not null && DenyUser is not null)
        {
            throw new ArgumentException(
                "One request either grants access or takes it away.",
                nameof(DenyUser));
        }

        if (ReadOnly && ReadWrite)
        {
            throw new ArgumentException(
                "One request either grants looking or acting, not both.",
                nameof(ReadWrite));
        }

        return AllowUser ?? DenyUser;
    }

    /// <summary>Returns an access request as one tmux command.</summary>
    /// <param name="server">The server whose access is changed.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <remarks>
    /// The command itself arrived in tmux 3.3, so batching does not soften the
    /// refusal below that: an older server has nothing to send it to.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="server" /> is null.</exception>
    /// <exception cref="TmuxVersionTooLowException">tmux is older than 3.3.</exception>
    [UnsupportedOSPlatform("windows")]
    public TmuxCommand ToCommand(Server server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return TmuxChaining.Command([.. server.BuildServerAccessArguments(this)]);
    }
}
