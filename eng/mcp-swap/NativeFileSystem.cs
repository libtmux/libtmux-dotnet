using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace LibTmux.McpSwap;

internal sealed record NativeIdentity(
    ulong Device,
    ulong Inode,
    uint Mode,
    ulong LinkCount,
    uint UserId,
    long Size,
    long ModifiedNanoseconds)
{
    internal bool IsRegular => (Mode & 0xF000U) == 0x8000U;

    internal bool IsDirectory => (Mode & 0xF000U) == 0x4000U;

    internal bool IsSymbolicLink => (Mode & 0xF000U) == 0xA000U;

    internal int Permissions => (int)(Mode & 0xFFFU);
}

internal sealed record FileSnapshot(NativeIdentity Identity, byte[] Bytes, string Digest)
{
    internal bool SameFile(FileSnapshot other) =>
        Identity == other.Identity
        && string.Equals(Digest, other.Digest, StringComparison.Ordinal)
        && Bytes.AsSpan().SequenceEqual(other.Bytes);
}

internal sealed record DirectoryBinding(
    string Logical,
    string Physical,
    NativeIdentity Identity,
    string? LinkTarget,
    NativeIdentity? LinkIdentity);

internal sealed record PathBinding(
    string Logical,
    string Physical,
    DirectoryBinding Parent,
    string? LinkTarget,
    NativeIdentity? LinkIdentity,
    FileSnapshot File);

internal sealed record DestinationBinding(
    string Logical,
    string Physical,
    DirectoryBinding Parent,
    FileSnapshot? File);

internal static partial class NativeFileSystem
{
    private const int MetadataBufferSize = 512;
    private const int MaximumConfigBytes = 16 * 1024 * 1024;

    internal static PathBinding CaptureConfig(string path)
    {
        string logical = Path.GetFullPath(path);
        NativeIdentity link = LStat(logical);
        string? linkTarget = link.IsSymbolicLink ? new FileInfo(logical).LinkTarget : null;
        string physical = RealPath(logical);
        DirectoryBinding parent = CaptureDirectory(Path.GetDirectoryName(logical)!);
        FileSnapshot file = ReadStable(physical, MaximumConfigBytes);
        if (!file.Identity.IsRegular)
        {
            throw new IOException($"config is not a regular file: {logical}");
        }

        return new(
            logical,
            physical,
            parent,
            linkTarget,
            link.IsSymbolicLink ? link : null,
            file);
    }

    internal static DestinationBinding CaptureDestination(string path, bool required = false)
    {
        string logical = Path.GetFullPath(path);
        string parentPath = Path.GetDirectoryName(logical)!;
        DirectoryBinding parent = CaptureDirectory(parentPath);
        string physical = Path.Combine(parent.Physical, Path.GetFileName(logical));
        if (!TryLStat(logical, out NativeIdentity? raw))
        {
            if (File.GetAttributes(parent.Physical).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException($"destination parent topology is unsupported: {parentPath}");
            }

            if (required)
            {
                throw new FileNotFoundException($"required file does not exist: {logical}", logical);
            }

            return new(logical, physical, parent, null);
        }

        if (raw!.IsSymbolicLink)
        {
            throw new IOException($"transaction artifact is a symlink: {logical}");
        }

        FileSnapshot file = ReadStable(physical, MaximumConfigBytes);
        if (!file.Identity.IsRegular)
        {
            throw new IOException($"transaction artifact is not a regular file: {logical}");
        }

        return new(logical, physical, parent, file);
    }

    internal static DirectoryBinding CaptureDirectory(string path)
    {
        string logical = Path.GetFullPath(path);
        NativeIdentity link = LStat(logical);
        if (!link.IsDirectory && !link.IsSymbolicLink)
        {
            throw new IOException($"not a directory or directory symlink: {logical}");
        }

        string physical = RealPath(logical);
        NativeIdentity identity = Stat(physical);
        if (!identity.IsDirectory)
        {
            throw new IOException($"not a directory: {logical}");
        }

        return new(
            logical,
            physical,
            identity,
            link.IsSymbolicLink ? new DirectoryInfo(logical).LinkTarget : null,
            link.IsSymbolicLink ? link : null);
    }

