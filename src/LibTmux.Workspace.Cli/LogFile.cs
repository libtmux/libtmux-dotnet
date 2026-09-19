using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LibTmux.Workspace.Cli;

internal static partial class LogFile
{
    internal static StreamWriter Open(string path)
    {
        if (ModeField is not (int offset, bool wide))
            throw new CliException("log_file_unsupported", "Log files are not supported on this system.");
        const int writeOnly = 1, create = 0x40, noControllingTerminal = 0x100, append = 0x400, nonBlocking = 0x800, closeOnExec = 0x80000, noFollow = 0x20000;
        const uint ownerReadWrite = 0x180, fileTypeMask = 0xF000, regularFile = 0x8000;
        byte[] status = new byte[256];
        SafeFileHandle? handle = null;
        try
        {
            int inspected = PathStatus(path, ref status[0]);
            if (inspected == 0 && (Mode(status, offset, wide) & fileTypeMask) != regularFile) throw new IOException("The log destination must be a regular file.");
            if (inspected != 0 && Marshal.GetLastPInvokeError() != 2) throw new IOException(new Win32Exception(Marshal.GetLastPInvokeError()).Message);
            handle = OpenFile(path, writeOnly | create | noControllingTerminal | append | nonBlocking | closeOnExec | noFollow, ownerReadWrite);
            if (handle.IsInvalid || FileStatus(handle, ref status[0]) != 0) throw new IOException(new Win32Exception(Marshal.GetLastPInvokeError()).Message);
            if ((Mode(status, offset, wide) & fileTypeMask) != regularFile) throw new IOException("The log destination must be a regular file.");
            return new StreamWriter(new FileStream(handle, FileAccess.Write), new System.Text.UTF8Encoding(false));
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException)
        {
            handle?.Dispose();
            throw new CliException("log_file_unavailable", $"Cannot open log file '{path}': {failure.Message}");
        }
    }

    private static uint Mode(byte[] status, int offset, bool wide) =>
        wide ? BitConverter.ToUInt32(status, offset) : BitConverter.ToUInt16(status, offset);

    // Only st_mode is read, so this records that one field rather than a
    // whole struct stat: its byte offset, and whether it is 32 bits wide.
    // The kernel's generic 64-bit layout puts it at 16; x86-64 has its own
    // at 24. An ABI nobody has verified keeps the refusal above.
    private static (int Offset, bool Wide)? ModeField => OperatingSystem.IsLinux()
        ? RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => (24, true),
            Architecture.Arm64 => (16, true),
            _ => null,
        }
        : null;

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial SafeFileHandle OpenFile(string path, int flags, uint mode);

    [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static partial int FileStatus(SafeFileHandle handle, ref byte status);

    [LibraryImport("libc", EntryPoint = "lstat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int PathStatus(string path, ref byte status);
}
