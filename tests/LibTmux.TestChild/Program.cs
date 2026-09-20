using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace LibTmux.TestChild;

internal static class Program
{
    private const int UsageExitCode = 2;

    public static async Task<int> Main(string[] arguments)
    {
        if (arguments.Length == 0)
        {
            return UsageExitCode;
        }

        return arguments[0] switch
        {
            "attach-session" when !OperatingSystem.IsWindows() => await AttachSessionAsync(arguments),
            "concurrent-raw" => await WriteConcurrentRawAsync(arguments),
            "cleanup-fault" => await HoldAsync(arguments, "cleanup-fault-ready"),
            "descendant-survival" => await StartDescendantAsync(arguments),
            "hold-pump" => await HoldAsync(arguments, "pump-ready"),
            "invalid-utf8" => await WriteInvalidUtf8Async(arguments),
            "nonzero-exit" => await ExitNonzeroAsync(arguments),
            "partial-final" => await WritePartialFinalAsync(arguments),
            _ => UsageExitCode,
        };
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static async Task<int> AttachSessionAsync(string[] arguments)
    {
        if (arguments.Length != 7)
        {
            return UsageExitCode;
        }
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var watching = new CancellationTokenSource();
        Server server = await Server.ConnectAsync(new ServerConnectionOptions
        {
            TmuxBinaryPath = arguments[1],
            SocketPath = arguments[2],
            ConfigurationFile = "/dev/null",
        }, lifetime.Token);
        Session session = await server.GetSessionAsync(new SessionId(int.Parse(arguments[3].AsSpan(1), CultureInfo.InvariantCulture)), lifetime.Token);
        await using TmuxWaitChannel cancel = server.OpenWaitChannel(arguments[4]);
        Task cancellation = ObserveCancellationAsync();
        try
        {
            Session refreshed = await session.AttachAsync(cancellationToken: lifetime.Token);
            await File.WriteAllTextAsync(arguments[5], $"returned:{refreshed.Id}:{refreshed.ActiveWindow.Value.Id}", CancellationToken.None);
        }
        catch (TmuxOperationCanceledException error) when (arguments[6] == "cancel" && lifetime.IsCancellationRequested)
        {
            await File.WriteAllTextAsync(arguments[5], $"cancelled:{error.CommandMayHaveExecuted}", CancellationToken.None);
        }
        finally
        {
            await watching.CancelAsync();
            try
            {
                await cancellation;
            }
            catch (OperationCanceledException) when (watching.IsCancellationRequested)
            {
            }
        }
        return 0;

        async Task ObserveCancellationAsync()
        {
            if (await cancel.WaitAsync(TimeSpan.FromSeconds(10), watching.Token))
            {
                await lifetime.CancelAsync();
            }
        }
    }

    private static async Task<int> WriteConcurrentRawAsync(string[] arguments)
    {
        if (arguments.Length != 4
            || !int.TryParse(
                arguments[3],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int repeatCount)
            || repeatCount <= 0)
        {
            return UsageExitCode;
        }

        byte[] stdoutChunk;
        byte[] stderrChunk;
        try
        {
            stdoutChunk = Convert.FromBase64String(arguments[1]);
            stderrChunk = Convert.FromBase64String(arguments[2]);
        }
        catch (FormatException)
        {
            return UsageExitCode;
        }

        await Task.WhenAll(
            WriteRepeatedAsync(Console.OpenStandardOutput(), stdoutChunk, repeatCount),
            WriteRepeatedAsync(Console.OpenStandardError(), stderrChunk, repeatCount));
        return 0;
    }

    private static async Task<int> WriteInvalidUtf8Async(string[] arguments)
    {
        if (arguments.Length != 1)
        {
            return UsageExitCode;
        }

        await WriteAsync(Console.OpenStandardOutput(), [0x66, 0x80, 0xff, 0x0a]);
        return 0;
    }

    private static async Task<int> WritePartialFinalAsync(string[] arguments)
    {
        if (arguments.Length != 1)
        {
            return UsageExitCode;
        }

        await Task.WhenAll(
            WriteAsync(Console.OpenStandardOutput(), "final-record"u8.ToArray()),
            WriteAsync(Console.OpenStandardError(), "final-error"u8.ToArray()));
        return 0;
    }

    private static async Task<int> ExitNonzeroAsync(string[] arguments)
    {
        if (arguments.Length != 2
            || !int.TryParse(
                arguments[1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int exitCode)
            || exitCode is < 1 or > 255)
        {
            return UsageExitCode;
        }

        await Task.WhenAll(
            WriteAsync(Console.OpenStandardOutput(), "nonzero-output\n"u8.ToArray()),
            WriteAsync(Console.OpenStandardError(), "nonzero-error\n"u8.ToArray()));
        return exitCode;
    }

    private static async Task<int> StartDescendantAsync(string[] arguments)
    {
        if (arguments.Length != 2
            || !(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
        {
            return UsageExitCode;
        }

        ProcessStartInfo startInfo = new("/bin/sh")
        {
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("exec sleep 300 </dev/null >/dev/null 2>&1");
        using Process descendant = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The descendant process did not start.");
        await WriteReadyFileAsync(
            arguments[1],
            descendant.Id.ToString(CultureInfo.InvariantCulture));
        await WriteAsync(
            Console.OpenStandardOutput(),
            Encoding.UTF8.GetBytes(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"descendant-ready:{descendant.Id}\n")));
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }

    private static async Task<int> HoldAsync(string[] arguments, string ready)
    {
        if (arguments.Length != 2)
        {
            return UsageExitCode;
        }

        await WriteReadyFileAsync(arguments[1], ready);
        await WriteAsync(
            Console.OpenStandardOutput(),
            Encoding.UTF8.GetBytes(ready + "\n"));
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }

    private static async Task WriteRepeatedAsync(
        Stream stream,
        byte[] chunk,
        int repeatCount)
    {
        for (int index = 0; index < repeatCount; index++)
        {
            await stream.WriteAsync(chunk);
        }

        await stream.FlushAsync();
    }

    private static async Task WriteAsync(Stream stream, byte[] bytes)
    {
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    private static async Task WriteReadyFileAsync(string path, string value)
    {
        string candidate = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(candidate, value);
            File.Move(candidate, path);
        }
        finally
        {
            File.Delete(candidate);
        }
    }
}
