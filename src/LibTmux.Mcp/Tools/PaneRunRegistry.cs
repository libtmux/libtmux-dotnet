using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ModelContextProtocol;

namespace LibTmux.Mcp;

/// <summary>Coordinates pane input across MCP tool instances in this process.</summary>
[UnsupportedOSPlatform("windows")]
internal static class PaneRunRegistry
{
    private static readonly object Gate = new();
    private static readonly Dictionary<PaneRunIdentity, Reservation> Active = [];

    internal static PaneRunLease Acquire(Pane pane)
    {
        PaneRunIdentity identity = PaneRunIdentity.For(pane);
        Guid token = Guid.NewGuid();
        lock (Gate)
        {
            if (Active.TryGetValue(identity, out Reservation occupied))
            {
                throw Conflict(identity.PaneId.ToString(), "run_shell_command", occupied.Kind);
            }

            Active.Add(identity, new Reservation(token, ReservationKind.Run));
        }

        return new PaneRunLease(identity, token);
    }

    internal static PaneInputLease? Authorize(
        IReadOnlyList<Pane> panes,
        PaneRunLease? runOwner,
        PaneInputLease? inputOwner,
        string toolName,
        bool reserveDispatch)
    {
        PaneRunIdentity[] identities = [.. panes.Select(PaneRunIdentity.For)];
        lock (Gate)
        {
            if (runOwner is not null)
            {
                if (inputOwner is not null
                    || reserveDispatch
                    || identities.Length != 1
                    || !runOwner.OwnsLocked(identities[0]))
                {
                    throw new McpException(
                        "run_shell_command lost its pane reservation or route before "
                        + "dispatch. The command was not sent.");
                }

                return null;
            }

            if (inputOwner is not null)
            {
                if (reserveDispatch || !inputOwner.OwnsLocked(identities))
                {
                    throw new McpException(
                        $"{toolName} refuses because its configured pane membership or "
                        + "input reservation changed before dispatch.");
                }

                return null;
            }

            foreach (PaneRunIdentity identity in identities)
            {
                if (Active.TryGetValue(identity, out Reservation occupied))
                {
                    throw Conflict(identity.PaneId.ToString(), toolName, occupied.Kind);
                }
            }

            if (!reserveDispatch)
            {
                return null;
            }

            Guid token = Guid.NewGuid();
            foreach (PaneRunIdentity identity in identities)
            {
                Active.Add(identity, new Reservation(token, ReservationKind.Input));
            }

            return new PaneInputLease(identities, token);
        }
    }

    private static McpException Conflict(
        string paneId,
        string toolName,
        ReservationKind kind) =>
        kind == ReservationKind.Run
            ? new McpException(
                $"{toolName} refuses pane {paneId} because a command started by this MCP "
                + "process is still active. Wait for that run to finish before sending "
                + "more input.")
            : new McpException(
                $"{toolName} refuses pane {paneId} because another pane-input dispatch "
                + "is in progress. Wait for it to finish and retry.");

    private static void Release(
        IReadOnlyList<PaneRunIdentity> identities,
        Guid token,
        ReservationKind kind)
    {
        lock (Gate)
        {
            foreach (PaneRunIdentity identity in identities)
            {
                if (Active.TryGetValue(identity, out Reservation reservation)
                    && reservation.Token == token
                    && reservation.Kind == kind)
                {
                    _ = Active.Remove(identity);
                }
            }
        }
    }

    internal sealed class PaneRunLease
    {
        private readonly PaneRunIdentity _identity;
        private readonly Guid _token;
        private int _released;

        internal PaneRunLease(PaneRunIdentity identity, Guid token)
        {
            _identity = identity;
            _token = token;
        }

        internal bool OwnsLocked(PaneRunIdentity identity) =>
            Volatile.Read(ref _released) == 0
            && _identity == identity
            && Active.TryGetValue(identity, out Reservation reservation)
            && reservation.Token == _token
            && reservation.Kind == ReservationKind.Run;

        internal void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                PaneRunRegistry.Release([_identity], _token, ReservationKind.Run);
            }
        }
    }

    internal sealed class PaneInputLease
    {
        private readonly PaneRunIdentity[] _identities;
        private readonly Guid _token;
        private int _released;

        internal PaneInputLease(PaneRunIdentity[] identities, Guid token)
        {
            _identities = identities;
            _token = token;
        }

        internal bool OwnsLocked(PaneRunIdentity[] identities) =>
            Volatile.Read(ref _released) == 0
            && _identities.AsSpan().SequenceEqual(identities)
            && identities.All(identity =>
                Active.TryGetValue(identity, out Reservation reservation)
                && reservation.Token == _token
                && reservation.Kind == ReservationKind.Input);

        internal void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                PaneRunRegistry.Release(_identities, _token, ReservationKind.Input);
            }
        }
    }

    internal readonly record struct PaneRunIdentity(
        PaneInputEndpointIdentity Endpoint,
        ServerGeneration Generation,
        PaneId PaneId)
    {
        [UnsupportedOSPlatform("windows")]
        internal static PaneRunIdentity For(Pane pane)
        {
            return new PaneRunIdentity(
                PaneInputEndpoint.From(pane),
                pane.Generation,
                pane.Id);
        }
    }

    private readonly record struct Reservation(Guid Token, ReservationKind Kind);

    private enum ReservationKind
    {
        Run,
        Input,
    }
}

[UnsupportedOSPlatform("windows")]
internal readonly record struct PaneInputEndpointIdentity(ulong Device, ulong Inode);

[UnsupportedOSPlatform("windows")]
internal static class PaneInputEndpoint
{
    private const uint FileTypeMask = 0xF000;
    private const uint SocketType = 0xC000;
    private const int StatBufferBytes = 256;

