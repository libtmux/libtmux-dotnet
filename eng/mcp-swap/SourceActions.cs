using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace LibTmux.McpSwap;

internal sealed record SwapRuntime(
    string Home,
    string ConfigHome,
    string StateHome,
    string DotnetPath)
{
    internal static SwapRuntime Discover(bool requireDotnet)
    {
        string home = Environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!Path.IsPathFullyQualified(home))
        {
            throw new InvalidOperationException("HOME must name an absolute directory");
        }

        string configHome = AbsoluteEnvironment("XDG_CONFIG_HOME")
            ?? Path.Combine(home, ".config");
        string stateHome = AbsoluteEnvironment("XDG_STATE_HOME")
            ?? Path.Combine(home, ".local", "state");
        return new(
            Path.GetFullPath(home),
            Path.GetFullPath(configHome),
            Path.GetFullPath(stateHome),
            requireDotnet ? DotnetLocator.Find() : string.Empty);
    }

    internal string SwapDirectory => Path.Combine(StateHome, "libtmux-mcp-dev", "swap");

    internal string StateDirectory => Path.Combine(SwapDirectory, "dotnet");

    internal string StateFile => Path.Combine(StateDirectory, "state.json");

    internal string LockFile => Path.Combine(SwapDirectory, "state.lock");

    private static string? AbsoluteEnvironment(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return !string.IsNullOrEmpty(value) && Path.IsPathFullyQualified(value) ? value : null;
    }
}

internal interface ISourceProvisioner
{
    public void Prepare(SourcePlan plan, CommandOptions options);
}

internal sealed class SourceProvisioner : ISourceProvisioner
{
    public void Prepare(SourcePlan plan, CommandOptions options)
    {
        if (plan.Source is SourceKind.Debug or SourceKind.Release && !options.NoBuild)
        {
            Run(
                plan.Spec.Environment["DOTNET_ROOT"] + Path.DirectorySeparatorChar + "dotnet",
                [
                    "build",
                    plan.ProjectFile,
                    "--configuration",
                    plan.Source == SourceKind.Debug ? "Debug" : "Release",
                    "-m:5",
                ]);
        }
        else if (plan.Source == SourceKind.Published && !File.Exists(plan.Spec.Command))
        {
            string destination = Path.GetDirectoryName(plan.Spec.Command)!;
            Directory.CreateDirectory(destination);
            Run(
                plan.Spec.Environment["DOTNET_ROOT"] + Path.DirectorySeparatorChar + "dotnet",
                [
                    "tool",
                    "install",
                    "LibTmux.Mcp",
                    "--version",
                    options.Version!,
                    "--tool-path",
                    destination,
                ]);
        }

        if (!File.Exists(plan.Spec.Command))
        {
            throw new FileNotFoundException(
                $"{plan.Source.ToString().ToLowerInvariant()}: launcher does not exist: {plan.Spec.Command}",
                plan.Spec.Command);
        }
    }

    private static void Run(string command, IReadOnlyList<string> arguments)
    {
        using Process process = new()
        {
            StartInfo = new()
            {
                FileName = command,
                UseShellExecute = false,
            },
        };
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            if (!process.Start())
            {
                throw new IOException($"source preparation did not start: {command}");
            }

            if (!process.WaitForExit(300_000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                throw new IOException($"source preparation timed out: {command} {string.Join(' ', arguments)}");
            }

            if (process.ExitCode != 0)
            {
                throw new IOException($"source preparation failed: {command} {string.Join(' ', arguments)}");
            }
        }
        catch (System.ComponentModel.Win32Exception failure)
        {
            throw new IOException($"source preparation could not launch {command}", failure);
        }
    }
}

internal interface IServerPreflight
{
    public string? Probe(ServerSpec spec);
}

