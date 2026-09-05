using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux.Mcp;

/// <content>Failure semantics shared by composite MCP mutations.</content>
[UnsupportedOSPlatform("windows")]
internal sealed partial class WriteTools
{
    private static async Task MutateAsync(
        TmuxMutationSequence sequence,
        Func<Task> mutation,
        string ambiguousMessage)
    {
        try
        {
            await sequence.MutateAsync(mutation).ConfigureAwait(false);
        }
        catch (TmuxOperationCanceledException error) when (error.CommandMayHaveExecuted)
        {
            throw new LibTmuxException(
                ambiguousMessage,
                TmuxDispatchState.Unknown,
                error);
        }
    }
}
