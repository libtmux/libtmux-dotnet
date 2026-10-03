using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace LibTmux.IntegrationTests.Infrastructure;

/// <summary>Writes a script a test runs in place of a program.</summary>
[UnsupportedOSPlatform("windows")]
internal static class TestExecutable
{
    // errno 26. Process.Start surfaces it as the native error code on Linux.
    private const int TextFileBusy = 26;

    /// <summary>Writes an executable script and returns once it can be run.</summary>
    /// <remarks>
    /// A process another test starts at the same moment inherits the write
    /// handle until it execs, and running the script meanwhile fails with
    /// "Text file busy", so this waits until a run with <c>-V</c> starts.
    /// </remarks>
    internal static async Task WriteAsync(string path, string contents, CancellationToken cancellationToken)
    {
        string candidate = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(candidate, contents, cancellationToken);
            File.SetUnixFileMode(
                candidate,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.Move(candidate, path);
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TestBudget.Settle);
            while (!CanExecute(path))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
            }
        }
        finally
        {
            File.Delete(candidate);
        }
    }

    private static bool CanExecute(string path)
    {
        try
        {
            using Process? probe = Process.Start(
                new ProcessStartInfo(path, "-V")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
            probe?.WaitForExit();
            return true;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == TextFileBusy)
        {
            return false;
        }
    }
}
