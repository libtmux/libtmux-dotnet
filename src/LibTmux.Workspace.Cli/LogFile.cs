using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LibTmux.Workspace.Cli;

internal static partial class LogFile
{
    internal static StreamWriter Open(string path)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new CliException("log-file-unsupported", "Log files currently require Linux x64.");
        const int writeOnly = 1, create = 0x40, noControllingTerminal = 0x100, append = 0x400, nonBlocking = 0x800, closeOnExec = 0x80000, noFollow = 0x20000;
        const uint ownerReadWrite = 0x180, fileTypeMask = 0xF000, regularFile = 0x8000;
        SafeFileHandle? handle = null;
        try
        {
            int inspected = PathStatus(path, out LinuxStat before);
            if (inspected == 0 && (before.Mode & fileTypeMask) != regularFile) throw new IOException("The log destination must be a regular file.");
            if (inspected != 0 && Marshal.GetLastPInvokeError() != 2) throw new IOException(new Win32Exception(Marshal.GetLastPInvokeError()).Message);
            handle = OpenFile(path, writeOnly | create | noControllingTerminal | append | nonBlocking | closeOnExec | noFollow, ownerReadWrite);
            if (handle.IsInvalid || FileStatus(handle, out LinuxStat status) != 0) throw new IOException(new Win32Exception(Marshal.GetLastPInvokeError()).Message);
            if ((status.Mode & fileTypeMask) != regularFile) throw new IOException("The log destination must be a regular file.");
            return new StreamWriter(new FileStream(handle, FileAccess.Write), new System.Text.UTF8Encoding(false));
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException)
        {
            handle?.Dispose();
            throw new CliException("log-file-unavailable", $"Cannot open log file '{path}': {failure.Message}");
        }
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial SafeFileHandle OpenFile(string path, int flags, uint mode);

    [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static partial int FileStatus(SafeFileHandle handle, out LinuxStat status);

    [LibraryImport("libc", EntryPoint = "lstat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int PathStatus(string path, out LinuxStat status);

    // Linux x86-64 glibc/musl struct stat. Other ABIs need their own verified declaration.
    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxStat
    {
        internal ulong Device, Inode, LinkCount;
        internal uint Mode, UserId, GroupId;
        internal int Padding;
        internal ulong DeviceType;
        internal long Size, BlockSize, Blocks;
        internal Timespec Access, Modification, Change;
        internal long Reserved0, Reserved1, Reserved2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        internal long Seconds, Nanoseconds;
    }
}
