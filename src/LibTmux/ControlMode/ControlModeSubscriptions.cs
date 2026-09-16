using System.Runtime.Versioning;

namespace LibTmux;

/// <summary>Subscribes a control client to a format changing.</summary>
/// <remarks>
/// tmux answers <c>refresh-client -B</c> with <c>%subscription-changed</c>
/// whenever the named format's value changes. There is no typed surface for
/// building that command otherwise, so a caller renders it by hand - and
/// tmux 3.8 tightened session-scoped targets: the session id form that fired
/// on every earlier release (<c>name:$0:format</c>) silently stops firing,
/// with no error, while an empty session field (<c>name::format</c>) fires
/// on every released version. This always renders the empty form, so a
/// session-scoped subscription is never reachable the wrong way.
/// </remarks>
[UnsupportedOSPlatform("windows")]
public static class ControlModeSubscriptions
{
    /// <summary>Subscribes to a session-scoped format changing.</summary>
    /// <param name="session">The control client to subscribe on.</param>
    /// <param name="name">
    /// The subscription's name, echoed back as the first word of a
    /// <c>%subscription-changed</c> notification for it.
    /// </param>
    /// <param name="format">The tmux format to watch, without <c>#{}</c>.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The lines tmux printed answering the subscribe command.</returns>
    public static Task<IReadOnlyList<string>> SubscribeSessionAsync(
        this IControlModeSession session,
        string name,
        string format,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        return session.SendAsync(
            TmuxCommand.Create("refresh-client", "-B", $"{name}::{format}"),
            cancellationToken);
    }
}
