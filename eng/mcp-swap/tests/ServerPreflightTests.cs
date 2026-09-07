using System.Diagnostics;

namespace LibTmux.McpSwap.Tests;

public sealed class ServerPreflightTests
{
    [Fact]
    public void InitializeReplyIsAcceptedBeforeTheServerExitsAndItsProcessTreeIsReaped()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TestPaths paths = new();
        string pidFile = Path.Combine(paths.Root, "child.pid");
        ServerSpec spec = Shell(
            "sleep 30 & child=$!; printf '%s' \"$child\" > \"$PID_FILE\"; "
            + "printf '{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"protocolVersion\":\"2025-06-18\"}}\\n'; wait",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["PID_FILE"] = pidFile });
        Stopwatch elapsed = Stopwatch.StartNew();

        string? failure = new ServerPreflight(TimeSpan.FromSeconds(5)).Probe(spec);

        elapsed.Stop();
        Assert.Null(failure);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(3), $"preflight took {elapsed.Elapsed}");
        int child = int.Parse(File.ReadAllText(pidFile), System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(WaitUntilExited(child, TimeSpan.FromSeconds(2)), $"child process {child} survived preflight");
    }

    [Fact]
    public void SpecEnvironmentReachesTheServer()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        ServerSpec spec = Shell(
            "if [ \"$PREFLIGHT_TOKEN\" = expected ]; then "
            + "printf '{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"protocolVersion\":\"2025-06-18\"}}\\n'; fi",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PREFLIGHT_TOKEN"] = "expected",
            });

        Assert.Null(new ServerPreflight(TimeSpan.FromSeconds(2)).Probe(spec));
    }

    [Theory]
    [InlineData("{\"id\":1,\"result\":{\"protocolVersion\":\"2025-06-18\"}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":[]}")]
    public void IncompleteInitializeRepliesAreRejected(string response)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string? failure = new ServerPreflight(TimeSpan.FromSeconds(2)).Probe(
            Shell($"printf '%s\\n' '{response}'"));

        Assert.Contains("exited before answering initialize", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void TimeoutTerminatesAQuietServer()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Stopwatch elapsed = Stopwatch.StartNew();

        string? failure = new ServerPreflight(TimeSpan.FromMilliseconds(100)).Probe(
            Shell("sleep 30"));

        elapsed.Stop();
        Assert.Contains("did not answer initialize", failure, StringComparison.Ordinal);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(3), $"timeout took {elapsed.Elapsed}");
    }

    [Fact]
    public void StderrFromAnEarlyExitExplainsTheFailure()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string? failure = new ServerPreflight(TimeSpan.FromSeconds(2)).Probe(
            Shell("printf 'broken server\\n' >&2; exit 23"));

        Assert.Contains("broken server", failure, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("stdout")]
    [InlineData("stderr")]
    public void OversizedServerOutputIsBounded(string stream)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        int bytes = stream == "stdout"
            ? ServerPreflight.MaximumStdoutCharacters + 1
            : ServerPreflight.MaximumStderrCharacters + 1;
        string redirect = stream == "stderr" ? " >&2" : string.Empty;
        string? failure = new ServerPreflight(TimeSpan.FromSeconds(10)).Probe(
            Shell($"head -c {bytes} /dev/zero | tr '\\000' x{redirect}"));

        Assert.Contains($"server {stream} exceeds", failure, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("stdout")]
    [InlineData("stderr")]
    public void OversizedOutputFromALongLivedServerDoesNotWaitForExit(string stream)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Stopwatch elapsed = Stopwatch.StartNew();
        string redirect = stream == "stderr" ? " >&2" : string.Empty;
        string? failure = new ServerPreflight(TimeSpan.FromSeconds(10)).Probe(
            Shell($"head -c {ServerPreflight.MaximumStdoutCharacters + 1} /dev/zero | tr '\\000' x{redirect}; sleep 30"));

        elapsed.Stop();
        Assert.Contains($"server {stream} exceeds", failure, StringComparison.Ordinal);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(3), $"preflight took {elapsed.Elapsed}");
    }

    [Fact]
    public void MissingLauncherIsReportedWithoutThrowing()
    {
        using TestPaths paths = new();
        ServerSpec spec = new(Path.Combine(paths.Root, "missing-server"));

        string? failure = new ServerPreflight(TimeSpan.FromSeconds(1)).Probe(spec);

        Assert.Contains("could not launch", failure, StringComparison.Ordinal);
    }

    private static ServerSpec Shell(
        string command,
        IReadOnlyDictionary<string, string>? environment = null) =>
        new("/bin/sh", ["-c", "IFS= read -r request; " + command], environment);

    private static bool WaitUntilExited(int processId, TimeSpan timeout)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < timeout)
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }

            Thread.Sleep(20);
        }

        return false;
    }
}
