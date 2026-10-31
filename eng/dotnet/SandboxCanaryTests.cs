using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using LibTmux.IntegrationTests.Infrastructure;

namespace LibTmux.Engineering;

/// <summary>Runs one test at a time because some of these tests rewrite the process environment.</summary>
[CollectionDefinition("Sandbox canary", DisableParallelization = true)]
public sealed class OneCanaryAtATime;

/// <summary>Shows what a tmux started from a test process is handed.</summary>
/// <remarks>
/// A recording stand-in for tmux sees the environment a real one would. The
/// first three tests fail if a variable that could reach another server gets
/// through; the rest fail if that measurement stops noticing one.
/// </remarks>
[Collection("Sandbox canary")]
[UnsupportedOSPlatform("windows")]
public sealed class SandboxCanaryTests
{
    private const string Secret = "hunter2-must-not-be-recorded";
    private const string ExitChildVariable = "LIBTMUX_CANARY_EXIT_CHILD";
    private const string ExitModeVariable = "LIBTMUX_CANARY_EXIT_MODE";

    private static readonly UnixFileMode OwnerOnly =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private static readonly string[] Decoys =
    [
        "TMUX",
        "TMUX_PANE",
        "LIBTMUX_SOCKET",
        "LIBTMUX_SOCKET_PATH",
        "LIBTMUX_SOCKET_NAME",
    ];

