using System.Runtime.Versioning;
using BenchmarkDotNet.Attributes;
using LibTmux.Testing;
using Microsoft.FSharp.Core;

namespace LibTmux.Benchmarks;

/// <summary>Measures what one tmux change costs a live mirror, against the capture it rebuilds from.</summary>
/// <remarks>
/// A mirror captures the whole server again on each announcement, so its cost
/// grows with the server. Renaming a window and waiting until a view shows the
/// new name covers the command, tmux's announcement, the capture and the
/// publish; a snapshot capture alone is the part that grows.
/// </remarks>
[UnsupportedOSPlatform("windows")]
[MemoryDiagnoser]
[Config(typeof(FSharpControlFoldBenchmarkConfig))]
public class FSharpMirrorBenchmarks : IAsyncDisposable
{
    private const int WindowsPerSession = 4;
    private static readonly TimeSpan SetupBudget = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SeenWithin = TimeSpan.FromSeconds(10);

    private TemporaryServerScope? _scope;
    private Server _server = null!;
    private ServerMirror _mirror = null!;
    private int _renames;

    /// <summary>How many sessions of four windows the mirrored server holds.</summary>
    [Params(1, 16)]
    public int Sessions { get; set; }

    /// <summary>Builds the server and a mirror of it, and checks the mirror sees a rename.</summary>
    [GlobalSetup]
    public async Task Setup()
    {
        using var setup = new CancellationTokenSource(SetupBudget);
        CancellationToken cancellationToken = setup.Token;
        var options = new TmuxTestOptions(new ServerConnectionOptions
        {
            TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux",
            SocketName = $"lt-mirror-{Guid.NewGuid():N}"[..22],
            ConfigurationFile = "/dev/null",
            ChildEnvironment = new Dictionary<string, string?> { ["TMUX"] = null, ["TMUX_PANE"] = null },
        });
        _scope = await new TmuxTestFactory().CreateServerAsync(options, cancellationToken).ConfigureAwait(false);
        _server = _scope.Server;

        for (int session = 0; session < Sessions; session++)
        {
            TmuxChain chain = _server.Chain().Then(TmuxCommand.Create(
                "new-session", "-d", "-s", $"s{session:D2}", "-n", "w0", "sleep 3600"));
            for (int window = 1; window < WindowsPerSession; window++)
            {
                chain = chain.Then(TmuxCommand.Create(
                    "new-window", "-d", "-t", $"s{session:D2}", "-n", $"w{window}", "sleep 3600"));
            }

            await chain.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        }

        Session anchor = (await _server.GetSessionsAsync(cancellationToken).ConfigureAwait(false))
            .Single(session => session.Name == "s00");
        _mirror = await LibTmux.FSharp.Mirror.start(cancellationToken, anchor).ConfigureAwait(false);

        if (!await RenameUntilSeen().ConfigureAwait(false))
        {
            throw new InvalidOperationException("The mirror did not publish a renamed window.");
        }
    }

    [GlobalCleanup]
    public async ValueTask DisposeAsync()
    {
        if (_mirror is not null)
        {
            await _mirror.DisposeAsync().ConfigureAwait(false);
        }

        if (_scope is not null)
        {
            await _scope.DisposeAsync().ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Captures the server to pane depth, as each rebuild does.</summary>
    [Benchmark(Baseline = true)]
    public async Task<int> CaptureSnapshot()
    {
        Server snapshot = await _server.CaptureSnapshotAsync(SnapshotDepth.Panes).ConfigureAwait(false);
        return snapshot.Panes.Count;
    }

    /// <summary>Renames a window and waits until a mirror view shows the new name.</summary>
    [Benchmark]
    public async Task<bool> RenameUntilSeen()
    {
        string name = $"r{++_renames}";
        await _server.Chain()
            .Then(TmuxCommand.Create("rename-window", "-t", "s00:0", name))
            .ExecuteAsync()
            .ConfigureAwait(false);
        ServerMirrorView view = await LibTmux.FSharp.Mirror.waitUntil(
                CancellationToken.None,
                SeenWithin,
                FuncConvert.FromFunc((ServerMirrorView seen) => seen.Server.Windows.Any(window => window.Name == name)),
                _mirror)
            .ConfigureAwait(false);
        return view.Epoch > 0;
    }
}
