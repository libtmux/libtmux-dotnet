using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Workspace;

namespace LibTmux.IntegrationTests;

[UnsupportedOSPlatform("windows")]
public sealed class WorkspaceHostScriptTests
{
    [UnixFact]
    public async Task Host_environment_and_directory_are_explicit_and_frozen()
    {
        await using HostFixture fixture = new();
        string secret = $"LIBTMUX_HOST_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(secret, "must-not-leak");
        try
        {
            Dictionary<string, string> environment = new() { ["ONLY"] = "original" };
            WorkspaceHostCommand command = new(
                $"printf '%s|%s|%s|' \"$PWD\" \"$ONLY\" \"${{{secret}-missing}}\"; if read value; then printf input; else printf closed; fi",
                fixture.Directory, 1024, TimeSpan.FromSeconds(1), environment);
            environment["ONLY"] = "changed";

            WorkspaceHostResult result = await WorkspaceHostScript.RunAsync(command, TestContext.Current.CancellationToken);

            Assert.Equal($"{fixture.Directory}|original|missing|closed", result.StandardOutput);
            Assert.True(result.Started);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(0, result.DroppedOutputBytes);
            Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)command.Environment).Clear());
        }
        finally
        {
            Environment.SetEnvironmentVariable(secret, null);
        }
    }

    [UnixFact]
    public async Task Both_pipes_drain_after_the_combined_utf8_capture_budget_fills()
    {
        await using HostFixture fixture = new();
        const string output = "観測-é\n";
        const int repetitions = 12000;
        WorkspaceHostCommand command = fixture.Command(
            "i=0; while [ \"$i\" -lt 12000 ]; do printf '観測-é\\n'; printf '観測-é\\n' >&2; i=$((i+1)); done", maxOutputBytes: 257);

        WorkspaceHostResult result = await WorkspaceHostScript.RunAsync(command, TestContext.Current.CancellationToken);

        int retained = Encoding.UTF8.GetByteCount(result.StandardOutput) + Encoding.UTF8.GetByteCount(result.StandardError);
        Assert.InRange(retained, 1, 257);
        Assert.DoesNotContain('\uFFFD', result.StandardOutput);
        Assert.DoesNotContain('\uFFFD', result.StandardError);
        Assert.Equal((long)Encoding.UTF8.GetByteCount(output) * repetitions * 2 - retained, result.DroppedOutputBytes);
        Assert.Equal(0, result.ExitCode);
    }

    [UnixFact]
    public async Task Nonzero_exit_retains_both_output_streams_and_original_failure()
    {
        await using HostFixture fixture = new();
        WorkspaceHostFailureException failure = await Assert.ThrowsAsync<WorkspaceHostFailureException>(() =>
            WorkspaceHostScript.RunAsync(fixture.Command("printf partial; printf problem >&2; exit 7"), TestContext.Current.CancellationToken));

        Assert.Equal(7, failure.Result.ExitCode);
        Assert.True(failure.Result.Started);
        Assert.Equal("partial", failure.Result.StandardOutput);
        Assert.Equal("problem", failure.Result.StandardError);
        Assert.NotNull(failure.InnerException);
    }

    [UnixFact]
    public async Task Precancellation_never_starts_a_host_process()
    {
        await using HostFixture fixture = new();
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        WorkspaceHostCanceledException failure = await Assert.ThrowsAsync<WorkspaceHostCanceledException>(() =>
            WorkspaceHostScript.RunAsync(fixture.Command(": > ready"), cancellation.Token));

        Assert.False(failure.Result.Started);
        Assert.Null(failure.Result.ExitCode);
        Assert.False(File.Exists(Path.Combine(fixture.Directory, "ready")));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
    }

    [UnixFact]
    public async Task Cancellation_stops_the_owned_shell_tree_and_keeps_captured_output()
    {
        await using HostFixture fixture = new();
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task<WorkspaceHostResult> execution = WorkspaceHostScript.RunAsync(fixture.Command(
            "mkfifo block; cat block & child=$!; printf '%s' \"$child\" > child; printf '%s' \"$$\" > shell; printf '%131072s' partial; : > ready; wait \"$child\""), cancellation.Token);
        await fixture.ReadyAsync(execution);
        Stopwatch elapsed = Stopwatch.StartNew();

        await cancellation.CancelAsync();
        WorkspaceHostCanceledException failure = await Assert.ThrowsAsync<WorkspaceHostCanceledException>(() => execution);

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1));
        Assert.True(failure.Result.Started);
        Assert.NotNull(failure.Result.ExitCode);
        Assert.NotEmpty(failure.Result.StandardOutput);
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        await fixture.AssertProcessesExitedAsync();
    }

    [UnixFact]
    public async Task Timeout_bounds_inherited_pipes_after_the_shell_has_exited()
    {
        // The persistent marker must suffice when its watcher continuation arrives late.
        await using HostFixture fixture = new(publishReadyNotification: false);
        Task<WorkspaceHostResult> execution = WorkspaceHostScript.RunAsync(fixture.Command(
            "mkfifo block; cat block & printf '%s' \"$!\" > child; printf before-exit; : > ready; exit 0",
            timeout: TimeSpan.FromMilliseconds(250)), TestContext.Current.CancellationToken);
        await fixture.ReadyAsync(execution.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));

        WorkspaceHostFailureException failure = await Assert.ThrowsAsync<WorkspaceHostFailureException>(() =>
            execution.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));

        Assert.IsType<TimeoutException>(failure.InnerException);
        Assert.True(failure.Result.Started);
        Assert.Equal(0, failure.Result.ExitCode);
        Assert.Equal("before-exit", failure.Result.StandardOutput);
    }

    [UnixFact]
    public async Task Readiness_does_not_accept_execution_failure_without_its_persistent_marker()
    {
        await using HostFixture fixture = new(publishReadyNotification: false);
        WorkspaceHostFailureException expected = new(new WorkspaceHostResult(true, 0, "before-exit", string.Empty, 0),
            new TimeoutException("The host deadline expired before readiness."));
        Task<WorkspaceHostResult> execution = Task.FromException<WorkspaceHostResult>(expected);

        WorkspaceHostFailureException actual = await Assert.ThrowsAsync<WorkspaceHostFailureException>(() => fixture.ReadyAsync(execution));

        Assert.Same(expected, actual);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4294967295)]
    public void Invalid_timeouts_are_rejected_before_process_admission(long milliseconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorkspaceHostCommand(
            "true", Path.GetTempPath(), 1, TimeSpan.FromMilliseconds(milliseconds)));

    [Fact]
    public void Invalid_script_directory_and_capture_budget_are_rejected_before_admission()
    {
        Assert.Throws<ArgumentException>(() => new WorkspaceHostCommand(" ", Path.GetTempPath(), 1, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentException>(() => new WorkspaceHostCommand("bad\0value", Path.GetTempPath(), 1, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentException>(() => new WorkspaceHostCommand("true", "relative", 1, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorkspaceHostCommand("true", Path.GetTempPath(), 0, TimeSpan.FromSeconds(1)));
    }

    private sealed class HostFixture : IAsyncDisposable
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly FileSystemWatcher _watcher;
        private readonly List<Process> _processes = [];

        internal HostFixture(bool publishReadyNotification = true)
        {
            Directory = System.IO.Directory.CreateTempSubdirectory("libtmux-host-").FullName;
            _watcher = new FileSystemWatcher(Directory, "ready");
            _watcher.Created += (_, _) =>
            {
                if (publishReadyNotification) _ready.TrySetResult();
            };
            _watcher.EnableRaisingEvents = true;
        }

        internal string Directory { get; }

        internal WorkspaceHostCommand Command(string script, int maxOutputBytes = 1024, TimeSpan? timeout = null) =>
            new(script, Directory, maxOutputBytes, timeout ?? TimeSpan.FromSeconds(1),
                new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin" });

        internal async Task ReadyAsync(Task<WorkspaceHostResult> execution)
        {
            try
            {
                Task observed = await Task.WhenAny(_ready.Task, execution).WaitAsync(TestContext.Current.CancellationToken);
                if (observed == execution && !File.Exists(Path.Combine(Directory, "ready")))
                {
                    await execution;
                    Assert.Fail("The script exited before its readiness event.");
                }
            }
            finally
            {
                // Keep fixture ownership even when execution wins or the test is cancelled.
                foreach (string name in new[] { "child", "shell" })
                {
                    string file = Path.Combine(Directory, name);
                    if (File.Exists(file))
                    {
                        int pid = int.Parse(await File.ReadAllTextAsync(file, CancellationToken.None), CultureInfo.InvariantCulture);
                        try { _processes.Add(Process.GetProcessById(pid)); }
                        catch (ArgumentException) { }
                    }
                }
            }
        }

        internal async Task AssertProcessesExitedAsync()
        {
            foreach (Process process in _processes)
            {
                await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _watcher.Dispose();
            foreach (Process process in _processes)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1));
                    }
                }
                finally
                {
                    process.Dispose();
                }
            }

            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