    [Fact]
    public void This_process_can_reach_no_server_outside_the_root()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "No tmux runs on Windows.");

        Assert.Empty(WorkspaceSocketRoot.Findings(
            WorkspaceSocketRoot.Snapshot(),
            WorkspaceSocketRoot.Root));
        Assert.True(
            Path.GetTempPath().StartsWith(WorkspaceSocketRoot.Root, StringComparison.Ordinal),
            "TMPDIR is not under the test root.");
    }

    // The assertions on paths and variables are boolean with a message that
    // names the check: a failed Equal prints both values.
    [Fact]
    public void This_run_has_a_root_of_its_own()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "No tmux runs on Windows.");
        string root = WorkspaceSocketRoot.Root;
        string? chosen = Environment.GetEnvironmentVariable(WorkspaceSocketRoot.RootVariable);
        string parent = string.IsNullOrWhiteSpace(chosen)
            ? "/tmp/libtmux-dotnet-test"
            : Path.TrimEndingDirectorySeparator(chosen);

        Assert.True(Path.GetDirectoryName(root) == parent, "The root is not directly under the parent.");
        Assert.True(Regex.IsMatch(Path.GetFileName(root), "^r[0-9a-f]{8}$"), "The root is not named like a run.");
        Assert.True(
            Encoding.UTF8.GetByteCount(root) <= WorkspaceSocketRoot.MaximumRootLength,
            "The root is longer than a socket path allows.");
        Assert.True(File.GetUnixFileMode(root) == OwnerOnly, "The root is not owner-only.");
        Assert.True(Environment.GetEnvironmentVariable("TMUX_TMPDIR") == root, "TMUX_TMPDIR is not the root.");
        Assert.True(Environment.GetEnvironmentVariable("TMPDIR") == root, "TMPDIR is not the root.");
    }

    [Fact]
    public void Two_runs_never_share_a_root()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "No tmux runs on Windows.");
        string parent = Path.GetDirectoryName(WorkspaceSocketRoot.Root)!;
        string first = WorkspaceSocketRoot.NewRunDirectory(parent);
        string second = WorkspaceSocketRoot.NewRunDirectory(parent);
        try
        {
            Assert.True(first != second, "Two runs were given one directory.");
            Assert.True(first != WorkspaceSocketRoot.Root && second != WorkspaceSocketRoot.Root, "A run was given this run's directory.");
            Assert.True(File.GetUnixFileMode(first) == OwnerOnly, "The first directory is not owner-only.");
            Assert.True(File.GetUnixFileMode(second) == OwnerOnly, "The second directory is not owner-only.");
        }
        finally
        {
            Directory.Delete(first);
            Directory.Delete(second);
        }
    }

    // The parents are made in a short directory of their own, not under Root:
    // a named parent over the limit is refused, so nesting these under a root
    // of any length would refuse them for the wrong reason.
    [Fact]
    public void A_named_parent_must_be_short_private_and_hold_only_earlier_runs()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "No tmux runs on Windows.");
        string work = ScratchDirectory("ltp");
        try
        {
            Assert.Throws<InvalidOperationException>(() => WorkspaceSocketRoot.ChooseParent("/tmp"));
            Assert.Throws<InvalidOperationException>(() => WorkspaceSocketRoot.ChooseParent("relative/parent"));
            Assert.Throws<InvalidOperationException>(
                () => WorkspaceSocketRoot.ChooseParent(Path.Combine(work, new string('x', 60))));

            // Few characters, too many bytes.
            Assert.Throws<InvalidOperationException>(
                () => WorkspaceSocketRoot.ChooseParent(Path.Combine(work, new string('\u00e9', 10))));

            string open = MakeParent(work, "open", OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
            Assert.Throws<InvalidOperationException>(() => WorkspaceSocketRoot.ChooseParent(open));

            // What /run/user/1000 or a developer's own tmux root looks like.
            string tmuxRoot = MakeParent(work, "root", OwnerOnly);
            Directory.CreateDirectory(Path.Combine(tmuxRoot, "tmux-1000"));
            Assert.Throws<InvalidOperationException>(() => WorkspaceSocketRoot.ChooseParent(tmuxRoot));

            string other = MakeParent(work, "other", OwnerOnly);
            File.WriteAllText(Path.Combine(other, "bus"), string.Empty);
            Assert.Throws<InvalidOperationException>(() => WorkspaceSocketRoot.ChooseParent(other));

            string earlier = MakeParent(work, "earlier", OwnerOnly);
            Directory.CreateDirectory(Path.Combine(earlier, "r0123abcd"));
            Assert.True(WorkspaceSocketRoot.ChooseParent(earlier) == earlier, "A parent holding earlier runs was refused.");

            // The longest a named parent may be is accepted, and one byte more
            // is refused before anything is made.
            int most = WorkspaceSocketRoot.MaximumParentLength;
            Assert.True(most == 29, "The limit is the one measured against the suites: measure again before changing it.");
            string longest = Path.Combine(work, new string('x', most - work.Length - 1));
            Assert.True(Encoding.UTF8.GetByteCount(longest) == most, "The longest parent is not the limit.");
            Assert.True(WorkspaceSocketRoot.ChooseParent(longest) == longest, "A parent at the limit was refused.");
            string tooLong = Path.Combine(work, new string('x', most - work.Length));
            Assert.True(Encoding.UTF8.GetByteCount(tooLong) == most + 1, "The parent past the limit is not one byte over.");
            Assert.Throws<InvalidOperationException>(() => WorkspaceSocketRoot.ChooseParent(tooLong));
            Assert.False(Directory.Exists(tooLong), "A parent past the limit was made.");

            string fresh = Path.Combine(work, "fresh");
            Assert.True(WorkspaceSocketRoot.ChooseParent(fresh + "/") == fresh, "A new parent was refused.");
            Assert.True(File.GetUnixFileMode(fresh) == OwnerOnly, "A new parent is not owner-only.");
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void A_run_directory_goes_once_no_server_answers_and_stays_while_one_does()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "No tmux runs on Windows.");
        string work = ScratchDirectory("ltr");
        try
        {
            // tmux leaves the socket of a server it stopped: a file nothing answers on.
            string dead = MakeRun(work, "r00000001");
            File.WriteAllText(Path.Combine(dead, "tmux-1000", "named"), string.Empty);
            Assert.True(WorkspaceSocketRoot.RemoveRunWhenNoServerAnswers(dead, TimeSpan.Zero), "A directory of dead sockets was kept.");
            Assert.False(Directory.Exists(dead), "A directory of dead sockets is still there.");

            string alive = MakeRun(work, "r00000002");
            using Socket listener = Listen(Path.Combine(alive, "tmux-1000", "named"));
            Assert.False(WorkspaceSocketRoot.RemoveRunWhenNoServerAnswers(alive, TimeSpan.Zero), "A directory with a live server was removed.");
            Assert.True(Directory.Exists(alive), "A directory with a live server is gone.");
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void Earlier_runs_are_pruned_only_when_nothing_holds_them_and_no_server_answers()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "No tmux runs on Windows.");
        string work = ScratchDirectory("ltq");
        try
        {
            string killed = MakeRun(work, "r00000001");
            File.WriteAllText(Path.Combine(killed, "tmux-1000", "named"), string.Empty);
            File.WriteAllText(Path.Combine(killed, ".lock"), string.Empty);

            // Held by a process that is still running, however old and silent.
            string running = MakeRun(work, "r00000002");
            using FileStream held = new(
                Path.Combine(running, ".lock"),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);

            string answering = MakeRun(work, "r00000003");
            using Socket listener = Listen(Path.Combine(answering, "tmux-1000", "named"));

            // New enough that its owner may not have taken the lock yet.
            string starting = MakeRun(work, "r00000004");

            string bystander = Path.Combine(work, "elsewhere");
            Directory.CreateDirectory(bystander);

            foreach (string old in new[] { killed, running, answering, bystander })
            {
                Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow - TimeSpan.FromMinutes(5));
            }

            Assert.True(WorkspaceSocketRoot.PruneEarlierRuns(work) == 1, "More or fewer than the one killed run was pruned.");

            Assert.False(Directory.Exists(killed), "A killed run was kept.");
            Assert.True(Directory.Exists(running), "A running run was pruned.");
            Assert.True(Directory.Exists(answering), "A run with a live server was pruned.");
            Assert.True(Directory.Exists(starting), "A run that was just made was pruned.");
            Assert.True(Directory.Exists(bystander), "A directory that is not a run was pruned.");
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    // Left on purpose when the child ends; the next test starts it and reads
    // what it recorded. Run any other way it has nothing to leave.
    [Fact]
    public async Task Leaves_a_bare_server_when_it_is_the_exit_check_child()
    {
        string? result = Environment.GetEnvironmentVariable(ExitChildVariable);
        if (string.IsNullOrEmpty(result))
        {
            return;
        }

        // A bare server, or one on a socket of its own inside the run's directory.
        string tmux = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux";
        string[] socket = Environment.GetEnvironmentVariable(ExitModeVariable) == "custom"
            ? ["-S", Path.Combine(WorkspaceSocketRoot.Root, "custom.sock")]
            : [];
        (int started, _) = await RunAsync(tmux, [.. socket, "-f", "/dev/null", "new-session", "-d", "-s", "left-behind"]);
        (int asked, string pid) = await RunAsync(tmux, [.. socket, "display-message", "-p", "#{pid}"]);
        if (started != 0 || asked != 0 || !int.TryParse(pid.Trim(), out int server))
        {
            await File.WriteAllTextAsync(result, "NOSERVER\n", TestContext.Current.CancellationToken);
            return;
        }

        await File.WriteAllTextAsync(
            result,
            $"ROOT:{WorkspaceSocketRoot.Root}\nPID:{server.ToString(CultureInfo.InvariantCulture)}\n",
            TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("bare", "a server was left on the default socket")]
    [InlineData("custom", "a server was left running under the test root")]
    public async Task A_server_left_at_exit_is_stopped_the_run_fails_and_its_directory_goes(string mode, string notice)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "No tmux runs on Windows.");
        CancellationToken token = TestContext.Current.CancellationToken;
        string scratch = Path.Combine(WorkspaceSocketRoot.Root, $"exit-{Guid.NewGuid():N}"[..13]);
        Directory.CreateDirectory(scratch, OwnerOnly);
        string result = Path.Combine(scratch, "result");
        int server = 0;
        string? childRoot = null;
        try
        {
            string host = Path.Combine(AppContext.BaseDirectory, Assembly.GetEntryAssembly()!.GetName().Name!);
            ProcessStartInfo start = new(host)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            start.ArgumentList.Add("-method");
            start.ArgumentList.Add("LibTmux.Engineering.SandboxCanaryTests.Leaves_a_bare_server_when_it_is_the_exit_check_child");
            start.Environment[ExitChildVariable] = result;
            start.Environment[ExitModeVariable] = mode;

            using Process child = Process.Start(start)
                ?? throw new InvalidOperationException("The exit-check child did not start.");
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            Task<string> standardOutput = child.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> standardError = child.StandardError.ReadToEndAsync(timeout.Token);
            await child.WaitForExitAsync(timeout.Token);
            _ = await standardOutput;
            string notices = await standardError;

            string[] lines = File.Exists(result) ? File.ReadAllLines(result) : [];
            foreach (string line in lines)
            {
                if (line.StartsWith("ROOT:", StringComparison.Ordinal))
                {
                    childRoot = line[5..];
                }
                else if (line.StartsWith("PID:", StringComparison.Ordinal))
                {
                    server = int.Parse(line[4..], CultureInfo.InvariantCulture);
                }
            }

            Assert.SkipWhen(lines.Contains("NOSERVER"), "The tmux on this machine did not start a server.");
            Assert.True(childRoot is not null && server != 0, "The child recorded no server.");
            Assert.True(child.ExitCode == 78, "The run did not fail with the leak exit code.");
            Assert.True(
                notices.Contains("Sandbox: " + notice, StringComparison.Ordinal),
                "The run did not say a server was left.");
            Assert.True(await DiesAsync(server, token), "The leaked server outlived the run.");
            Assert.False(Directory.Exists(childRoot), "The run directory was kept.");
        }
        finally
        {
            if (server != 0 && IsAlive(server))
            {
                using Process leaked = Process.GetProcessById(server);
                leaked.Kill();
            }

            if (childRoot is not null && Directory.Exists(childRoot))
            {
                Directory.Delete(childRoot, recursive: true);
            }

            Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public async Task A_server_left_on_the_default_socket_is_stopped_by_its_path_and_reported()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "No tmux runs on Windows.");
        await using Recorder recorder = await Recorder.StartAsync(TestContext.Current.CancellationToken);
        string root = Path.Combine(WorkspaceSocketRoot.Root, $"leak-{Guid.NewGuid():N}"[..13]);
        string directory = Path.Combine(root, "tmux-1000");
        Directory.CreateDirectory(directory);
        string socket = Path.Combine(directory, "default");
        using Socket listener = Listen(socket);
        try
        {
            List<string> leaks = WorkspaceSocketRoot.StopLeakedServers(root, recorder.Executable, TimeSpan.Zero);

            // The stand-in stops nothing, so the stop is also reported as not taking effect.
            Assert.Contains("a server was left on the default socket under the test root", leaks);
            Recorded stop = Assert.Single(recorder.Calls());
            Assert.True(
                stop.Arguments.SequenceEqual(["-S", socket, "kill-server"]),
                "The stop did not name the socket with -S and run kill-server.");
            Assert.Empty(Violations(stop));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_socket_that_is_not_the_default_is_no_leak_and_a_link_is_never_followed()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "No tmux runs on Windows.");
        await using Recorder recorder = await Recorder.StartAsync(TestContext.Current.CancellationToken);
        string root = Path.Combine(WorkspaceSocketRoot.Root, $"leak-{Guid.NewGuid():N}"[..13]);
        string directory = Path.Combine(root, "tmux-1000");
        Directory.CreateDirectory(directory);
        string target = Path.Combine(root, "target");
        File.WriteAllText(target, string.Empty);
        try
        {
            File.WriteAllText(Path.Combine(directory, "named"), string.Empty);
            Assert.Empty(WorkspaceSocketRoot.StopLeakedServers(root, recorder.Executable, TimeSpan.Zero));

            File.CreateSymbolicLink(Path.Combine(directory, "default"), target);
            Assert.Single(WorkspaceSocketRoot.StopLeakedServers(root, recorder.Executable, TimeSpan.Zero));

            Assert.Empty(recorder.Calls());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_server_on_a_socket_of_its_own_is_found_wherever_it_is_and_stopped_by_its_path()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "No tmux runs on Windows.");
        await using Recorder recorder = await Recorder.StartAsync(TestContext.Current.CancellationToken);
        string root = Path.Combine(WorkspaceSocketRoot.Root, $"leak-{Guid.NewGuid():N}"[..13]);
        Directory.CreateDirectory(Path.Combine(root, "deeper"));
        string custom = Path.Combine(root, "custom.sock");
        string nested = Path.Combine(root, "deeper", "n.sock");
        using Socket first = Listen(custom);
        using Socket second = Listen(nested);
        try
        {
            List<string> leaks = WorkspaceSocketRoot.StopLeakedServers(root, recorder.Executable, TimeSpan.Zero);

            Assert.Equal(2, leaks.Count(leak => leak == "a server was left running under the test root"));
            List<Recorded> stops = recorder.Calls();
            Assert.True(
                stops.Any(stop => stop.Arguments.SequenceEqual(["-S", custom, "kill-server"]))
                && stops.Any(stop => stop.Arguments.SequenceEqual(["-S", nested, "kill-server"])),
                "A socket outside tmux-<uid> was not stopped with -S and kill-server.");
            Assert.All(stops, stop => Assert.Empty(Violations(stop)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_stop_that_does_nothing_is_reported_and_keeps_the_directory()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "No tmux runs on Windows.");
        await using Recorder recorder = await Recorder.StartAsync(TestContext.Current.CancellationToken);
        string root = Path.Combine(WorkspaceSocketRoot.Root, $"leak-{Guid.NewGuid():N}"[..13]);
        Directory.CreateDirectory(root);
        using Socket server = Listen(Path.Combine(root, "custom.sock"));
        try
        {
            (List<string> findings, bool removed) = WorkspaceSocketRoot.EndRun(
                root,
                recorder.Executable,
                TimeSpan.FromMilliseconds(200));

            Assert.Contains("a server did not stop when asked", findings);
            Assert.False(removed, "The directory was removed while a server still answered.");
            Assert.True(Directory.Exists(root), "The directory is gone while a server still answers.");
            Assert.NotEmpty(recorder.Calls());
        }
        finally
        {
            server.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    // A server that has not accepted the clients waiting for it refuses one
    // more, and still holds its socket: a full queue is not a dead server.
    [Fact]
    public void A_server_whose_queue_of_waiting_clients_is_full_still_answers()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "No tmux runs on Windows.");
        string root = Path.Combine(WorkspaceSocketRoot.Root, $"leak-{Guid.NewGuid():N}"[..13]);
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "busy.sock");
        using Socket server = Listen(path, backlog: 1);
        List<Socket> waiting = [];
        try
        {
            for (int client = 0; client < 4; client++)
            {
                // Not blocking: a connect to a full queue would wait for ever.
                Socket waiter = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified) { Blocking = false };
                waiting.Add(waiter);
                try
                {
                    waiter.Connect(new UnixDomainSocketEndPoint(path));
                }
                catch (SocketException)
                {
                    // The queue is full, which is the state under test.
                }
            }

            Assert.False(
                WorkspaceSocketRoot.RemoveRunWhenNoServerAnswers(root, TimeSpan.Zero),
                "A server with a full queue was taken for a dead one.");
            Assert.True(Directory.Exists(root), "The directory of a server with a full queue is gone.");
        }
        finally
        {
            foreach (Socket waiter in waiting)
            {
                waiter.Dispose();
            }

            server.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task A_link_is_not_followed_out_of_the_run_and_the_runtimes_own_sockets_are_no_server()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "No tmux runs on Windows.");
        await using Recorder recorder = await Recorder.StartAsync(TestContext.Current.CancellationToken);
        string outside = ScratchDirectory("ltl");
        string root = Path.Combine(WorkspaceSocketRoot.Root, $"leak-{Guid.NewGuid():N}"[..13]);
        Directory.CreateDirectory(root);
        string elsewhere = Path.Combine(outside, "away.sock");
        using Socket beyond = Listen(elsewhere);
        File.CreateSymbolicLink(Path.Combine(root, "out"), outside);
        File.CreateSymbolicLink(Path.Combine(root, "away.sock"), elsewhere);
        using Socket runtime = Listen(Path.Combine(root, "dotnet-diagnostic-1-2-socket"));
        try
        {
            (List<string> findings, bool removed) = WorkspaceSocketRoot.EndRun(
                root,
                recorder.Executable,
                TimeSpan.Zero);

            Assert.Empty(findings);
            Assert.Empty(recorder.Calls());
            Assert.True(removed, "A run with only links and the runtime's sockets was kept.");
            Assert.True(File.Exists(elsewhere), "A socket outside the run was removed through a link.");
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task The_record_is_private_and_holds_names_never_values()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "No tmux runs on Windows.");
        await using Recorder recorder = await Recorder.StartAsync(TestContext.Current.CancellationToken);

        await recorder.RunAsync(["list-sessions"], decoys: true, TestContext.Current.CancellationToken);

        Assert.True(File.GetUnixFileMode(recorder.Directory) == OwnerOnly, "The record directory is not owner-only.");
        Assert.True(
            File.GetUnixFileMode(recorder.Record) == (UnixFileMode.UserRead | UnixFileMode.UserWrite),
            "The record file is readable by more than its owner.");

        // Boolean assertions: a failed Contains prints the text it searched,
        // and this text is the thing that must not hold a value.
        string text = File.ReadAllText(recorder.Record);
        Assert.True(
            text.Contains("SET:TMUX" + Environment.NewLine, StringComparison.Ordinal),
            "The record does not name TMUX.");
        Assert.False(text.Contains(Secret, StringComparison.Ordinal), "The record holds an unrelated value.");
        Assert.False(text.Contains("/decoy/value", StringComparison.Ordinal), "The record holds a selector value.");
        Assert.False(text.Contains("/decoy/tmpdir", StringComparison.Ordinal), "The record holds a TMUX_TMPDIR value.");
    }

    [Fact]
    public void A_recorded_call_prints_the_socket_flags_it_named_and_never_what_followed_them()
    {
        Recorded call = new(
            ["-S", "/leaked/value", "-L", "leaked-name", "list-sessions"],
            new Dictionary<string, string?>());

        string text = call.ToString();

        Assert.True(
            text.Contains("-S", StringComparison.Ordinal) && text.Contains("-L", StringComparison.Ordinal),
            "The text does not name the flags.");
        Assert.False(text.Contains("leaked", StringComparison.Ordinal), "The text prints an argument of a socket flag.");
    }

    [Fact]
    public async Task A_child_inherits_the_root_and_none_of_the_selectors()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "No tmux runs on Windows.");
        await using Recorder recorder = await Recorder.StartAsync(TestContext.Current.CancellationToken);

        await recorder.RunAsync(
            ["list-sessions"],
            decoys: false,
            TestContext.Current.CancellationToken);

        Recorded call = Assert.Single(recorder.Calls());
        Assert.Empty(Violations(call));
    }

    [Fact]
    public async Task A_server_named_by_nothing_is_reached_through_the_root_only()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "No tmux runs on Windows.");
        await using Recorder recorder = await Recorder.StartAsync(TestContext.Current.CancellationToken);
        Server server = Server.Open(new ServerConnectionOptions
        {
            TmuxBinaryPath = recorder.Executable,
            ChildEnvironment = new Dictionary<string, string?>
            {
                // The process transport hands tmux its arguments, which is what is recorded.
                ["LIBTMUX_CONTROL_TRANSPORT"] = "0",
            },
        });

        try
        {
            _ = await server.ExecuteCommandAsync(["list-sessions"], TestContext.Current.CancellationToken);
        }
        catch (LibTmuxException)
        {
            // The recorder is not a server and refuses the version probe. What
            // it was handed before that is the measurement.
        }

        List<Recorded> calls = recorder.Calls();
        Assert.NotEmpty(calls);
        Assert.All(calls, call => Assert.Empty(Violations(call)));
    }

    // The measurement above passes whenever nothing leaks, so it is shown to
    // fail: the same recorder, handed a decoy for every selector.
    [Fact]
    public async Task The_measurement_names_each_selector_a_child_is_handed()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "No tmux runs on Windows.");
        await using Recorder recorder = await Recorder.StartAsync(TestContext.Current.CancellationToken);

        await recorder.RunAsync(
            ["list-sessions"],
            decoys: true,
            TestContext.Current.CancellationToken);

        List<string> violations = Violations(Assert.Single(recorder.Calls()));
        foreach (string decoy in Decoys)
        {
            Assert.Contains($"{decoy} is set", violations);
        }

        Assert.Contains("TMUX_TMPDIR is not the test root", violations);
        Assert.Contains(violations, line => line.Contains("-S", StringComparison.Ordinal));
    }

    [Fact]
    public void Findings_name_a_variable_and_stay_quiet_about_the_rest()
    {
        string root = WorkspaceSocketRoot.Root;
        Dictionary<string, string?> clean = new(StringComparer.Ordinal)
        {
            ["TMUX_TMPDIR"] = root,
            ["LIBTMUX_TMUX"] = "/usr/bin/tmux",
            ["LIBTMUX_CONTROL_TRANSPORT"] = "0",
            [WorkspaceSocketRoot.RootVariable] = root,
            ["TMUX_PLUGIN_MANAGER_PATH"] = "/somewhere",
        };

        Assert.Empty(WorkspaceSocketRoot.Findings(clean, root));
        foreach (string decoy in Decoys)
        {
            Dictionary<string, string?> dirty = new(clean, StringComparer.Ordinal)
            {
                [decoy] = "/decoy/value,1,0",
            };

            string finding = Assert.Single(WorkspaceSocketRoot.Findings(dirty, root));
            Assert.Equal($"{decoy} is set", finding);
        }

        Dictionary<string, string?> empty = new(clean, StringComparer.Ordinal) { ["LIBTMUX_SOCKET_PATH"] = string.Empty };
        Assert.Single(WorkspaceSocketRoot.Findings(empty, root));
        Dictionary<string, string?> elsewhere = new(clean, StringComparer.Ordinal) { ["TMUX_TMPDIR"] = "/tmp" };
        Assert.Equal(
            "TMUX_TMPDIR is not the test root",
            Assert.Single(WorkspaceSocketRoot.Findings(elsewhere, root)));
        Dictionary<string, string?> unrouted = new(clean, StringComparer.Ordinal);
        unrouted.Remove("TMUX_TMPDIR");
        Assert.Single(WorkspaceSocketRoot.Findings(unrouted, root));
        const string NotShort = "the test root is not a short absolute directory of its own";
        Assert.Contains(NotShort, WorkspaceSocketRoot.Findings(clean, "/tmp"));
        Assert.Contains(NotShort, WorkspaceSocketRoot.Findings(clean, "relative/root"));
        Assert.Contains(NotShort, WorkspaceSocketRoot.Findings(clean, "/var/../tmp"));
        int most = WorkspaceSocketRoot.MaximumRootLength;
        Assert.Contains(NotShort, WorkspaceSocketRoot.Findings(clean, "/tmp/" + new string('a', most - 4)));
        Assert.DoesNotContain(NotShort, WorkspaceSocketRoot.Findings(clean, "/tmp/" + new string('a', most - 5)));
    }

    [Fact]
    public void Refusing_names_the_variable_and_never_its_value()
    {
        Dictionary<string, string?> dirty = new(StringComparer.Ordinal)
        {
            ["TMUX_TMPDIR"] = WorkspaceSocketRoot.Root,
            ["TMUX"] = "/home/someone/secret-socket,1,0",
        };

        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(
            () => WorkspaceSocketRoot.Require(dirty));

        Assert.Contains("TMUX is set", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-socket", refusal.Message, StringComparison.Ordinal);
    }

    // Claim runs once, before any test. A launcher that sets a selector later
    // is caught by running it again from a dirty process.
    [Fact]
    public void Claiming_the_root_clears_what_a_launcher_left_behind()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "No tmux runs on Windows.");
        try
        {
            foreach (string decoy in Decoys)
            {
                Environment.SetEnvironmentVariable(decoy, "/decoy/value,1,0");
            }

            Environment.SetEnvironmentVariable("TMUX_TMPDIR", "/decoy/tmpdir");
            Assert.NotEmpty(WorkspaceSocketRoot.Findings(
                WorkspaceSocketRoot.Snapshot(),
                WorkspaceSocketRoot.Root));

            WorkspaceSocketRoot.Claim();

            Assert.Empty(WorkspaceSocketRoot.Findings(
                WorkspaceSocketRoot.Snapshot(),
                WorkspaceSocketRoot.Root));
        }
        finally
        {
            WorkspaceSocketRoot.Claim();
        }
    }

    [Fact]
    public void A_launch_can_drop_the_selectors_without_relying_on_the_process()
    {
        ProcessStartInfo start = new("tmux");
        foreach (string decoy in Decoys)
        {
            start.Environment[decoy] = "/decoy/value,1,0";
        }

        start.Environment["LIBTMUX_TMUX"] = "kept";
        WorkspaceSocketRoot.Isolate(start);

        foreach (string decoy in Decoys)
        {
            Assert.False(start.Environment.ContainsKey(decoy), decoy);
        }

        Assert.Equal("kept", start.Environment["LIBTMUX_TMUX"]);
    }

    // Short, and outside the root under test: a named parent has a limit of its own.
    private static string ScratchDirectory(string prefix)
    {
        string path = Path.Combine("/tmp", $"{prefix}{Guid.NewGuid():N}"[..9]);
        Directory.CreateDirectory(path, OwnerOnly);
        File.SetUnixFileMode(path, OwnerOnly);
        return path;
    }

    private static string MakeRun(string work, string name)
    {
        string run = Path.Combine(work, name);
        Directory.CreateDirectory(Path.Combine(run, "tmux-1000"));
        return run;
    }

    private static Socket Listen(string path, int backlog = 64)
    {
        Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(backlog);
        return listener;
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string executable, string[] arguments)
    {
        ProcessStartInfo start = new(executable)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using Process? process = Process.Start(start);
            if (process is null)
            {
                return (-1, string.Empty);
            }

            CancellationToken token = TestContext.Current.CancellationToken;
            Task<string> output = process.StandardOutput.ReadToEndAsync(token);
            _ = await process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            return (process.ExitCode, await output);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (-1, string.Empty);
        }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task<bool> DiesAsync(int pid, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (IsAlive(pid) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }

        return !IsAlive(pid);
    }

    private static string MakeParent(string work, string name, UnixFileMode mode)
    {
        string path = Path.Combine(work, name);
        Directory.CreateDirectory(path);
        File.SetUnixFileMode(path, mode);
        return path;
    }

    private static List<string> Violations(Recorded call)
    {
        List<string> violations = [.. WorkspaceSocketRoot.Findings(call.Environment, WorkspaceSocketRoot.Root)];
        int socket = Array.IndexOf(call.Arguments, "-S");
        if (socket >= 0
            && (socket + 1 >= call.Arguments.Length
                || !call.Arguments[socket + 1].StartsWith(
                    WorkspaceSocketRoot.Root + "/",
                    StringComparison.Ordinal)))
        {
            violations.Add("-S names a socket outside the test root");
        }

        return violations;
    }

    // The values of the environment are never recorded, so a failed assertion
    // cannot print a credential: Environment holds names, set to a placeholder,
    // and TMUX_TMPDIR as the root or as something else.
    private sealed record Recorded(string[] Arguments, IReadOnlyDictionary<string, string?> Environment)
    {
        private static readonly string[] SocketFlags = ["-S", "-L"];

        // A failed assertion prints its subject. A leaked LIBTMUX_SOCKET_PATH
        // turns into the argument of -S, so say which socket flags the call
        // named and never what followed them.
        public override string ToString()
        {
            string[] named = [.. SocketFlags.Where(flag => Arguments.Contains(flag, StringComparer.Ordinal))];
            return named.Length == 0 ? "a call that named no socket flag" : "a call that named " + string.Join(" and ", named);
        }
    }

    /// <summary>A stand-in for tmux that writes down its arguments and which selectors it was handed, and does nothing else.</summary>
    private sealed class Recorder : IAsyncDisposable
    {
        private Recorder(string directory)
        {
            Directory = directory;
            Record = Path.Combine(directory, "record");
            Executable = Path.Combine(directory, "tmux");
        }

        internal string Directory { get; }

        internal string Record { get; }

        internal string Executable { get; }

        internal static async Task<Recorder> StartAsync(CancellationToken cancellationToken)
        {
            string directory = Path.Combine(
                WorkspaceSocketRoot.Root,
                $"canary-{Guid.NewGuid():N}"[..15]);
            System.IO.Directory.CreateDirectory(directory, OwnerOnly);
            File.SetUnixFileMode(directory, OwnerOnly);
            Recorder recorder = new(directory);
            File.WriteAllText(recorder.Record, string.Empty);
            File.SetUnixFileMode(recorder.Record, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await TestExecutable.WriteAsync(recorder.Executable, $$"""
                #!/bin/sh
                {
                  printf 'CALL\n'
                  for argument in "$@"; do printf 'ARG:%s\n' "$argument"; done
                  env | sed -n -E 's/^(TMUX|TMUX_PANE|LIBTMUX_SOCKET[A-Za-z0-9_]*)=.*/SET:\1/p'
                  if test -z "${TMUX_TMPDIR+x}"; then printf 'ROUTE:unset\n'
                  elif test "$TMUX_TMPDIR" = '{{WorkspaceSocketRoot.Root}}'; then printf 'ROUTE:root\n'
                  else printf 'ROUTE:other\n'; fi
                  printf 'END\n'
                } >> '{{recorder.Record}}'
                if test "$1" = -V; then printf 'tmux 3.8a\n'; exit 0; fi
                printf 'no server running\n' >&2
                exit 1
                """, cancellationToken);

            // Writing the script ran it once, to see that it could be run.
            File.WriteAllText(recorder.Record, string.Empty);
            return recorder;
        }

        internal async Task RunAsync(string[] arguments, bool decoys, CancellationToken cancellationToken)
        {
            ProcessStartInfo start = new(Executable)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (string argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            // Not a selector, so it reaches the recorder, which must not write it down.
            start.Environment["CANARY_SECRET"] = Secret;
            if (decoys)
            {
                foreach (string decoy in Decoys)
                {
                    start.Environment[decoy] = "/decoy/value,1,0";
                }

                start.Environment["TMUX_TMPDIR"] = "/decoy/tmpdir";
                start.ArgumentList.Insert(0, "/decoy/socket");
                start.ArgumentList.Insert(0, "-S");
            }

            using Process process = Process.Start(start)
                ?? throw new InvalidOperationException("The recording tmux did not start.");
            _ = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
        }

        internal List<Recorded> Calls()
        {
            List<Recorded> calls = [];
            if (!File.Exists(Record))
            {
                return calls;
            }

            List<string>? arguments = null;
            Dictionary<string, string?> environment = new(StringComparer.Ordinal);
            foreach (string line in File.ReadAllLines(Record))
            {
                if (line == "CALL")
                {
                    arguments = [];
                    environment = new Dictionary<string, string?>(StringComparer.Ordinal);
                }
                else if (line == "END" && arguments is not null)
                {
                    calls.Add(new Recorded([.. arguments], environment));
                    arguments = null;
                }
                else if (arguments is not null && line.StartsWith("ARG:", StringComparison.Ordinal))
                {
                    arguments.Add(line[4..]);
                }
                else if (arguments is not null && line.StartsWith("SET:", StringComparison.Ordinal))
                {
                    environment[line[4..]] = "set";
                }
                else if (arguments is not null && line == "ROUTE:root")
                {
                    environment["TMUX_TMPDIR"] = WorkspaceSocketRoot.Root;
                }
                else if (arguments is not null && line == "ROUTE:other")
                {
                    environment["TMUX_TMPDIR"] = "elsewhere";
                }
            }

            return calls;
        }

        public ValueTask DisposeAsync()
        {
            System.IO.Directory.Delete(Directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
