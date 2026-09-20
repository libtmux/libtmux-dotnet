using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;

namespace LibTmux.IntegrationTests.Parity;

[UnsupportedOSPlatform("windows")]
public sealed class Component02ParityTests
{
    public static TheoryData<string> OwnedRows =>
    [
        "libtmux.pane:Pane.from_pane_id",
        "libtmux.pane:Pane.id",
        "libtmux.server:Server.colors",
        "libtmux.server:Server.config_file",
        "libtmux.server:Server.socket_name",
        "libtmux.server:Server.socket_path",
        "libtmux.server:Server.tmux_bin",
        "libtmux.session:Session.from_session_id",
        "libtmux.session:Session.id",
        "libtmux.window:Window.from_window_id",
        "libtmux.window:Window.id",
    ];

    [Theory(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    [MemberData(nameof(OwnedRows))]
    public async Task Owned_parity_row_has_production_behavior(string pythonSymbolId)
    {
        await using RawTmuxTestContext context = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        var options = new ServerConnectionOptions
        {
            TmuxBinaryPath = context.TmuxBinaryPath,
            SocketPath = context.SocketPath,
            ConfigurationFile = "/dev/null",
            ColorMode = TmuxColorMode.Colors256,
        };
        Server server = await Server.ConnectAsync(
            options,
            TestContext.Current.CancellationToken);

        (string observed, string expected) = pythonSymbolId switch
        {
            "libtmux.pane:Pane.from_pane_id" => (
                (await server.GetPaneAsync(
                    new PaneId(0),
                    TestContext.Current.CancellationToken)).Id.ToString(),
                "%0"),
            "libtmux.pane:Pane.id" => (
                (await server.GetPaneAsync(
                    new PaneId(0),
                    TestContext.Current.CancellationToken)).Id.ToString(),
                "%0"),
            "libtmux.server:Server.colors" => (
                server.ConnectionOptions.ColorMode.ToString(),
                "Colors256"),
            "libtmux.server:Server.config_file" => (
                server.ConnectionOptions.ConfigurationFile!,
                "/dev/null"),
            "libtmux.server:Server.socket_name" => (
                Server.Open(new ServerConnectionOptions { SocketName = "parity-name" })
                    .ConnectionOptions.SocketName!,
                "parity-name"),
            "libtmux.server:Server.socket_path" => (
                server.ConnectionOptions.SocketPath!,
                context.SocketPath),
            "libtmux.server:Server.tmux_bin" => (
                server.ConnectionOptions.TmuxBinaryPath,
                context.TmuxBinaryPath),
            "libtmux.session:Session.from_session_id" => (
                (await server.GetSessionAsync(
                    new SessionId(0),
                    TestContext.Current.CancellationToken)).Id.ToString(),
                "$0"),
            "libtmux.session:Session.id" => (
                (await server.GetSessionAsync(
                    new SessionId(0),
                    TestContext.Current.CancellationToken)).Id.ToString(),
                "$0"),
            "libtmux.window:Window.from_window_id" => (
                (await server.GetWindowAsync(
                    new WindowId(0),
                    TestContext.Current.CancellationToken)).Id.ToString(),
                "@0"),
            "libtmux.window:Window.id" => (
                (await server.GetWindowAsync(
                    new WindowId(0),
                    TestContext.Current.CancellationToken)).Id.ToString(),
                "@0"),
            _ => throw new InvalidOperationException($"Unexpected parity row: {pythonSymbolId}"),
        };

        Assert.Equal(expected, observed);
        TmuxCommandResult alive = await server.ExecuteCommandAsync(
            ["display-message", "-p", "#{pid}:#{start_time}"],
            TestContext.Current.CancellationToken);
        Assert.Equal(0, alive.ExitCode);
        Assert.Single(alive.StandardOutputLines);
    }
}