    internal static void Validate(PathBinding expected)
    {
        DirectoryBinding parent = CaptureDirectory(expected.Parent.Logical);
        if (!SameDirectory(parent, expected.Parent))
        {
            throw new IOException($"config parent changed: {expected.Parent.Logical}");
        }

        NativeIdentity currentLink = LStat(expected.Logical);
        if (expected.LinkIdentity is null)
        {
            if (currentLink.IsSymbolicLink)
            {
                throw new IOException($"config became a symlink: {expected.Logical}");
            }
        }
        else if (currentLink != expected.LinkIdentity
            || !string.Equals(
                new FileInfo(expected.Logical).LinkTarget,
                expected.LinkTarget,
                StringComparison.Ordinal))
        {
            throw new IOException($"config symlink changed: {expected.Logical}");
        }

        if (!string.Equals(RealPath(expected.Logical), expected.Physical, StringComparison.Ordinal))
        {
            throw new IOException($"config target changed: {expected.Logical}");
        }

        FileSnapshot current = ReadStable(expected.Physical, MaximumConfigBytes);
        if (!current.SameFile(expected.File))
        {
            throw new IOException($"config identity or bytes changed: {expected.Logical}");
        }
    }

    internal static void Validate(DestinationBinding expected)
    {
        DirectoryBinding parent = CaptureDirectory(expected.Parent.Logical);
        if (!SameDirectory(parent, expected.Parent))
        {
            throw new IOException($"artifact parent changed: {expected.Parent.Logical}");
        }

        bool exists = TryLStat(expected.Logical, out NativeIdentity? raw);
        if (expected.File is null)
        {
            if (exists)
            {
                throw new IOException($"transaction destination appeared: {expected.Logical}");
            }

            return;
        }

        if (!exists || raw!.IsSymbolicLink)
        {
            throw new IOException($"transaction artifact topology changed: {expected.Logical}");
        }

        FileSnapshot current = ReadStable(expected.Physical, MaximumConfigBytes);
        if (!current.SameFile(expected.File))
        {
            throw new IOException($"transaction artifact changed: {expected.Logical}");
        }
    }

