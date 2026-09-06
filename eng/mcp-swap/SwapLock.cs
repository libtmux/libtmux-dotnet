using Microsoft.Win32.SafeHandles;

namespace LibTmux.McpSwap;

internal sealed record LockObservation(string Logical, string Physical, NativeIdentity? Identity);

internal sealed class SwapLock : IDisposable
{
    private static readonly SemaphoreSlim ProcessGate = new(1, 1);
    private static SwapLock? active;
    private readonly FileStream stream;
    private readonly List<FileStream> retainedAliases = [];
    private readonly SafeFileHandle directoryHandle;
    private readonly DirectoryBinding directory;
    private readonly string logical;
    private readonly string physical;
    private readonly NativeIdentity identity;
    private bool disposed;

    private SwapLock(
        FileStream stream,
        SafeFileHandle directoryHandle,
        DirectoryBinding directory,
        string logical,
        string physical,
        NativeIdentity identity)
    {
        this.stream = stream;
        this.directoryHandle = directoryHandle;
        this.directory = directory;
        this.logical = logical;
        this.physical = physical;
        this.identity = identity;
    }

    internal string Logical => logical;

    internal string Physical => physical;

    internal NativeIdentity Identity => identity;

    internal static LockObservation Inspect(SwapRuntime runtime) =>
        Inspect(runtime.SwapDirectory, runtime.LockFile);

    internal static SwapLock Acquire(SwapRuntime runtime)
    {
        ProcessGate.Wait();
        SafeFileHandle? directoryHandle = null;
        FileStream? stream = null;
        try
        {
            NativeFileSystem.CreatePrivateDirectory(runtime.SwapDirectory);
            DirectoryBinding directory = NativeFileSystem.CaptureDirectory(runtime.SwapDirectory);
            directoryHandle = NativeFileSystem.OpenDirectoryNoFollow(runtime.SwapDirectory);
            NativeIdentity openedDirectory = NativeFileSystem.FStat(directoryHandle);
            if (!SameDirectoryIdentity(directory.Identity, openedDirectory))
            {
                throw new IOException($"swap lock directory changed: {runtime.SwapDirectory}");
            }

            string logical = Path.GetFullPath(runtime.LockFile);
            SafeFileHandle lockHandle = NativeFileSystem.OpenLockNoFollow(
                directoryHandle,
                Path.GetFileName(logical));
            stream = new FileStream(lockHandle, FileAccess.ReadWrite, bufferSize: 1, isAsync: false);
            NativeFileSystem.Lock(stream);
            NativeIdentity opened = NativeFileSystem.FStat(stream.SafeFileHandle);
            ValidateIdentity(opened, logical);

            LockObservation observed = Inspect(runtime);
            if (observed.Identity != opened)
            {
                throw new IOException($"swap lock path does not name the open lock: {logical}");
            }

            SwapLock held = new(
                stream,
                directoryHandle,
                directory,
                logical,
                observed.Physical,
                opened);
            held.Validate();
            active = held;
            return held;
        }
        catch
        {
            stream?.Dispose();
            directoryHandle?.Dispose();
            ProcessGate.Release();
            throw;
        }
    }

