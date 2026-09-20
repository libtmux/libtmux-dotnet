using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

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

        bool smoke = args is ["--smoke"];
        if (args.Length != 0 && !smoke)
        {
            Console.Error.WriteLine("Usage: LibTmux.Examples [--smoke | --psmux].");
            return 2;
        }

        if (OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine(
                "The ordinary examples require tmux on Linux or macOS; use --psmux for the Windows query preview.");
            return 1;
        }

        return await RunTmuxExamplesAsync(smoke);
    }

    [UnsupportedOSPlatform("windows")]
    private static async Task<int> RunTmuxExamplesAsync(bool smoke)
    {
        int failed = 0;
        foreach (ExampleCase example in ExampleCase.Discover().Take(smoke ? 1 : int.MaxValue))
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
}