    internal static FileSnapshot ReadStable(string path, int maximumBytes)
    {
        string full = Path.GetFullPath(path);
        NativeIdentity before = Stat(full);
        if (!before.IsRegular)
        {
            throw new IOException($"not a regular file: {full}");
        }

        if (before.Size < 0 || before.Size > maximumBytes)
        {
            throw new IOException($"file exceeds {maximumBytes} bytes: {full}");
        }

        // Refuse before opening. The retention below cannot be reached on a
        // platform whose FileShare implementation opens and closes a
        // descriptor to discover the conflict, and that close takes this
        // process's record locks on the file with it.
        if (SwapLock.IsActiveLock(before))
        {
            throw new IOException($"file aliases the held swap lock: {full}");
        }

        FileStream? stream = new(
            full,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        try
        {
            NativeIdentity opened = FStat(stream.SafeFileHandle);
            if (SwapLock.RetainIfActiveAlias(opened, stream))
            {
                stream = null;
                throw new IOException($"file aliases the held swap lock: {full}");
            }

            if (opened != before)
            {
                throw new IOException($"file changed while opened: {full}");
            }

            byte[] bytes = new byte[checked((int)before.Size)];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
            {
                throw new IOException($"file grew while read: {full}");
            }

            NativeIdentity after = FStat(stream.SafeFileHandle);
            if (before != after || bytes.LongLength != after.Size)
            {
                throw new IOException($"file changed while read: {full}");
            }

            return new(after, bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        finally
        {
            stream?.Dispose();
        }
    }

    internal static string Stage(string directory, string logicalName, string role, byte[] bytes, int mode)
    {
        string parent = Path.GetFullPath(directory);
        for (int attempt = 0; attempt < 100; attempt++)
        {
            string path = Path.Combine(
                parent,
                $".{logicalName}.mcp-swap-{role}-{Environment.ProcessId}-{Guid.NewGuid():N}");
            try
            {
                FileStreamOptions options = new()
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    BufferSize = 4096,
                    Options = FileOptions.WriteThrough,
                };
                if (!OperatingSystem.IsWindows())
                {
                    options.UnixCreateMode = (UnixFileMode)mode;
                }

                using FileStream stream = new(path, options);

                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
                return path;
            }
            catch (IOException) when (TryLStat(path, out _))
            {
            }
        }

        throw new IOException($"could not create private stage beside {logicalName}");
    }

    internal static FileSnapshot PublishNoReplace(string source, string destination, FileSnapshot expected)
    {
        FileSnapshot sourceBefore = ReadStable(source, MaximumConfigBytes);
        if (!sourceBefore.SameFile(expected))
        {
            throw new IOException($"stage changed before publication: {source}");
        }

        RenameNoReplace(source, destination);
        FileSnapshot published = ReadStable(destination, MaximumConfigBytes);
        if (!published.SameFile(expected))
        {
            throw new IOException($"published file is not exact: {destination}");
        }

        return published;
    }

    internal static FileSnapshot TakeAside(
        string source,
        string destination,
        FileSnapshot sourceExpected,
        FileSnapshot destinationExpected)
    {
        RemoveExact(destination, destinationExpected);
        FileSnapshot current = ReadStable(source, MaximumConfigBytes);
        if (!current.SameFile(sourceExpected))
        {
            throw new IOException($"source changed before take-aside: {source}");
        }

        RenameNoReplace(source, destination);
        FileSnapshot retained = ReadStable(destination, MaximumConfigBytes);
        if (!retained.SameFile(sourceExpected))
        {
            throw new IOException($"take-aside changed source identity: {source}");
        }

        return retained;
    }

    internal static void RemoveExact(string path, FileSnapshot expected)
    {
        FileSnapshot current = ReadStable(path, MaximumConfigBytes);
        if (!current.SameFile(expected))
        {
            throw new IOException($"file changed before removal: {path}");
        }

        string parent = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string quarantineDirectory = Path.Combine(
            parent,
            $".{Path.GetFileName(path)}.mcp-swap-retained-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(quarantineDirectory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                quarantineDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        string quarantine = Path.Combine(quarantineDirectory, "artifact");
        RenameNoReplace(path, quarantine);
        FileSnapshot moved = ReadStable(quarantine, MaximumConfigBytes);
        if (!moved.SameFile(expected))
        {
            throw new IOException($"wrong file retained during removal: {quarantineDirectory}");
        }

        if (TryLStat(path, out _))
        {
            throw new IOException($"file appeared during removal; retained at {quarantineDirectory}");
        }

        File.Delete(quarantine);
        Directory.Delete(quarantineDirectory);
    }

    internal static int Mode(FileSnapshot snapshot) => snapshot.Identity.Permissions;

    internal static bool SameContentAndMetadata(FileSnapshot left, FileSnapshot right) =>
        left.Identity.Mode == right.Identity.Mode
        && left.Identity.Size == right.Identity.Size
        && left.Identity.ModifiedNanoseconds == right.Identity.ModifiedNanoseconds
        && string.Equals(left.Digest, right.Digest, StringComparison.Ordinal)
        && left.Bytes.AsSpan().SequenceEqual(right.Bytes);

    internal static bool SameDirectory(DirectoryBinding left, DirectoryBinding right) =>
        string.Equals(left.Logical, right.Logical, StringComparison.Ordinal)
        && string.Equals(left.Physical, right.Physical, StringComparison.Ordinal)
        && string.Equals(left.LinkTarget, right.LinkTarget, StringComparison.Ordinal)
        && left.LinkIdentity == right.LinkIdentity
        && left.Identity.Device == right.Identity.Device
        && left.Identity.Inode == right.Identity.Inode
        && left.Identity.Mode == right.Identity.Mode
        && left.Identity.UserId == right.Identity.UserId;

    internal static void CreatePrivateDirectory(string path)
    {
        string full = Path.GetFullPath(path);
        bool existed = TryLStat(full, out NativeIdentity? before);
        if (existed && (before!.IsSymbolicLink || !before.IsDirectory))
        {
            throw new IOException($"private directory is unsafe: {full}");
        }

        if (!existed)
        {
            if (!OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(
                    full,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            else
            {
                Directory.CreateDirectory(full);
            }
        }

        NativeIdentity identity = LStat(full);
        if (!identity.IsDirectory
            || identity.IsSymbolicLink
            || identity.Permissions != 0x1C0
            || identity.UserId != GetUserId())
        {
            throw new IOException($"private directory is unsafe: {full}");
        }
    }

    internal static string ProspectivePhysical(string path)
    {
        string full = Path.GetFullPath(path);
        string? cursor = Path.GetDirectoryName(full);
        Stack<string> missing = new();
        missing.Push(Path.GetFileName(full));
        while (cursor is not null && !Directory.Exists(cursor))
        {
            missing.Push(Path.GetFileName(cursor));
            cursor = Path.GetDirectoryName(cursor);
        }

        if (cursor is null)
        {
            return full;
        }

        string physical = RealPath(cursor);
        while (missing.TryPop(out string? component))
        {
            physical = Path.Combine(physical, component);
        }

        return physical;
    }

    internal static NativeIdentity Stat(string path) => ReadStat(path, follow: true);

    internal static NativeIdentity LStat(string path) => ReadStat(path, follow: false);

    /// <summary>Dumps the first bytes lstat wrote, to check a decode against a platform.</summary>
    /// <param name="path">The path to stat.</param>
    /// <returns>The leading bytes as hex, or why the call failed.</returns>
    internal static string RawStatHex(string path)
    {
        byte[] buffer = new byte[MetadataBufferSize];
        return InvokeStat(Path.GetFullPath(path), buffer, follow: false) != 0
            ? $"lstat errno {Marshal.GetLastPInvokeError()}"
            : Convert.ToHexString(buffer.AsSpan(0, 32));
    }

    internal static bool TryLStat(string path, out NativeIdentity? identity)
    {
        try
        {
            identity = LStat(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            identity = null;
            return false;
        }
    }

    internal static uint GetUserId() => OperatingSystem.IsWindows() ? 0 : GetUid();

    internal static SafeFileHandle OpenDirectoryNoFollow(string path)
    {
        EnsureUnix();
        int descriptor = OpenNative(
            Path.GetFullPath(path),
            OpenReadOnly | OpenDirectory | OpenNoFollow | OpenCloseOnExec,
            0);
        return OwnedHandle(descriptor, $"cannot open lock directory {path}");
    }

    internal static SafeFileHandle OpenLockNoFollow(SafeFileHandle directory, string name)
    {
        EnsureUnix();
        int flags = OpenReadWrite | OpenCreate | OpenExclusive | OpenNoFollow | OpenCloseOnExec;
        int descriptor = OpenAtNative(directory.DangerousGetHandle().ToInt32(), name, flags, 0x180);
        bool created = descriptor >= 0;
        if (!created && Marshal.GetLastPInvokeError() == 17)
        {
            flags = OpenReadWrite | OpenNoFollow | OpenCloseOnExec;
            descriptor = OpenAtNative(directory.DangerousGetHandle().ToInt32(), name, flags, 0);
        }

        SafeFileHandle handle = OwnedHandle(descriptor, $"cannot open swap lock {name}");
        if (!created)
        {
            return handle;
        }

        // openat is variadic in C, and Apple's arm64 ABI passes a variadic
        // argument on the stack where every other target we build for passes
        // it in a register. So the mode above never reaches the kernel on
        // Apple silicon and the lock is created with whatever the stack held,
        // which the 0600 check then rejects. fchmod is not variadic. Only the
        // descriptor this call created is touched, so a lock that was already
        // there is still rejected rather than repaired, and O_EXCL plus the
        // owned 0700 directory leave no window another user could reach.
        if (FChangeMode(handle.DangerousGetHandle().ToInt32(), 0x180) < 0)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException(
                $"cannot set the mode of swap lock {name}: "
                + Marshal.GetPInvokeErrorMessage(error));
        }

        return handle;
    }

    internal static NativeIdentity FStat(SafeFileHandle handle)
    {
        EnsureUnix();
        byte[] buffer = new byte[MetadataBufferSize];
        if (InvokeFStat(handle.DangerousGetHandle().ToInt32(), buffer) != 0)
        {
            int error = Marshal.GetLastPInvokeError();
            throw new IOException($"cannot inspect open file: {Marshal.GetPInvokeErrorMessage(error)}");
        }

        return OperatingSystem.IsMacOS() ? DecodeMac(buffer) : DecodeLinux(buffer);
    }

    internal static void CreateHardLink(string linkPath, string existingPath)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("mcp-swap hard-link transactions require Unix");
        }

        if (LinkNative(existingPath, linkPath) != 0)
        {
            int error = Marshal.GetLastPInvokeError();
            throw new IOException(
                $"cannot link {linkPath}: {Marshal.GetPInvokeErrorMessage(error)}");
        }
    }

    internal static void RenameNoReplace(string source, string destination)
    {
        int result;
        if (OperatingSystem.IsLinux())
        {
            result = RenameAt2(-100, source, -100, destination, 1);
        }
        else if (OperatingSystem.IsMacOS())
        {
            result = RenameExclusive(source, destination, 4);
        }
        else
        {
            throw new PlatformNotSupportedException(
                "mcp-swap no-replace transactions require Linux or macOS");
        }

        if (result != 0)
        {
            int error = Marshal.GetLastPInvokeError();
            throw new IOException(
                $"cannot move {source} to {destination} without replacement: {Marshal.GetPInvokeErrorMessage(error)}");
        }
    }

    internal static void Lock(FileStream stream)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("mcp-swap transactions require Unix");
        }

        stream.Position = 0;
        int descriptor = stream.SafeFileHandle.DangerousGetHandle().ToInt32();
        int result;
        do
        {
            result = LockFile(descriptor, 1, 0);
        }
        while (result != 0 && Marshal.GetLastPInvokeError() == 4);

        if (result != 0)
        {
            int error = Marshal.GetLastPInvokeError();
            throw new IOException($"cannot acquire swap lock: {Marshal.GetPInvokeErrorMessage(error)}");
        }
    }

    internal static void Unlock(FileStream stream)
    {
        if (!OperatingSystem.IsWindows())
        {
            stream.Position = 0;
            if (LockFile(stream.SafeFileHandle.DangerousGetHandle().ToInt32(), 0, 0) != 0)
            {
                int error = Marshal.GetLastPInvokeError();
                throw new IOException($"cannot release swap lock: {Marshal.GetPInvokeErrorMessage(error)}");
            }
        }
    }

    internal static string RealPath(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.GetFullPath(path);
        }

        nint pointer = RealPathNative(Path.GetFullPath(path), 0);
        if (pointer == 0)
        {
            throw new IOException(
                $"cannot resolve path {path}: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}");
        }

        try
        {
            return Marshal.PtrToStringUTF8(pointer)
                ?? throw new IOException($"cannot decode resolved path {path}");
        }
        finally
        {
            Free(pointer);
        }
    }