    internal static PaneInputEndpointIdentity From(Pane pane)
    {
        if (!pane.RawFormatFields.TryGetValue("socket_path", out string? path)
            || string.IsNullOrWhiteSpace(path))
        {
            throw new McpException(
                "Pane input route is refused because its tmux socket path is unavailable.");
        }

        return Identify(path, "pane input route socket path");
    }

    /// <summary>Answers whether two paths name one directory.</summary>
    /// <param name="first">A path.</param>
    /// <param name="second">Another path.</param>
    /// <returns><see langword="true" /> when both name the same directory.</returns>
    /// <remarks>
    /// Spelling does not settle it. macOS reaches its temporary directory
    /// through a symlink, so a caller asking for <c>/tmp</c> lands in
    /// <c>/private/tmp</c> and a string comparison reads that as a different
    /// directory. Device and inode are what "the same directory" means, and a
    /// path that cannot be stat'd is not the same as one that can.
    /// </remarks>
    internal static bool SameDirectory(string first, string second)
    {
        return TryIdentify(first) is (ulong, ulong) left
            && TryIdentify(second) is (ulong, ulong) right
            && left == right;
    }

    private static (ulong Device, ulong Inode)? TryIdentify(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return null;
        }

        nint buffer = Marshal.AllocHGlobal(StatBufferBytes);
        try
        {
            nint encoded = Marshal.StringToCoTaskMemUTF8(Path.GetFullPath(path));
            int result;
            try
            {
                result = InvokeStat(encoded, buffer);
            }
            finally
            {
                Marshal.FreeCoTaskMem(encoded);
            }

            if (result != 0)
            {
                return null;
            }

            (ulong device, ulong inode, uint _) = ReadIdentity(buffer, "directory");
            return (device, inode);
        }
        catch (McpException)
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static PaneInputEndpointIdentity Identify(string path, string subject)
    {
        McpStartup.RequireSafeRouteValue(path, subject);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new McpException($"{subject} must be absolute.");
        }

        nint buffer = Marshal.AllocHGlobal(StatBufferBytes);
        try
        {
            string full = Path.GetFullPath(path);
            nint encodedPath = Marshal.StringToCoTaskMemUTF8(full);
            int result;
            int error;
            try
            {
                result = InvokeStat(encodedPath, buffer);
                error = Marshal.GetLastPInvokeError();
            }
            finally
            {
                Marshal.FreeCoTaskMem(encodedPath);
            }

            if (result != 0)
            {
                throw new Win32Exception(error);
            }

            (ulong device, ulong inode, uint mode) = ReadIdentity(buffer, subject);
            if ((mode & FileTypeMask) != SocketType)
            {
                throw new McpException($"{subject} must identify a Unix socket.");
            }

            return new PaneInputEndpointIdentity(device, inode);
        }
        catch (Exception error) when (
            error is ArgumentException
                or Win32Exception
                or IOException
                or NotSupportedException
                or UnauthorizedAccessException)
        {
            throw new McpException($"{subject} cannot be resolved safely.", error);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static (ulong Device, ulong Inode, uint Mode) ReadIdentity(
        nint buffer,
        string subject)
    {
        if (OperatingSystem.IsLinux())
        {
            return (
                unchecked((ulong)Marshal.ReadInt64(buffer, 0)),
                unchecked((ulong)Marshal.ReadInt64(buffer, 8)),
                unchecked((uint)Marshal.ReadInt32(
                    buffer,
                    LinuxModeOffset(RuntimeInformation.ProcessArchitecture))));
        }

        if (OperatingSystem.IsMacOS())
        {
            return ReadMacOSIdentity(buffer);
        }

        throw new McpException($"{subject} cannot be identified on this platform.");
    }

    internal static (ulong Device, ulong Inode, uint Mode) ReadMacOSIdentity(nint buffer) =>
        (
            unchecked((uint)Marshal.ReadInt32(buffer, 0)),
            unchecked((ulong)Marshal.ReadInt64(buffer, 8)),
            unchecked((ushort)Marshal.ReadInt16(buffer, 4)));

    internal static int LinuxModeOffset(Architecture architecture) =>
        architecture switch
        {
            Architecture.X64 => 24,
            Architecture.Arm64 => 16,
            _ => throw new PlatformNotSupportedException(
                $"Linux {architecture} stat layout is not supported."),
        };

    internal static bool UseDarwinInode64EntryPoint(Architecture architecture) =>
        architecture switch
        {
            Architecture.X64 => true,
            Architecture.Arm64 => false,
            _ => throw new PlatformNotSupportedException(
                $"macOS {architecture} stat layout is not supported."),
        };

    private static int InvokeStat(nint path, nint buffer) =>
        OperatingSystem.IsMacOS()
            && UseDarwinInode64EntryPoint(RuntimeInformation.ProcessArchitecture)
                ? StatDarwinInode64(path, buffer)
                : Stat(path, buffer);

    /// <summary>Gets the real user id this process runs as.</summary>
    /// <remarks>
    /// tmux composes a named socket's path as
    /// <c>&lt;root&gt;/tmux-&lt;uid&gt;/&lt;name&gt;</c>, and .NET exposes no
    /// portable way to read the id, so composing that path without asking tmux
    /// needs this.
    /// </remarks>
    internal static uint UserId => GetUid();

    [DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetUid();

    [DllImport(
        "libc",
        EntryPoint = "stat",
        SetLastError = true)]
    private static extern int Stat(nint path, nint buffer);

    [DllImport(
        "libc",
        EntryPoint = "stat$INODE64",
        SetLastError = true)]
    private static extern int StatDarwinInode64(nint path, nint buffer);
}
