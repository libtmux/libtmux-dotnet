using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Internal;
using Microsoft.Extensions.Logging;

namespace LibTmux.IntegrationTests.Parity;

[UnsupportedOSPlatform("windows")]
public sealed class Component17ParityTests
{
    public static TheoryData<string> OwnedRows =>
    [
        "libtmux._internal.query_list:logger",
        "libtmux.client:logger",
        "libtmux.common:logger",
        "libtmux.common:session_check_name",
        "libtmux.exc:<module>",
        "libtmux.exc:AdjustmentDirectionRequiresAdjustment",
        "libtmux.exc:AmbiguousOption",
        "libtmux.exc:BadSessionName",
        "libtmux.exc:DeprecatedError",
        "libtmux.exc:InvalidOption",
        "libtmux.exc:LibTmuxException",
        "libtmux.exc:MultipleActiveWindows",
        "libtmux.exc:MultipleObjectsReturned",
        "libtmux.exc:NoActiveWindow",
        "libtmux.exc:NoWindowsExist",
        "libtmux.exc:NotInsideTmux",
        "libtmux.exc:ObjectDoesNotExist",
        "libtmux.exc:OptionError",
        "libtmux.exc:PaneAdjustmentDirectionRequiresAdjustment",
        "libtmux.exc:PaneError",
        "libtmux.exc:PaneNotFound",
        "libtmux.exc:RequiresDigitOrPercentage",
        "libtmux.exc:TmuxCommandNotFound",
        "libtmux.exc:TmuxObjectDoesNotExist",
        "libtmux.exc:TmuxSessionExists",
        "libtmux.exc:UnknownColorOption",
        "libtmux.exc:UnknownOption",
        "libtmux.exc:VariableUnpackingError",
        "libtmux.exc:VersionTooLow",
        "libtmux.exc:WaitTimeout",
        "libtmux.exc:WindowAdjustmentDirectionRequiresAdjustment",
        "libtmux.exc:WindowError",
        "libtmux.hooks:logger",
        "libtmux.neo:logger",
        "libtmux.options:logger",
        "libtmux.pane:logger",
        "libtmux.pytest_plugin:logger",
        "libtmux.server:logger",
        "libtmux.session:logger",
        "libtmux.window:logger",
    ];

    [Theory(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    [MemberData(nameof(OwnedRows))]
    public async Task Owned_parity_row_has_diagnostic_behavior(string pythonSymbolId)
    {
        bool proved = pythonSymbolId switch
        {
            "libtmux.exc:<module>" => ProvesExceptionsShareARoot(),
            "libtmux.common:session_check_name" => ProvesNameChecking(),
            _ when pythonSymbolId.EndsWith(":logger", StringComparison.Ordinal) =>
                await ProvesOneRecorderAsync(),
            _ => ProvesReplacement(pythonSymbolId),
        };

        Assert.True(proved, $"Parity behavior was not proved for {pythonSymbolId}.");
    }

    private static bool ProvesExceptionsShareARoot()
    {
        // Every libtmux failure shares one base, except a stale handle (an
        // InvalidOperationException) and a timed-out wait (a TimeoutException).
        Dictionary<string, Type> byDesign = new(StringComparer.Ordinal)
        {
            [nameof(StaleServerGenerationException)] = typeof(InvalidOperationException),
            [nameof(TmuxWaitTimeoutException)] = typeof(TimeoutException),
        };

        foreach (Type type in typeof(LibTmuxException).Assembly.GetExportedTypes()
            .Where(candidate => candidate.IsSubclassOf(typeof(Exception))))
        {
            if (byDesign.TryGetValue(type.Name, out Type? expected))
            {
                Assert.True(
                    expected.IsAssignableFrom(type),
                    $"{type.Name} no longer answers to {expected.Name}.");
                continue;
            }

            Assert.True(
                typeof(LibTmuxException).IsAssignableFrom(type)
                    || typeof(OperationCanceledException).IsAssignableFrom(type),
                $"{type.Name} is neither a libtmux failure nor a cancellation.");
        }

        return true;
    }

    private static bool ProvesNameChecking()
    {
        // tmux reads a colon or a full stop in a target as a separator, so a
        // session named with one could never be addressed again.
        Assert.Throws<ArgumentException>(() => SessionName.Validate("has:colon"));
        Assert.Throws<ArgumentException>(() => SessionName.Validate("has.dot"));
        Assert.Throws<ArgumentException>(() => SessionName.Validate(" "));
        Assert.Throws<ArgumentNullException>(() => SessionName.Validate(null));
        return SessionName.Validate("ordinary") == "ordinary";
    }

    private static bool ProvesReplacement(string pythonSymbolId)
    {
        // Every Python failure this library does not carry names the one that
        // took its place, so a reader coming from Python is told where to look.
        string replacement = Assert.IsType<string>(
            SupportedAliases.Replacement(pythonSymbolId));
        return replacement.Length > 0;
    }

    private static async Task<bool> ProvesOneRecorderAsync()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        CountingLogger logger = new();
        Server server = await Server.ConnectAsync(
            new ServerConnectionOptions
            {
                TmuxBinaryPath = raw.TmuxBinaryPath,
                SocketPath = raw.SocketPath,
                ConfigurationFile = "/dev/null",
                Logger = logger,
            },
            token);

        // Every tmux command passes through one dispatcher, so one recorder
        // covers them all and logs a command once, not once per helper module.
        logger.Clear();
        Session session = await TestHierarchy.RequireFirstSessionAsync(server, token);
        await session.Options.SetAsync(new SetOptionRequest("@recorded", "yes"), token);
        await session.Hooks.SetAsync(
            new SetHookRequest("alert-bell", "display-message rang"),
            token);
        await server.SetBufferAsync("recorded", "libtmux-recorded", cancellationToken: token);

        Assert.Contains("set-option", logger.Subcommands);
        Assert.Contains("set-hook", logger.Subcommands);
        Assert.Contains("set-buffer", logger.Subcommands);
        return logger.Subcommands.Count(name => name == "set-buffer") == 1;
    }

    private sealed class CountingLogger : ILogger
    {
        private readonly List<string> _subcommands = [];

        public List<string> Subcommands => _subcommands;

        public void Clear() => _subcommands.Clear();

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
            if (state is not IReadOnlyList<KeyValuePair<string, object?>> fields)
            {
                return;
            }

            foreach ((string key, object? value) in fields)
            {
                if (key == "TmuxSubcommand" && value?.ToString() is string subcommand)
                {
                    _subcommands.Add(subcommand);
                }
            }
        }
    }
}
