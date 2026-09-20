using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LibTmux.Workspace;

internal static class WorkspaceOwnedProcessTree
{
    private const int Stop = 19, Continue = 18, Kill = 9, Gone = 3;
    private sealed record Identity(int Pid, int ParentPid, ulong Started, char State);
    private sealed record Node(Identity Identity, SafeFileHandle Handle);
    internal sealed class Hooks
    {
        internal Action? BeforePreflightRead { get; init; }
        internal Func<int, IEnumerable<int>>? ExtraCandidates { get; init; }
        internal Action<int>? AfterAdmitted { get; init; }
        internal Action<int>? AfterStopped { get; init; }
    }

    internal static bool Available(Hooks? hooks = null)
    {
        if (!OperatingSystem.IsLinux()) return false;
        try
        {
            hooks?.BeforePreflightRead?.Invoke();
            if (Read("/proc/self/stat")?.Pid != Environment.ProcessId
                || !SamePidNamespace(File.ReadAllText("/proc/self/status")))
                return false;
            using SafeFileHandle? self = Open(Environment.ProcessId);
            return self is not null && Signal(self, 0)
                && File.Exists("/proc/thread-self/children");
        }
        catch (Exception failure) when (failure is EntryPointNotFoundException or DllNotFoundException
            or Win32Exception or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool SamePidNamespace(string status)
    {
        string? line = status.Split('\n').FirstOrDefault(value => value.StartsWith("NSpid:", StringComparison.Ordinal));
        string[]? ids = line?[6..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return ids is [string value] && int.TryParse(value, CultureInfo.InvariantCulture, out int pid)
            && pid == Environment.ProcessId;
    }

    internal static async Task<bool> TryKillAsync(Process root, Hooks? hooks = null)
    {
        if (!Available(hooks))
            return false;
        List<Node> admitted = [];
        HashSet<(int, ulong)> seen = [];
        long deadline = Stopwatch.GetTimestamp();
        try
        {
            SafeFileHandle? rootHandle = Open(root.Id);
            if (rootHandle is null)
                return true;
            bool transferred = false;
            try
            {
                Identity? rootIdentity = Read($"/proc/{root.Id}/stat");
                // The retained Process disambiguates a root PID reused before pidfd_open.
                if (root.HasExited || rootIdentity is null)
                    return true;
                if (rootIdentity.ParentPid != Environment.ProcessId)
                    throw new InvalidOperationException("The admitted root is not this caller's direct child.");
                Node rootNode = new(rootIdentity, rootHandle);
                admitted.Add(rootNode);
                transferred = true;
                seen.Add((rootIdentity.Pid, rootIdentity.Started));
                await VisitAsync(rootNode).ConfigureAwait(false);
            }
            finally
            {
                if (!transferred)
                    rootHandle.Dispose();
            }
            for (int index = admitted.Count - 1; index >= 0; index--)
                Signal(admitted[index].Handle, Kill);
            return true;
        }
        catch (Exception original)
        {
            List<Exception> failures = [original];
            foreach (Node node in admitted)
            {
                try { Signal(node.Handle, Continue); }
                catch (Exception resumeFailure) { failures.Add(resumeFailure); }
            }
            if (failures.Count > 1)
                throw new AggregateException("Discovery failed; one or more owned processes could not be resumed.", failures);
            throw;
        }
        finally
        {
            foreach (Node node in admitted)
                node.Handle.Dispose();
        }

        async Task VisitAsync(Node node)
        {
            if (!Signal(node.Handle, Stop))
            {
                throw CoverageLost(node);
            }
            while (true)
            {
                if (!StillSame(node))
                {
                    throw CoverageLost(node);
                }
                string[] tasks = Directory.GetDirectories($"/proc/{node.Identity.Pid}/task");
                bool stopped = tasks.Length != 0;
                foreach (string task in tasks)
                {
                    Identity? identity = Read(Path.Combine(task, "stat"));
                    if (identity is not null && identity.State is not ('T' or 't' or 'Z' or 'X'))
                        stopped = false;
                }
                if (stopped)
                    break;
                if (Stopwatch.GetElapsedTime(deadline) >= TimeSpan.FromMilliseconds(500))
                    throw new TimeoutException("The owned threads did not acknowledge SIGSTOP within discovery's budget.");
                await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            }
            hooks?.AfterStopped?.Invoke(node.Identity.Pid);
            HashSet<int> children = [];
            foreach (string task in Directory.EnumerateDirectories($"/proc/{node.Identity.Pid}/task"))
            {
                foreach (string value in File.ReadAllText(Path.Combine(task, "children")).Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    children.Add(int.Parse(value, CultureInfo.InvariantCulture));
            }
            if (hooks?.ExtraCandidates is not null)
                children.UnionWith(hooks.ExtraCandidates(node.Identity.Pid));
            foreach (int pid in children)
            {
                SafeFileHandle? handle = Open(pid);
                if (handle is null)
                {
                    continue;
                }
                bool transferred = false;
                try
                {
                    Identity? candidate = Read($"/proc/{pid}/stat");
                    if (candidate is null || candidate.State is 'Z' or 'X' || !Signal(handle, 0))
                    {
                        continue;
                    }
                    if (!StillSame(node))
                        throw CoverageLost(node);
                    if (candidate.ParentPid != node.Identity.Pid)
                    {
                        continue;
                    }
                    if (!seen.Add((pid, candidate.Started)))
                    {
                        continue;
                    }
                    Node child = new(candidate, handle);
                    admitted.Add(child);
                    transferred = true;
                    hooks?.AfterAdmitted?.Invoke(pid);
                    await VisitAsync(child).ConfigureAwait(false);
                }
                finally { if (!transferred) handle.Dispose(); }
            }
            if (!StillSame(node))
                throw CoverageLost(node);
        }
    }

    private static bool StillSame(Node node) =>
        Read($"/proc/{node.Identity.Pid}/stat") is Identity current
        && current.State is not ('Z' or 'X')
        && current.Started == node.Identity.Started && Signal(node.Handle, 0);

    private static InvalidOperationException CoverageLost(Node node) =>
        new($"Admitted process {node.Identity.Pid} exited before its descendants were fully inspected; cleanup coverage is unknown.");

    private static Identity? Read(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        int split = text.LastIndexOf(')');
        string[] rest = text[(split + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return new(int.Parse(text.AsSpan(0, text.IndexOf(' ')), CultureInfo.InvariantCulture),
            int.Parse(rest[1], CultureInfo.InvariantCulture), ulong.Parse(rest[19], CultureInfo.InvariantCulture), rest[0][0]);
    }

    private static SafeFileHandle? Open(int pid)
    {
        int fd = pidfd_open(pid, 0);
        if (fd >= 0)
            return new(new IntPtr(fd), ownsHandle: true);
        int error = Marshal.GetLastPInvokeError();
        if (error == Gone)
            return null;
        throw new Win32Exception(error);
    }

    private static bool Signal(SafeFileHandle handle, int signal)
    {
        if (pidfd_send_signal(handle, signal, IntPtr.Zero, 0) == 0)
            return true;
        int error = Marshal.GetLastPInvokeError();
        if (error == Gone)
            return false;
        throw new Win32Exception(error);
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", SetLastError = true)]
    private static extern int pidfd_open(int pid, uint flags);
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", SetLastError = true)]
    private static extern int pidfd_send_signal(SafeFileHandle fd, int signal, IntPtr info, uint flags);
}
