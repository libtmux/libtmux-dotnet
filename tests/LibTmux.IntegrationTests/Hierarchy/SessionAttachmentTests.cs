using System.Diagnostics;
using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;

namespace LibTmux.IntegrationTests.Hierarchy;

[UnsupportedOSPlatform("windows")]
public sealed class SessionAttachmentTests
{
    [Theory(Skip = "Requires a Unix terminal.", SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Public_attachment_accepts_terminal_input_and_detaches_without_stopping_borrowed_clients(bool cancel)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        CancellationToken token = deadline.Token;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        await using ControlModeClientScope sentinel = await ControlModeClientScope.StartAsync(raw, token);
        Server server = await Server.ConnectAsync(new ServerConnectionOptions
        {
            TmuxBinaryPath = raw.TmuxBinaryPath,
            SocketPath = raw.SocketPath,
            ConfigurationFile = "/dev/null",
        }, token);
        Session session = (await server.GetSessionsAsync(token)).Single();
        string attachedChannel = $"attached_{Guid.NewGuid():N}";
        string cancelChannel = $"cancel_{Guid.NewGuid():N}";
        string resultPath = Path.Combine(Path.GetTempPath(), $"attachment_{Guid.NewGuid():N}.txt");
        Assert.Equal(0, (await raw.ExecuteAsync(["respawn-pane", "-k", "-t", session.Id + ":", "exec /bin/cat"], token)).ExitCode);
        Assert.Equal(0, (await raw.ExecuteAsync(["set-hook", "-g", "client-attached", $"wait-for -S {attachedChannel}"], token)).ExitCode);
        await using TmuxWaitChannel attached = server.OpenWaitChannel(attachedChannel);
        using Process client = Process.Start(CreateStartInfo(raw, session.Id, cancelChannel, resultPath, cancel))
            ?? throw new InvalidOperationException("The attached API test child did not start.");
        Task output = client.StandardOutput.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);
        Task errors = client.StandardError.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);
        try
        {
            Assert.True(await attached.WaitAsync(TimeSpan.FromSeconds(5), token));
            string marker = $"typed_{Guid.NewGuid():N}";
            await client.StandardInput.WriteLineAsync(marker.AsMemory(), token);
            await client.StandardInput.FlushAsync(token);
            while (true)
            {
                string? line = await sentinel.ReadLineAsync(token);
                Assert.NotNull(line);
                if (line.StartsWith("%output ", StringComparison.Ordinal)
                    && line.Contains(marker, StringComparison.Ordinal))
                {
                    break;
                }
            }
            RawTmuxResult captured = await raw.ExecuteAsync(["capture-pane", "-p", "-t", session.Id + ":"], token);
            Assert.Contains(marker, captured.StandardOutputText, StringComparison.Ordinal);
            if (cancel)
            {
                await server.WaitForAsync(new WaitForRequest(cancelChannel, TmuxWaitMode.Signal), token);
            }
            else
            {
                RawTmuxResult clients = await raw.ExecuteAsync(["list-clients", "-F", "#{client_control_mode}\t#{client_tty}"], token);
                string terminal = Assert.Single(clients.StandardOutputLines,
                    line => line.StartsWith("0\t", StringComparison.Ordinal))[2..];
                Assert.Equal(0, (await raw.ExecuteAsync(["detach-client", "-t", terminal], token)).ExitCode);
            }
            await client.WaitForExitAsync(token);
            Assert.Equal(0, client.ExitCode);
            string result = await File.ReadAllTextAsync(resultPath, token);
            Assert.StartsWith(cancel ? "cancelled:True" : $"returned:{session.Id}:@", result, StringComparison.Ordinal);
            Assert.Equal(server.Generation, (await server.InspectAsync(token))!.Generation);
            Assert.Equal(0, (await raw.ExecuteAsync(["has-session", "-t", session.Id.ToString()], token)).ExitCode);
            RawTmuxResult survivors = await raw.ExecuteAsync(["list-clients", "-F", "#{client_pid}"], token);
            Assert.Contains(sentinel.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), survivors.StandardOutputLines);
        }
        finally
        {
            if (!client.HasExited)
            {
                client.Kill(entireProcessTree: true);
            }
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.WaitForExitAsync(cleanup.Token);
            await Task.WhenAll(output, errors).WaitAsync(cleanup.Token);
            File.Delete(resultPath);
        }
    }

    private static ProcessStartInfo CreateStartInfo(RawTmuxTestContext raw, SessionId session,
        string cancelChannel, string resultPath, bool cancel)
    {
        DirectoryInfo framework = new(AppContext.BaseDirectory);
        DirectoryInfo configuration = framework.Parent!;
        string child = Path.Combine(configuration.Parent!.Parent!.Parent!.FullName,
            "LibTmux.TestChild", "bin", configuration.Name, framework.Name, "LibTmux.TestChild.dll");
        string[] command = ["dotnet", child, "attach-session", raw.TmuxBinaryPath,
            raw.SocketPath, session.ToString(), cancelChannel, resultPath, cancel ? "cancel" : "detach"];
        var start = new ProcessStartInfo("/usr/bin/script")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-q");
        if (OperatingSystem.IsLinux())
        {
            start.ArgumentList.Add("-e");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("exec " + string.Join(' ', command.Select(static value =>
                "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'")));
            start.ArgumentList.Add("/dev/null");
        }
        else
        {
            start.ArgumentList.Add("/dev/null");
            foreach (string argument in command)
            {
                start.ArgumentList.Add(argument);
            }
        }
        RawTmuxTestContext.ConfigureEnvironment(start);
        start.Environment["TERM"] = "xterm-256color";
        return start;
    }
}
