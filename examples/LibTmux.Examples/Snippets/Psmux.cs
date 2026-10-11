namespace LibTmux.Examples.Snippets;

/// <summary>
/// Compile-checked psmux examples published by the Windows preview guide.
/// </summary>
public static class Psmux
{
    /// <summary>
    /// Reads the sole session, its windows, its panes, and pane text.
    /// </summary>
    [Example(
        "Query one pinned psmux namespace from Windows or WSL",
        RunsInDefaultSuite = false)]
    public static async Task QueryPsmux()
    {
        #region QueryPsmux
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        CancellationToken token = deadline.Token;

        static string Require(string name) =>
            Environment.GetEnvironmentVariable(name)
            ?? throw new InvalidOperationException($"{name} is required.");

        string executable = Require("LIBTMUX_PSMUX_BINARY");
        string dataDirectory = Require("PSMUX_DATA_DIR");
        string namespaceName = Require("LIBTMUX_PSMUX_NAMESPACE");

        PsmuxServer server = await PsmuxServer.ConnectAsync(
            new PsmuxConnectionOptions(
                executablePath: executable,
                expectedBinarySha256: PsmuxServer.SupportedBinarySha256,
                dataDirectory: dataDirectory,
                namespaceName: namespaceName),
            token);
        PsmuxSession session = await server.GetSessionAsync(token);

        Console.WriteLine($"{session.Id} {session.Name}");
        foreach (PsmuxWindow window in await session.GetWindowsAsync(token))
        {
            Console.WriteLine($"  {window.Id} {window.Index}: {window.Name}");
            foreach (PsmuxPane pane in await window.GetPanesAsync(token))
            {
                IReadOnlyList<string> lines = await pane.CaptureAsync(
                    new PsmuxCaptureOptions(joinWrappedLines: true),
                    token);
                Console.WriteLine($"    {pane.Id} {pane.Width}x{pane.Height}");
                foreach (string line in lines)
                {
                    Console.WriteLine($"      {line}");
                }
            }
        }
        #endregion
    }
}
