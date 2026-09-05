using System.Runtime.Versioning;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LibTmux.Mcp;

/// <summary>Bounds a serialized tool result to policy.</summary>
[UnsupportedOSPlatform("windows")]
internal static class ToolResponseBudgetFilter
{
    /// <summary>Builds the response-budget filter.</summary>
    internal static McpRequestFilter<CallToolRequestParams, CallToolResult> Create(
        ServerPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return next => async (request, cancellationToken) =>
        {
            CallToolResult result = await next(request, cancellationToken).ConfigureAwait(false);
            if (string.Equals(
                    request?.Params?.Name,
                    "call_read_tools_batch",
                    StringComparison.Ordinal)
                && result.IsError != true
                && TryReadBatchResult(result.StructuredContent, out ReadToolBatchResult? batch)
                && batch is not null)
            {
                ReadToolBatchResult fitted = CapabilityTools.FitBatchResponse(
                    batch.Results,
                    batch.OnError,
                    batch.StoppedAt,
                    request!.JsonRpcRequest.Id,
                    CapabilityTools.MaximumBatchResponseBytes);
                return CapabilityTools.CreateBatchToolResult(fitted);
            }

            if (Utf8JsonBudget.FitsToolResult(result, policy.MaxBytes, ToolJson.Options))
            {
                return result;
            }

            if (result.IsError != true
                && TryReadActionResult(result.StructuredContent, out ActionResult? action)
                && action is not null)
            {
                return CompletedAction(action);
            }

            string message = result.IsError == true
                ? OversizedError(request!, policy.MaxBytes)
                : $"The tool response exceeded this server's {policy.MaxBytes} UTF-8 "
                    + "byte limit. Narrow the target or result count, or raise "
                    + $"{ServerPolicy.MaxBytesVariable} and restart the MCP server.";
            return new CallToolResult
            {
                IsError = true,
                Content =
                [
                    new TextContentBlock
                    {
                        Text = message,
                    },
                ],
            };
        };
    }

    private static bool TryReadBatchResult(
        JsonElement? structuredContent,
        out ReadToolBatchResult? batch)
    {
        batch = null;
        if (structuredContent is not JsonElement structured)
        {
            return false;
        }

        try
        {
            batch = structured.Deserialize<ReadToolBatchResult>(ToolJson.Options);
            return batch is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string OversizedError(
        RequestContext<CallToolRequestParams> request,
        int maxBytes)
    {
        string tool = request.Params?.Name ?? "a tmux tool";
        return ToolMetadata.MayModify(request, tool)
            ? $"The tool failed, and its detailed error exceeded this server's "
                + $"{maxBytes} UTF-8 byte limit. tmux may have acted. Do not retry; "
                + "inspect tmux state first."
            : $"The read failed, and its detailed error exceeded this server's "
                + $"{maxBytes} UTF-8 byte limit. Narrow the target or raise "
                + $"{ServerPolicy.MaxBytesVariable} and restart the MCP server.";
    }

    private static bool TryReadActionResult(
        JsonElement? structuredContent,
        out ActionResult? action)
    {
        action = null;
        if (structuredContent is not JsonElement structured
            || structured.ValueKind != JsonValueKind.Object
            || !structured.TryGetProperty("changed", out JsonElement changed)
            || changed.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        try
        {
            action = structured.Deserialize<ActionResult>(ToolJson.Options);
            return action is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static CallToolResult CompletedAction(ActionResult original)
    {
        var acknowledgement = new ActionResult(
            "The action completed, but its detailed acknowledgement exceeded the "
            + "server response limit. Do not retry it; inspect tmux state first.",
            PaneId: ValidPaneId(original.PaneId),
            WindowId: ValidWindowId(original.WindowId),
            SessionId: ValidSessionId(original.SessionId));
        JsonElement structured = JsonSerializer.SerializeToElement(
            acknowledgement,
            ToolJson.Options);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = structured.GetRawText() }],
            StructuredContent = structured,
        };
    }

    private static string? ValidPaneId(string? value) =>
        PaneId.TryParse(value, out PaneId id) ? id.ToString() : null;

    private static string? ValidWindowId(string? value) =>
        WindowId.TryParse(value, out WindowId id) ? id.ToString() : null;

    private static string? ValidSessionId(string? value) =>
        SessionId.TryParse(value, out SessionId id) ? id.ToString() : null;
}
