using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LibTmux.Internal;

/// <summary>Reads which file a path names: its device and inode, following symlinks.</summary>
/// <remarks>
/// A path that names another file than it did names another tmux daemon when
/// it is a socket, which is what the shared control client checks before it
/// carries a command. It costs one system call.
/// </remarks>
[UnsupportedOSPlatform("windows")]
internal static class UnixFileIdentity
{
    private const int StatBufferBytes = 256;

    /// <summary>Reads the identity of a path.</summary>
    /// <param name="path">The path.</param>
    /// <returns>The device and inode, or null when the path names nothing or the platform's layout is not known.</returns>
    internal static (ulong Device, ulong Inode)? TryRead(string path)
    {
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
        {
            return null;
        }

        nint buffer = Marshal.AllocHGlobal(StatBufferBytes);
        nint encoded = Marshal.StringToCoTaskMemUTF8(path);
        try
        {
            if (InvokeStat(encoded, buffer) != 0)
            {
                return null;
            }

            return OperatingSystem.IsLinux()
                ? (unchecked((ulong)Marshal.ReadInt64(buffer, 0)), unchecked((ulong)Marshal.ReadInt64(buffer, 8)))
                : (unchecked((uint)Marshal.ReadInt32(buffer, 0)), unchecked((ulong)Marshal.ReadInt64(buffer, 8)));
        }
        catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(encoded);
            Marshal.FreeHGlobal(buffer);
        }
    }

    // macOS on x86-64 keeps the 64-bit inode layout behind another entry point.
    private static int InvokeStat(nint path, nint buffer) =>
        OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? StatDarwinInode64(path, buffer)
            : Stat(path, buffer);

    /// <summary>Gets the path tmux gives a named socket, or null where it has none.</summary>
    /// <param name="name">The socket name given with -L.</param>
    /// <returns>The path, derived as tmux derives it.</returns>
    internal static string? NamedSocketPath(string name)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        string directory = Environment.GetEnvironmentVariable("TMUX_TMPDIR") is { Length: > 0 } configured
            ? configured
            : "/tmp";
        return System.IO.Path.Combine(
            directory,
            "tmux-" + GetUid().ToString(System.Globalization.CultureInfo.InvariantCulture),
            name);
    }

    [DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetUid();

    [DllImport("libc", EntryPoint = "stat")]
    private static extern int Stat(nint path, nint buffer);

    [DllImport("libc", EntryPoint = "stat$INODE64")]
    private static extern int StatDarwinInode64(nint path, nint buffer);
}
