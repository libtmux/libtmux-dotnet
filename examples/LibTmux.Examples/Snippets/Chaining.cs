using System.Runtime.Versioning;

namespace LibTmux.Examples.Snippets;

/// <summary>
/// Many commands handed to tmux at once, paying for one process.
/// </summary>
[UnsupportedOSPlatform("windows")]
public static class Chaining
{
    /// <summary>Runs three commands through a single tmux invocation.</summary>
    [Example("Three commands, one process")]
    public static async Task ManyCommandsOneProcess(
        Server server,
        CancellationToken ct)
    {
        #region ManyCommandsOneProcess
        await server.Chain()
            .Then("new-window", "-d", "-n", "build")
            .Then("new-window", "-d", "-n", "test")
            .Then("new-window", "-d", "-n", "lint")
            .ExecuteAsync(ct);
        #endregion
    }

    /// <summary>Asks the chain itself for something to read back.</summary>
    [Example("Read a value back out of a chain")]
    public static async Task ReadBackFromAChain(
        Server server,
        CancellationToken ct)
    {
        #region ReadBackFromAChain
        TmuxCommandResult result = await server.Chain()
            .Then("new-window", "-d", "-n", "build")
            .Then("display-message", "-p", "#{window_id}")
            .ExecuteAsync(ct);
        #endregion

        Console.WriteLine(result.StandardOutputLines[0]);
    }
}
