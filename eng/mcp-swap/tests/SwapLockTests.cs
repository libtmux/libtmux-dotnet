namespace LibTmux.McpSwap.Tests;

public sealed class SwapLockTests
{
    [Fact]
    public async Task ConcurrentOwnerWaitsForThePersistentLock()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TestPaths paths = new();
        SwapRuntime runtime = Runtime(paths);
        SwapLock first = SwapLock.Acquire(runtime);
        Task<SwapLock> contender = Task.Run(() => SwapLock.Acquire(runtime));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(contender.IsCompleted);

        first.Dispose();
        using SwapLock second = await contender.WaitAsync(
            TimeSpan.FromSeconds(3),
            TestContext.Current.CancellationToken);

        NativeIdentity identity = NativeFileSystem.LStat(runtime.LockFile);
        Assert.Equal(0x180, identity.Permissions);
        Assert.Equal<ulong>(1, identity.LinkCount);
    }

    [Fact]
    public async Task PythonLockfOwnerSerializesNativeAcquisition()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TestPaths paths = new();
        SwapRuntime runtime = Runtime(paths);
        using (SwapLock created = SwapLock.Acquire(runtime))
        {
        }

        string python = ExecutableFinder.Find("python3")
            ?? throw new InvalidOperationException("python3 is required for the lock interoperability test");
        using System.Diagnostics.Process holder = new()
        {
            StartInfo = new()
            {
                FileName = python,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            },
        };
        holder.StartInfo.ArgumentList.Add("-c");
        holder.StartInfo.ArgumentList.Add(
            "import fcntl,sys; f=open(sys.argv[1],'r+'); fcntl.lockf(f,fcntl.LOCK_EX); print('ready',flush=True); sys.stdin.read()");
        holder.StartInfo.ArgumentList.Add(runtime.LockFile);
        Assert.True(holder.Start());
        Assert.Equal(
            "ready",
            await holder.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));

        Task<SwapLock> contender = Task.Run(() => SwapLock.Acquire(runtime));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(contender.IsCompleted);

        holder.StandardInput.Close();
        await holder.WaitForExitAsync(TestContext.Current.CancellationToken);
        using SwapLock acquired = await contender.WaitAsync(
            TimeSpan.FromSeconds(3),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ValidationAndAnOpenedAliasDoNotReleaseTheNativeRecordLock()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TestPaths paths = new();
        SwapRuntime runtime = Runtime(paths);

        // The lock is released only by Dispose, and the semaphore it releases
        // is process-wide, so anything that escapes before it hangs every
        // later test that acquires. Keep the using and decorate from outside.
        string state = "not reached";
        try
        {
            using SwapLock held = SwapLock.Acquire(runtime);
            held.Validate();
            string alias = Path.Combine(paths.Root, "lock-alias");
            NativeFileSystem.CreateHardLink(alias, runtime.LockFile);
            Assert.Throws<IOException>(() => NativeFileSystem.ReadStable(alias, 1024));

            string python = ExecutableFinder.Find("python3")
                ?? throw new InvalidOperationException("python3 is required for the lock interoperability test");
            using System.Diagnostics.Process contender = new()
            {
                StartInfo = new()
                {
                    FileName = python,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                },
            };
            contender.StartInfo.ArgumentList.Add("-c");
            contender.StartInfo.ArgumentList.Add(
                "import fcntl,sys; f=open(sys.argv[1],'r+');\ntry: fcntl.lockf(f,fcntl.LOCK_EX|fcntl.LOCK_NB); print('acquired')\nexcept BlockingIOError: print('blocked')");
            contender.StartInfo.ArgumentList.Add(runtime.LockFile);
            Assert.True(contender.Start());
            string result = await contender.StandardOutput.ReadToEndAsync(
                TestContext.Current.CancellationToken);
            await contender.WaitForExitAsync(TestContext.Current.CancellationToken);

            Assert.Equal("blocked\n", result);
            File.Delete(alias);

            // Validate below refuses a lock carrying more than one link, so it
            // cannot tell "the alias outlived the delete" from "validation is
            // broken". Say which before asking.
            Assert.Equal<ulong>(1, NativeFileSystem.LStat(runtime.LockFile).LinkCount);

            held.Validate();

            // The dispose-time validate is the one that fails on macOS, and the
            // one on the line above does not, so the state has to be read here,
            // at the last point before the scope ends and disposal validates.
            state = $"hex {NativeFileSystem.RawStatHex(runtime.LockFile)}, "
                + (File.Exists(alias) ? "alias still present" : "alias gone")
                + ", tree "
                + string.Join(
                    ",",
                    Directory.EnumerateFileSystemEntries(paths.Root, "*", SearchOption.AllDirectories)
                        .Select(entry => Path.GetRelativePath(paths.Root, entry))
                        .Order(StringComparer.Ordinal));
        }
        catch (IOException error)
        {
            throw new IOException($"[before dispose: {state}] {error.Message}", error);
        }
    }

    [Theory]
    [InlineData("mode")]
    [InlineData("hardlink")]
    [InlineData("symlink")]
    [InlineData("directory")]
    public void UnsafeExistingLockIsRejectedWithoutRepairingIt(string defect)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TestPaths paths = new();
        SwapRuntime runtime = Runtime(paths);
        NativeFileSystem.CreatePrivateDirectory(runtime.SwapDirectory);
        string other = Path.Combine(paths.Root, "other");
        File.WriteAllText(other, string.Empty);
        File.SetUnixFileMode(other, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        switch (defect)
        {
            case "mode":
                File.WriteAllText(runtime.LockFile, string.Empty);
                File.SetUnixFileMode(runtime.LockFile, UnixFileMode.UserRead | UnixFileMode.GroupRead);
                break;
            case "hardlink":
                NativeFileSystem.CreateHardLink(runtime.LockFile, other);
                break;
            case "symlink":
                File.CreateSymbolicLink(runtime.LockFile, other);
                break;
            case "directory":
                Directory.CreateDirectory(runtime.LockFile);
                break;
            default:
                throw new InvalidOperationException(defect);
        }

        Assert.Throws<IOException>(() => SwapLock.Acquire(runtime));

        Assert.True(NativeFileSystem.TryLStat(runtime.LockFile, out _));
    }

    [Fact]
    public void ReplacingTheLockDirectoryInvalidatesTheOpenDescriptor()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TestPaths paths = new();
        SwapRuntime runtime = Runtime(paths);
        SwapLock held = SwapLock.Acquire(runtime);
        string displaced = runtime.SwapDirectory + "-displaced";
        Directory.Move(runtime.SwapDirectory, displaced);
        NativeFileSystem.CreatePrivateDirectory(runtime.SwapDirectory);

        Assert.Throws<IOException>(() => held.Validate());
        Assert.Throws<IOException>(() => held.Dispose());
        Assert.True(File.Exists(Path.Combine(displaced, "state.lock")));
        Assert.False(File.Exists(runtime.LockFile));
    }

    [Fact]
    public void UnsafeLockDirectoryIsRejectedWithoutChangingItsMode()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TestPaths paths = new();
        SwapRuntime runtime = Runtime(paths);
        Directory.CreateDirectory(runtime.SwapDirectory);
        File.SetUnixFileMode(
            runtime.SwapDirectory,
            UnixFileMode.UserRead
            | UnixFileMode.UserWrite
            | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead
            | UnixFileMode.GroupExecute);

        Assert.Throws<IOException>(() => SwapLock.Acquire(runtime));

        Assert.Equal(0x1E8, NativeFileSystem.LStat(runtime.SwapDirectory).Permissions);
    }

    private static SwapRuntime Runtime(TestPaths paths) =>
        new(paths.Home, paths.ConfigHome, paths.StateHome, "/opt/dotnet/dotnet");
}
