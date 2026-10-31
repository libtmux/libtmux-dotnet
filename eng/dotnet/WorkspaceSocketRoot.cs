using System.Collections;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace LibTmux.Engineering;

/// <summary>Keeps this repository's tmux out of the socket root other ports share.</summary>
/// <remarks>
/// <para>
/// Every libtmux port on this machine can reach the default socket root, so
/// one port's cleanup can kill another's servers mid-run. This must run before
/// the first server starts, because claiming the root after does not move it.
/// </para>
/// <para>
/// tmux follows an inherited <c>TMUX</c> to the server its caller is inside,
/// ahead of <c>TMUX_TMPDIR</c>, and <c>TMUX_PANE</c> retargets a command that
/// names no pane. A suite started from a tmux pane would otherwise reach that
/// pane's server, so both are cleared along with every <c>LIBTMUX_SOCKET*</c>
/// variable, and the process refuses to run if any survives.
/// </para>
/// <para>
/// The sockets of one run live in a directory of its own, made for that run
/// under a shared parent. Two test assemblies started together therefore never
/// meet at <c>&lt;root&gt;/tmux-&lt;uid&gt;/default</c>, and a server left at
/// that path when the process exits fails the run.
/// </para>
/// <para>
/// A run's directory goes when the run ends and no server in it still answers.
/// A run that was killed leaves its directory behind, so the next run removes
/// the directories of runs that no process holds, that are over a minute old,
/// and that no server answers in.
/// </para>
/// </remarks>
internal static class WorkspaceSocketRoot
{
    /// <summary>The variable that moves the shared parent of the per-run roots.</summary>
    internal const string RootVariable = "LIBTMUX_TEST_SOCKET_ROOT";

    /// <summary>The longest per-run root, in bytes, at which every socket path the suites build fits.</summary>
    internal const int MaximumRootLength = 39;

    /// <summary>The longest parent, in bytes, that leaves room for a run's directory in that root.</summary>
    internal const int MaximumParentLength = MaximumRootLength - RunNameLength - 1;

    private const int RunNameLength = 9;
    private const string DefaultParent = "/tmp/libtmux-dotnet-test";
    private const string SocketVariablePrefix = "LIBTMUX_SOCKET";
    private const string ClaimKey = "LibTmux.Engineering.TestSocketRoot";
    private const string WatchKey = "LibTmux.Engineering.TestSocketRoot.Watch";
    private const string LockKey = "LibTmux.Engineering.TestSocketRoot.Lock";
    private const string LockName = ".lock";
    private const int LeakExitCode = 78;

    private static readonly TimeSpan AbandonedAfter = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SettleFor = TimeSpan.FromSeconds(3);

    private static readonly UnixFileMode OwnerOnly =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>The directory this process's tmux sockets live under, which no other run shares.</summary>
    internal static readonly string Root = ClaimRoot();

    /// <summary>Points tmux and the temporary directory at <see cref="Root"/>.</summary>
    /// <remarks>
    /// Covers both socket routes: tmux reads <c>TMUX_TMPDIR</c> for a <c>-L</c>
    /// socket, while a <c>-S</c> path ignores it and is built from <c>TMPDIR</c> instead.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The process could still reach a server it does not own.</exception>
    [ModuleInitializer]
    internal static void Claim()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (string name in Selectors(Snapshot().Keys))
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        Environment.SetEnvironmentVariable("TMUX_TMPDIR", Root);
        Environment.SetEnvironmentVariable("TMPDIR", Root);
        try
        {
            Require();
        }
        catch (InvalidOperationException)
        {
            // A process that refuses to run leaves nothing behind.
            Abandon(Root);
            throw;
        }

