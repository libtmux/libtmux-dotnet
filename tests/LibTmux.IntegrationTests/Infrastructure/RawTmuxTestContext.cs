using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace LibTmux.IntegrationTests.Infrastructure;

internal sealed class RawTmuxTestContext : IAsyncDisposable
{
    private const int ExitPollAttempts = 400;
    private static readonly TimeSpan ExitPollInterval = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);
    private int disposed;
    private int serverProcessId;

    private RawTmuxTestContext(
        string tmuxBinaryPath,
        string socketPath,
        string sessionName)
    {
        TmuxBinaryPath = tmuxBinaryPath;
        SocketPath = socketPath;
        SessionName = sessionName;
    }

    internal string TmuxBinaryPath { get; }

    internal string SocketPath { get; }

    internal string SessionName { get; }

    public static async Task<RawTmuxTestContext> StartAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Real tmux integration tests require Linux or macOS.");
        }

        string configuredBinary = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? string.Empty;
        string tmuxBinaryPath = string.IsNullOrWhiteSpace(configuredBinary)
            ? "tmux"
            : configuredBinary;
        string nonce = Guid.NewGuid().ToString("N")[..16];
        string socketPath = Path.Combine(Path.GetTempPath(), $"ltcs-{nonce}.sock");
        string sessionName = $"ltcs-{nonce}";
        RawTmuxTestContext context = new(tmuxBinaryPath, socketPath, sessionName);

        try
        {
            RawTmuxResult result = await context.ExecuteAsync(
                // A named window never renames itself. Without -n tmux turns
                // automatic-rename on for it, and the name then changes as the
                // shell starts, between two reads a test expected to agree.
                ["new-session", "-d", "-s", sessionName, "-n", "main", "-x", "80", "-y", "24"],
                cancellationToken);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"tmux test server failed to start: {result.StandardErrorText}");
            }

            RawTmuxResult serverPid = await context.ExecuteAsync(
                ["display-message", "-p", "#{pid}"],
                cancellationToken);
            if (serverPid.ExitCode != 0
                || serverPid.StandardOutputLines.Count != 1
                || !int.TryParse(
                    serverPid.StandardOutputLines[0],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out context.serverProcessId)
                || context.serverProcessId <= 0)
            {
                throw new InvalidOperationException(
                    "tmux did not report the test server process identifier.");
            }

            return context;
        }
        catch (Exception startFailure)
        {
            try
            {
                await context.DisposeAsync();
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(startFailure, cleanupFailure);
            }

            throw;
        }
    }

    public async Task<RawTmuxResult> ExecuteAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref disposed) != 0,
            this);
        cancellationToken.ThrowIfCancellationRequested();
        ProcessStartInfo startInfo = CreateStartInfo(arguments);
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("tmux did not start.");
        Task<byte[]> stdout = ReadAllBytesAsync(process.StandardOutput.BaseStream);
        Task<byte[]> stderr = ReadAllBytesAsync(process.StandardError.BaseStream);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (Exception executionFailure)
        {
            try
            {
                await KillAndReapAsync(process, entireProcessTree: false);
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(executionFailure, cleanupFailure);
            }

            throw;
        }

        await Task.WhenAll(stdout, stderr);
        return new RawTmuxResult(process.ExitCode, await stdout, await stderr);
    }

    /// <summary>Waits until the endpoint is serving or definitively gone.</summary>
    /// <remarks>
    /// tmux forks and returns before a dying server finishes exiting, so for a
    /// moment the socket is neither serving nor gone and the next command fails
    /// with "server exited unexpectedly" a few percent of the time. A test that
    /// stops a server and immediately starts another has to wait that out.
    /// </remarks>
    public async Task WaitForSettledAsync(CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            RawTmuxResult result = await ExecuteAsync(["list-sessions"], cancellationToken);
            if (result.ExitCode == 0
                || result.StandardErrorText.Contains(
                    "no server running",
                    StringComparison.Ordinal)
                || result.StandardErrorText.Contains(
                    "No such file or directory",
                    StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken);
        }

        throw new TimeoutException("The tmux server neither served nor went away.");
    }

    internal ProcessStartInfo CreateStartInfo(
        IReadOnlyList<string> arguments,
        bool redirectStandardInput = false)
    {
        ProcessStartInfo startInfo = new(TmuxBinaryPath)
        {
            RedirectStandardError = true,
            RedirectStandardInput = redirectStandardInput,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (string argument in BuildInvocationArguments(arguments))
        {
            startInfo.ArgumentList.Add(argument);
        }

        ConfigureEnvironment(startInfo);
        return startInfo;
    }

    internal IReadOnlyList<string> BuildInvocationArguments(
        IReadOnlyList<string> arguments) =>
        ["-f", "/dev/null", "-S", SocketPath, .. arguments];

    internal static void ConfigureEnvironment(ProcessStartInfo startInfo)
    {
        startInfo.Environment.Remove("TMUX");
        string locale = OperatingSystem.IsMacOS() ? "en_US.UTF-8" : "C.UTF-8";
        startInfo.Environment["LANG"] = locale;
        startInfo.Environment["LC_ALL"] = locale;
        startInfo.Environment["TERM"] = "xterm-256color";
    }

    /// <summary>Waits until the server this context started has really exited.</summary>
    /// <remarks>
    /// tmux answers <c>kill-server</c> when the command lands rather than when
    /// the server goes, so a session created in that window is created on the
    /// dying server and dies with it, leaving the socket with no server at all.
    /// The socket file is no signal here: it outlives the server that made it.
    /// </remarks>
    internal async Task WaitForServerExitAsync(CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < ExitPollAttempts; attempt++)
        {
            RawTmuxResult probe = await ExecuteAsync(["list-sessions"], cancellationToken);
            if (probe.ExitCode != 0
                && (serverProcessId <= 0 || !IsProcessAlive(serverProcessId)))
            {
                return;
            }

            await Task.Delay(ExitPollInterval, cancellationToken);
        }

        throw new InvalidOperationException(
            "The tmux test server was still running after kill-server.");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        bool killServerCompleted = await TryKillServerAsync();
        if (serverProcessId > 0 && IsProcessAlive(serverProcessId))
        {
            await KillAndReapByIdAsync(serverProcessId);
        }

        bool serverStopped = serverProcessId > 0
            ? !IsProcessAlive(serverProcessId)
            : killServerCompleted || !File.Exists(SocketPath);
        if (!serverStopped)
        {
            throw new InvalidOperationException(
                "The tmux test server could not be stopped; its socket was retained.");
        }

        File.Delete(SocketPath);
    }

    private ProcessStartInfo CreateStartInfoForCleanup(IReadOnlyList<string> arguments)
    {
        ProcessStartInfo startInfo = new(TmuxBinaryPath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (string argument in BuildInvocationArguments(arguments))
        {
            startInfo.ArgumentList.Add(argument);
        }

        ConfigureEnvironment(startInfo);
        return startInfo;
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream)
    {
        using MemoryStream bytes = new();
        await stream.CopyToAsync(bytes);
        return bytes.ToArray();
    }

    private async Task<bool> TryKillServerAsync()
    {
        Process? process = null;
        try
        {
            ProcessStartInfo startInfo = CreateStartInfoForCleanup(["kill-server"]);
            process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            Task stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            Task stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            try
            {
                using CancellationTokenSource cleanup = new(CleanupTimeout);
                await process.WaitForExitAsync(cleanup.Token);
            }
            catch (OperationCanceledException)
            {
                await KillAndReapAsync(process, entireProcessTree: false);
            }

            using CancellationTokenSource pumpCleanup = new(CleanupTimeout);
            await Task.WhenAll(stdout, stderr).WaitAsync(pumpCleanup.Token);
            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            if (process is not null && IsProcessAlive(process.Id))
            {
                try
                {
                    await KillAndReapAsync(process, entireProcessTree: false);
                }
                catch (Exception)
                {
                }
            }

            return false;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task KillAndReapByIdAsync(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            await KillAndReapAsync(process, entireProcessTree: false);
        }
        catch (ArgumentException)
        {
        }
    }

    private static async Task KillAndReapAsync(
        Process process,
        bool entireProcessTree)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree);
        }

        using CancellationTokenSource cleanup = new(CleanupTimeout);
        await process.WaitForExitAsync(cleanup.Token);
    }
}

internal sealed class RawTmuxResult
{
    public RawTmuxResult(int exitCode, byte[] standardOutput, byte[] standardError)
    {
        ExitCode = exitCode;
        StandardOutput = standardOutput;
        StandardError = standardError;
        StandardOutputText = Encoding.UTF8.GetString(standardOutput);
        StandardErrorText = Encoding.UTF8.GetString(standardError);
        StandardOutputLines = ProjectLines(StandardOutputText);
        StandardErrorLines = ProjectLines(StandardErrorText);
    }

    public int ExitCode { get; }

    public byte[] StandardOutput { get; }

    public byte[] StandardError { get; }

    public string StandardOutputText { get; }

    public string StandardErrorText { get; }

    public IReadOnlyList<string> StandardOutputLines { get; }

    public IReadOnlyList<string> StandardErrorLines { get; }

    private static string[] ProjectLines(string value)
    {
        string normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        string[] lines = normalized.Split('\n');
        int count = lines.Length;
        while (count > 0 && lines[count - 1].Length == 0)
        {
            count--;
        }

        return lines[..count];
    }
}
