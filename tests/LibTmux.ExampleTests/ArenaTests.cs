using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using LibTmux.Examples;

namespace LibTmux.ExampleTests;

/// <summary>Runs the arena entrypoint against a tmux server the example does not own.</summary>
[Collection("Examples")]
[UnsupportedOSPlatform("windows")]
public sealed class ArenaTests
{
    private const string OneShotArtifact = "csharp-one-shot";
    private const string Challenge = "borrowed \"challenge\"";
    private const string ExecutableInvocation = "arena-client";

    [Fact]
    public async Task An_inactive_arena_alias_uses_an_owned_example_server()
    {
        await using BorrowedArena arena = await BorrowedArena.StartAsync(
            TestContext.Current.CancellationToken);

        ExampleRun run = await RunAsync(
            OneShotArtifact,
            BorrowedArena.BuildEnvironment(
                ("LIBTMUX_ARENA_ARTIFACT", OneShotArtifact),
                ("LIBTMUX_SOCKET_PATH", arena.SocketPath),
                ("LIBTMUX_TMUX_BIN", arena.TmuxBinaryPath)));

        Assert.Equal(0, run.ExitCode);
        Assert.DoesNotContain("LIBTMUX_ARENA_EVIDENCE=", run.StandardOutput, StringComparison.Ordinal);
        Assert.True(await arena.Server.IsAliveAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain(
            await arena.Server.GetSessionsAsync(TestContext.Current.CancellationToken),
            session => session.Name == "build");
    }

    [Fact]
    public async Task An_unknown_artifact_id_is_rejected_before_anything_is_touched()
    {
        await using BorrowedArena arena = await BorrowedArena.StartAsync(
            TestContext.Current.CancellationToken);

        ExampleRun run = await RunAsync(
            "csharp-does-not-exist",
            BorrowedArena.BuildEnvironment(
                ("LIBTMUX_ARENA_DESCRIPTOR", "borrow"),
                ("LIBTMUX_ARENA_ARTIFACT", "csharp-does-not-exist"),
                ("LIBTMUX_SOCKET_PATH", arena.SocketPath),
                ("LIBTMUX_TMUX_BIN", arena.TmuxBinaryPath)));

        Assert.NotEqual(0, run.ExitCode);
        Assert.DoesNotContain("LIBTMUX_ARENA_EVIDENCE=", run.StandardOutput, StringComparison.Ordinal);
        Assert.True(await arena.Server.IsAliveAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_activated_arena_rejects_incomplete_or_mismatched_contracts()
    {
        await using BorrowedArena arena = await BorrowedArena.StartAsync(
            TestContext.Current.CancellationToken);

        foreach ((string name, string? value) in new[]
        {
            ("LIBTMUX_ARENA_ARTIFACT", null),
            ("LIBTMUX_ARENA_ARTIFACT", string.Empty),
            ("LIBTMUX_SOCKET_PATH", null),
            ("LIBTMUX_SOCKET_PATH", string.Empty),
            ("LIBTMUX_TMUX_BIN", null),
            ("LIBTMUX_TMUX_BIN", string.Empty),
            ("LIBTMUX_TMUX_BIN", "tmux"),
            ("LIBTMUX_ARENA_ARTIFACT", "OneShot.Other"),
        })
        {
            ExampleRun run = await RunAsync(
                OneShotArtifact,
                BorrowedArena.BuildEnvironment(
                    ("LIBTMUX_ARENA_DESCRIPTOR", "borrow"),
                    ("LIBTMUX_ARENA_ARTIFACT", OneShotArtifact),
                    ("LIBTMUX_SOCKET_PATH", arena.SocketPath),
                    ("LIBTMUX_TMUX_BIN", arena.TmuxBinaryPath),
                    (name, value)));

            Assert.NotEqual(0, run.ExitCode);
            Assert.Contains("arena contract", run.StandardError, StringComparison.Ordinal);
            Assert.DoesNotContain("LIBTMUX_ARENA_EVIDENCE=", run.StandardOutput, StringComparison.Ordinal);
            Assert.True(await arena.Server.IsAliveAsync(TestContext.Current.CancellationToken));
            Assert.DoesNotContain(
                await arena.Server.GetSessionsAsync(TestContext.Current.CancellationToken),
                session => session.Name == "build");
        }
    }

    [Fact]
    public async Task An_activated_arena_runs_the_one_shot_body_and_preserves_its_server()
    {
        await using BorrowedArena arena = await BorrowedArena.StartAsync(
            TestContext.Current.CancellationToken);

        ExampleRun run = await RunAsync(
            OneShotArtifact,
            BorrowedArena.BuildEnvironment(
                ("LIBTMUX_ARENA_DESCRIPTOR", " "),
                ("LIBTMUX_ARENA_ARTIFACT", OneShotArtifact),
                ("LIBTMUX_SOCKET_PATH", arena.SocketPath),
                ("LIBTMUX_TMUX_BIN", arena.TmuxBinaryPath)));

        Assert.Equal(0, run.ExitCode);
        using JsonDocument evidence = JsonDocument.Parse(ArenaEvidence(run.StandardOutput));
        JsonElement root = evidence.RootElement;
        Assert.Equal(1, root.GetProperty("schema").GetInt32());
        Assert.Equal(OneShotArtifact, root.GetProperty("artifact").GetString());
        Assert.Equal(Challenge, root.GetProperty("challenge").GetString());
        Assert.Equal(arena.ProcessId, root.GetProperty("server_pid").GetInt32());
        Assert.Equal(arena.SocketPath, root.GetProperty("socket_path").GetString());
        Assert.True(
            File.Exists(arena.InvocationMarkerPath),
            "The arena tmux executable was not invoked.");
        string[] invocations = await File.ReadAllLinesAsync(
            arena.InvocationMarkerPath,
            TestContext.Current.CancellationToken);
        Assert.NotEmpty(invocations);
        Assert.All(
            invocations,
            invocation => Assert.Equal(ExecutableInvocation, invocation));
        Assert.True(await arena.Server.IsAliveAsync(TestContext.Current.CancellationToken));
        Assert.Contains(
            await arena.Server.GetSessionsAsync(TestContext.Current.CancellationToken),
            session => session.Name == "build");
    }

    [Fact]
    public async Task An_activated_arena_runs_any_selected_example_without_stopping_its_server()
    {
        const string artifact = "csharp-show-hierarchy";
        await using BorrowedArena arena = await BorrowedArena.StartAsync(
            TestContext.Current.CancellationToken);

        ExampleRun run = await RunAsync(
            artifact,
            BorrowedArena.BuildEnvironment(
                ("LIBTMUX_ARENA_DESCRIPTOR", " "),
                ("LIBTMUX_ARENA_ARTIFACT", artifact),
                ("LIBTMUX_SOCKET_PATH", arena.SocketPath),
                ("LIBTMUX_TMUX_BIN", arena.TmuxBinaryPath)));

        Assert.Equal(0, run.ExitCode);
        using JsonDocument evidence = JsonDocument.Parse(ArenaEvidence(run.StandardOutput));
        JsonElement root = evidence.RootElement;
        Assert.Equal(artifact, root.GetProperty("artifact").GetString());
        Assert.Equal(arena.SocketPath, root.GetProperty("socket_path").GetString());

        // Unlike the one-shot body above, which self-connects and never
        // touches the injected Server, ShowHierarchy takes the Server
        // ExampleNamespace hands it directly -- proving that handle is the
        // lent one, and that it outlives the namespace's own disposal.
        Assert.True(await arena.Server.IsAliveAsync(TestContext.Current.CancellationToken));
    }

    private static string ArenaEvidence(string output)
    {
        const string marker = "LIBTMUX_ARENA_EVIDENCE=";
        string? evidence = output
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .SingleOrDefault(line => line.StartsWith(marker, StringComparison.Ordinal));
        Assert.NotNull(evidence);
        return evidence[marker.Length..];
    }

    private static async Task<ExampleRun> RunAsync(
        string artifact,
        IReadOnlyDictionary<string, string?> environment)
    {
        ProcessStartInfo startInfo = new(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(typeof(ExampleCase).Assembly.Location);
        startInfo.ArgumentList.Add("--arena");
        startInfo.ArgumentList.Add(artifact);
        startInfo.Environment.Remove("TMUX");
        startInfo.Environment.Remove("TMUX_PANE");
        foreach ((string name, string? value) in environment)
        {
            startInfo.Environment[name] = value;
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The example entrypoint did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return new ExampleRun(process.ExitCode, await output, await error);
    }

    private sealed record ExampleRun(int ExitCode, string StandardOutput, string StandardError);

    private sealed class BorrowedArena : IAsyncDisposable
    {
        private readonly OwnedServerScope _owned;
        private readonly string _executableProbeDirectory;

        private BorrowedArena(
            OwnedServerScope owned,
            Server server,
            string socketPath,
            string executableProbeDirectory,
            string tmuxBinaryPath,
            string invocationMarkerPath)
        {
            _owned = owned;
            Server = server;
            SocketPath = socketPath;
            _executableProbeDirectory = executableProbeDirectory;
            TmuxBinaryPath = tmuxBinaryPath;
            InvocationMarkerPath = invocationMarkerPath;
        }

        public Server Server { get; }

        public string SocketPath { get; }

        public string TmuxBinaryPath { get; }

        public string InvocationMarkerPath { get; }

        public int ProcessId => Server.Generation!.Value.ProcessId;

        public static async Task<BorrowedArena> StartAsync(CancellationToken cancellationToken)
        {
            string socketPath = Path.Combine(Path.GetTempPath(), $"lta-{Guid.NewGuid():N}.sock");
            string executableProbeDirectory = Path.Combine(
                Path.GetTempPath(),
                $"lta-bin-{Guid.NewGuid():N}");
            var options = new ServerConnectionOptions(
                tmuxBinaryPath: ResolveTmuxBinaryPath(),
                socketPath: socketPath,
                configurationFile: "/dev/null");
            OwnedServerScope owned = await Server.CreateOwnedAsync(options, cancellationToken);

            try
            {
                await owned.Value.CreateSessionAsync(
                    new NewSessionRequest(name: "arena"),
                    cancellationToken);
                Server server = await owned.Value.ConnectAsync(cancellationToken);
                await server.Options.SetAsync(
                    new SetOptionRequest(
                        "@libtmux_arena_challenge",
                        Challenge,
                        OptionScope.Session,
                        global: true),
                    cancellationToken);

                Directory.CreateDirectory(executableProbeDirectory);
                string invocationMarkerPath = Path.Combine(
                    executableProbeDirectory,
                    "invocations");
                string tmuxBinaryPath = Path.Combine(executableProbeDirectory, "tmux-arena-probe");
                string script = $"""
                    #!/bin/sh
                    printf '%s\n' {ShellQuote(ExecutableInvocation)} >> {ShellQuote(invocationMarkerPath)}
                    exec {ShellQuote(options.TmuxBinaryPath)} "$@"
                    """;
                await File.WriteAllTextAsync(tmuxBinaryPath, script, cancellationToken);
                File.SetUnixFileMode(
                    tmuxBinaryPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

                return new BorrowedArena(
                    owned,
                    server,
                    socketPath,
                    executableProbeDirectory,
                    tmuxBinaryPath,
                    invocationMarkerPath);
            }
            catch
            {
                await owned.DisposeAsync();
                File.Delete(socketPath);
                if (Directory.Exists(executableProbeDirectory))
                {
                    Directory.Delete(executableProbeDirectory, recursive: true);
                }

                throw;
            }
        }

        public static Dictionary<string, string?> BuildEnvironment(
            params (string Name, string? Value)[] overrides)
        {
            Dictionary<string, string?> environment = new(StringComparer.Ordinal)
            {
                ["LIBTMUX_ARENA_DESCRIPTOR"] = null,
                ["LIBTMUX_ARENA_ARTIFACT"] = null,
                ["LIBTMUX_SOCKET_PATH"] = null,
                ["LIBTMUX_TMUX_BIN"] = null,
            };
            foreach ((string name, string? value) in overrides)
            {
                environment[name] = value;
            }

            return environment;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _owned.DisposeAsync();
            }
            finally
            {
                File.Delete(SocketPath);
                Directory.Delete(_executableProbeDirectory, recursive: true);
            }
        }

        private static string ShellQuote(string value) =>
            $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

        private static string ResolveTmuxBinaryPath()
        {
            string configured = System.Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux";
            if (Path.IsPathFullyQualified(configured))
            {
                return configured;
            }

            string? path = System.Environment.GetEnvironmentVariable("PATH");
            foreach (string directory in (path ?? string.Empty).Split(Path.PathSeparator))
            {
                string candidate = Path.Combine(
                    string.IsNullOrEmpty(directory) ? System.Environment.CurrentDirectory : directory,
                    configured);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }

            throw new FileNotFoundException("The configured tmux binary was not found.", configured);
        }
    }
}
