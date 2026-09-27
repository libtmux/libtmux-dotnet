using System.Runtime.Versioning;
using System.Text;
using BenchmarkDotNet.Attributes;
using LibTmux.Testing;

namespace LibTmux.Benchmarks;

/// <summary>Runs the same marker, capture, and pane query through five tmux modes.</summary>
[UnsupportedOSPlatform("windows")]
[MemoryDiagnoser]
[Config(typeof(ModeWorkloadBenchmarkConfig))]
public class ModeWorkloadBenchmarks
{
    private const int Items = 8;
    private const int Concurrency = 4;
    private const string QueryFormat = "Q|#{pane_id}";
    private static readonly TimeSpan SetupBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SampleBudget = TimeSpan.FromSeconds(10);

    private TemporaryHierarchyScope? _scope;
    private Server? _server;
    private Pane? _pane;
    private IControlModeSession? _control;
    private TmuxCommand? _captureCommand;
    private TmuxCommand? _queryCommand;
    private string? _fixtureToken;
    private string? _runNonce;
    private string[]? _expectedCapture;
    private byte[]? _captureBytes;
    private int _iteration;
    private int _processes;
    private int _counting;

    /// <summary>Selects one dispatch pattern for the same eight inputs.</summary>
    [Params("serial-process", "concurrent-process", "process-chain", "serial-control", "concurrent-control")]
    public string Mode { get; set; } = null!;

