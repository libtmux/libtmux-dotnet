using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LibTmux.Mcp;

/// <summary>Turns a failure into something the caller can act on.</summary>
/// <remarks>
/// <para>
/// An unhandled exception reaches the client as "An error occurred invoking
/// 'run_shell_command'" — true, and useless. A model reading that has no way to tell a
/// dead pane from a missing binary from its own bad argument, so it retries
/// the same call. Every failure here names what went wrong and what to do
/// instead.
/// </para>
/// <para>
/// It also retries once when the tmux server was replaced underneath a cached
/// handle. That happens whenever tmux is restarted between calls, and it is
/// not something a caller can be expected to understand, let alone fix.
/// </para>
/// </remarks>
[UnsupportedOSPlatform("windows")]
internal static class ToolFailureFilter
{
    /// <summary>Builds the filter.</summary>
    /// <returns>The filter.</returns>
    /// <remarks>
    /// Everything it needs comes off the request rather than being captured,
    /// because the filter is composed while the container is still being built
    /// — capturing a second accessor here would give it a cache nothing else
    /// writes to, and invalidating that would fix nothing.
    /// </remarks>
    internal static McpRequestFilter<CallToolRequestParams, CallToolResult> Create() =>
        next => async (request, cancellationToken) =>
    {
        string tool = request.Params?.Name ?? "a tmux tool";
        bool mayModify = ToolMetadata.MayModify(request.Services, tool);
        ILogger logger = request.Services?.GetService<ILoggerFactory>()
                ?.CreateLogger(nameof(ToolFailureFilter))
            ?? NullLogger.Instance;
        try
        {
            return await next(request, cancellationToken).ConfigureAwait(false);
        }
        catch (StaleServerGenerationException)
        {
            // The socket now holds a different tmux process. Forgetting the
            // handle and asking again is exactly what a caller would have to
            // do, and they have no way to know that.
            request.Services?.GetService<TmuxConnectionAccessor>()
                ?.Invalidate();
            try
            {
                return await next(request, cancellationToken).ConfigureAwait(false);
            }
            catch (LibTmuxException retried)
            {
                return Failure(
                    logger,
                    tool,
                    retried,
                    mayModify,
                    "The tmux server was restarted and the retry failed too. "
                    + "Call get_server_info to see what is running now.");
            }
        }
        catch (TmuxVersionTooLowException error)
        {
            return Failure(
                logger,
                tool,
                error,
                mayModify,
                "This tmux is too old for that operation. "
                + "Call get_server_info to see which version is running.");
        }
        catch (TmuxObjectNotFoundException error)
        {
            return Failure(
                logger,
                tool,
                error,
                mayModify,
                "That session, window or pane no longer exists. "
                + "Call list_sessions, list_windows, or list_panes to see what does.");
        }
        catch (TmuxCommandException error)
        {
            // tmux's own message is the most specific thing anybody has.
            return Failure(
                logger,
                tool,
                error,
                mayModify,
                $"tmux refused the command: {error.Message}");
        }
        catch (TmuxOperationCanceledException error) when (error.CommandMayHaveExecuted)
        {
            return Failure(logger, tool, error, mayModify, error.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(
                logger,
                tool,
                null,
                mayModify,
                "The operation ran out of time. Nothing was rolled back — whatever "
                + "was started is still running in its pane. Do not retry until you "
                + "have read the pane, so you do not start it twice.");
        }
        catch (LibTmuxException error)
        {
            return Failure(logger, tool, error, mayModify, error.Message);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // The backstop for anything unhandled; see the class remarks.
            return Failure(
                logger,
                tool,
                error,
                mayModify,
                $"{error.Message} This is unexpected — check the server's log on "
                + "standard error before retrying, because retrying unchanged will "
                + "most likely fail the same way.");
        }
    };

    private static CallToolResult Failure(
        ILogger logger,
        string tool,
        Exception? error,
        bool mayModify,
        string advice)
    {
        if (error is not null)
        {
            Log.ToolFailed(logger, error, tool);
        }

        return new CallToolResult
        {
            IsError = true,
            Content =
            [
                new TextContentBlock
                {
                    Text = $"{tool} failed. {ActionableAdvice(tool, error, mayModify, advice)}",
                },
            ],
        };
    }

    internal static string ActionableAdvice(
        string tool,
        Exception? error,
        bool mayModify,
        string advice)
    {
        if (TryPasteCleanup(error, out string? buffer))
        {
            return $"The paste failed, and temporary tmux buffer {buffer} may still "
                + "contain the pasted text because cleanup failed. Do not retry the paste. "
                + "Ask the operator to inspect and remove that "
                + $"exact buffer with tmux delete-buffer -b {buffer}.";
        }

        bool mayHaveActed = error is TmuxOperationCanceledException cancellation
                && cancellation.CommandMayHaveExecuted
            || error is LibTmuxException tmux
                && tmux.Dispatch != TmuxDispatchState.NotDispatched;
        if (!mayModify || !mayHaveActed)
        {
            return advice;
        }

        const string recovery = " Inspect tmux state first.";
        return advice
            + " tmux may have acted before the failure. Do not retry this operation."
            + recovery;
    }

    private static bool TryPasteCleanup(Exception? error, out string? buffer)
    {
        buffer = null;
        if (error?.Data[WriteTools.PasteBufferCleanupFailureDataKey] is not Exception
            || error.Data[WriteTools.PasteBufferCleanupBufferDataKey] is not string candidate
            || candidate.Length is < 1 or > 64
            || !candidate.StartsWith("libtmux_mcp_", StringComparison.Ordinal)
            || candidate.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character != '_'))
        {
            return false;
        }

        buffer = candidate;
        return true;
    }
}
