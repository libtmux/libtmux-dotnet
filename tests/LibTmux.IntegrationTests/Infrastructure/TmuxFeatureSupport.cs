using System.Runtime.Versioning;

namespace LibTmux.IntegrationTests.Infrastructure;

/// <summary>Skips a test whose feature the running tmux does not promise.</summary>
/// <remarks>
/// Each requirement is a per-feature promise rather than a floor, so a test
/// that needs one skips on an older tmux while every ordinary test still runs.
/// </remarks>
[UnsupportedOSPlatform("windows")]
internal static class TmuxFeatureSupport
{
    private static readonly Lazy<TmuxVersion> Running = new(() =>
    {
        string? configured = Environment.GetEnvironmentVariable("LIBTMUX_TMUX");
        return TmuxVersion.DetectAsync(string.IsNullOrWhiteSpace(configured) ? "tmux" : configured)
            .GetAwaiter()
            .GetResult();
    });

    /// <summary>Gets the version of the tmux executable the tests run against.</summary>
    internal static TmuxVersion Version => Running.Value;

    /// <summary>Skips unless library-managed control clients are promised.</summary>
    internal static void RequireControl() =>
        Assert.SkipWhen(
            Version < TmuxVersion.Parse("3.7"),
            "Library-managed control clients need tmux 3.7.");

    /// <summary>Skips unless named buffers can be deleted exactly.</summary>
    internal static void RequireNamedBuffers() =>
        Assert.SkipWhen(
            Version < TmuxVersion.Parse("3.4"),
            "Staging and cleaning up a named buffer need tmux 3.4.");

    /// <summary>Skips unless a foreground run-shell returns its output.</summary>
    internal static void RequireCapturedRunShell() =>
        Assert.SkipWhen(
            Version < TmuxVersion.Parse("3.5"),
            "A returned run-shell output needs tmux 3.5.");
}
