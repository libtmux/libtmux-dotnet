using BenchmarkDotNet.Running;

namespace LibTmux.Benchmarks;

internal static class Program
{
    private static async Task Main(string[] arguments)
    {
        if (arguments.Length == 2 && arguments[0] is "--topology-probe" or "--topology-probe-smoke")
        {
            if (OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("The topology probe requires tmux on Unix.");
            }

            await TopologyModeProbe.RunAsync(
                arguments[1], smoke: arguments[0] == "--topology-probe-smoke").ConfigureAwait(false);
            return;
        }

        if (arguments.Length == 2 && arguments[0] is "--stream-probe" or "--stream-probe-smoke")
        {
            if (OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("The streaming probe requires tmux on Unix.");
            }

            StreamingProbeOptions? options = arguments[0] == "--stream-probe-smoke"
                ? new StreamingProbeOptions
                {
                    SamplesPerPayload = 1,
                    WarmupsPerPayload = 0,
                    OverflowSamples = 1,
                    DisposalSamples = 1,
                    OverflowBufferCapacity = 4,
                    OverflowNotifications = 16,
                }
                : null;
            await StreamingProbe.RunAsync(arguments[1], options).ConfigureAwait(false);
            return;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(arguments);
    }
}
