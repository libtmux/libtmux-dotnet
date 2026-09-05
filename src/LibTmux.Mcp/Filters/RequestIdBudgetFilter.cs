using System.Runtime.Versioning;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LibTmux.Mcp;

/// <summary>Rejects a request whose identifier cannot fit in a bounded reply.</summary>
[UnsupportedOSPlatform("windows")]
internal static class RequestIdBudgetFilter
{
    internal const int MaximumSerializedBytes = 512 * 1024;

    internal static McpMessageFilter Create() => next => async (context, cancellationToken) =>
    {
        if (context.JsonRpcMessage is JsonRpcRequest request
            && Utf8JsonBudget.GetByteCount(request.Id.Id, ToolJson.Options)
                > MaximumSerializedBytes)
        {
            await context.Server.SendMessageAsync(
                    new JsonRpcError
                    {
                        Id = default,
                        Error = new JsonRpcErrorDetail
                        {
                            Code = (int)McpErrorCode.InvalidRequest,
                            Message = $"Request id exceeds {MaximumSerializedBytes} bytes.",
                        },
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await next(context, cancellationToken).ConfigureAwait(false);
    };
}
