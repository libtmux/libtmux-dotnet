using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LibTmux.Workspace;
using Microsoft.Win32.SafeHandles;

namespace LibTmux.IntegrationTests;

[CollectionDefinition("Owned process handles", DisableParallelization = true)]
public sealed class OwnedProcessHandlesTests;

internal static class OwnedProcessEnvironment
{
    public static bool Available => WorkspaceOwnedProcessTree.Available();
}

[Collection("Owned process handles")]
[UnsupportedOSPlatform("windows")]
public sealed class WorkspaceOwnedProcessTreeTests
{
    private const string Unavailable = "Linux pidfds and matching procfs are required.";

    [Theory(Skip = Unavailable, SkipType = typeof(OwnedProcessEnvironment), SkipUnless = nameof(OwnedProcessEnvironment.Available))]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unavailable_preflight_falls_back_before_stopping_and_preserves_cancellation(bool denied)
    {
        await using TreeFixture fixture = new();
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        int preflights = 0;
        int stopped = 0;
        int descriptors = Pidfds();
        Task<WorkspaceHostResult> execution = fixture.Run(cancellation.Token, new()
        {
            BeforePreflightRead = () =>
            {
                preflights++;
                throw denied ? new UnauthorizedAccessException("Injected denied procfs.") : new IOException("Injected unreadable procfs.");
            },
            AfterStopped = _ => stopped++,
        });
        await fixture.ReadyAsync(execution);

        await cancellation.CancelAsync();
        WorkspaceHostCanceledException failure = await Assert.ThrowsAsync<WorkspaceHostCanceledException>(() => execution);

        Assert.Equal(1, preflights);
        Assert.Equal(0, stopped);
        Assert.IsAssignableFrom<OperationCanceledException>(failure.InnerException);
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.NotEmpty(failure.Result.StandardOutput);
        await fixture.AssertExitedAsync();
        Assert.False(fixture.Borrowed.HasExited);
        Assert.Equal(descriptors, Pidfds());
    }

    [Theory(Skip = Unavailable, SkipType = typeof(OwnedProcessEnvironment), SkipUnless = nameof(OwnedProcessEnvironment.Available))]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lost_admitted_descendants_remain_uncertain_after_fallback_even_if_root_exited(bool reap)
    {
        await using TreeFixture fixture = new(grandchild: true);
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        int descriptors = Pidfds();
        Task<WorkspaceHostResult> execution = fixture.Run(cancellation.Token, new()
        {
            AfterAdmitted = pid =>
            {
                if (pid != fixture.Child.Id)
                    return;
                KillAndObserveExit(fixture.Child);
                Assert.NotEqual(pid, ParentPid(fixture.Grandchild!.Id));
                if (reap)
                {
                    Signal(fixture.Root.Id, 18);
                    Assert.True(fixture.Root.WaitForExit(1000));
                    Assert.False(File.Exists($"/proc/{pid}/stat"));
                }
            },
        });
        await fixture.ReadyAsync(execution);

        await cancellation.CancelAsync();
        WorkspaceHostCanceledException failure = await Assert.ThrowsAsync<WorkspaceHostCanceledException>(() => execution);

        Assert.Contains("cleanup coverage is unknown", failure.InnerException!.ToString(), StringComparison.Ordinal);
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.NotEmpty(failure.Result.StandardOutput);
        Assert.False(fixture.Borrowed.HasExited);
        Assert.False(fixture.Grandchild!.HasExited);
        Assert.NotEqual('T', State(fixture.Root.Id));
        await fixture.Root.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.Equal(descriptors, Pidfds());
    }

