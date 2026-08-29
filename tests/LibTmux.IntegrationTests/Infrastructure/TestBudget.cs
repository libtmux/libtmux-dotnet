namespace LibTmux.IntegrationTests.Infrastructure;

/// <summary>How long a test waits for tmux to reach a state it expects.</summary>
internal static class TestBudget
{
    /// <summary>The budget a poll expecting success is given.</summary>
    /// <remarks>
    /// Every one of these waits for something tmux is about to do, so the
    /// budget only has to outlast a slow machine. Ten seconds did not: four
    /// different tests failed at that mark while the suite ran beside a build,
    /// each looking like a distinct bug.
    /// </remarks>
    internal static readonly TimeSpan Settle = TimeSpan.FromSeconds(60);
}