internal sealed class ServerPreflight(TimeSpan? timeout = null) : IServerPreflight
{
    internal const int MaximumStdoutCharacters = 1 << 20;
    internal const int MaximumStderrCharacters = 64 << 10;
    private static readonly string InitializeFrame = JsonSerializer.Serialize(
        new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { },
                clientInfo = new { name = "mcp-swap-preflight", version = "1" },
            },
        });

    private readonly TimeSpan timeout = timeout ?? TimeSpan.FromMinutes(5);

    public string? Probe(ServerSpec spec)
    {
        using Process process = new()
        {
            StartInfo = new()
            {
                FileName = spec.Command,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            },
        };
        foreach (string argument in spec.Arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        foreach ((string name, string value) in spec.Environment)
        {
            process.StartInfo.Environment[name] = value;
        }

        try
        {
            if (!process.Start())
            {
                return $"could not launch {spec.Command}";
            }
        }
        catch (Exception failure) when (failure is IOException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            return $"could not launch {spec.Command}: {failure.Message}";
        }

        try
        {
            return ProbeStarted(process);
        }
        finally
        {
            Terminate(process);
        }
    }

    private string? ProbeStarted(Process process)
    {
        using CancellationTokenSource cancellation = new(timeout);
        Task<CapturedText> stderr = CaptureAsync(
            process.StandardError,
            MaximumStderrCharacters,
            cancellation.Token);
        try
        {
            process.StandardInput.WriteLine(InitializeFrame);
            process.StandardInput.Flush();
        }
        catch (IOException failure)
        {
            return $"{Specification(process)} closed stdin before answering: {failure.Message}";
        }

        try
        {
            Task<string?> response = ReadResponseAsync(process.StandardOutput, cancellation.Token);
            Task completed = Task.WhenAny(response, stderr).GetAwaiter().GetResult();
            if (ReferenceEquals(completed, stderr))
            {
                CapturedText earlyDiagnostic = stderr.GetAwaiter().GetResult();
                if (earlyDiagnostic.Exceeded)
                {
                    Terminate(process);
                    return $"server stderr exceeds {MaximumStderrCharacters} characters";
                }
            }

            string? responseFailure = response.GetAwaiter().GetResult();
            if (responseFailure is null)
            {
                process.StandardInput.Close();
                return null;
            }

            Terminate(process);
            CapturedText diagnostic = stderr.GetAwaiter().GetResult();
            if (diagnostic.Exceeded)
            {
                return $"server stderr exceeds {MaximumStderrCharacters} characters";
            }

            string detail = diagnostic.Text.Trim();
            return detail.Length > 0
                ? $"server exited before initialize: {detail}"
                : responseFailure;
        }
        catch (OperationCanceledException)
        {
            return $"{Specification(process)} did not answer initialize within {timeout.TotalSeconds:g} seconds";
        }
    }

    private static async Task<string?> ReadResponseAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        char[] buffer = new char[4096];
        StringBuilder line = new();
        int total = 0;
        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return line.Length > 0 && IsInitializeResponse(line.ToString())
                    ? null
                    : "server exited before answering initialize";
            }

            total = checked(total + read);
            if (total > MaximumStdoutCharacters)
            {
                return $"server stdout exceeds {MaximumStdoutCharacters} characters before initialize";
            }

            for (int index = 0; index < read; index++)
            {
                char character = buffer[index];
                if (character == '\n')
                {
                    if (IsInitializeResponse(line.ToString()))
                    {
                        return null;
                    }

                    line.Clear();
                }
                else if (character != '\r')
                {
                    line.Append(character);
                }
            }
        }
    }

    private static bool IsInitializeResponse(string line)
    {
        try
        {
            using JsonDocument message = JsonDocument.Parse(line);
            JsonElement root = message.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("jsonrpc", out JsonElement jsonrpc)
                && jsonrpc.ValueKind == JsonValueKind.String
                && string.Equals(jsonrpc.GetString(), "2.0", StringComparison.Ordinal)
                && root.TryGetProperty("id", out JsonElement id)
                && id.TryGetInt32(out int value)
                && value == 1
                && root.TryGetProperty("result", out JsonElement result)
                && result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("protocolVersion", out JsonElement protocolVersion)
                && protocolVersion.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(protocolVersion.GetString());
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<CapturedText> CaptureAsync(
        StreamReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        char[] buffer = new char[4096];
        StringBuilder kept = new();
        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return new(kept.ToString(), Exceeded: false);
            }

            int remaining = maximumCharacters - kept.Length;
            if (remaining > 0)
            {
                kept.Append(buffer, 0, Math.Min(read, remaining));
            }

            if (read > remaining)
            {
                return new(kept.ToString(), Exceeded: true);
            }
        }
    }

    private static string Specification(Process process) => process.StartInfo.FileName;

    private static void Terminate(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
    }

    private sealed record CapturedText(string Text, bool Exceeded);
}

internal static class DotnetLocator
{
    internal static string Find()
    {
        string? host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(host) && File.Exists(host))
        {
            return Path.GetFullPath(host);
        }

        string? rooted = FromRoot(Environment.GetEnvironmentVariable("DOTNET_ROOT"));
        if (rooted is not null)
        {
            return rooted;
        }

        try
        {
            ProcessStartInfo start = new()
            {
                FileName = "mise",
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("which");
            start.ArgumentList.Add("dotnet");
            using Process process = Process.Start(start) ?? throw new IOException("mise did not start");
            string resolved = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            if (process.ExitCode == 0 && File.Exists(resolved))
            {
                return Path.GetFullPath(resolved);
            }
        }
        catch (Exception failure) when (failure is IOException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
        }

        string? path = ExecutableFinder.Find("dotnet");
        if (path is not null)
        {
            return path;
        }

        throw new FileNotFoundException(
            "no dotnet on PATH and mise could not name one; install the pinned SDK or run inside mise exec");
    }

    internal static string? FromRoot(string? root)
    {
        if (string.IsNullOrEmpty(root) || !Path.IsPathFullyQualified(root))
        {
            return null;
        }

        string candidate = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
    }
}
