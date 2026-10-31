namespace LibTmux.FSharp.Tests

open System
open System.Collections.Generic
open System.ComponentModel
open System.Diagnostics
open System.Globalization
open System.IO
open System.Net.Sockets
open System.Reflection
open System.Text
open System.Text.RegularExpressions
open System.Threading
open LibTmux
open Xunit

/// Runs one test at a time because one of these tests rewrites the process environment.
[<CollectionDefinition("Sandbox canary", DisableParallelization = true)>]
type OneCanaryAtATime() = class end

/// What a recording stand-in for tmux was handed by one call: its arguments,
/// and the selectors that were set, by name. No value is ever recorded.
type Recorded =
    {
        Arguments: string list
        Environment: Map<string, string>
    }

    // A failed assertion prints its subject. A leaked LIBTMUX_SOCKET_PATH
    // turns into the argument of -S, so say which socket flags the call named
    // and never what followed them.
    override this.ToString() =
        match [ "-S"; "-L" ] |> List.filter (fun flag -> List.contains flag this.Arguments) with
        | [] -> "a call that named no socket flag"
        | named -> "a call that named " + String.Join(" and ", named)

module CanaryFiles =
    let ownerOnly =
        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute

    let secret = "hunter2-must-not-be-recorded"

    [<Literal>]
    let ExitChildVariable = "LIBTMUX_CANARY_EXIT_CHILD"

    [<Literal>]
    let ExitModeVariable = "LIBTMUX_CANARY_EXIT_MODE"

    // Short, and outside the root under test: a named parent may be 50 bytes at most.
    let scratchDirectory (prefix: string) =
        let path =
            Path.Combine("/tmp", (prefix + Guid.NewGuid().ToString "N").Substring(0, 9))

        Directory.CreateDirectory(path, ownerOnly) |> ignore
        File.SetUnixFileMode(path, ownerOnly)
        path

    let makeRun (work: string) (name: string) =
        let run = Path.Combine(work, name)
        Directory.CreateDirectory(Path.Combine(run, "tmux-1000")) |> ignore
        run

    let listenWith (backlog: int) (path: string) =
        let listener =
            new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)

        listener.Bind(UnixDomainSocketEndPoint path)
        listener.Listen backlog
        listener

    let listen (path: string) = listenWith 64 path

    let runTmux (executable: string) (arguments: string list) : int * string =
        let start = ProcessStartInfo(executable)
        start.UseShellExecute <- false
        start.RedirectStandardError <- true
        start.RedirectStandardOutput <- true

        for argument in arguments do
            start.ArgumentList.Add argument

        try
            match Process.Start start with
            | null -> -1, ""
            | started ->
                use child = started
                let output = child.StandardOutput.ReadToEndAsync()
                child.StandardError.ReadToEnd() |> ignore
                child.WaitForExit()
                child.ExitCode, output.Result
        with :? Win32Exception ->
            -1, ""

    let isAlive (pid: int) =
        try
            use process' = Process.GetProcessById pid
            not process'.HasExited
        with :? ArgumentException ->
            false

    let dies (pid: int) =
        let deadline = DateTime.UtcNow.AddSeconds 10.

        while isAlive pid && DateTime.UtcNow < deadline do
            Thread.Sleep 50

        not (isAlive pid)

    let decoys =
        [
            "TMUX"
            "TMUX_PANE"
            "LIBTMUX_SOCKET"
            "LIBTMUX_SOCKET_PATH"
            "LIBTMUX_SOCKET_NAME"
        ]

