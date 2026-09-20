using System.Runtime.Versioning;

namespace LibTmux.IntegrationTests.Infrastructure;

/// <summary>Waits for the first entity in a hierarchy a test arranged.</summary>
[UnsupportedOSPlatform("windows")]
internal static class TestHierarchy
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    internal static Task<Session> RequireFirstSessionAsync(
        Server server,
        CancellationToken cancellationToken) =>
        RequireFirstAsync(
            () => server.GetSessionsAsync(cancellationToken),
            "sessions",
            cancellationToken);

    internal static Task<Window> RequireFirstWindowAsync(
        Session session,
        CancellationToken cancellationToken) =>
        RequireFirstAsync(
            () => session.GetWindowsAsync(cancellationToken),
            "windows",
            cancellationToken);

    internal static Task<Pane> RequireFirstPaneAsync(
        Window window,
        CancellationToken cancellationToken) =>
        RequireFirstAsync(
            () => window.GetPanesAsync(cancellationToken),
            "panes",
            cancellationToken);

    private static async Task<T> RequireFirstAsync<T>(
        Func<Task<IReadOnlyList<T>>> read,
        string relation,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + Patience;
        while (true)
        {
            IReadOnlyList<T> items = await read().ConfigureAwait(false);
            if (items.Count > 0)
            {
                return items[0];
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new InvalidOperationException(
                    $"tmux reported no {relation} for a hierarchy this test arranged.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
