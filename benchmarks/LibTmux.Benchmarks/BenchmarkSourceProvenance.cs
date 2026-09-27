using System.Diagnostics;

namespace LibTmux.Benchmarks;

/// <summary>Reads the source revision that produced a benchmark sample.</summary>
internal static class BenchmarkSourceProvenance
{
    internal static async Task<(string Commit, bool Clean)> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            string? repositoryRoot = FindRepositoryRoot();
            if (repositoryRoot is null)
            {
                return (string.Empty, false);
            }

            string commit = (await GitOutputAsync(repositoryRoot, ["rev-parse", "HEAD"], cancellationToken)
                .ConfigureAwait(false)).Trim();
            string status = await GitOutputAsync(
                repositoryRoot, ["status", "--porcelain", "--untracked-files=normal"], cancellationToken)
                .ConfigureAwait(false);
            return (commit, commit.Length == 40 && commit.All(Uri.IsHexDigit) && status.Length == 0);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return (string.Empty, false);
        }
    }

    private static string? FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
            directory is not null;
            directory = directory.Parent)
        {
            string git = Path.Combine(directory.FullName, ".git");
            if ((Directory.Exists(git) || File.Exists(git))
                && File.Exists(Path.Combine(directory.FullName, "LibTmux.slnx")))
            {
                return directory.FullName;
            }
        }

        return null;
    }

    private static async Task<string> GitOutputAsync(
        string repositoryRoot,
        string[] arguments,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = repositoryRoot,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("Git did not start for benchmark source provenance.");
        try
        {
            string output = await process.StandardOutput.ReadToEndAsync(cancellationToken)
                .ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException("Git could not read benchmark source provenance.");
            }

            return output;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }
}