    /// <summary>Creates one owned pane and checks the selected mode before timing.</summary>
    [GlobalSetup]
    public async Task Setup()
    {
        using var setup = new CancellationTokenSource(SetupBudget);
        CancellationToken cancellationToken = setup.Token;
        try
        {
            var options = new TmuxTestOptions(new ServerConnectionOptions
            {
                TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux",
                SocketName = $"lt-workload-{Guid.NewGuid():N}"[..24],
                ConfigurationFile = "/dev/null",
                CommandTimeout = SampleBudget,
                ChildEnvironment = new Dictionary<string, string?>
                {
                    ["TMUX"] = null,
                    ["TMUX_PANE"] = null,
                },
                Interceptor = (_, next, token) =>
                {
                    if (Volatile.Read(ref _counting) != 0)
                    {
                        Interlocked.Increment(ref _processes);
                    }

                    return next(token);
                },
            });

            _scope = await new TmuxTestFactory().CreateHierarchyAsync(options, cancellationToken)
                .ConfigureAwait(false);
            _server = _scope.Server;
            _pane = _scope.Pane;
            _fixtureToken = $"fixture{Guid.NewGuid():N}"[..19];
            _runNonce = Guid.NewGuid().ToString("N")[..8];
            _captureCommand = new CapturePaneRequest { JoinWrappedLines = true }.ToCommand(_pane);
            _queryCommand = TmuxCommand.Create("list-panes", "-a", "-F", QueryFormat);

            IControlModeSession seedClient = await _server.EnterControlModeAsync(
                cancellationToken: cancellationToken).ConfigureAwait(false);
            try
            {
                await _pane.RespawnAsync(new RespawnRequest
                {
                    Command = "cat",
                    KillExistingProcess = true,
                }, cancellationToken).ConfigureAwait(false);
                await SeedPaneAsync(seedClient, cancellationToken).ConfigureAwait(false);
                if (IsControlMode())
                {
                    _control = seedClient;
                }
            }
            finally
            {
                if (_control is null)
                {
                    await seedClient.DisposeAsync().ConfigureAwait(false);
                }
            }

            TmuxCommandResult baseline = await _server.Chain()
                .Then(_captureCommand)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);
            _expectedCapture = CanonicalCapture(baseline.StandardOutputLines);
            _captureBytes = baseline.StandardOutput.ToArray();
            if (!_expectedCapture.Any(line => line.Contains(_fixtureToken, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("The pane capture lacks its fixture marker.");
            }

            if (_expectedCapture.Any(static line => line.StartsWith("M|", StringComparison.Ordinal)
                    || line.StartsWith("Q|", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("The pane capture contains a reserved result prefix.");
            }

            IReadOnlyList<Client> clients = await _server.GetClientsAsync(cancellationToken)
                .ConfigureAwait(false);
            int expectedClients = IsControlMode() ? 1 : 0;
            if (clients.Count != expectedClients
                || clients.Any(static client => !client.IsControlClient))
            {
                throw new InvalidOperationException(
                    $"Mode '{Mode}' expected {expectedClients} control clients; found {clients.Count} clients.");
            }

            Interlocked.Exchange(ref _processes, 0);
            Volatile.Write(ref _counting, 1);
            try
            {
                ItemCommands[] input = BuildInput();
                using var preflight = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                preflight.CancelAfter(SampleBudget);
                ItemResult[] results = await RunModeAsync(input, preflight.Token).ConfigureAwait(false);
                AssertResults(input, results);
            }
            finally
            {
                Volatile.Write(ref _counting, 0);
            }

            int expectedProcesses = Mode switch
            {
                "serial-process" or "concurrent-process" => Items * 3,
                "process-chain" => 1,
                "serial-control" or "concurrent-control" => 0,
                _ => throw new InvalidOperationException($"Unknown benchmark mode '{Mode}'."),
            };
            int actualProcesses = Volatile.Read(ref _processes);
            if (actualProcesses != expectedProcesses)
            {
                throw new InvalidOperationException(
                    $"Mode '{Mode}' started {actualProcesses} tmux processes; expected {expectedProcesses}.");
            }

            Console.WriteLine(
                $"Mode '{Mode}' preflight: {actualProcesses} tmux processes, {clients.Count} control clients, "
                + $"{Items * 3} logical commands, concurrency cap {Concurrency}.");
            (string commit, bool clean) = await BenchmarkSourceProvenance.ReadAsync(cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine($"Mode '{Mode}' source: commit {commit}; clean {clean.ToString().ToLowerInvariant()}.");
        }
        catch
        {
            await Cleanup().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Runs and checks eight marker, capture, and query results.</summary>
    [Benchmark]
    public async Task Workload()
    {
        using var sample = new CancellationTokenSource(SampleBudget);
        ItemCommands[] input = BuildInput();
        ItemResult[] results = await RunModeAsync(input, sample.Token).ConfigureAwait(false);
        AssertResults(input, results);
    }

    /// <summary>Stops the control client and owned tmux server.</summary>
    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_control is not null)
        {
            await _control.DisposeAsync().ConfigureAwait(false);
            _control = null;
        }

        if (_scope is not null)
        {
            await _scope.DisposeAsync().ConfigureAwait(false);
            _scope = null;
        }
    }

    private bool IsControlMode() => Mode is "serial-control" or "concurrent-control";

    private async Task SeedPaneAsync(IControlModeSession control, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        await using IAsyncEnumerator<TmuxEvent> events = control.Events.GetAsyncEnumerator(timeout.Token);
        Task<bool> next = events.MoveNextAsync().AsTask();
        await _pane!.SendTextAsync(_fixtureToken!, enter: false, timeout.Token).ConfigureAwait(false);

        var seen = new StringBuilder();
        while (await next.ConfigureAwait(false))
        {
            if (events.Current is TmuxOutputEvent output && output.PaneId == _pane.Id)
            {
                seen.Append(output.Data);
                if (seen.ToString().Contains(_fixtureToken!, StringComparison.Ordinal))
                {
                    return;
                }
            }

            next = events.MoveNextAsync().AsTask();
        }

        throw new InvalidOperationException("The pane stopped producing output before the fixture marker appeared.");
    }

    private ItemCommands[] BuildInput()
    {
        int iteration = Interlocked.Increment(ref _iteration);
        return [.. Enumerable.Range(0, Items).Select(index =>
        {
            string marker = $"M|{_runNonce}|{iteration}|{index}";
            return new ItemCommands(
                marker,
                new DisplayMessageRequest { Message = marker, ReturnText = true }.ToCommand(_server!),
                _captureCommand!,
                _queryCommand!);
        })];
    }

    private Task<ItemResult[]> RunModeAsync(ItemCommands[] input, CancellationToken cancellationToken) => Mode switch
    {
        "serial-process" => RunSerialAsync(input, RunProcessItemAsync, cancellationToken),
        "concurrent-process" => RunConcurrentAsync(input, RunProcessItemAsync, cancellationToken),
        "process-chain" => RunChainAsync(input, cancellationToken),
        "serial-control" => RunSerialAsync(input, RunControlItemAsync, cancellationToken),
        "concurrent-control" => RunConcurrentAsync(input, RunControlItemAsync, cancellationToken),
        _ => throw new InvalidOperationException($"Unknown benchmark mode '{Mode}'."),
    };

    private static async Task<ItemResult[]> RunSerialAsync(
        ItemCommands[] input,
        Func<ItemCommands, CancellationToken, Task<ItemResult>> run,
        CancellationToken cancellationToken)
    {
        ItemResult[] results = new ItemResult[input.Length];
        for (int index = 0; index < input.Length; index++)
        {
            results[index] = await run(input[index], cancellationToken).ConfigureAwait(false);
        }

        return results;
    }

    private static async Task<ItemResult[]> RunConcurrentAsync(
        ItemCommands[] input,
        Func<ItemCommands, CancellationToken, Task<ItemResult>> run,
        CancellationToken cancellationToken)
    {
        using var slots = new SemaphoreSlim(Concurrency);
        Task<ItemResult>[] tasks = [.. input.Select(async commands =>
        {
            await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await run(commands, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                slots.Release();
            }
        })];
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<ItemResult> RunProcessItemAsync(ItemCommands item, CancellationToken cancellationToken)
    {
        TmuxCommandResult marker = await _server!.Chain().Then(item.MarkerCommand)
            .ExecuteAsync(cancellationToken).ConfigureAwait(false);
        TmuxCommandResult capture = await _server.Chain().Then(item.CaptureCommand)
            .ExecuteAsync(cancellationToken).ConfigureAwait(false);
        TmuxCommandResult query = await _server.Chain().Then(item.QueryCommand)
            .ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return ReadItem(marker.StandardOutputLines, capture.StandardOutputLines, query.StandardOutputLines);
    }

    private async Task<ItemResult> RunControlItemAsync(ItemCommands item, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> marker = await _control!.SendAsync(item.MarkerCommand, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<string> capture = await _control.SendAsync(item.CaptureCommand, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<string> query = await _control.SendAsync(item.QueryCommand, cancellationToken)
            .ConfigureAwait(false);
        return ReadItem(marker, capture, query);
    }

    private async Task<ItemResult[]> RunChainAsync(ItemCommands[] input, CancellationToken cancellationToken)
    {
        TmuxChain chain = _server!.Chain();
        foreach (ItemCommands item in input)
        {
            chain = chain.Then(item.MarkerCommand).Then(item.CaptureCommand).Then(item.QueryCommand);
        }

        TmuxCommandResult result = await chain.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return ReadChain(input, result.StandardOutput.ToArray());
    }

    private ItemResult[] ReadChain(ItemCommands[] input, byte[] output)
    {
        ItemResult[] results = new ItemResult[input.Length];
        int offset = 0;
        for (int index = 0; index < input.Length; index++)
        {
            ItemCommands item = input[index];
            ReadSegment(output, ref offset, Encoding.UTF8.GetBytes(item.Marker + "\n"), index, "marker");
            ReadSegment(output, ref offset, _captureBytes!, index, "capture");
            ReadSegment(output, ref offset, Encoding.UTF8.GetBytes($"Q|{_pane!.Id}\n"), index, "query");
            results[index] = new ItemResult(item.Marker, _expectedCapture!, $"Q|{_pane.Id}");
        }

        if (offset != output.Length)
        {
            throw new InvalidOperationException(
                $"The chain returned {output.Length - offset} unattributed output bytes.");
        }

        return results;
    }

    private static void ReadSegment(byte[] output, ref int offset, byte[] expected, int index, string kind)
    {
        if (expected.Length > output.Length - offset
            || !output.AsSpan(offset, expected.Length).SequenceEqual(expected))
        {
            throw new InvalidOperationException(
                $"The chain's {kind} output for input {index} differs at byte {offset}.");
        }

        offset += expected.Length;
    }

    private static ItemResult ReadItem(
        IReadOnlyList<string> marker,
        IReadOnlyList<string> capture,
        IReadOnlyList<string> query)
    {
        if (marker.Count != 1 || query.Count != 1)
        {
            throw new InvalidOperationException(
                $"The item returned {marker.Count} marker lines and {query.Count} query lines.");
        }

        return new ItemResult(marker[0], CanonicalCapture(capture), query[0]);
    }

    private static string[] CanonicalCapture(IReadOnlyList<string> lines)
    {
        int count = lines.Count;
        while (count > 0 && lines[count - 1].Length == 0)
        {
            count--;
        }

        return [.. lines.Take(count)];
    }

    private void AssertResults(ItemCommands[] input, ItemResult[] results)
    {
        if (results.Length != Items)
        {
            throw new InvalidOperationException(
                $"Mode '{Mode}' returned {results.Length} items; expected {Items}.");
        }

        string query = $"Q|{_pane!.Id}";
        for (int index = 0; index < Items; index++)
        {
            ItemResult result = results[index];
            bool markerMatches = string.Equals(result.Marker, input[index].Marker, StringComparison.Ordinal);
            bool queryMatches = string.Equals(result.Query, query, StringComparison.Ordinal);
            bool captureMatches = result.Capture.SequenceEqual(_expectedCapture!, StringComparer.Ordinal);
            if (!markerMatches || !queryMatches || !captureMatches)
            {
                int firstDifferentLine = Enumerable.Range(0, Math.Min(result.Capture.Count, _expectedCapture!.Length))
                    .FirstOrDefault(line => !string.Equals(
                        result.Capture[line], _expectedCapture[line], StringComparison.Ordinal), -1);
                string difference = string.Empty;
                if (firstDifferentLine >= 0)
                {
                    string actual = result.Capture[firstDifferentLine];
                    string expected = _expectedCapture[firstDifferentLine];
                    int character = Enumerable.Range(0, Math.Min(actual.Length, expected.Length))
                        .FirstOrDefault(position => actual[position] != expected[position], -1);
                    difference = $", line lengths={actual.Length}/{expected.Length}, first differing character={character}";
                    if (character >= 0)
                    {
                        difference += $", character codes={(int)actual[character]}/{(int)expected[character]}";
                    }
                    else if (actual.Length > expected.Length)
                    {
                        difference += $", first extra character code={(int)actual[expected.Length]}, "
                            + $"extra characters all whitespace={actual[expected.Length..].All(char.IsWhiteSpace)}";
                    }
                }

                throw new InvalidOperationException(
                    $"Mode '{Mode}' returned different data for input {index}: "
                    + $"marker={markerMatches}, query={queryMatches}, capture={captureMatches}, "
                    + $"capture lines={result.Capture.Count}/{_expectedCapture.Length}, "
                    + $"first differing line={firstDifferentLine}{difference}.");
            }
        }
    }

    private sealed record ItemCommands(
        string Marker,
        TmuxCommand MarkerCommand,
        TmuxCommand CaptureCommand,
        TmuxCommand QueryCommand);

    private sealed record ItemResult(string Marker, IReadOnlyList<string> Capture, string Query);
}