    private static NativeIdentity ReadStat(string path, bool follow)
    {
        if (OperatingSystem.IsWindows())
        {
            FileInfo file = new(path);
            uint mode = file.Attributes.HasFlag(FileAttributes.Directory) ? 0x4000U : 0x8000U;
            return new(0, 0, mode, 1, 0, file.Exists ? file.Length : 0, file.LastWriteTimeUtc.Ticks * 100);
        }

        byte[] buffer = new byte[MetadataBufferSize];
        int result = InvokeStat(path, buffer, follow);
        if (result != 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error is 2 or 20)
            {
                throw new FileNotFoundException($"cannot inspect missing path {path}", path);
            }

            throw new IOException($"cannot inspect {path}: {Marshal.GetPInvokeErrorMessage(error)}");
        }

        return OperatingSystem.IsMacOS() ? DecodeMac(buffer) : DecodeLinux(buffer);
    }

    private static NativeIdentity DecodeLinux(ReadOnlySpan<byte> data)
    {
        int modeOffset;
        int linksOffset;
        int userOffset;
        switch (RuntimeInformation.ProcessArchitecture)
        {
            case Architecture.X64:
                modeOffset = 24;
                linksOffset = 16;
                userOffset = 28;
                break;
            case Architecture.Arm64:
                modeOffset = 16;
                linksOffset = 24;
                userOffset = 28;
                break;
            default:
                throw new PlatformNotSupportedException(
                    $"Linux {RuntimeInformation.ProcessArchitecture} stat layout is not supported");
        }

        return new(
            BinaryPrimitives.ReadUInt64LittleEndian(data),
            BinaryPrimitives.ReadUInt64LittleEndian(data[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[modeOffset..]),
            BinaryPrimitives.ReadUInt64LittleEndian(data[linksOffset..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[userOffset..]),
            BinaryPrimitives.ReadInt64LittleEndian(data[48..]),
            checked(
                BinaryPrimitives.ReadInt64LittleEndian(data[88..]) * 1_000_000_000
                + BinaryPrimitives.ReadInt64LittleEndian(data[96..])));
    }

    private static NativeIdentity DecodeMac(ReadOnlySpan<byte> data) => new(
        BinaryPrimitives.ReadUInt32LittleEndian(data),
        BinaryPrimitives.ReadUInt64LittleEndian(data[8..]),
        BinaryPrimitives.ReadUInt16LittleEndian(data[4..]),
        BinaryPrimitives.ReadUInt16LittleEndian(data[6..]),
        BinaryPrimitives.ReadUInt32LittleEndian(data[16..]),
        BinaryPrimitives.ReadInt64LittleEndian(data[96..]),
        checked(
            BinaryPrimitives.ReadInt64LittleEndian(data[48..]) * 1_000_000_000
            + BinaryPrimitives.ReadInt64LittleEndian(data[56..])));

    private static int InvokeStat(string path, byte[] buffer, bool follow)
    {
        if (OperatingSystem.IsMacOS()
            && RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            return follow ? StatDarwinInode64(path, buffer) : LStatDarwinInode64(path, buffer);
        }

        return follow ? StatNative(path, buffer) : LStatNative(path, buffer);
    }

    private static int InvokeFStat(int descriptor, byte[] buffer) =>
        OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? FStatDarwinInode64(descriptor, buffer)
            : FStatNative(descriptor, buffer);

    private static SafeFileHandle OwnedHandle(int descriptor, string message)
    {
        if (descriptor >= 0)
        {
            return new SafeFileHandle(new nint(descriptor), ownsHandle: true);
        }

        int error = Marshal.GetLastPInvokeError();
        throw new IOException($"{message}: {Marshal.GetPInvokeErrorMessage(error)}");
    }

    private static void EnsureUnix()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("mcp-swap transactions require Linux or macOS");
        }
    }

    private static int OpenReadOnly => 0;

    private static int OpenReadWrite => 2;

    private static int OpenCreate => OperatingSystem.IsMacOS() ? 0x200 : 0x40;

    private static int OpenExclusive => OperatingSystem.IsMacOS() ? 0x800 : 0x80;

    private static int OpenNoFollow => OperatingSystem.IsMacOS() ? 0x100 : 0x20000;

    private static int OpenDirectory => OperatingSystem.IsMacOS() ? 0x100000 : 0x10000;

    private static int OpenCloseOnExec => OperatingSystem.IsMacOS() ? 0x1000000 : 0x80000;

    [LibraryImport("libc", EntryPoint = "stat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int StatNative(string path, [Out] byte[] buffer);

    [LibraryImport("libc", EntryPoint = "lstat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LStatNative(string path, [Out] byte[] buffer);

    [LibraryImport("libc", EntryPoint = "stat$INODE64", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int StatDarwinInode64(string path, [Out] byte[] buffer);

    [LibraryImport("libc", EntryPoint = "lstat$INODE64", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LStatDarwinInode64(string path, [Out] byte[] buffer);

    [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static partial int FStatNative(int descriptor, [Out] byte[] buffer);

    [LibraryImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static partial int FStatDarwinInode64(int descriptor, [Out] byte[] buffer);

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenNative(string path, int flags, uint mode);

    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenAtNative(int directory, string path, int flags, uint mode);

    [LibraryImport("libc", EntryPoint = "realpath", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint RealPathNative(string path, nint buffer);

    [LibraryImport("libc", EntryPoint = "free")]
    private static partial void Free(nint pointer);

    [LibraryImport("libc", EntryPoint = "fchmod", SetLastError = true)]
    private static partial int FChangeMode(int descriptor, uint mode);

    [LibraryImport("libc", EntryPoint = "getuid")]
    private static partial uint GetUid();

    [LibraryImport("libc", EntryPoint = "link", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LinkNative(string existingPath, string linkPath);

    [LibraryImport("libc", EntryPoint = "lockf", SetLastError = true)]
    private static partial int LockFile(int descriptor, int function, long length);

    [LibraryImport("libc", EntryPoint = "renameat2", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int RenameAt2(
        int oldDirectory,
        string oldPath,
        int newDirectory,
        string newPath,
        uint flags);

    [LibraryImport("libc", EntryPoint = "renamex_np", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int RenameExclusive(string oldPath, string newPath, uint flags);
}