/// A stand-in for tmux that writes down its arguments and which selectors it was handed, and does nothing else.
type Recorder private (directory: string) =
    let record = Path.Combine(directory, "record")
    let executable = Path.Combine(directory, "tmux")

    // errno 26. Process.Start surfaces it as the native error code on Linux.
    static let textFileBusy = 26

    static let canExecute (path: string) =
        try
            let start = ProcessStartInfo(path, "-V")
            start.RedirectStandardOutput <- true
            start.RedirectStandardError <- true

            match Process.Start start with
            | null -> false
            | probe ->
                use probe = probe
                probe.WaitForExit()
                true
        with :? Win32Exception as failure when failure.NativeErrorCode = textFileBusy ->
            false

    member _.Directory = directory

    member _.Executable = executable

    member _.Record = record

    static member Start() : Recorder =
        let directory =
            Path.Combine(Sandbox.root.Value, ("canary-" + Guid.NewGuid().ToString "N").Substring(0, 15))

        Directory.CreateDirectory(directory, CanaryFiles.ownerOnly) |> ignore
        File.SetUnixFileMode(directory, CanaryFiles.ownerOnly)
        let recorder = new Recorder(directory)
        File.WriteAllText(recorder.Record, "")
        File.SetUnixFileMode(recorder.Record, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
        let candidate = recorder.Executable + "." + Guid.NewGuid().ToString "N" + ".tmp"

        File.WriteAllText(
            candidate,
            $"""#!/bin/sh
{{
  printf 'CALL\n'
  for argument in "$@"; do printf 'ARG:%%s\n' "$argument"; done
  env | sed -n -E 's/^(TMUX|TMUX_PANE|LIBTMUX_SOCKET[A-Za-z0-9_]*)=.*/SET:\1/p'
  if test -z "${{TMUX_TMPDIR+x}}"; then printf 'ROUTE:unset\n'
  elif test "$TMUX_TMPDIR" = '{Sandbox.root.Value}'; then printf 'ROUTE:root\n'
  else printf 'ROUTE:other\n'; fi
  printf 'END\n'
}} >> '{recorder.Record}'
if test "$1" = -V; then printf 'tmux 3.8a\n'; exit 0; fi
printf 'no server running\n' >&2
exit 1
"""
        )

        File.SetUnixFileMode(candidate, CanaryFiles.ownerOnly)
        File.Move(candidate, recorder.Executable)
        let deadline = DateTime.UtcNow.AddSeconds 60.

        while not (canExecute recorder.Executable) && DateTime.UtcNow < deadline do
            Thread.Sleep 20

        // Writing the script ran it once, to see that it could be run.
        File.WriteAllText(recorder.Record, "")
        recorder

    /// Runs the stand-in as a child that inherits this process's environment,
    /// with decoys for every selector when asked.
    member this.Run(arguments: string list, decoys: bool) =
        let start = ProcessStartInfo(executable)
        start.UseShellExecute <- false
        start.RedirectStandardError <- true
        start.RedirectStandardOutput <- true

        let handed =
            if decoys then
                [ "-S"; "/decoy/socket" ] @ arguments
            else
                arguments

        for argument in handed do
            start.ArgumentList.Add argument

        // Not a selector, so it reaches the recorder, which must not write it down.
        start.Environment["CANARY_SECRET"] <- CanaryFiles.secret

        if decoys then
            for name in CanaryFiles.decoys do
                start.Environment[name] <- "/decoy/value,1,0"

            start.Environment["TMUX_TMPDIR"] <- "/decoy/tmpdir"

        match Process.Start start with
        | null -> invalidOp "The recording tmux did not start."
        | started ->
            use child = started
            child.StandardOutput.ReadToEnd() |> ignore
            child.WaitForExit()

    member _.Calls() : Recorded list =
        if not (File.Exists record) then
            []
        else
            let calls = ResizeArray<Recorded>()
            let mutable arguments: string list option = None
            let mutable environment = Map.empty<string, string>

            for line in File.ReadAllLines record do
                match arguments with
                | _ when line = "CALL" ->
                    arguments <- Some []
                    environment <- Map.empty
                | Some collected when line = "END" ->
                    calls.Add
                        {
                            Arguments = collected
                            Environment = environment
                        }

                    arguments <- None
                | Some collected when line.StartsWith("ARG:", StringComparison.Ordinal) ->
                    arguments <- Some(collected @ [ line.Substring 4 ])
                | Some _ when line.StartsWith("SET:", StringComparison.Ordinal) ->
                    environment <- environment |> Map.add (line.Substring 4) "set"
                | Some _ when line = "ROUTE:root" ->
                    environment <- environment |> Map.add "TMUX_TMPDIR" Sandbox.root.Value
                | Some _ when line = "ROUTE:other" -> environment <- environment |> Map.add "TMUX_TMPDIR" "elsewhere"
                | _ -> ()

            List.ofSeq calls

    interface IDisposable with
        member _.Dispose() = Directory.Delete(directory, true)

/// Shows what a tmux started from a test process is handed.
///
/// The first three tests fail if a variable that could reach another server
/// gets through; the rest fail if that measurement stops noticing one.
[<Collection("Sandbox canary")>]
type SandboxCanaryTests() =
    static let decoys = CanaryFiles.decoys

    static let violations (call: Recorded) =
        let root = Sandbox.root.Value
        let socket = List.tryFindIndex ((=) "-S") call.Arguments

        [
            yield! Sandbox.findings call.Environment root
            match socket with
            | Some index when
                (match List.tryItem (index + 1) call.Arguments with
                 | Some path -> not (path.StartsWith(root + "/", StringComparison.Ordinal))
                 | None -> true)
                ->
                yield "-S names a socket outside the test root"
            | _ -> ()
        ]

    [<Fact>]
    member _.``this process can reach no server outside the root``() =
        Assert.SkipWhen(OperatingSystem.IsWindows(), "No tmux runs on Windows.")

        Assert.Empty(Sandbox.findings (Sandbox.snapshot ()) Sandbox.root.Value)

        Assert.True(
            Path.GetTempPath().StartsWith(Sandbox.root.Value, StringComparison.Ordinal),
            "TMPDIR is not under the test root."
        )

    // The assertions on paths and variables are boolean with a message that
    // names the check: a failed Equal prints both values.
    [<Fact>]
    member _.``this run has a root of its own``() =
        Assert.SkipWhen(OperatingSystem.IsWindows(), "No tmux runs on Windows.")
        let root = Sandbox.root.Value

        let parent =
            match Environment.GetEnvironmentVariable Sandbox.RootVariable with
            | null -> "/tmp/libtmux-dotnet-test"
            | text when String.IsNullOrWhiteSpace text -> "/tmp/libtmux-dotnet-test"
            | text -> Path.TrimEndingDirectorySeparator text

        Assert.True((Path.GetDirectoryName root |> string) = parent, "The root is not directly under the parent.")

        Assert.True(
            Regex.IsMatch(Path.GetFileName root |> string, "^r[0-9a-f]{8}$"),
            "The root is not named like a run."
        )

        Assert.True(
            Encoding.UTF8.GetByteCount root <= Sandbox.MaximumRootLength,
            "The root is longer than a socket path allows."
        )

        Assert.True((File.GetUnixFileMode root = CanaryFiles.ownerOnly), "The root is not owner-only.")
        Assert.True((Environment.GetEnvironmentVariable "TMUX_TMPDIR" = root), "TMUX_TMPDIR is not the root.")
        Assert.True((Environment.GetEnvironmentVariable "TMPDIR" = root), "TMPDIR is not the root.")

    [<Fact>]
    member _.``two runs never share a root``() =
        Assert.SkipWhen(OperatingSystem.IsWindows(), "No tmux runs on Windows.")
        let parent = Path.GetDirectoryName Sandbox.root.Value |> string
        let first = Sandbox.newRunDirectory parent
        let second = Sandbox.newRunDirectory parent

        try
            Assert.True((first <> second), "Two runs were given one directory.")

            Assert.True(
                (first <> Sandbox.root.Value && second <> Sandbox.root.Value),
                "A run was given this run's directory."
            )

            Assert.True((File.GetUnixFileMode first = CanaryFiles.ownerOnly), "The first directory is not owner-only.")

            Assert.True(
                (File.GetUnixFileMode second = CanaryFiles.ownerOnly),
                "The second directory is not owner-only."
            )
        finally
            Directory.Delete first
            Directory.Delete second

    // The parents are made in a short directory of their own, not under the
    // root: a named parent over 50 bytes is refused, so nesting these under a
    // root of any length would refuse them for the wrong reason.
    [<Fact>]
    member _.``a named parent must be short, private and hold only earlier runs``() =
        Assert.SkipWhen(OperatingSystem.IsWindows(), "No tmux runs on Windows.")
        let work = CanaryFiles.scratchDirectory "ltp"

        let makeParent name (mode: UnixFileMode) =
            let path = Path.Combine(work, name)
            Directory.CreateDirectory path |> ignore
            File.SetUnixFileMode(path, mode)
            path

        let refused (path: string) =
            Assert.Throws<InvalidOperationException>(fun () -> Sandbox.chooseParent path |> ignore)
            |> ignore

        try
            refused "/tmp"
            refused "relative/parent"
            refused (Path.Combine(work, String('x', 60)))

            // Few characters, too many bytes.
            refused (Path.Combine(work, String('\u00e9', 10)))

            refused (makeParent "open" (CanaryFiles.ownerOnly ||| UnixFileMode.GroupRead ||| UnixFileMode.GroupExecute))

            // What /run/user/1000 or a developer's own tmux root looks like.
            let tmuxRoot = makeParent "root" CanaryFiles.ownerOnly
            Directory.CreateDirectory(Path.Combine(tmuxRoot, "tmux-1000")) |> ignore
            refused tmuxRoot

            let other = makeParent "other" CanaryFiles.ownerOnly
            File.WriteAllText(Path.Combine(other, "bus"), "")
            refused other

            let earlier = makeParent "earlier" CanaryFiles.ownerOnly
            Directory.CreateDirectory(Path.Combine(earlier, "r0123abcd")) |> ignore
            Assert.True((Sandbox.chooseParent earlier = earlier), "A parent holding earlier runs was refused.")

            // The longest a named parent may be is accepted, and one byte more
            // is refused before anything is made.
            let most = Sandbox.MaximumParentLength

            Assert.True(
                (most = 29),
                "The limit is the one measured against the suites: measure again before changing it."
            )

            let longest = Path.Combine(work, String('x', most - work.Length - 1))
            Assert.True((Encoding.UTF8.GetByteCount longest = most), "The longest parent is not the limit.")
            Assert.True((Sandbox.chooseParent longest = longest), "A parent at the limit was refused.")
            let tooLong = Path.Combine(work, String('x', most - work.Length))

            Assert.True(
                (Encoding.UTF8.GetByteCount tooLong = most + 1),
                "The parent past the limit is not one byte over."
            )

            refused tooLong
            Assert.False(Directory.Exists tooLong, "A parent past the limit was made.")

            let fresh = Path.Combine(work, "fresh")
            Assert.True((Sandbox.chooseParent (fresh + "/") = fresh), "A new parent was refused.")
            Assert.True((File.GetUnixFileMode fresh = CanaryFiles.ownerOnly), "A new parent is not owner-only.")
        finally
            Directory.Delete(work, true)

    [<Fact>]
    member _.``a run directory goes once no server answers and stays while one does``() =
        Assert.SkipWhen(OperatingSystem.IsWindows(), "No tmux runs on Windows.")
        let work = CanaryFiles.scratchDirectory "ltr"

        try
            // tmux leaves the socket of a server it stopped: a file nothing answers on.
            let dead = CanaryFiles.makeRun work "r00000001"
            File.WriteAllText(Path.Combine(dead, "tmux-1000", "named"), "")

            Assert.True(
                Sandbox.removeRunWhenNoServerAnswers dead TimeSpan.Zero,
                "A directory of dead sockets was kept."
            )

            Assert.False(Directory.Exists dead, "A directory of dead sockets is still there.")

            let alive = CanaryFiles.makeRun work "r00000002"
            use _listener = CanaryFiles.listen (Path.Combine(alive, "tmux-1000", "named"))

            Assert.False(
                Sandbox.removeRunWhenNoServerAnswers alive TimeSpan.Zero,
                "A directory with a live server was removed."
            )

            Assert.True(Directory.Exists alive, "A directory with a live server is gone.")
        finally
            Directory.Delete(work, true)

    [<Fact>]
    member _.``earlier runs are pruned only when nothing holds them and no server answers``() =
        Assert.SkipWhen(OperatingSystem.IsWindows(), "No tmux runs on Windows.")
        let work = CanaryFiles.scratchDirectory "ltq"

        try
            let killed = CanaryFiles.makeRun work "r00000001"
            File.WriteAllText(Path.Combine(killed, "tmux-1000", "named"), "")
            File.WriteAllText(Path.Combine(killed, ".lock"), "")

            // Held by a process that is still running, however old and silent.
            let running = CanaryFiles.makeRun work "r00000002"

            use _held =
                new FileStream(Path.Combine(running, ".lock"), FileMode.CreateNew, FileAccess.Write, FileShare.None)

            let answering = CanaryFiles.makeRun work "r00000003"
            use _listener = CanaryFiles.listen (Path.Combine(answering, "tmux-1000", "named"))

            // New enough that its owner may not have taken the lock yet.
            let starting = CanaryFiles.makeRun work "r00000004"

            let bystander = Path.Combine(work, "elsewhere")
            Directory.CreateDirectory bystander |> ignore

            for old in [ killed; running; answering; bystander ] do
                Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow - TimeSpan.FromMinutes 5.)

            Assert.True((Sandbox.pruneEarlierRuns work = 1), "More or fewer than the one killed run was pruned.")

            Assert.False(Directory.Exists killed, "A killed run was kept.")
            Assert.True(Directory.Exists running, "A running run was pruned.")
            Assert.True(Directory.Exists answering, "A run with a live server was pruned.")
            Assert.True(Directory.Exists starting, "A run that was just made was pruned.")
            Assert.True(Directory.Exists bystander, "A directory that is not a run was pruned.")
        finally
            Directory.Delete(work, true)

    // Left on purpose when the child ends; the next test starts it and reads
    // what it recorded. Run any other way it has nothing to leave.
    [<Fact>]
    member _.``leaves a bare server when it is the exit check child``() =
        match Environment.GetEnvironmentVariable CanaryFiles.ExitChildVariable with
        | null
        | "" -> ()
        | result ->
            let tmux =
                match Environment.GetEnvironmentVariable "LIBTMUX_TMUX" with
                | null -> "tmux"
                | text -> text

            // A bare server, or one on a socket of its own inside the run's directory.
            let socket =
                match Environment.GetEnvironmentVariable CanaryFiles.ExitModeVariable with
                | "custom" -> [ "-S"; Path.Combine(Sandbox.root.Value, "custom.sock") ]
                | _ -> []

            let started, _ =
                CanaryFiles.runTmux tmux (socket @ [ "-f"; "/dev/null"; "new-session"; "-d"; "-s"; "left-behind" ])

            let asked, pid =
                CanaryFiles.runTmux tmux (socket @ [ "display-message"; "-p"; "#{pid}" ])

            match Int32.TryParse(pid.Trim(), CultureInfo.InvariantCulture) with
            | true, server when started = 0 && asked = 0 ->
                File.WriteAllText(result, $"ROOT:{Sandbox.root.Value}\nPID:{server}\n")
            | _ -> File.WriteAllText(result, "NOSERVER\n")

    [<Theory>]
    [<InlineData("bare", "a server was left on the default socket")>]
    [<InlineData("custom", "a server was left running under the test root")>]
    member _.``a server left at exit is stopped the run fails and its directory goes``(mode: string, notice: string) =
        Assert.SkipWhen(OperatingSystem.IsWindows(), "No tmux runs on Windows.")

        let scratch =
            Path.Combine(Sandbox.root.Value, ("exit-" + Guid.NewGuid().ToString "N").Substring(0, 13))

        Directory.CreateDirectory(scratch, CanaryFiles.ownerOnly) |> ignore
        let result = Path.Combine(scratch, "result")
        let mutable server = 0
        let mutable exitCode = 0
        let mutable childRoot = ""

        try
            let name =
                match Assembly.GetEntryAssembly() with
                | null -> invalidOp "No entry assembly."
                | entry -> entry.GetName().Name |> string

            let start = ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, name))
            start.UseShellExecute <- false
            start.RedirectStandardError <- true
            start.RedirectStandardOutput <- true
            start.ArgumentList.Add "-method"

            start.ArgumentList.Add "*SandboxCanaryTests.leaves a bare server when it is the exit check child"

            start.Environment[CanaryFiles.ExitChildVariable] <- result
            start.Environment[CanaryFiles.ExitModeVariable] <- mode

            let notices =
                match Process.Start start with
                | null -> invalidOp "The exit-check child did not start."
                | started ->
                    use child = started
                    let output = child.StandardOutput.ReadToEndAsync()
                    let errors = child.StandardError.ReadToEndAsync()

                    if not (child.WaitForExit(TimeSpan.FromMinutes 2.)) then
                        child.Kill()

                    output.Result |> ignore
                    exitCode <- child.ExitCode
                    errors.Result

            let lines =
                if File.Exists result then
                    File.ReadAllLines result
                else
                    [||]

            for line in lines do
                if line.StartsWith("ROOT:", StringComparison.Ordinal) then
                    childRoot <- line.Substring 5
                elif line.StartsWith("PID:", StringComparison.Ordinal) then
                    server <- Int32.Parse(line.Substring 4, CultureInfo.InvariantCulture)

            Assert.SkipWhen(Array.contains "NOSERVER" lines, "The tmux on this machine did not start a server.")
            Assert.True((childRoot <> "" && server <> 0), "The child recorded no server.")
            Assert.True((exitCode = 78), "The run did not fail with the leak exit code.")

            Assert.True(
                notices.Contains("Sandbox: " + notice, StringComparison.Ordinal),
                "The run did not say a server was left."
            )

            Assert.True(CanaryFiles.dies server, "The leaked server outlived the run.")
            Assert.False(Directory.Exists childRoot, "The run directory was kept.")
        finally
            if server <> 0 && CanaryFiles.isAlive server then
                use leaked = Process.GetProcessById server
                leaked.Kill()

            if childRoot <> "" && Directory.Exists childRoot then
                Directory.Delete(childRoot, true)

            Directory.Delete(scratch, true)

    [<Fact>]
    member _.``a server left on the default socket is stopped by its path and reported``() =
        Assert.SkipWhen(OperatingSystem.IsWindows(), "No tmux runs on Windows.")
        use recorder = Recorder.Start()

        let root =
            Path.Combine(Sandbox.root.Value, ("leak-" + Guid.NewGuid().ToString "N").Substring(0, 13))

        let directory = Path.Combine(root, "tmux-1000")
        Directory.CreateDirectory directory |> ignore
        let socket = Path.Combine(directory, "default")
        use _listener = CanaryFiles.listen socket

        try
            let leaks = Sandbox.stopLeakedServers root recorder.Executable TimeSpan.Zero

            // The stand-in stops nothing, so the stop is also reported as not taking effect.
            Assert.Contains("a server was left on the default socket under the test root", leaks)
            let stop = Assert.Single(recorder.Calls())

            Assert.True(
                (stop.Arguments = [ "-S"; socket; "kill-server" ]),
                "The stop did not name the socket with -S and run kill-server."
            )

            Assert.Empty(violations stop)
        finally
            Directory.Delete(root, true)

    [<Fact>]
    member _.``a socket that is not the default is no leak and a link is never followed``() =
        Assert.SkipWhen(OperatingSystem.IsWindows(), "No tmux runs on Windows.")
        use recorder = Recorder.Start()

        let root =
            Path.Combine(Sandbox.root.Value, ("leak-" + Guid.NewGuid().ToString "N").Substring(0, 13))

        let directory = Path.Combine(root, "tmux-1000")
        Directory.CreateDirectory directory |> ignore
        let target = Path.Combine(root, "target")
        File.WriteAllText(target, "")

        try
            File.WriteAllText(Path.Combine(directory, "named"), "")
            Assert.Empty(Sandbox.stopLeakedServers root recorder.Executable TimeSpan.Zero)

            File.CreateSymbolicLink(Path.Combine(directory, "default"), target) |> ignore

            Assert.Single(Sandbox.stopLeakedServers root recorder.Executable TimeSpan.Zero)
            |> ignore

            Assert.Empty(recorder.Calls())
        finally
            Directory.Delete(root, true)

    [<Fact>]
    member _.``a server on a socket of its own is found wherever it is and stopped by its path``() =
        Assert.SkipWhen(OperatingSystem.IsWindows(), "No tmux runs on Windows.")
        use recorder = Recorder.Start()

        let root =
            Path.Combine(Sandbox.root.Value, ("leak-" + Guid.NewGuid().ToString "N").Substring(0, 13))

        Directory.CreateDirectory(Path.Combine(root, "deeper")) |> ignore
        let custom = Path.Combine(root, "custom.sock")
        let nested = Path.Combine(root, "deeper", "n.sock")
        use _first = CanaryFiles.listen custom
        use _second = CanaryFiles.listen nested

        try
            let leaks = Sandbox.stopLeakedServers root recorder.Executable TimeSpan.Zero

            Assert.Equal(
                2,
                leaks
                |> List.filter (fun leak -> leak = "a server was left running under the test root")
                |> List.length
            )

            let stops = recorder.Calls()

            Assert.True(
                (stops
                 |> List.exists (fun stop -> stop.Arguments = [ "-S"; custom; "kill-server" ]))
                && (stops
                    |> List.exists (fun stop -> stop.Arguments = [ "-S"; nested; "kill-server" ])),
                "A socket outside tmux-<uid> was not stopped with -S and kill-server."
            )

            for stop in stops do
                Assert.Empty(violations stop)
        finally
            Directory.Delete(root, true)

    [<Fact>]
    member _.``a stop that does nothing is reported and keeps the directory``() =
        Assert.SkipWhen(OperatingSystem.IsWindows(), "No tmux runs on Windows.")
        use recorder = Recorder.Start()

        let root =
            Path.Combine(Sandbox.root.Value, ("leak-" + Guid.NewGuid().ToString "N").Substring(0, 13))

        Directory.CreateDirectory root |> ignore
        let server = CanaryFiles.listen (Path.Combine(root, "custom.sock"))

        try
            let findings, removed =
                Sandbox.endRun root recorder.Executable (TimeSpan.FromMilliseconds 200.)

            Assert.Contains("a server did not stop when asked", findings)
            Assert.False(removed, "The directory was removed while a server still answered.")
            Assert.True(Directory.Exists root, "The directory is gone while a server still answers.")
            Assert.NotEmpty(recorder.Calls())
        finally
            server.Dispose()

            if Directory.Exists root then
                Directory.Delete(root, true)

    // A server that has not accepted the clients waiting for it refuses one
    // more, and still holds its socket: a full queue is not a dead server.
    [<Fact>]
    member _.``a server whose queue of waiting clients is full still answers``() =
        Assert.SkipWhen(OperatingSystem.IsWindows(), "No tmux runs on Windows.")

        let root =
            Path.Combine(Sandbox.root.Value, ("leak-" + Guid.NewGuid().ToString "N").Substring(0, 13))

        Directory.CreateDirectory root |> ignore
        let path = Path.Combine(root, "busy.sock")
        let server = CanaryFiles.listenWith 1 path
        let waiting = ResizeArray<Socket>()

        try
            for _ in 1..4 do
                // Not blocking: a connect to a full queue would wait for ever.
                let waiter =
                    new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified, Blocking = false)

                waiting.Add waiter

                try
                    waiter.Connect(UnixDomainSocketEndPoint path)
                with :? SocketException ->
                    // The queue is full, which is the state under test.
                    ()

            Assert.False(
                Sandbox.removeRunWhenNoServerAnswers root TimeSpan.Zero,
                "A server with a full queue was taken for a dead one."
            )

            Assert.True(Directory.Exists root, "The directory of a server with a full queue is gone.")
        finally
            for waiter in waiting do
                waiter.Dispose()

            server.Dispose()

            if Directory.Exists root then
                Directory.Delete(root, true)

    [<Fact>]
    member _.``a link is not followed out of the run and the runtimes own sockets are no server``() =
        Assert.SkipWhen(OperatingSystem.IsWindows(), "No tmux runs on Windows.")
        use recorder = Recorder.Start()
        let outside = CanaryFiles.scratchDirectory "ltl"

        let root =
            Path.Combine(Sandbox.root.Value, ("leak-" + Guid.NewGuid().ToString "N").Substring(0, 13))

        Directory.CreateDirectory root |> ignore
        let elsewhere = Path.Combine(outside, "away.sock")
        use _beyond = CanaryFiles.listen elsewhere
        File.CreateSymbolicLink(Path.Combine(root, "out"), outside) |> ignore
        File.CreateSymbolicLink(Path.Combine(root, "away.sock"), elsewhere) |> ignore

        use _runtime =
            CanaryFiles.listen (Path.Combine(root, "dotnet-diagnostic-1-2-socket"))

        try
            let findings, removed = Sandbox.endRun root recorder.Executable TimeSpan.Zero

            Assert.Empty findings
            Assert.Empty(recorder.Calls())
            Assert.True(removed, "A run with only links and the runtime's sockets was kept.")
            Assert.True(File.Exists elsewhere, "A socket outside the run was removed through a link.")
        finally
            Directory.Delete(outside, true)

            if Directory.Exists root then
                Directory.Delete(root, true)

    [<Fact>]
    member _.``the record is private and holds names never values``() =
        Assert.SkipWhen(OperatingSystem.IsWindows(), "No tmux runs on Windows.")
        use recorder = Recorder.Start()

        recorder.Run([ "list-sessions" ], decoys = true)

        Assert.True(
            (File.GetUnixFileMode recorder.Directory = CanaryFiles.ownerOnly),
            "The record directory is not owner-only."
        )

        Assert.True(
            (File.GetUnixFileMode recorder.Record = (UnixFileMode.UserRead ||| UnixFileMode.UserWrite)),
            "The record file is readable by more than its owner."
        )

        // Boolean assertions: a failed Contains prints the text it searched,
        // and this text is the thing that must not hold a value.
        let text = File.ReadAllText recorder.Record
        Assert.True(text.Contains("SET:TMUX" + Environment.NewLine), "The record does not name TMUX.")
        Assert.False(text.Contains CanaryFiles.secret, "The record holds an unrelated value.")
        Assert.False(text.Contains "/decoy/value", "The record holds a selector value.")
        Assert.False(text.Contains "/decoy/tmpdir", "The record holds a TMUX_TMPDIR value.")

    [<Fact>]
    member _.``a recorded call prints the socket flags it named and never what followed them``() =
        let call =
            {
                Arguments = [ "-S"; "/leaked/value"; "-L"; "leaked-name"; "list-sessions" ]
                Environment = Map.empty
            }

        let text = call.ToString()

        Assert.True(text.Contains "-S" && text.Contains "-L", "The text does not name the flags.")
        Assert.False(text.Contains "leaked", "The text prints an argument of a socket flag.")

    [<Fact>]
    member _.``a child inherits the root and none of the selectors``() =
        Assert.SkipWhen(OperatingSystem.IsWindows(), "No tmux runs on Windows.")
        use recorder = Recorder.Start()

        recorder.Run([ "list-sessions" ], decoys = false)

        let call = Assert.Single(recorder.Calls())
        Assert.Empty(violations call)

    [<Fact>]
    member _.``a server named by nothing is reached through the root only``() =
        task {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "No tmux runs on Windows.")
            use recorder = Recorder.Start()

            // The process transport hands tmux its arguments, which is what is recorded.
            let childEnvironment = Dictionary<string, string | null>()
            childEnvironment["LIBTMUX_CONTROL_TRANSPORT"] <- "0"

            let server =
                LibTmux.Server.Open(
                    ServerConnectionOptions(TmuxBinaryPath = recorder.Executable, ChildEnvironment = childEnvironment)
                )

            try
                let! _ =
                    server.ExecuteCommandAsync([ "list-sessions" ], TestContext.Current.CancellationToken)

                ()
            with :? LibTmuxException ->
                // The recorder is not a server and refuses the version probe.
                // What it was handed before that is the measurement.
                ()

            let calls = recorder.Calls()
            Assert.NotEmpty calls
            Assert.All(calls, fun call -> Assert.Empty(violations call))
        }

    // The measurement above passes whenever nothing leaks, so it is shown to
    // fail: the same recorder, handed a decoy for every selector.
    [<Fact>]
    member _.``the measurement names each selector a child is handed``() =
        Assert.SkipWhen(OperatingSystem.IsWindows(), "No tmux runs on Windows.")
        use recorder = Recorder.Start()

        recorder.Run([ "list-sessions" ], decoys = true)

        let found = violations (Assert.Single(recorder.Calls()))

        for decoy in decoys do
            Assert.Contains($"{decoy} is set", found)

        Assert.Contains("TMUX_TMPDIR is not the test root", found)
        Assert.Contains("-S names a socket outside the test root", found)

    [<Fact>]
    member _.``findings name a variable and stay quiet about the rest``() =
        let root = Sandbox.root.Value

        let clean =
            Map.ofList
                [
                    "TMUX_TMPDIR", root
                    "LIBTMUX_TMUX", "/usr/bin/tmux"
                    "LIBTMUX_CONTROL_TRANSPORT", "0"
                    Sandbox.RootVariable, root
                    "TMUX_PLUGIN_MANAGER_PATH", "/somewhere"
                ]

        Assert.Empty(Sandbox.findings clean root)

        for decoy in decoys do
            let dirty = clean |> Map.add decoy "/decoy/value,1,0"
            Assert.Equal<string list>([ $"{decoy} is set" ], Sandbox.findings dirty root)

        let empty = clean |> Map.add "LIBTMUX_SOCKET_PATH" ""
        Assert.Single(Sandbox.findings empty root) |> ignore

        let elsewhere = clean |> Map.add "TMUX_TMPDIR" "/tmp"

        Assert.Equal<string list>([ "TMUX_TMPDIR is not the test root" ], Sandbox.findings elsewhere root)

        let unrouted = clean |> Map.remove "TMUX_TMPDIR"
        Assert.Single(Sandbox.findings unrouted root) |> ignore

        let notShort = "the test root is not a short absolute directory of its own"

        let most = Sandbox.MaximumRootLength

        for shared in [ "/tmp"; "relative/root"; "/var/../tmp"; "/tmp/" + String('a', most - 4) ] do
            Assert.Contains(notShort, Sandbox.findings clean shared)

        Assert.DoesNotContain(notShort, Sandbox.findings clean ("/tmp/" + String('a', most - 5)))

    // enter runs once, before any test. A launcher that sets a selector later
    // is caught by running it again from a dirty process.
    [<Fact>]
    member _.``entering the sandbox clears what a launcher left behind``() =
        Assert.SkipWhen(OperatingSystem.IsWindows(), "No tmux runs on Windows.")

        try
            for decoy in decoys do
                Environment.SetEnvironmentVariable(decoy, "/decoy/value,1,0")

            Environment.SetEnvironmentVariable("TMUX_TMPDIR", "/decoy/tmpdir")
            Assert.NotEmpty(Sandbox.findings (Sandbox.snapshot ()) Sandbox.root.Value)

            Sandbox.enter ()

            Assert.Empty(Sandbox.findings (Sandbox.snapshot ()) Sandbox.root.Value)
        finally
            Sandbox.enter ()
