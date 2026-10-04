using System.Globalization;
using System.Runtime.Versioning;
using BenchmarkDotNet.Attributes;
using LibTmux.Testing;

namespace LibTmux.Benchmarks;

/// <summary>Measures how soon a caller learns a pane printed something: waiting on its output against polling its screen.</summary>
/// <remarks>
/// Each operation types a command into a shell and returns once its output is
/// on the pane, printed at once or after a delay. <c>Pane.sendAndWait</c>
/// sleeps on the pane's output through a control client it attaches for the
/// wait, or through one <c>Session.holdWaitClient</c> keeps attached. The polling route captures the screen every 50 ms until the output
/// shows, as Python libtmux's <c>retry_until</c> does by default, since Python
/// libtmux has no wait of its own; output already there is its best case. A
/// delayed command sleeps 0 to 40 ms longer in turn, so the polls land at
/// every point of their interval rather than the same point each time, as
/// they do against output nothing synchronizes with them. The marker is
/// assembled by <c>printf</c>, so the typed command's echo cannot match it.
/// </remarks>
[UnsupportedOSPlatform("windows")]
[MemoryDiagnoser]
[Config(typeof(FSharpControlFoldBenchmarkConfig))]
public class FSharpWaitLatencyBenchmarks : IAsyncDisposable
{
    private static readonly TimeSpan SetupBudget = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private TemporaryServerScope? _scope;
    private Pane _pane = null!;
    private IAsyncDisposable? _held;
    private int _marks;

    /// <summary>How long the command sleeps before printing, in milliseconds, before the spread.</summary>
    [Params(0, 250)]
    public int DelayMs { get; set; }

    /// <summary>Starts a shell, holds the session's wait client, and checks the held route sees output.</summary>
    [GlobalSetup(Target = nameof(WaitHoldingClient))]
    public async Task SetupHolding()
    {
        await Setup().ConfigureAwait(false);
        _held = await LibTmux.FSharp.Session.holdWaitClient(CancellationToken.None, _pane.Session).ConfigureAwait(false);
        if (!await WaitHoldingClient().ConfigureAwait(false))
        {
            throw new InvalidOperationException("The held route ended without seeing the command's output.");
        }
    }

    /// <summary>Starts a shell and checks both routes see a command's output.</summary>
    [GlobalSetup]
    public async Task Setup()
    {
        using var setup = new CancellationTokenSource(SetupBudget);
        CancellationToken cancellationToken = setup.Token;
        var options = new TmuxTestOptions(new ServerConnectionOptions
        {
            TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux",
            SocketName = $"lt-wait-{Guid.NewGuid():N}"[..20],
            ConfigurationFile = "/dev/null",
            ChildEnvironment = new Dictionary<string, string?> { ["TMUX"] = null, ["TMUX_PANE"] = null },
        });
        _scope = await new TmuxTestFactory().CreateServerAsync(options, cancellationToken).ConfigureAwait(false);
        Session session = await _scope.Server
            .CreateSessionAsync(new NewSessionRequest { Name = "wait", Command = "/bin/sh" }, cancellationToken)
            .ConfigureAwait(false);
        _pane = (await session.GetPanesAsync(cancellationToken).ConfigureAwait(false))[0];

        if (!await PollScreen().ConfigureAwait(false)
            || !await WaitForOutput().ConfigureAwait(false)
            || !await RunCommand().ConfigureAwait(false))
        {
            throw new InvalidOperationException("A route ended without seeing the command's output.");
        }
    }

    [GlobalCleanup]
    public async ValueTask DisposeAsync()
    {
        if (_held is not null)
        {
            await _held.DisposeAsync().ConfigureAwait(false);
        }

        if (_scope is not null)
        {
            await _scope.DisposeAsync().ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Types the command, then captures the screen every 50 ms until its output shows.</summary>
    /// <returns>Whether the output showed within the budget.</returns>
    [Benchmark(Baseline = true)]
    public async Task<bool> PollScreen()
    {
        using var budget = new CancellationTokenSource(WaitBudget);
        string marker = NextMarker();
        await _pane.SendTextAsync(Command(marker), cancellationToken: budget.Token).ConfigureAwait(false);
        while (true)
        {
            IReadOnlyList<string> screen = await _pane.CaptureAsync(cancellationToken: budget.Token).ConfigureAwait(false);
            if (screen.Any(line => line.Contains(marker, StringComparison.Ordinal)))
            {
                return true;
            }

            await Task.Delay(PollInterval, budget.Token).ConfigureAwait(false);
        }
    }

    /// <summary>Types the command through <c>Pane.sendAndWait</c>, which returns once its output arrives.</summary>
    /// <returns>Whether the output arrived within the budget.</returns>
    [Benchmark]
    public async Task<bool> WaitForOutput()
    {
        using var budget = new CancellationTokenSource(WaitBudget);
        string marker = NextMarker();
        PaneWaitResult result = await LibTmux.FSharp.Pane
            .sendAndWait(budget.Token, WaitBudget, Command(marker), marker, _pane)
            .ConfigureAwait(false);
        return result.Found;
    }

    /// <summary>Runs the command through <c>Pane.run</c>, which returns once it exits, with its status and output.</summary>
    /// <returns>Whether the command's output came back with it.</returns>
    [Benchmark]
    public async Task<bool> RunCommand()
    {
        using var budget = new CancellationTokenSource(WaitBudget);
        string marker = NextMarker();
        PaneRunResult result = await LibTmux.FSharp.Pane
            .run(budget.Token, WaitBudget, Command(marker), _pane)
            .ConfigureAwait(false);
        return result.ExitStatus == 0 && result.Output.Any(line => line.Contains(marker, StringComparison.Ordinal));
    }

    /// <summary>The same wait, while <c>Session.holdWaitClient</c> keeps its control client attached.</summary>
    /// <returns>Whether the output arrived within the budget.</returns>
    [Benchmark]
    public Task<bool> WaitHoldingClient() => WaitForOutput();

    private string NextMarker() => $"mark-{++_marks}-done";

    // The echo shows the format and the number apart; only printf's output
    // joins them into the marker.
    private string Command(string marker) =>
        (DelayMs == 0
            ? ""
            : string.Create(CultureInfo.InvariantCulture, $"sleep {(DelayMs + _marks % 5 * 10) / 1000.0:0.###}; "))
        + $"printf '%s-done\\n' {marker[..marker.LastIndexOf('-')]}";
}