    [Fact(Skip = Unavailable, SkipType = typeof(OwnedProcessEnvironment), SkipUnless = nameof(OwnedProcessEnvironment.Available))]
    public async Task Failure_after_stop_is_retained_while_fallback_reaps_the_owned_tree()
    {
        await using TreeFixture fixture = new();
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        int stopped = 0;
        int descriptors = Pidfds();
        Task<WorkspaceHostResult> execution = fixture.Run(cancellation.Token, new()
        {
            AfterStopped = _ =>
            {
                if (++stopped == 2)
                    throw new IOException("Injected after both owned processes stopped.");
            },
        });
        await fixture.ReadyAsync(execution);

        await cancellation.CancelAsync();
        WorkspaceHostCanceledException failure = await Assert.ThrowsAsync<WorkspaceHostCanceledException>(() => execution);

        Assert.Equal(2, stopped);
        Assert.Contains("Injected after both owned processes stopped.", failure.InnerException!.ToString(), StringComparison.Ordinal);
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.NotEmpty(failure.Result.StandardOutput);
        await fixture.AssertExitedAsync();
        Assert.False(fixture.Borrowed.HasExited);
        Assert.Equal(descriptors, Pidfds());
    }

    [Fact(Skip = Unavailable, SkipType = typeof(OwnedProcessEnvironment), SkipUnless = nameof(OwnedProcessEnvironment.Available))]
    public async Task Native_failure_resumes_every_stopped_process_and_releases_handles()
    {
        await using TreeFixture fixture = new();
        await fixture.StartDirectAsync();
        List<int> stopped = [];
        int descriptors = Pidfds();

        IOException failure = await Assert.ThrowsAsync<IOException>(() => WorkspaceOwnedProcessTree.TryKillAsync(fixture.Root, new()
        {
            AfterStopped = pid =>
            {
                stopped.Add(pid);
                if (stopped.Count == 2)
                    throw new IOException("Injected discovery failure.");
            },
        }));

        Assert.Equal("Injected discovery failure.", failure.Message);
        Assert.Equal(2, stopped.Count);
        Assert.All(stopped, pid => Assert.DoesNotContain(State(pid), new char?[] { 'T', 't' }));
        Assert.False(fixture.Root.HasExited);
        Assert.False(fixture.Child.HasExited);
        Assert.False(fixture.Borrowed.HasExited);
        Assert.Equal(descriptors, Pidfds());
    }

    [Fact(Skip = Unavailable, SkipType = typeof(OwnedProcessEnvironment), SkipUnless = nameof(OwnedProcessEnvironment.Available))]
    public async Task Native_cleanup_rejects_a_foreign_candidate_and_does_not_capture_the_callers_context()
    {
        await using TreeFixture fixture = new();
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        List<int> admitted = [];
        int candidates = 0;
        int descriptors = Pidfds();
        SynchronizationContext? previous = SynchronizationContext.Current;
        Task<WorkspaceHostResult> execution;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new RejectingContext());
            execution = fixture.Run(cancellation.Token, new()
            {
                ExtraCandidates = pid =>
                {
                    Assert.Null(SynchronizationContext.Current);
                    candidates++;
                    return pid == fixture.Root.Id ? [fixture.Borrowed.Id] : [];
                },
                AfterAdmitted = pid => admitted.Add(pid),
            });
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
        await fixture.ReadyAsync(execution);

        await cancellation.CancelAsync();
        WorkspaceHostCanceledException failure = await Assert.ThrowsAsync<WorkspaceHostCanceledException>(() => execution);