    internal void Validate()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!SameDirectoryIdentity(directory.Identity, NativeFileSystem.FStat(directoryHandle))
            || !NativeFileSystem.SameDirectory(
                NativeFileSystem.CaptureDirectory(directory.Logical),
                directory))
        {
            throw new IOException($"swap lock directory changed: {directory.Logical}");
        }

        LockObservation observed = Inspect(directory.Logical, logical);
        if (!string.Equals(observed.Physical, physical, StringComparison.Ordinal)
            || observed.Identity != identity)
        {
            throw new IOException($"swap lock path changed: {logical}");
        }

        NativeIdentity opened = NativeFileSystem.FStat(stream.SafeFileHandle);
        ValidateIdentity(opened, logical);
        if (opened != identity)
        {
            throw new IOException($"swap lock descriptor changed: {logical}");
        }
    }

    internal static bool RetainIfActiveAlias(NativeIdentity identity, FileStream candidate)
    {
        SwapLock? held = active;
        if (held is null
            || held.identity.Device != identity.Device
            || held.identity.Inode != identity.Inode)
        {
            return false;
        }

        lock (held.retainedAliases)
        {
            held.retainedAliases.Add(candidate);
        }

        return true;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        try
        {
            try
            {
                Validate();
            }
            catch (IOException error)
            {
                throw new IOException($"swap lock invalid at dispose: {error.Message}", error);
            }
        }
        finally
        {
            disposed = true;
            try
            {
                NativeFileSystem.Unlock(stream);
            }
            finally
            {
                stream.Dispose();
                lock (retainedAliases)
                {
                    foreach (FileStream alias in retainedAliases)
                    {
                        alias.Dispose();
                    }
                }

                directoryHandle.Dispose();
                active = null;
                ProcessGate.Release();
            }
        }
    }

    private static LockObservation Inspect(string directoryPath, string lockPath)
    {
        string logical = Path.GetFullPath(lockPath);
        if (!NativeFileSystem.TryLStat(directoryPath, out NativeIdentity? rawDirectory))
        {
            return new(logical, NativeFileSystem.ProspectivePhysical(logical), null);
        }

        ValidateDirectory(rawDirectory!, directoryPath);
        DirectoryBinding directory = NativeFileSystem.CaptureDirectory(directoryPath);
        if (directory.LinkIdentity is not null)
        {
            throw new IOException($"swap lock directory is a symlink: {directoryPath}");
        }

        string physical = Path.Combine(directory.Physical, Path.GetFileName(logical));
        if (!NativeFileSystem.TryLStat(logical, out NativeIdentity? raw))
        {
            return new(logical, physical, null);
        }

        if (raw!.IsSymbolicLink || !raw.IsRegular)
        {
            throw new IOException($"swap lock is not a regular file: {logical}");
        }

        if (!string.Equals(NativeFileSystem.RealPath(logical), physical, StringComparison.Ordinal))
        {
            throw new IOException($"swap lock changed while inspected: {logical}");
        }

        NativeIdentity current = NativeFileSystem.Stat(physical);
        if (current != raw)
        {
            throw new IOException($"swap lock changed while inspected: {logical}");
        }

        if (!IsOwnedPrivateFile(current))
        {
            throw new IOException(
                $"swap lock must be an owned 0600 regular file with one link: {logical} "
                + $"{Observed(current)} [raw {Observed(raw)}, "
                + $"physical {physical}, realpath {NativeFileSystem.RealPath(logical)}, "
                + $"dirphysical {directory.Physical}, "
                + $"hex(logical) {NativeFileSystem.RawStatHex(logical)}, "
                + $"hex(physical) {NativeFileSystem.RawStatHex(physical)}]");
        }

        return new(logical, physical, current);
    }

    private static void ValidateDirectory(NativeIdentity identity, string path)
    {
        if (identity.IsSymbolicLink
            || !identity.IsDirectory
            || identity.Permissions != 0x1C0
            || identity.UserId != NativeFileSystem.GetUserId())
        {
            throw new IOException(
                $"swap lock directory must be an owned 0700 directory: {path} {Observed(identity)}");
        }
    }

    private static bool IsOwnedPrivateFile(NativeIdentity identity) =>
        identity.IsRegular
        && identity.Permissions == 0x180
        && identity.LinkCount == 1
        && identity.UserId == NativeFileSystem.GetUserId();

    private static void ValidateIdentity(NativeIdentity identity, string path)
    {
        if (!IsOwnedPrivateFile(identity))
        {
            throw new IOException(
                $"swap lock must be an owned 0600 regular file with one link: "
                + $"{path} {Observed(identity)}");
        }
    }

    /// <summary>Says what was there, since the message above says what was wanted.</summary>
    /// <param name="identity">The identity that failed a check.</param>
    /// <returns>The fields the checks read.</returns>
    private static string Observed(NativeIdentity identity)
    {
        return $"(mode {Convert.ToString(identity.Permissions, 8)}, links {identity.LinkCount}, "
            + $"uid {identity.UserId} against {NativeFileSystem.GetUserId()}, "
            + $"regular {identity.IsRegular}, directory {identity.IsDirectory}, "
            + $"symlink {identity.IsSymbolicLink})";
    }

    private static bool SameDirectoryIdentity(NativeIdentity expected, NativeIdentity current) =>
        expected.Device == current.Device
        && expected.Inode == current.Inode
        && expected.Mode == current.Mode
        && expected.UserId == current.UserId;
}
