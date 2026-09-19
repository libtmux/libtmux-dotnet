using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;
using Microsoft.Extensions.Logging;

namespace LibTmux.IntegrationTests.Diagnostics;

[UnsupportedOSPlatform("windows")]
public sealed class StructuredLoggingTests
{
    [UnixFact]
    public async Task Records_stable_scalar_context_without_payload_leakage()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        RecordingLogger logger = new();
        Server server = await ConnectAsync(raw, token, logger);
        Session session = await TestHierarchy.RequireFirstSessionAsync(server, token);

        // Every tmux command passes through one dispatcher, so one command is
        // recorded once rather than once per layer that helped send it.
        logger.Clear();
        await session.Options.SetAsync(new SetOptionRequest("@logged", "yes"), token);
        IReadOnlyList<Recorded> setting = logger.Entries
            .Where(entry => entry.Subcommand == "set-option")
            .ToArray();
        Assert.Single(setting);

        // The fields are scalars a log aggregator can filter and group on.
        Recorded recorded = setting[0];
        Assert.Equal(LogLevel.Debug, recorded.Level);
        Assert.Equal(0, recorded.ExitCode);
        Assert.Contains("set-option", recorded.CommandLine, StringComparison.Ordinal);

        // A failure is recorded at a level that survives a production filter,
        // and says what tmux objected to.
        logger.Clear();
        await Assert.ThrowsAsync<TmuxOptionException>(
            () => session.Options.GetAsync(new GetOptionRequest("no-such-option"), token));
        Recorded failure = Assert.Single(
            logger.Entries,
            entry => entry.Level == LogLevel.Error);
        Assert.Equal("show-options", failure.Subcommand);
        Assert.NotEqual(0, failure.ExitCode);

        // What tmux printed is kept only at debug and is capped, because a
        // capture runs to megabytes and a buffer holds whatever was copied.
        logger.Clear();
        string wide = new('x', 4096);
        await server.SetBufferAsync(wide, "libtmux-wide", cancellationToken: token);
        await server.GetBufferAsync("libtmux-wide", token);
        Assert.All(
            logger.Entries,
            entry => Assert.True(
                entry.Rendered.Length < wide.Length,
                "A log line carried the whole payload."));

        // Nothing is recorded at all when no logger was given, so a caller who
        // wants none pays for none.
        Server quiet = await ConnectAsync(raw, token);
        await quiet.Options.SetAsync(new SetOptionRequest("@unlogged", "yes"), token);
        Assert.Equal(
            "yes",
            Assert.Single(await quiet.Options.GetAsync(new GetOptionRequest("@unlogged"), token))
                .Value.Raw);
    }

    private static Task<Server> ConnectAsync(
        RawTmuxTestContext raw,
        CancellationToken token,
        ILogger? logger = null) =>
        Server.ConnectAsync(
            new ServerConnectionOptions
            {
                TmuxBinaryPath = raw.TmuxBinaryPath,
                SocketPath = raw.SocketPath,
                ConfigurationFile = "/dev/null",
                Logger = logger,
            },
            token);

    private sealed record Recorded(
        LogLevel Level,
        string? Subcommand,
        int ExitCode,
        string CommandLine,
        string Rendered);

    private sealed class RecordingLogger : ILogger
    {
        private readonly List<Recorded> _entries = [];

        public List<Recorded> Entries => _entries;

        public void Clear() => _entries.Clear();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            string? subcommand = null;
            int exitCode = 0;
            string commandLine = string.Empty;
            if (state is IReadOnlyList<KeyValuePair<string, object?>> fields)
            {
                foreach ((string key, object? value) in fields)
                {
                    switch (key)
                    {
                        case "TmuxSubcommand":
                            subcommand = value?.ToString();
                            break;
                        case "TmuxExitCode":
                            exitCode = value is int code ? code : 0;
                            break;
                        case "TmuxCmd":
                            commandLine = value?.ToString() ?? string.Empty;
                            break;
                        default:
                            break;
                    }
                }
            }

            _entries.Add(
                new Recorded(
                    logLevel,
                    subcommand,
                    exitCode,
                    commandLine,
                    formatter(state, exception)));
        }
    }
}
