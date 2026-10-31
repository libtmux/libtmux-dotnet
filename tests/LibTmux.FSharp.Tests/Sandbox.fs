namespace LibTmux.FSharp.Tests

open System
open System.Collections
open System.Diagnostics
open System.IO
open System.Net.Sockets
open System.Security.Cryptography
open System.Text
open System.Threading
open Xunit

/// Keeps this project's tmux out of the socket root other ports share.
///
/// eng/dotnet/WorkspaceSocketRoot.cs does this for the C# test projects as a
/// module initializer. F# has no such attribute, so an assembly fixture does
/// it, before the first test. Keep the two in step.
module Sandbox =
    /// The variable that moves the shared parent of the per-run roots.
    [<Literal>]
    let RootVariable = "LIBTMUX_TEST_SOCKET_ROOT"

    /// The longest per-run root, in bytes, at which every socket path the suites build fits.
    [<Literal>]
    let MaximumRootLength = 39

    /// The longest parent, in bytes, that leaves room for a run's directory in that root.
    [<Literal>]
    let MaximumParentLength = MaximumRootLength - 10

    [<Literal>]
    let private DefaultParent = "/tmp/libtmux-dotnet-test"

    [<Literal>]
    let private ClaimKey = "LibTmux.Engineering.TestSocketRoot"

    [<Literal>]
    let private WatchKey = "LibTmux.Engineering.TestSocketRoot.Watch"

    [<Literal>]
    let private LockKey = "LibTmux.Engineering.TestSocketRoot.Lock"

    [<Literal>]
    let private LockName = ".lock"

    [<Literal>]
    let private LeakExitCode = 78

    let private abandonedAfter = TimeSpan.FromMinutes 1.

    let private settleFor = TimeSpan.FromSeconds 3.

    let private ownerOnly =
        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute

    let private isSelector (name: string) =
        name = "TMUX"
        || name = "TMUX_PANE"
        || name.StartsWith("LIBTMUX_SOCKET", StringComparison.Ordinal)

    /// This process's environment, by name.
    let snapshot () : Map<string, string> =
        [
            for entry in Environment.GetEnvironmentVariables() |> Seq.cast<DictionaryEntry> ->
                string entry.Key,
                (match entry.Value with
                 | null -> ""
                 | value -> string value)
        ]
        |> Map.ofList

    // A root with fewer than two segments is /tmp or the like, where other
    // users' and other ports' servers live. A long one leaves no room for the
    // socket name in the address tmux binds.
    let private utf8Length (text: string) = Encoding.UTF8.GetByteCount text

    let private isPrivateRoot (path: string) =
        Path.IsPathFullyQualified path
        && utf8Length path <= MaximumRootLength
        && not (path.Split('/') |> Array.contains "..")
        && (path.Split('/', StringSplitOptions.RemoveEmptyEntries)).Length >= 2

    /// Names what would send a tmux process to a server outside the root,
    /// by variable and never by value.
    let findings (environment: Map<string, string>) (root: string) : string list =
        [
            yield!
                environment
                |> Map.toList
                |> List.map fst
                |> List.filter isSelector
                |> List.sortWith (fun left right -> String.CompareOrdinal(left, right))
                |> List.map (fun name -> $"{name} is set")

            if not (isPrivateRoot root) then
                yield "the test root is not a short absolute directory of its own"
            elif environment |> Map.tryFind "TMUX_TMPDIR" <> Some root then
                yield "TMUX_TMPDIR is not the test root"
        ]

    let private runName () =
        "r"
        + (RandomNumberGenerator.GetBytes 4 |> Convert.ToHexString).ToLowerInvariant()

    let private isRunName (name: string) =
        name.Length = 9
        && name[0] = 'r'
        && name.Substring 1 |> Seq.forall Char.IsAsciiHexDigitLower

    /// Chooses the directory the per-run roots are made in. The default is
    /// this repository's own and may hold earlier runs, so it is tightened. A
    /// named one is refused unless it is short, owner-only, not a tmux root,
    /// and holds nothing but earlier runs.
    let chooseParent (chosen: string | null) : string =
        let named =
            match chosen with
            | null -> None
            | text when String.IsNullOrWhiteSpace text -> None
            | text -> Some(Path.TrimEndingDirectorySeparator text)

        match named with
        | None ->
            Directory.CreateDirectory(DefaultParent, ownerOnly) |> ignore

            try
                File.SetUnixFileMode(DefaultParent, ownerOnly)
            with
            | :? UnauthorizedAccessException
            | :? IOException as failure ->
                raise (
                    InvalidOperationException(
                        $"{DefaultParent} is not an owner-only directory and cannot be made one.",
                        failure
                    )
                )

            DefaultParent
        | Some parent ->
            if not (isPrivateRoot parent) || utf8Length parent > MaximumParentLength then
                invalidOp $"{RootVariable} must name a short absolute directory of its own."

            if Directory.Exists parent then
                if (File.GetUnixFileMode parent ||| ownerOnly) <> ownerOnly then
                    invalidOp $"{RootVariable} must name an owner-only directory."

                if
                    Directory.EnumerateFileSystemEntries parent
                    |> Seq.exists (fun entry -> not (isRunName (Path.GetFileName(entry: string) |> string)))
                then
                    invalidOp (
                        $"{RootVariable} must name a directory that holds nothing but earlier test runs, "
                        + "and not one that is already a tmux root."
                    )
            else
                Directory.CreateDirectory(parent, ownerOnly) |> ignore

            parent

    /// Makes a directory for one run, which nothing else uses.
    let newRunDirectory (parent: string) : string =
        let rec attempt remaining =
            if remaining = 0 then
                invalidOp "No unused test run directory could be made in the parent."

            let candidate = Path.Combine(parent, runName ())

            if Directory.Exists candidate || File.Exists candidate then
                attempt (remaining - 1)
            else
                Directory.CreateDirectory(candidate, ownerOnly) |> ignore
                File.SetUnixFileMode(candidate, ownerOnly)
                candidate

        attempt 16

    // A server whose queue of waiting clients is full refuses to wait for one
    // more, and still holds the socket.
    let private isBusy (failure: exn | null) =
        match failure with
        | :? SocketException as refused ->
            refused.SocketErrorCode = SocketError.WouldBlock
            || refused.SocketErrorCode = SocketError.TryAgain
        | _ -> false

    // A server too busy to accept still holds the socket.
    let private answers (socket: string) =
        try
            use connection =
                new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)

            let connecting = connection.ConnectAsync(UnixDomainSocketEndPoint socket)

            not (connecting.Wait(TimeSpan.FromSeconds 1.))
            || connecting.IsCompletedSuccessfully
        with
        | :? AggregateException as failure when isBusy failure.InnerException -> true
        | :? SocketException as failure when isBusy failure -> true
        | :? AggregateException
        | :? SocketException
        | :? ArgumentOutOfRangeException -> false

    // The runtime keeps sockets of its own in the temporary directory, which is
    // the root, and they answer for as long as the process lives.
    let private isRuntimeSocket (path: string) =
        let name = Path.GetFileName path |> string

        name.StartsWith("dotnet-diagnostic-", StringComparison.Ordinal)
        || name.StartsWith("clr-debug-pipe-", StringComparison.Ordinal)

    // Every file under a directory, without following a link out of it.
    let rec private filesUnder (directory: string) : string seq =
        seq {
            for entry in Directory.EnumerateFileSystemEntries directory do
                let info = FileInfo entry

                let attributes =
                    try
                        if isNull info.LinkTarget then
                            Some info.Attributes
                        else
                            None
                    with
                    | :? IOException
                    | :? UnauthorizedAccessException -> None

                match attributes with
                | None -> ()
                | Some found when found.HasFlag FileAttributes.Directory -> yield! filesUnder entry
                | Some _ -> yield entry
        }

    // Every socket under a directory that something answers on, whichever
    // directory it is in and never through a link.
    let private liveServers (root: string) : string seq =
        filesUnder root
        |> Seq.filter (fun file -> not (isRuntimeSocket file) && (Path.GetFileName file |> string) <> LockName)
        |> Seq.filter answers

    let private anyServerAnswers (root: string) =
        Directory.Exists root && liveServers root |> Seq.isEmpty |> not

    let private tryDelete (directory: string) =
        try
            Directory.Delete(directory, true)
            true
        with
        | :? IOException
        | :? UnauthorizedAccessException -> false

    // The lock lives as long as the process does, and the system lets go of it
    // when the process is killed.
    let private holdRun (root: string) =
        let options = FileStreamOptions()
        options.Mode <- FileMode.CreateNew
        options.Access <- FileAccess.Write
        options.Share <- FileShare.None
        options.UnixCreateMode <- Nullable(UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
        AppDomain.CurrentDomain.SetData(LockKey, new FileStream(Path.Combine(root, LockName), options))

    let private releaseRun () =
        match AppDomain.CurrentDomain.GetData LockKey with
        | :? FileStream as held -> held.Dispose()
        | _ -> ()

        AppDomain.CurrentDomain.SetData(LockKey, null)

    let private isAbandoned (run: string) =
        let directory = DirectoryInfo run

        if
            not (isNull directory.LinkTarget)
            || DateTime.UtcNow - directory.LastWriteTimeUtc < abandonedAfter
        then
            false
        else
            let held = Path.Combine(run, LockName)

            let unheld =
                not (File.Exists held)
                || (try
                        use _probe =
                            new FileStream(held, FileMode.Open, FileAccess.ReadWrite, FileShare.None)

                        true
                    with
                    | :? IOException
                    | :? UnauthorizedAccessException -> false)

            unheld && not (anyServerAnswers run)

    /// Removes the directories of earlier runs that nothing holds, and says
    /// how many. A run holds a lock on a file in its directory for as long as
    /// it lives; a directory under a minute old is left alone, because its
    /// owner may not have taken the lock yet; a server that still answers
    /// keeps its directory.
    let pruneEarlierRuns (parent: string) : int =
        Directory.EnumerateDirectories parent
        |> Seq.filter (fun run -> isRunName (Path.GetFileName(run: string) |> string))
        |> Seq.filter (fun run -> isAbandoned run && tryDelete run)
        |> Seq.length

    /// Removes a run's directory once no server in it answers, and says
    /// whether it did. tmux leaves the socket of a server it stopped, so a
    /// socket is dead when connecting to it fails, not when it is absent.
    let removeRunWhenNoServerAnswers (root: string) (settle: TimeSpan) : bool =
        let deadline = DateTime.UtcNow + settle

        while anyServerAnswers root && DateTime.UtcNow < deadline do
            Thread.Sleep 50

        not (anyServerAnswers root) && tryDelete root

    let private stopServer (tmux: string) (socket: string) =
        let start = ProcessStartInfo(tmux)
        start.UseShellExecute <- false
        start.RedirectStandardError <- true
        start.RedirectStandardOutput <- true

        for name in start.Environment.Keys |> Seq.filter isSelector |> Seq.toList do
            start.Environment.Remove name |> ignore

        for argument in [ "-S"; socket; "kill-server" ] do
            start.ArgumentList.Add argument

        try
            match Process.Start start with
            | null -> ()
            | started ->
                use child = started

                if not (child.WaitForExit(TimeSpan.FromSeconds 10.)) then
                    child.Kill()
        with
        | :? System.ComponentModel.Win32Exception
        | :? InvalidOperationException -> ()

    /// Finds every server left under a run's directory, stops each by its
    /// path, and says so: one line per server found, and one for each that did
    /// not stop. A server counts when a connect to its socket succeeds,
    /// wherever under the root the socket is, so one started with an explicit
    /// -S is found as well as one on tmux-<uid>/default. A socket nothing
    /// answers on is no leak, except that the default socket counts while its
    /// file is there. The stop names the socket with -S and is never given a
    /// path outside the root, nor one that goes through a link, which the
    /// search does not follow either. The sockets the .NET runtime keeps in the
    /// temporary directory are not servers. A server has stopped when a
    /// connect to its socket fails.
    let stopLeakedServers (root: string) (tmux: string) (settle: TimeSpan) : string list =
        if not (Directory.Exists root) then
            []
        else
            let stopped = ResizeArray<string>()
            let leaks = ResizeArray<string>()

            for directory in
                Directory.EnumerateDirectories(root, "tmux-*")
                |> Seq.sortWith (fun left right -> String.CompareOrdinal(left, right)) do
                let socket = Path.Combine(directory, "default")

                if File.Exists socket then
                    leaks.Add "a server was left on the default socket under the test root"

                    if
                        isNull (DirectoryInfo(directory).LinkTarget)
                        && isNull (FileInfo(socket).LinkTarget)
                    then
                        stopped.Add socket
                        stopServer tmux socket

            for socket in
                liveServers root
                |> Seq.sortWith (fun left right -> String.CompareOrdinal(left, right))
                |> Seq.toList do
                if not (stopped.Contains socket) then
                    leaks.Add "a server was left running under the test root"
                    stopped.Add socket
                    stopServer tmux socket

            let deadline = DateTime.UtcNow + settle

            for socket in stopped do
                while answers socket && DateTime.UtcNow < deadline do
                    Thread.Sleep 50

                if answers socket then
                    leaks.Add "a server did not stop when asked"

            List.ofSeq leaks

    /// Ends a run: stops what it left, and removes its directory unless a
    /// server still answers in it.
    let endRun (root: string) (tmux: string) (settle: TimeSpan) : string list * bool =
        let findings = stopLeakedServers root tmux settle
        findings, removeRunWhenNoServerAnswers root TimeSpan.Zero

    /// The directory this process's tmux sockets live under, which no other run shares.
    let root =
        lazy
            (match AppDomain.CurrentDomain.GetData ClaimKey with
             | :? string as claimed -> claimed
             | _ ->
                 let parent = chooseParent (Environment.GetEnvironmentVariable RootVariable)
                 pruneEarlierRuns parent |> ignore
                 let made = newRunDirectory parent
                 holdRun made
                 AppDomain.CurrentDomain.SetData(ClaimKey, made)
                 made)

    /// Refuses to go on when this process could reach a server it does not own.
    let require () =
        match findings (snapshot ()) root.Value with
        | [] -> ()
        | reasons ->
            invalidOp (
                "This test process could reach a tmux server it does not own: "
                + String.Join("; ", reasons)
                + ". Run the tests through the documented command rather than around "
                + "tests/LibTmux.FSharp.Tests/Sandbox.fs."
            )

    let private watchForLeaks () =
        if isNull (AppDomain.CurrentDomain.GetData WatchKey) then
            AppDomain.CurrentDomain.SetData(WatchKey, box true)

            AppDomain.CurrentDomain.ProcessExit.Add(fun _ ->
                let tmux =
                    match Environment.GetEnvironmentVariable "LIBTMUX_TMUX" with
                    | null -> "tmux"
                    | text -> text

                releaseRun ()
                let findings, removed = endRun root.Value tmux settleFor

                for finding in findings do
                    Console.Error.WriteLine $"Sandbox: {finding}."

                if not findings.IsEmpty then
                    Environment.ExitCode <- LeakExitCode

                // A directory that still holds a live server is kept, so it can be found.
                if not removed then
                    Console.Error.WriteLine
                        "Sandbox: a server is still running under the test root, so its directory was kept.")

    /// Clears the selectors, claims the root, and refuses if anything is left.
    let enter () =
        if not (OperatingSystem.IsWindows()) then
            for name in snapshot () |> Map.toList |> List.map fst |> List.filter isSelector do
                Environment.SetEnvironmentVariable(name, null)

            Environment.SetEnvironmentVariable("TMUX_TMPDIR", root.Value)
            Environment.SetEnvironmentVariable("TMPDIR", root.Value)

            try
                require ()
            with :? InvalidOperationException ->
                // A process that refuses to run leaves nothing behind.
                releaseRun ()
                tryDelete root.Value |> ignore
                reraise ()

            watchForLeaks ()

/// Claims the root once, before the first test of the assembly.
type SandboxFixture() =
    do Sandbox.enter ()

module AssemblyFixtures =
    [<assembly: AssemblyFixture(typeof<SandboxFixture>)>]
    do ()
