using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace LibTmux.Examples;

/// <summary>Runs the example suite selected on the command line.</summary>
/// <remarks>
/// The same list <c>LibTmux.ExampleTests</c> runs, on the console instead of
/// in a test report.
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args is ["--psmux"])
        {
            Console.OutputEncoding = new UTF8Encoding(false, true);
            await Snippets.Psmux.QueryPsmux();
            return 0;
        }

        if (args is ["--arena-one-shot"])
        {
            if (OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine("The tmux arena requires Linux or macOS.");
                return 1;
            }

            return await RunArenaOneShotAsync();
        }

        if (args.Length != 0)
        {
            Console.Error.WriteLine("usage: LibTmux.Examples [--arena-one-shot|--psmux]");
            return 2;
        }

        if (OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine(
                "The ordinary examples require tmux on Linux or macOS; use --psmux for the Windows query preview.");
            return 1;
        }

        return await RunTmuxExamplesAsync();
    }

    [UnsupportedOSPlatform("windows")]
    private static async Task<int> RunArenaOneShotAsync()
    {
        if (Environment.GetEnvironmentVariable("LIBTMUX_ARENA_DESCRIPTOR") is null or "")
        {
            ExampleCase example = ExampleCase.Discover().Single(
                example => string.Equals(
                    $"{example.Topic}.{example.Id}",
                    "OneShot.ConnectAndBuild",
                    StringComparison.Ordinal));
            await example.RunAsync();
            return 0;
        }

        ArenaContract? contract = ArenaContract.Read();
        if (contract is null)
        {
            return 2;
        }

        try
        {
            Server server = await Server.ConnectAsync(
                new ServerConnectionOptions(
                    tmuxBinaryPath: contract.TmuxBinaryPath,
                    socketPath: contract.SocketPath));
            await Snippets.OneShot.BuildHierarchy(server);

            IReadOnlyList<string>? identity = await server.DisplayMessageAsync(
                new DisplayMessageRequest("#{pid}\t#{socket_path}", returnText: true));
            if (identity is not [string value])
            {
                throw new InvalidOperationException("tmux did not report one arena server identity.");
            }

            string[] parts = value.Split('\t');
            if (parts.Length != 2
                || !int.TryParse(
                    parts[0],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int processId)
                || processId <= 0
                || !string.Equals(parts[1], contract.SocketPath, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("tmux reported an invalid arena server identity.");
            }

            IReadOnlyList<TmuxOption> options = await server.Options.GetAsync(
                new GetOptionRequest(
                    "@libtmux_arena_challenge",
                    OptionScope.Session,
                    global: true,
                    quiet: true));
            if (options is not [{ Value.Raw: string challenge }] || string.IsNullOrWhiteSpace(challenge))
            {
                throw new InvalidOperationException("tmux did not report an arena challenge.");
            }

            string evidence = JsonSerializer.Serialize(
                new
                {
                    schema = 1,
                    server_pid = processId,
                    socket_path = parts[1],
                    challenge,
                    artifact = contract.Artifact,
                });
            Console.WriteLine($"LIBTMUX_ARENA_EVIDENCE={evidence}");
            return 0;
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            Console.Error.WriteLine(failure.Message);
            return 1;
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static async Task<int> RunTmuxExamplesAsync()
    {
        int failed = 0;
        foreach (ExampleCase example in ExampleCase.Discover())
        {
            Console.WriteLine();
            Console.WriteLine($"── {example.Topic}.{example.Id} — {example.Title}");
            long started = Stopwatch.GetTimestamp();
            try
            {
                await example.RunAsync();
                Console.WriteLine(
                    $"   ok ({Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms)");
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                failed++;
                Console.Error.WriteLine($"   failed: {failure.Message}");
            }
        }

        Console.WriteLine();
        return failed == 0 ? 0 : 1;
    }

    private sealed record ArenaContract(string Artifact, string SocketPath, string TmuxBinaryPath)
    {
        public static ArenaContract? Read()
        {
            string? artifact = Environment.GetEnvironmentVariable("LIBTMUX_ARENA_ARTIFACT");
            string? socketPath = Environment.GetEnvironmentVariable("LIBTMUX_SOCKET_PATH");
            string? tmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX_BIN");
            if (string.IsNullOrWhiteSpace(artifact)
                || string.IsNullOrWhiteSpace(socketPath)
                || string.IsNullOrWhiteSpace(tmuxBinaryPath)
                || !Path.IsPathFullyQualified(tmuxBinaryPath)
                || !string.Equals(artifact, "OneShot.ConnectAndBuild", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("The arena contract is incomplete or mismatched.");
                return null;
            }

            return new ArenaContract(artifact, socketPath, tmuxBinaryPath);
        }
    }
}
