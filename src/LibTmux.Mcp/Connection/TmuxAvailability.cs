using System.Runtime.Versioning;

namespace LibTmux.Mcp;

/// <summary>Treats "nothing to list yet" as an answer rather than a failure.</summary>
/// <remarks>
/// <para>
/// tmux starts a server when something needs one and exits when the last
/// session closes, so a socket with nothing behind it is the ordinary state
/// before the first session and after the last. Asking it for a listing fails
/// with a connection error.
/// </para>
/// <para>
/// That is the first call an assistant makes. Answering "there are none" costs
/// it one turn; answering with an error costs it a turn and teaches it to
/// distrust the tool. Only those failures are softened — anything else tmux
/// refuses still propagates.
/// </para>
/// <para>
/// A running server holding no sessions fails differently again: tmux answers
/// <c>list-sessions</c> with an empty list but refuses <c>list-windows -a</c>
/// and <c>list-panes -a</c>, because those resolve a current target and there
/// is none. Both states mean the same thing to a caller asking what exists.
/// </para>
/// </remarks>
[UnsupportedOSPlatform("windows")]
internal static class TmuxAvailability
{
    /// <summary>Runs a listing, answering nothing when no server is running.</summary>
    /// <typeparam name="T">What is being listed.</typeparam>
    /// <param name="server">The server the listing would go to.</param>
    /// <param name="list">The listing to run.</param>
    /// <returns>What tmux holds, or an empty list when it holds nothing.</returns>
    internal static async Task<IReadOnlyList<T>> OrEmptyAsync<T>(
        Server server,
        Func<Task<IReadOnlyList<T>>> list)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(list);

        // An unmaterialized handle is one the accessor could not connect,
        // which is what a socket with no server behind it produces. Asking it
        // to list would throw for a reason that is not a fault.
        if (!server.IsMaterialized)
        {
            return [];
        }

        try
        {
            return await list().ConfigureAwait(false);
        }
        catch (LibTmuxException error) when (IsServerAbsent(error) || IsServerEmpty(error))
        {
            // The server was there when the handle was made and is not now,
            // or it is still there and holds nothing to list.
            return [];
        }
    }

    /// <summary>Answers whether a failure means the server holds nothing.</summary>
    /// <param name="error">What tmux reported.</param>
    /// <returns><see langword="true" /> when there was no target to resolve.</returns>
    /// <remarks>
    /// A server-wide listing resolves a current target before it filters, so
    /// on a server with no sessions tmux refuses rather than answering an
    /// empty list. The wording differs by command — <c>cmd-find.c</c> says
    /// "no current target", <c>options.c</c> and <c>cmd-show-environment.c</c>
    /// name the level they wanted — so the family is matched rather than the
    /// one string a single tmux version happens to emit. "no current client"
    /// is deliberately absent: nothing is attached to a dedicated socket even
    /// when it holds sessions, so treating that as emptiness would hide a
    /// real refusal.
    /// </remarks>
    internal static bool IsServerEmpty(LibTmuxException error)
    {
        ArgumentNullException.ThrowIfNull(error);

        // tmux's own wording reaches us on standard error; the exception
        // message is the operation that failed ("list-windows failed."), which
        // names no cause at all.
        return error is TmuxCommandException command
            && command.Result.StandardErrorLines.Any(static line =>
                line.Contains("no current target", StringComparison.Ordinal)
                || line.Contains("no current session", StringComparison.Ordinal)
                || line.Contains("no current window", StringComparison.Ordinal)
                || line.Contains("no current pane", StringComparison.Ordinal));
    }

    /// <summary>Answers whether a failure means there is no server, not a fault.</summary>
    /// <param name="error">What tmux reported.</param>
    /// <returns><see langword="true" /> when nothing is listening on the socket.</returns>
    /// <remarks>
    /// Matched on tmux's own wording because tmux exits 1 for every refusal
    /// alike, so the status cannot tell an absent server from a rejected
    /// command.
    /// </remarks>
    internal static bool IsServerAbsent(LibTmuxException error)
    {
        ArgumentNullException.ThrowIfNull(error);
        string message = error.Message;
        return message.Contains("error connecting to", StringComparison.OrdinalIgnoreCase)
            || message.Contains("no server running", StringComparison.OrdinalIgnoreCase)
            || message.Contains("server generation discovery failed", StringComparison.OrdinalIgnoreCase);
    }
}