        Assert.Equal(2, candidates);
        Assert.Equal([fixture.Child.Id], admitted);
        Assert.IsAssignableFrom<OperationCanceledException>(failure.InnerException);
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.NotEmpty(failure.Result.StandardOutput);
        await fixture.AssertExitedAsync();
        Assert.False(fixture.Borrowed.HasExited);
        Assert.Equal(descriptors, Pidfds());
    }

    private sealed class RejectingContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) =>
            throw new InvalidOperationException("Host cleanup captured the caller's synchronization context.");
    }

    private sealed class TreeFixture : IAsyncDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("libtmux-owned-tree-").FullName;
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly FileSystemWatcher _watcher;
        private readonly bool _grandchild;
        private Task? _directOutput;
        private Task? _directError;

        internal TreeFixture(bool grandchild = false)
        {
            _grandchild = grandchild;
            _watcher = new(_directory, "ready");
            _watcher.Created += (_, _) => _ready.TrySetResult();
            _watcher.EnableRaisingEvents = true;
            Borrowed = Launch("exec cat");
        }

        internal Process Root { get; private set; } = null!;
        internal Process Child { get; private set; } = null!;
        internal Process? Grandchild { get; private set; }
        internal Process Borrowed { get; }

        private string Script =>
            "mkfifo block signal; " + (_grandchild
                ? "/bin/sh -c 'cat block & grandchild=$!; printf \"%s\" \"$grandchild\" > grandchild; printf ready > signal; wait \"$grandchild\"'"
                : "/bin/sh -c 'printf ready > signal; exec cat block'") +
            " & child=$!; printf '%s' \"$child\" > child; printf '%s' \"$$\" > shell; read signal < signal; printf '%131072s' partial; : > ready; wait \"$child\"";

        internal Task<WorkspaceHostResult> Run(CancellationToken cancellation, WorkspaceOwnedProcessTree.Hooks? hooks = null) =>
            WorkspaceHostScript.RunAsync(new(Script, _directory, 1024, TimeSpan.FromSeconds(1),
                new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin" }), cancellation, hooks);

        internal async Task StartDirectAsync()
        {
            Root = Launch(Script);
            Root.StandardInput.Close();
            _directOutput = Root.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            _directError = Root.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await ReadyAsync(Root.WaitForExitAsync(TestContext.Current.CancellationToken));
        }

        internal async Task ReadyAsync(Task execution)
        {
            Task first = await Task.WhenAny(_ready.Task, execution).WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            Assert.Same(_ready.Task, first);
            Root ??= Observe("shell");
            Child = Observe("child");
            if (_grandchild)
                Grandchild = Observe("grandchild");
        }

        private Process Observe(string name) => Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(_directory, name)), CultureInfo.InvariantCulture));

        private Process Launch(string script)
        {
            ProcessStartInfo start = new("/bin/sh")
            {
                WorkingDirectory = _directory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(script);
            start.Environment.Clear();
            start.Environment.Add("PATH", "/usr/bin:/bin");
            return Process.Start(start)!;
        }

        internal async Task AssertExitedAsync()
        {
            await Root.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            await Child.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            _watcher.Dispose();
            foreach (Process? process in new[] { Root, Child, Grandchild, Borrowed })
            {
                if (process is null)
                    continue;
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1));
                }
                finally { process.Dispose(); }
            }
            if (_directOutput is not null)
                await Task.WhenAll(_directOutput, _directError!).WaitAsync(TimeSpan.FromSeconds(1));
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static int Pidfds() => Directory.EnumerateFiles("/proc/self/fd")
        .Count(path => new FileInfo(path).LinkTarget == "anon_inode:[pidfd]");

    private static string[]? Stat(int pid)
    {
        try
        {
            string value = File.ReadAllText($"/proc/{pid}/stat");
            return value[(value.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static char? State(int pid) => Stat(pid)?[0][0];
    private static int? ParentPid(int pid) => Stat(pid) is { } values ? int.Parse(values[1], CultureInfo.InvariantCulture) : null;

    private static void KillAndObserveExit(Process child)
    {
        int descriptor = pidfd_open(child.Id, 0);
        if (descriptor < 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        using SafeFileHandle handle = new(new IntPtr(descriptor), ownsHandle: true);
        PollFd pending = new() { Descriptor = descriptor, Events = 1 };
        child.Kill();
        Assert.Equal(1, poll(ref pending, 1, 1000));
        Assert.NotEqual(0, pending.Returned & 1);
    }

    private static void Signal(int pid, int signal) => Assert.Equal(0, kill(pid, signal));

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        internal int Descriptor;
        internal short Events;
        internal short Returned;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", SetLastError = true)]
    private static extern int pidfd_open(int pid, uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", SetLastError = true)]
    private static extern int poll(ref PollFd descriptors, nuint count, int timeout);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);
}
