using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LibTmux.Internal;

/// <summary>Prepares tmux's private per-user directory before a pinned socket launch.</summary>
[UnsupportedOSPlatform("windows")]
internal static partial class UnixSocketDirectory
{
    internal static uint UserId => GetUserId();

    internal static void Prepare(string path)
    {
        // mkdir creates only this leaf. Recursive creation would hide a removed
        // TMUX_TMPDIR and could redirect cleanup to a newly created endpoint.
        if (MakeDirectory(path, 0x1C0) != 0 && Marshal.GetLastPInvokeError() != 17)
        {
            throw new IOException($"Cannot prepare tmux socket directory '{path}'.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        if (ReadStatus(path, out FileStatus status) != 0)
        {
            throw new IOException($"Cannot inspect tmux socket directory '{path}'.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        if (!IsPrivateDirectory(status.Mode, status.Uid, UserId))
        {
            throw new UnauthorizedAccessException(
                $"Socket directory '{path}' must be a real directory owned by the current user with no other-user permissions.");
        }
    }

    internal static bool IsOwnedSocket(string path)
    {
        if (ReadStatus(path, out FileStatus status) != 0)
        {
            throw new IOException($"Cannot inspect socket candidate '{path}'.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }
        return (status.Mode & 0xF000) == 0xC000 && status.Uid == UserId;
    }

    internal static string? ResolveDiscoveryRoot(string path)
    {
        string leaf = path.TrimEnd('/');
        if (ReadStatus(leaf.Length == 0 ? "/" : leaf, out FileStatus status) != 0)
        {
            throw new IOException($"Cannot inspect discovery root '{path}'.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }
        if ((status.Mode & 0xF000) == 0xA000)
        {
            return null;
        }
        nint resolved = ResolvePath(path, 0);
        if (resolved == 0)
        {
            throw new IOException($"Cannot resolve discovery root '{path}'.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }
        try
        {
            return Marshal.PtrToStringUTF8(resolved)!;
        }
        finally
        {
            Free(resolved);
        }
    }

    internal static bool IsPrivateDirectory(int mode, uint owner, uint user) =>
        (mode & 0xF000) == 0x4000 && owner == user && (mode & 7) == 0;

    // System.Native's FileStatus ABI normalizes libc stat across Unix targets.
    // Only mode and UID are consumed; the runtime writes the full 120 bytes.
    [StructLayout(LayoutKind.Explicit, Size = 120)]
    private struct FileStatus
    {
        [FieldOffset(4)] internal int Mode;
        [FieldOffset(8)] internal uint Uid;
    }

    [LibraryImport("libc", EntryPoint = "getuid")]
    private static partial uint GetUserId();

    [LibraryImport("libc", EntryPoint = "realpath", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial nint ResolvePath(string path, nint resolved);

    [LibraryImport("libc", EntryPoint = "free")]
    private static partial void Free(nint pointer);

    [LibraryImport("System.Native", EntryPoint = "SystemNative_MkDir", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int MakeDirectory(string path, int mode);

    [LibraryImport("System.Native", EntryPoint = "SystemNative_LStat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int ReadStatus(string path, out FileStatus status);
}
