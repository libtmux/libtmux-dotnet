namespace LibTmux.UnitTests.Connection;

internal static class PsmuxSmokeEnvironment
{
    public static bool IsEnabled =>
        string.Equals(
            Environment.GetEnvironmentVariable("LIBTMUX_PSMUX_SMOKE"),
            "1",
            StringComparison.Ordinal);
}

public sealed class PsmuxProcessSmokeTests
{
    [Fact(
        Skip = "Requires an explicitly provisioned one-session psmux namespace.",
        SkipType = typeof(PsmuxSmokeEnvironment),
        SkipUnless = nameof(PsmuxSmokeEnvironment.IsEnabled))]
    public async Task Connect_and_typed_queries_use_audited_psmux()
    {
        string binary = Environment.GetEnvironmentVariable("LIBTMUX_PSMUX_BINARY")
            ?? throw new InvalidOperationException("LIBTMUX_PSMUX_BINARY is required.");
        string binarySha256 = Environment.GetEnvironmentVariable("LIBTMUX_PSMUX_SHA256")
            ?? throw new InvalidOperationException("LIBTMUX_PSMUX_SHA256 is required.");
        string dataDirectory = Environment.GetEnvironmentVariable("PSMUX_DATA_DIR")
            ?? throw new InvalidOperationException("PSMUX_DATA_DIR is required.");
        string socketName = Environment.GetEnvironmentVariable("LIBTMUX_PSMUX_NAMESPACE")
            ?? throw new InvalidOperationException("LIBTMUX_PSMUX_NAMESPACE is required.");
        string expectedText = Environment.GetEnvironmentVariable("LIBTMUX_PSMUX_EXPECTED_TEXT")
            ?? throw new InvalidOperationException("LIBTMUX_PSMUX_EXPECTED_TEXT is required.");
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        PsmuxServer server = await PsmuxServer.ConnectAsync(
            new PsmuxConnectionOptions(
                binary,
                binarySha256,
                dataDirectory,
                socketName),
            cancellationToken);

        Assert.Equal(
            TmuxVersion.Parse(LibTmux.Internal.PsmuxCompatibility.SupportedVersion),
            server.Version);
        PsmuxServer refreshed = await server.RefreshAsync(cancellationToken);
        Assert.Equal(server.Version, refreshed.Version);

        PsmuxSession session = await refreshed.GetSessionAsync(cancellationToken);
        PsmuxWindow window = Assert.Single(await session.GetWindowsAsync(cancellationToken));
        PsmuxWindow serverWindow = Assert.Single(
            await refreshed.GetWindowsAsync(cancellationToken));
        Assert.Equal(window.Id, serverWindow.Id);

        PsmuxPane pane = Assert.Single(await window.GetPanesAsync(cancellationToken));
        PsmuxPane sessionPane = Assert.Single(await session.GetPanesAsync(cancellationToken));
        PsmuxPane serverPane = Assert.Single(await refreshed.GetPanesAsync(cancellationToken));
        Assert.Equal(pane.Id, sessionPane.Id);
        Assert.Equal(pane.Id, serverPane.Id);
        IReadOnlyList<string> captured = await pane.CaptureAsync(
            new PsmuxCaptureOptions(joinWrappedLines: true),
            cancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(session.Name));
        Assert.True(window.Width > 0);
        Assert.True(window.Height > 0);
        Assert.True(pane.Width > 0);
        Assert.True(pane.Height > 0);
        Assert.Contains(captured, line => line.Contains(expectedText, StringComparison.Ordinal));
    }
}
