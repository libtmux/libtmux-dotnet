using System.Globalization;
using System.Runtime.Versioning;

namespace LibTmux.Workspace;

internal static class PaneReadinessWaiter
{
    private const string Format =
        "#{pane_current_command}\t#{cursor_x}\t#{cursor_y}";
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    internal static string? SelectShell(
        PaneReadiness paneReadiness,
        string defaultCommand,
        string defaultShell)
    {
        if (paneReadiness == PaneReadiness.Never || defaultCommand.Length > 0)
        {
            return null;
        }

        string shellCommand = Path.GetFileName(defaultShell);
        return paneReadiness == PaneReadiness.Always
            || string.Equals(shellCommand, "zsh", StringComparison.Ordinal)
                ? shellCommand
                : null;
    }

    [UnsupportedOSPlatform("windows")]
    internal static async Task WaitAsync(
        Pane pane,
        string expectedShellCommand,
        TimeSpan timeoutInterval,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(timeoutInterval);

        try
        {
            while (true)
            {
                IReadOnlyList<string>? sample = await pane.DisplayMessageAsync(
                        new DisplayMessageRequest(returnText: true, format: Format),
                        timeout.Token)
                    .ConfigureAwait(false);
                if (IsReady(sample, expectedShellCommand))
                {
                    return;
                }

                await Task.Delay(PollInterval, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException failure) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TmuxWaitTimeoutException(
                $"Pane {pane.Id} did not reach a prompt-like state within "
                + $"{timeoutInterval.TotalSeconds:0.###} seconds.",
                timeoutInterval,
                failure);
        }
    }

    private static bool IsReady(
        IReadOnlyList<string>? sample,
        string expectedShellCommand)
    {
        if (sample is not { Count: 1 })
        {
            return false;
        }

        string[] fields = sample[0].Split('\t');
        return fields.Length == 3
            && string.Equals(fields[0], expectedShellCommand, StringComparison.Ordinal)
            && uint.TryParse(
                fields[1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out uint cursorX)
            && uint.TryParse(
                fields[2],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out uint cursorY)
            && (cursorX != 0 || cursorY != 0);
    }
}
