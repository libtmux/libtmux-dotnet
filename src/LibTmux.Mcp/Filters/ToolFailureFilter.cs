using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
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
            // Before the SDK binds. Its binder coerces where it can and drops
            // what the schema does not declare, so a call that named an
            // argument this tool never had used to run as if it had not.
            RequireDeclaredArguments(request, tool);
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
        catch (OperationCanceledException error)
            when (error is TmuxOperationCanceledException { CommandMayHaveExecuted: true })
        {
            return Failure(
                logger,
                tool,
                error,
                mayModify,
                AdviceFor(
                    error,
                    ToolMetadata.Declaration(request.Services, tool),
                    tool,
                    request.Services?.GetService<CapabilityRegistry>()?.Selection));
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
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return Failure(
                logger,
                tool,
                error,
                mayModify,
                AdviceFor(
                    error,
                    ToolMetadata.Declaration(request.Services, tool),
                    tool,
                    request.Services?.GetService<CapabilityRegistry>()?.Selection));
        }
    };

    /// <summary>Answers what to tell a caller about one failure.</summary>
    /// <param name="error">What went wrong.</param>
    /// <param name="declaration">What this server declared about the tool, when known.</param>
    /// <param name="tool">The tool named by the call, when known.</param>
    /// <param name="selection">What this server selected, when known.</param>
    /// <returns>The sentence naming the cause and what to do instead.</returns>
    /// <remarks>
    /// Shared with the read batch, which dispatches its inner operations
    /// directly and so never passes through this filter. Without one source
    /// the same failure read differently depending on whether it was called
    /// alone or inside a batch, and the batch's wording was the better one.
    /// </remarks>
    internal static string AdviceFor(
        Exception error,
        ToolDefinition? declaration = null,
        string? tool = null,
        CapabilitySelection? selection = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error switch
        {
            // A gated-off tool is not unknown, it was not selected, and the
            // difference is the whole point of the toolset gates. Saying
            // "unknown" invites a caller to conclude the server cannot do it.
            _ when declaration is null && WasNotSelected(tool) =>
                $"'{tool}' was not selected for this server{ExclusionCause(tool, selection)}. "
                + "Read tmux://capabilities for the tools that are.",
            TmuxVersionTooLowException => "This tmux is too old for that operation. "
                + "Call get_server_info to see which version is running.",
            TmuxObjectNotFoundException => "That session, window or pane no longer exists. "
                + "Call list_sessions, list_windows, or list_panes to see what does.",

            // tmux's own message is the most specific thing anybody has.
            TmuxCommandException => $"tmux refused the command: {error.Message}",

            // A refusal this server wrote already names the cause and the cure,
            // and the backstop below would tell the caller to go read a log
            // instead, contradicting the sentence immediately before it.
            McpException or LibTmuxException => error.Message,

            // Argument validation is a refusal. So is a null arriving for a
            // field the caller was invited to send — that is bad input, not a
            // broken invariant, and the declared schema is what tells the two
            // apart. A null anywhere else stays on the backstop below.
            ArgumentNullException nulled when Declares(declaration, nulled.ParamName) =>
                WithoutParameterName(nulled),
            ArgumentException argument when argument is not ArgumentNullException =>
                WithoutParameterName(argument),
            _ => $"{error.Message} This is unexpected — check the server's log on "
                + "standard error before retrying, because retrying unchanged will "
                + "most likely fail the same way.",
        };
    }

    private static void RequireDeclaredArguments(
        RequestContext<CallToolRequestParams> request,
        string tool)
    {
        if (request.Params?.Arguments is not { } arguments
            || request.Services?.GetService<CapabilityRegistry>() is not { } registry
            || !registry.OuterSchemas.TryGetValue(tool, out JsonElement schema))
        {
            return;
        }

        ToolArgumentSchema.Validate(tool, arguments, schema, "arguments");
    }

    // Selection has three knobs and the refusal used to name one of them, so
    // a tool excluded by name was reported as a disabled toolset — which the
    // capabilities resource, read in the same session, contradicted.
    private static string ExclusionCause(string? tool, CapabilitySelection? selection)
    {
        if (tool is null || selection is null)
        {
            return string.Empty;
        }

        if (selection.ExcludedNames.Contains(tool))
        {
            return $": {CapabilitySelection.ExcludeToolsVariable} names it";
        }

        ToolDefinition? definition = CapabilityRegistry.Manifest
            .FirstOrDefault(candidate => string.Equals(
                candidate.Name, tool, StringComparison.Ordinal));
        return definition is not null && !selection.Toolsets.Contains(definition.Toolset)
            ? $": its {definition.Toolset.ToString().ToLowerInvariant()} toolset is not enabled"
            : string.Empty;
    }

    /// <summary>Answers whether a tool exists but was left out of this server.</summary>
    /// <param name="tool">The tool named by the call.</param>
    /// <returns><see langword="true" /> when the name is a real but unselected tool.</returns>
    private static bool WasNotSelected(string? tool) =>
        tool is not null
        && CapabilityRegistry.Manifest.Any(candidate =>
            string.Equals(candidate.Name, tool, StringComparison.Ordinal));

    /// <summary>Answers whether a name is one the caller was invited to send.</summary>
    /// <param name="declaration">What this server declared about the tool.</param>
    /// <param name="parameter">The parameter a null arrived for.</param>
    /// <returns><see langword="true" /> when the name is a declared input field.</returns>
    private static bool Declares(ToolDefinition? declaration, string? parameter) =>
        parameter is not null && declaration?.InputSinks.ContainsKey(parameter) == true;

    /// <summary>Drops the parameter name .NET appends to an argument failure.</summary>
    /// <param name="error">The refusal.</param>
    /// <returns>The sentence without the trailing plumbing.</returns>
    /// <remarks>
    /// <see cref="ArgumentException.Message" /> ends with " (Parameter 'x')",
    /// naming a .NET parameter the caller never wrote. The tool's own argument
    /// is named in the schema, so the suffix adds nothing a caller can act on.
    /// </remarks>
    private static string WithoutParameterName(ArgumentException error)
    {
        if (error.ParamName is not string name)
        {
            return error.Message;
        }

        int marker = error.Message.IndexOf(
            $" (Parameter '{name}')",
            StringComparison.Ordinal);
        return marker < 0 ? error.Message : error.Message[..marker];
    }

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
                && tmux.Dispatch != TmuxDispatchState.NotDispatched
                && !RefusedWithoutActing(tmux);

        // The advice may already carry this warning, and saying it twice reads
        // as two different failures rather than one.
        if (!mayModify
            || !mayHaveActed
            || advice.Contains("do not retry", StringComparison.OrdinalIgnoreCase))
        {
            return advice;
        }

        // tmux's own wording is a fragment more often than a sentence, so
        // appending to it unseparated produced "index in use: 0 tmux may have
        // acted before the failure."
        string ended = advice.Length == 0 || advice[^1] is '.' or '!' or '?' or ':'
            ? advice
            : advice + ".";
        return ended
            + " tmux may have acted before the failure. Do not retry this operation."
            + " Inspect tmux state first.";
    }

    // tmux reports a refusal two ways: as a command failure, and as an option
    // failure when it rejected the name or the value. Both mean tmux declined
    // to act, so both contradict a warning that it might have.
    private static bool RefusedWithoutActing(LibTmuxException error) => error switch
    {
        TmuxCommandException command => !ChainMayHavePartlyRun(command),
        TmuxOptionException option => option.InnerException
            is not TmuxCommandException inner || !ChainMayHavePartlyRun(inner),
        _ => false,
    };

    // tmux refusing one command is evidence that it did not run it, and the
    // warning contradicted tmux's own sentence. A chain is the exception, but
    // only past its first command: tmux stops at the failure, so a chain whose
    // first command it refused has mutated nothing either.
    //
    // The vector here is the caller's own commands — the generation guard's two
    // preamble links never reach it — so more than one link means more than one
    // command was dispatched.
    private static bool ChainMayHavePartlyRun(TmuxCommandException error) =>
        Links(error.Result.Arguments).Skip(1).Any();

    private static IEnumerable<string[]> Links(IReadOnlyList<string> arguments)
    {
        List<string> link = [];
        foreach (string argument in arguments)
        {
            if (string.Equals(argument, ";", StringComparison.Ordinal))
            {
                yield return [.. link];
                link.Clear();
                continue;
            }

            link.Add(argument);
        }

        yield return [.. link];
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