        WatchForLeaks();
    }

    /// <summary>Refuses to go on when this process could reach a server it does not own.</summary>
    /// <exception cref="InvalidOperationException">A variable or the root would route tmux outside the sandbox.</exception>
    internal static void Require() => Require(Snapshot());

    /// <summary>Refuses to go on when an environment could reach a server it does not own.</summary>
    /// <param name="environment">The environment a tmux process would inherit.</param>
    /// <exception cref="InvalidOperationException">A variable or the root would route tmux outside the sandbox.</exception>
    internal static void Require(IReadOnlyDictionary<string, string?> environment)
    {
        IReadOnlyList<string> findings = Findings(environment, Root);
        if (findings.Count > 0)
        {
            throw new InvalidOperationException(
                "This test process could reach a tmux server it does not own: "
                + string.Join("; ", findings)
                + ". Run the tests through the documented command rather than around"
                + " eng/dotnet/WorkspaceSocketRoot.cs.");
        }
    }

    /// <summary>Names what would send a tmux process to a server outside the root.</summary>
    /// <param name="environment">The environment a tmux process would inherit.</param>
    /// <param name="root">The directory that should hold every socket.</param>
    /// <returns>One line per finding, naming variables and never their values.</returns>
    internal static IReadOnlyList<string> Findings(
        IReadOnlyDictionary<string, string?> environment,
        string root)
    {
        ArgumentNullException.ThrowIfNull(environment);
        List<string> findings = [];
        foreach (string name in Selectors(environment.Keys).Order(StringComparer.Ordinal))
        {
            findings.Add($"{name} is set");
        }

        if (!IsPrivateRoot(root))
        {
            findings.Add("the test root is not a short absolute directory of its own");
        }
        else if (!environment.TryGetValue("TMUX_TMPDIR", out string? routed)
            || !string.Equals(routed, root, StringComparison.Ordinal))
        {
            findings.Add("TMUX_TMPDIR is not the test root");
        }

        return findings;
    }

    /// <summary>Takes the selectors out of one child's environment.</summary>
    /// <param name="startInfo">The child process description.</param>
    /// <remarks>
    /// A child inherits its parent's environment. This is for a launch that
    /// must not depend on the parent having been cleaned.
    /// </remarks>
    internal static void Isolate(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        string[] names = [.. Selectors(startInfo.Environment.Keys)];
        foreach (string name in names)
        {
            startInfo.Environment.Remove(name);
        }
    }

    /// <summary>Reads this process's environment.</summary>
    /// <returns>Every variable, by name.</returns>
    internal static Dictionary<string, string?> Snapshot()
    {
        Dictionary<string, string?> environment = new(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            environment[(string)entry.Key] = entry.Value?.ToString();
        }

        return environment;
    }

    /// <summary>Chooses the directory the per-run roots are made in.</summary>
    /// <param name="chosen">The value of <see cref="RootVariable"/>, if any.</param>
    /// <returns>The parent, which exists and is owner-only.</returns>
    /// <remarks>
    /// The default parent is this repository's own and may hold earlier runs,
    /// so it is tightened rather than refused. A parent a caller names is
    /// refused unless it is short, owner-only, not a tmux root, and holds
    /// nothing but earlier runs: a developer's own directory is never made into
    /// a test root.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The parent could hold a server that is not a test's.</exception>
    [UnsupportedOSPlatform("windows")]
    internal static string ChooseParent(string? chosen)
    {
        if (string.IsNullOrWhiteSpace(chosen))
        {
            Directory.CreateDirectory(DefaultParent, OwnerOnly);
            try
            {
                File.SetUnixFileMode(DefaultParent, OwnerOnly);
            }
            catch (Exception failure) when (failure is UnauthorizedAccessException or IOException)
            {
                throw new InvalidOperationException(
                    $"{DefaultParent} is not an owner-only directory and cannot be made one.",
                    failure);
            }

            return DefaultParent;
        }

        string parent = Path.TrimEndingDirectorySeparator(chosen);
        if (!IsPrivateRoot(parent) || Utf8Length(parent) > MaximumParentLength)
        {
            throw new InvalidOperationException(
                $"{RootVariable} must name a short absolute directory of its own.");
        }

        if (Directory.Exists(parent))
        {
            if ((File.GetUnixFileMode(parent) & ~OwnerOnly) != 0)
            {
                throw new InvalidOperationException($"{RootVariable} must name an owner-only directory.");
            }

            bool foreign = Directory.EnumerateFileSystemEntries(parent)
                .Select(static entry => Path.GetFileName(entry))
                .Any(static name => !IsRunName(name));
            if (foreign)
            {
                throw new InvalidOperationException(
                    $"{RootVariable} must name a directory that holds nothing but earlier test runs, "
                    + "and not one that is already a tmux root.");
            }
        }
        else
        {
            Directory.CreateDirectory(parent, OwnerOnly);
        }

        return parent;
    }

    /// <summary>Makes a directory for one run, which nothing else uses.</summary>
    /// <param name="parent">The directory that holds the runs.</param>
    /// <returns>The new owner-only directory.</returns>
    [UnsupportedOSPlatform("windows")]
    internal static string NewRunDirectory(string parent)
    {
        for (int attempt = 0; attempt < 16; attempt++)
        {
            string candidate = Path.Combine(parent, RunName());
            if (Directory.Exists(candidate) || File.Exists(candidate))
            {
                continue;
            }

            Directory.CreateDirectory(candidate, OwnerOnly);
            File.SetUnixFileMode(candidate, OwnerOnly);
            return candidate;
        }

        throw new InvalidOperationException("No unused test run directory could be made in the parent.");
    }

    /// <summary>Removes the directories of earlier runs that nothing holds, and says how many.</summary>
    /// <param name="parent">The directory that holds the runs.</param>
    /// <returns>The number of directories removed.</returns>
    /// <remarks>
    /// A run holds a lock on a file in its directory for as long as it lives,
    /// so a killed run is told from a running one. A directory under a minute
    /// old is left alone: its owner may not have taken the lock yet. A server
    /// that still answers keeps its directory.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    internal static int PruneEarlierRuns(string parent)
    {
        int removed = 0;
        foreach (string run in Directory.EnumerateDirectories(parent).Where(static run => IsRunName(Path.GetFileName(run))))
        {
            if (IsAbandoned(run) && TryDelete(run))
            {
                removed++;
            }
        }

        return removed;
    }

    /// <summary>Removes a run's directory once no server in it answers, and says whether it did.</summary>
    /// <param name="root">The run's directory.</param>
    /// <param name="settle">How long a server that was just stopped has to stop answering.</param>
    /// <returns><see langword="true"/> when the directory is gone.</returns>
    /// <remarks>
    /// tmux leaves the socket of a server that was stopped, so a socket is
    /// dead when connecting to it fails, not when it is absent.
    /// </remarks>
    internal static bool RemoveRunWhenNoServerAnswers(string root, TimeSpan settle)
    {
        DateTime deadline = DateTime.UtcNow + settle;
        while (AnyServerAnswers(root) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(50);
        }

        return !AnyServerAnswers(root) && TryDelete(root);
    }

    /// <summary>Finds every server left under a run's directory, stops each by its path, and says so.</summary>
    /// <param name="root">The run's directory, searched whole.</param>
    /// <param name="tmux">The tmux executable.</param>
    /// <param name="settle">How long a server that was just stopped has to stop answering.</param>
    /// <returns>One line per server found, and one for each that did not stop. Names only.</returns>
    /// <remarks>
    /// <para>
    /// A server counts when a connect to its socket succeeds, wherever under the
    /// root the socket is: a server started with an explicit <c>-S</c> is found
    /// as well as one on <c>tmux-&lt;uid&gt;/default</c>. A socket that nothing
    /// answers on is no leak, except that the default socket counts while its
    /// file is there.
    /// </para>
    /// <para>
    /// The stop names the socket with <c>-S</c> and is never given a path
    /// outside <paramref name="root"/>, nor one that goes through a link, which
    /// is not followed in the search either. The sockets the .NET runtime keeps
    /// in the temporary directory are not servers. A server has stopped when a
    /// connect to its socket fails.
    /// </para>
    /// </remarks>
    internal static List<string> StopLeakedServers(string root, string tmux, TimeSpan settle)
    {
        List<string> leaks = [];
        if (!Directory.Exists(root))
        {
            return leaks;
        }

        List<string> stopped = [];
        foreach (string directory in Directory.EnumerateDirectories(root, "tmux-*").Order(StringComparer.Ordinal))
        {
            string socket = Path.Combine(directory, "default");
            if (!File.Exists(socket))
            {
                continue;
            }

            leaks.Add("a server was left on the default socket under the test root");
            if (new DirectoryInfo(directory).LinkTarget is null && new FileInfo(socket).LinkTarget is null)
            {
                stopped.Add(socket);
                StopServer(tmux, socket);
            }
        }

        foreach (string socket in LiveServers(root).Order(StringComparer.Ordinal))
        {
            if (stopped.Contains(socket))
            {
                continue;
            }

            leaks.Add("a server was left running under the test root");
            stopped.Add(socket);
            StopServer(tmux, socket);
        }

        DateTime deadline = DateTime.UtcNow + settle;
        foreach (string socket in stopped)
        {
            while (Answers(socket) && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }

            if (Answers(socket))
            {
                leaks.Add("a server did not stop when asked");
            }
        }

        return leaks;
    }

    /// <summary>Ends a run: stops what it left, and removes its directory unless a server still answers in it.</summary>
    /// <param name="root">The run's directory.</param>
    /// <param name="tmux">The tmux executable.</param>
    /// <param name="settle">How long a server that was just stopped has to stop answering.</param>
    /// <returns>What was found, and whether the directory is gone.</returns>
    internal static (List<string> Findings, bool Removed) EndRun(string root, string tmux, TimeSpan settle)
    {
        List<string> findings = StopLeakedServers(root, tmux, settle);
        return (findings, RemoveRunWhenNoServerAnswers(root, TimeSpan.Zero));
    }

    private static string ClaimRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            return DefaultParent;
        }

        // A second assembly in this process that carries this file takes up
        // the root the first one made, rather than making another.
        if (AppDomain.CurrentDomain.GetData(ClaimKey) is string claimed)
        {
            return claimed;
        }

        string parent = ChooseParent(Environment.GetEnvironmentVariable(RootVariable));
        _ = PruneEarlierRuns(parent);
        string root = NewRunDirectory(parent);
        HoldRun(root);
        AppDomain.CurrentDomain.SetData(ClaimKey, root);
        return root;
    }

    // The lock lives as long as the process does, and the system lets go of it
    // when the process is killed.
    [UnsupportedOSPlatform("windows")]
    private static void HoldRun(string root)
    {
        FileStream held = new(
            Path.Combine(root, LockName),
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
        AppDomain.CurrentDomain.SetData(LockKey, held);
    }

    private static void ReleaseRun()
    {
        (AppDomain.CurrentDomain.GetData(LockKey) as FileStream)?.Dispose();
        AppDomain.CurrentDomain.SetData(LockKey, null);
    }

    private static void Abandon(string root)
    {
        ReleaseRun();
        _ = TryDelete(root);
    }

    [UnsupportedOSPlatform("windows")]
    private static bool IsAbandoned(string run)
    {
        DirectoryInfo directory = new(run);
        if (directory.LinkTarget is not null || DateTime.UtcNow - directory.LastWriteTimeUtc < AbandonedAfter)
        {
            return false;
        }

        string held = Path.Combine(run, LockName);
        if (File.Exists(held))
        {
            try
            {
                using FileStream probe = new(held, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        return !AnyServerAnswers(run);
    }

    private static bool AnyServerAnswers(string root) => Directory.Exists(root) && LiveServers(root).Any();

    // Every socket under a directory that something answers on, whichever
    // directory it is in and never through a link.
    private static IEnumerable<string> LiveServers(string root) =>
        FilesUnder(root)
            .Where(static file => !IsRuntimeSocket(file) && Path.GetFileName(file) != LockName)
            .Where(static file => Answers(file));

    private static IEnumerable<string> FilesUnder(string directory)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            FileInfo info = new(entry);
            FileAttributes attributes;
            try
            {
                if (info.LinkTarget is not null)
                {
                    continue;
                }

                attributes = info.Attributes;
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (!attributes.HasFlag(FileAttributes.Directory))
            {
                yield return entry;
                continue;
            }

            foreach (string inner in FilesUnder(entry))
            {
                yield return inner;
            }
        }
    }

    // The runtime keeps sockets of its own in the temporary directory, which is
    // the root, and they answer for as long as the process lives.
    private static bool IsRuntimeSocket(string path)
    {
        string name = Path.GetFileName(path);
        return name.StartsWith("dotnet-diagnostic-", StringComparison.Ordinal)
            || name.StartsWith("clr-debug-pipe-", StringComparison.Ordinal);
    }

    private static bool Answers(string socket)
    {
        try
        {
            using Socket connection = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            Task connecting = connection.ConnectAsync(new UnixDomainSocketEndPoint(socket));

            // A server too busy to accept still holds the socket.
            return !connecting.Wait(TimeSpan.FromSeconds(1)) || connecting.IsCompletedSuccessfully;
        }
        catch (AggregateException failure) when (failure.InnerException is SocketException busy && IsBusy(busy))
        {
            return true;
        }
        catch (SocketException busy) when (IsBusy(busy))
        {
            return true;
        }
        catch (Exception failure) when (failure is AggregateException or SocketException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    // A server whose queue of waiting clients is full refuses to wait for one
    // more, and still holds the socket.
    private static bool IsBusy(SocketException failure) =>
        failure.SocketErrorCode is SocketError.WouldBlock or SocketError.TryAgain;

    private static bool TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void WatchForLeaks()
    {
        if (AppDomain.CurrentDomain.GetData(WatchKey) is not null)
        {
            return;
        }

        AppDomain.CurrentDomain.SetData(WatchKey, true);
        AppDomain.CurrentDomain.ProcessExit += static (_, _) =>
        {
            ReleaseRun();
            (List<string> findings, bool removed) = EndRun(
                Root,
                Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux",
                SettleFor);
            foreach (string finding in findings)
            {
                Console.Error.WriteLine($"Sandbox: {finding}.");
            }

            if (findings.Count > 0)
            {
                Environment.ExitCode = LeakExitCode;
            }

            // A directory that still holds a live server is kept, so it can be found.
            if (!removed)
            {
                Console.Error.WriteLine("Sandbox: a server is still running under the test root, so its directory was kept.");
            }
        };
    }

    private static void StopServer(string tmux, string socket)
    {
        ProcessStartInfo start = new(tmux)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        Isolate(start);
        start.ArgumentList.Add("-S");
        start.ArgumentList.Add(socket);
        start.ArgumentList.Add("kill-server");
        try
        {
            using Process? process = Process.Start(start);
            if (process is not null && !process.WaitForExit(TimeSpan.FromSeconds(10)))
            {
                process.Kill();
            }
        }
        catch (Exception failure) when (failure is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    private static IEnumerable<string> Selectors(IEnumerable<string> names) =>
        names.Where(static name =>
            string.Equals(name, "TMUX", StringComparison.Ordinal)
            || string.Equals(name, "TMUX_PANE", StringComparison.Ordinal)
            || name.StartsWith(SocketVariablePrefix, StringComparison.Ordinal));

    // A root with fewer than two segments is /tmp or the like, where other
    // users' and other ports' servers live. A long one leaves no room for the
    // socket name in the address tmux binds.
    private static bool IsPrivateRoot(string root) =>
        Path.IsPathFullyQualified(root)
        && Utf8Length(root) <= MaximumRootLength
        && !root.Split('/').Contains("..")
        && root.Split('/', StringSplitOptions.RemoveEmptyEntries).Length >= 2;

    private static int Utf8Length(string text) => Encoding.UTF8.GetByteCount(text);

    private static string RunName() =>
        "r" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

    private static bool IsRunName(string name) =>
        name.Length == 9
        && name[0] == 'r'
        && name.Skip(1).All(static character => char.IsAsciiHexDigitLower(character));
}
