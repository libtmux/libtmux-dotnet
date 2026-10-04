using System.Runtime.Versioning;
using System.Text;
using BenchmarkDotNet.Attributes;
using LibTmux.Testing;

namespace LibTmux.Benchmarks;

/// <summary>Measures reading a pane's output flood through a real control client, by hand and through the pane watch.</summary>
/// <remarks>
/// Each operation has a shell print numbered lines and then a marker, and
/// reads the client's events until the marker arrives. The typed line is
/// echoed too, so the marker is assembled by <c>printf</c> and its echo
/// cannot match. Both routes read the same client, one after the other.
/// </remarks>
[UnsupportedOSPlatform("windows")]
[MemoryDiagnoser]
[Config(typeof(FSharpControlFoldBenchmarkConfig))]
public class FSharpPaneFloodBenchmarks : IAsyncDisposable
{
    private static readonly TimeSpan SetupBudget = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FloodBudget = TimeSpan.FromSeconds(30);

    private TemporaryServerScope? _scope;
    private IControlModeSession _control = null!;
    private Pane _pane = null!;
    private int _floods;

    /// <summary>How many lines each flood prints.</summary>
    [Params(1_000, 20_000)]
    public int Lines { get; set; }

    /// <summary>Starts a shell, attaches a control client, and checks both routes see a flood.</summary>
    [GlobalSetup]
    public async Task Setup()
    {
        using var setup = new CancellationTokenSource(SetupBudget);
        CancellationToken cancellationToken = setup.Token;
        var options = new TmuxTestOptions(new ServerConnectionOptions
        {
            TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux",
            SocketName = $"lt-flood-{Guid.NewGuid():N}"[..21],
            ConfigurationFile = "/dev/null",
            ChildEnvironment = new Dictionary<string, string?> { ["TMUX"] = null, ["TMUX_PANE"] = null },
        });
        _scope = await new TmuxTestFactory().CreateServerAsync(options, cancellationToken).ConfigureAwait(false);
        Session session = await _scope.Server
            .CreateSessionAsync(new NewSessionRequest { Name = "flood", Command = "/bin/sh" }, cancellationToken)
            .ConfigureAwait(false);
        _pane = (await session.GetPanesAsync(cancellationToken).ConfigureAwait(false))[0];
        _control = await _scope.Server.EnterControlModeAsync("flood", cancellationToken).ConfigureAwait(false);

        if (await ReadEvents().ConfigureAwait(false) < Lines || await WatchPane().ConfigureAwait(false) < Lines)
        {
            throw new InvalidOperationException("A flood ended before its lines arrived.");
        }
    }

    [GlobalCleanup]
    public async ValueTask DisposeAsync()
    {
        if (_control is not null)
        {
            await _control.DisposeAsync().ConfigureAwait(false);
        }

        if (_scope is not null)
        {
            await _scope.DisposeAsync().ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Reads every event of the client and keeps the pane's output by hand.</summary>
    /// <returns>How many newlines arrived before the marker.</returns>
    [Benchmark(Baseline = true)]
    public Task<int> ReadEvents() => FloodAsync(_control.Events);

    /// <summary>Reads the same flood through <c>Control.watchPane</c>.</summary>
    /// <returns>How many newlines arrived before the marker.</returns>
    [Benchmark]
    public Task<int> WatchPane() => FloodAsync(LibTmux.FSharp.Control.watchPane(_pane, _control));

    private async Task<int> FloodAsync(IAsyncEnumerable<TmuxEvent> events)
    {
        using var budget = new CancellationTokenSource(FloodBudget);
        int flood = ++_floods;
        string marker = $"flood-done-{flood}";
        await _pane
            .SendTextAsync($"seq 1 {Lines}; printf 'flood-done-%s\\n' {flood}", cancellationToken: budget.Token)
            .ConfigureAwait(false);

        var tail = new StringBuilder();
        int lines = 0;
        await foreach (TmuxEvent item in events.WithCancellation(budget.Token).ConfigureAwait(false))
        {
            if (item is not TmuxOutputEvent output || output.PaneId != _pane.Id)
            {
                continue;
            }

            lines += output.Data.Count(static character => character == '\n');
            tail.Append(output.Data);
            if (tail.ToString().Contains(marker, StringComparison.Ordinal))
            {
                return lines;
            }

            // A marker split across two events is still found; older text is not needed.
            if (tail.Length > marker.Length * 4)
            {
                tail.Remove(0, tail.Length - marker.Length);
            }
        }

        throw new InvalidOperationException("The control client ended before the flood's marker arrived.");
    }
}
